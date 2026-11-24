using System.Formats.Tar;
using System.IO.Compression;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0057: single-entry zip / unzip / tar / untar on pipe<byte>.
public sealed class ArchiveFilterTests
{
    private static AloeVm Vm(string source)
        => new(AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source))));

    private static string Writes(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
            sb.Append($"let b{i}: byte = {bytes[i]}; source.write(b{i});\n");
        return sb.ToString();
    }

    // Runs `source |: <stages> | result;` and returns the produced bytes.
    private static byte[] RunBytes(string stages, byte[] input, int capacity = 0)
    {
        var create = capacity > 0 ? $"create({capacity})" : "create()";
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.{{create}};
    let result: pipe<byte> = pipe<byte>.create();
    source {{stages}} | result;
    {{Writes(input)}}
    source.close();
    foreach (value in result) { print(value); }
    return 0;
}
""";
        var output = new List<byte>();
        var vm = Vm(source);
        vm.OutputWriter = line => output.Add(byte.Parse(line));
        vm.RunFromEntryPoint();
        return output.ToArray();
    }

    private static readonly byte[] Sample = System.Text.Encoding.ASCII.GetBytes("hello archive hello archive");

    [TestCase("|: zip |: unzip")]
    [TestCase("|: tar |: untar")]
    public void RoundTripRestoresTheInput(string stages)
    {
        Assert.That(RunBytes(stages, Sample), Is.EqualTo(Sample));
        Assert.That(RunBytes(stages, []), Is.Empty);
        Assert.That(RunBytes(stages, [7], capacity: 1), Is.EqualTo(new byte[] { 7 }));
    }

    [TestCase("|: zip")]
    [TestCase("|: tar")]
    public void OutputIsDeterministicAndReadableByDotNet(string stage)
    {
        var first = RunBytes(stage, Sample);
        Assert.That(RunBytes(stage, Sample), Is.EqualTo(first));
        if (stage == "|: zip")
        {
            using var archive = new ZipArchive(new MemoryStream(first));
            Assert.That(archive.Entries.Single().FullName, Is.EqualTo("data"));
        }
        else
        {
            using var reader = new TarReader(new MemoryStream(first));
            var entry = reader.GetNextEntry()!;
            Assert.That(entry.Name, Is.EqualTo("data"));
            Assert.That(entry.ModificationTime.Year, Is.EqualTo(1980));
        }
    }

    [Test]
    public void ExternallyCreatedSingleEntryArchivesAreExtracted()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        using (var s = archive.CreateEntry("other.txt").Open()) s.Write(Sample);
        Assert.That(RunBytes("|: unzip", zip.ToArray()), Is.EqualTo(Sample));
    }

    private static byte[] TwoEntryZip()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            archive.CreateEntry("a");
            archive.CreateEntry("b");
        }
        return zip.ToArray();
    }

    private static byte[] DirectoryTar()
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, true))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "dir/"));
        return tar.ToArray();
    }

    [Test]
    public void InvalidArchivesRaiseVmErrors()
    {
        Assert.Throws<VmException>(() => RunBytes("|: unzip", TwoEntryZip()));
        Assert.Throws<VmException>(() => RunBytes("|: untar", DirectoryTar()));
        Assert.Throws<VmException>(() => RunBytes("|: unzip", Sample));
        Assert.Throws<VmException>(() => RunBytes("|: unzip", []));
        Assert.Throws<VmException>(() => RunBytes("|: untar", []));
    }

    [TestCase("zip")]
    [TestCase("untar")]
    public void MismatchedPipeTypesAreCompileErrors(string filter)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<byte> = pipe<byte>.create();
    source |: {{filter}} | result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }
}
