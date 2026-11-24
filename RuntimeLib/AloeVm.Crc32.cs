using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // CRC-32/ISO-HDLC (zlib/PNG/ZIP): reflected polynomial 0xEDB88320,
    // initial value and final XOR 0xFFFFFFFF. Decision 0053 emits the value
    // as four big-endian bytes on a pipe<byte>.
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private void RunCrc32FilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];

        if (context.HashResult == null)
        {
            var crc = context.Crc32State ?? 0xFFFFFFFFu;
            while (input.Values.Count > 0)
            {
                var value = input.Values.Dequeue();
                if (!value.IsByte)
                    throw new VmException("The crc32 standard filter expects byte values.");
                crc = Crc32Table[(crc ^ value.AsByte) & 0xFF] ^ (crc >> 8);
                WakeOnePipeWriter(context.InputPipeId, input);
            }
            context.Crc32State = crc;

            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }

            var result = crc ^ 0xFFFFFFFFu;
            context.HashResult =
            [
                (byte)(result >> 24),
                (byte)(result >> 16),
                (byte)(result >> 8),
                (byte)result
            ];
            context.Crc32State = null;
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

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var c = i;
            for (var bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}
