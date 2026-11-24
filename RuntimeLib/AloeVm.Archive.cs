using System.Formats.Tar;
using System.IO.Compression;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // Decision 0057: single-entry archive filters on pipe<byte>.
    // -45 zip, -46 unzip, -47 tar, -48 untar.
    private const long ArchiveExtractLimit = 64L * 1024 * 1024;
    private const string ArchiveEntryName = "data";
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static bool IsArchiveFilter(int index) => index is -45 or -46 or -47 or -48;

    private static string ArchiveFilterName(int index) => index switch
    {
        -45 => "zip",
        -46 => "unzip",
        -47 => "tar",
        _ => "untar"
    };

    private void RunArchiveFilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];
        var index = context.Binding.FunctionIndex;

        if (context.HashResult == null)
        {
            var buffer = context.ArchiveInput ??= new MemoryStream();
            while (input.Values.Count > 0)
            {
                var value = input.Values.Dequeue();
                if (!value.IsByte)
                    throw new VmException($"The {ArchiveFilterName(index)} standard filter expects byte values.");
                buffer.WriteByte(value.AsByte);
                WakeOnePipeWriter(context.InputPipeId, input);
            }
            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }

            var bytes = buffer.ToArray();
            context.ArchiveInput = null;
            context.HashResult = index switch
            {
                -45 => CreateZip(bytes),
                -46 => ExtractZip(bytes),
                -47 => CreateTar(bytes),
                _ => ExtractTar(bytes)
            };
        }

        if (context.HashResultIndex < context.HashResult.Length)
        {
            if (output.IsClosed)
                throw new VmException("Cannot write to a closed pipe.");
            if (output.IsFull)
            {
                WaitForPipeWrite(outputId, output, context);
                _contextSuspendRequested = true;
                return;
            }
            output.Values.Enqueue(AloeValue.FromByte(context.HashResult[context.HashResultIndex++]));
            WakeOnePipeReader(outputId, output);
            SchedulePipeFilters(outputId, output);
            return;
        }

        output.IsClosed = true;
        WakeAllPipeWriters(outputId, output);
        WakeAllPipeReaders(outputId, output);
        SchedulePipeFilters(outputId, output);
        context.IsCompleted = true;
    }

    private static byte[] CreateZip(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(ArchiveEntryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }
        return stream.ToArray();
    }

    private static byte[] ExtractZip(byte[] data)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            if (archive.Entries.Count != 1)
                throw new VmException($"The unzip standard filter requires exactly one entry; found {archive.Entries.Count}.");
            var entry = archive.Entries[0];
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                throw new VmException("The unzip standard filter accepts only a regular file entry.");
            if (entry.Length > ArchiveExtractLimit)
                throw new VmException("The unzip standard filter output exceeds the 64 MiB limit.");
            using var entryStream = entry.Open();
            return ReadLimited(entryStream, "unzip");
        }
        catch (VmException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException)
        {
            throw new VmException($"The unzip standard filter received an invalid or unsupported ZIP archive: {exception.Message}");
        }
    }

    private static byte[] CreateTar(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, ArchiveEntryName)
            {
                ModificationTime = ArchiveTimestamp,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                Uid = 0,
                Gid = 0,
                UserName = string.Empty,
                GroupName = string.Empty,
                DataStream = new MemoryStream(data)
            };
            writer.WriteEntry(entry);
        }
        return stream.ToArray();
    }

    private static byte[] ExtractTar(byte[] data)
    {
        try
        {
            using var reader = new TarReader(new MemoryStream(data));
            var entry = reader.GetNextEntry(copyData: false)
                ?? throw new VmException("The untar standard filter requires exactly one entry; found 0.");
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new VmException("The untar standard filter accepts only a regular file entry.");
            if (entry.Length > ArchiveExtractLimit)
                throw new VmException("The untar standard filter output exceeds the 64 MiB limit.");
            var content = entry.DataStream == null ? Array.Empty<byte>() : ReadLimited(entry.DataStream, "untar");
            if (reader.GetNextEntry(copyData: false) != null)
                throw new VmException("The untar standard filter requires exactly one entry; found more.");
            return content;
        }
        catch (VmException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException or EndOfStreamException)
        {
            throw new VmException($"The untar standard filter received an invalid TAR archive: {exception.Message}");
        }
    }

    private static byte[] ReadLimited(Stream source, string filterName)
    {
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + read > ArchiveExtractLimit)
                throw new VmException($"The {filterName} standard filter output exceeds the 64 MiB limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
