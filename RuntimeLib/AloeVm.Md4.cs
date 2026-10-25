using System.Buffers.Binary;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void RunMd4FilterQuantum(VmExecutionContext context)
    {
        var input = _pipes[context.InputPipeId];
        var outputId = context.Binding!.OutputPipeId;
        var output = _pipes[outputId];

        if (context.HashResult == null)
        {
            if (input.Values.Count > 0)
            {
                var value = input.Values.Dequeue();
                if (!value.IsByte)
                    throw new VmException("The md4 standard filter expects byte values.");
                context.Md4Hash ??= new Md4HashState();
                context.Md4Hash.Append(value.AsByte);
                WakeOnePipeWriter(context.InputPipeId, input);
                return;
            }

            if (!input.IsClosed)
            {
                WaitForPipeRead(context.InputPipeId, input, context);
                _contextSuspendRequested = true;
                return;
            }

            context.Md4Hash ??= new Md4HashState();
            context.HashResult = context.Md4Hash.Finish();
            context.Md4Hash = null;
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
            output.Values.Enqueue(Aloe.CommonLib.AloeValue.FromByte(context.HashResult[context.HashResultIndex++]));
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

    private sealed class Md4HashState
    {
        private static readonly int[] Round1Order = [.. Enumerable.Range(0, 16)];
        private static readonly int[] Round2Order = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
        private static readonly int[] Round3Order = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
        private static readonly int[] Round1Shifts = [3, 7, 11, 19];
        private static readonly int[] Round2Shifts = [3, 5, 9, 13];
        private static readonly int[] Round3Shifts = [3, 9, 11, 15];

        private readonly uint[] _state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        private readonly byte[] _block = new byte[64];
        private int _bufferedBytes;
        private ulong _totalBytes;

        public void Append(byte value)
        {
            _block[_bufferedBytes++] = value;
            _totalBytes = unchecked(_totalBytes + 1);
            if (_bufferedBytes == _block.Length)
            {
                CompressBlock(_block);
                _bufferedBytes = 0;
            }
        }

        public byte[] Finish()
        {
            var bitLength = unchecked(_totalBytes * 8);
            _block[_bufferedBytes++] = 0x80;
            if (_bufferedBytes > 56)
            {
                Array.Clear(_block, _bufferedBytes, _block.Length - _bufferedBytes);
                CompressBlock(_block);
                _bufferedBytes = 0;
            }
            Array.Clear(_block, _bufferedBytes, 56 - _bufferedBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(_block.AsSpan(56), bitLength);
            CompressBlock(_block);

            var digest = new byte[16];
            for (var i = 0; i < _state.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(i * sizeof(uint), sizeof(uint)), _state[i]);
            return digest;
        }

        private void CompressBlock(ReadOnlySpan<byte> block)
        {
            Span<uint> words = stackalloc uint[16];
            Span<uint> work = stackalloc uint[4];
            for (var i = 0; i < words.Length; i++)
                words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * sizeof(uint), sizeof(uint)));

            _state.CopyTo(work);
            ApplyRound(work, words, Round1Order, Round1Shifts, 0);
            ApplyRound(work, words, Round2Order, Round2Shifts, 0x5A827999);
            ApplyRound(work, words, Round3Order, Round3Shifts, 0x6ED9EBA1);
            for (var i = 0; i < _state.Length; i++)
                _state[i] = unchecked(_state[i] + work[i]);
        }
    }

    private static void ApplyRound(Span<uint> work, ReadOnlySpan<uint> words, ReadOnlySpan<int> order,
        ReadOnlySpan<int> shifts, uint additiveConstant)
    {
        for (var i = 0; i < order.Length; i++)
        {
            var target = (4 - i % 4) % 4;
            var x = work[(target + 1) % 4];
            var y = work[(target + 2) % 4];
            var z = work[(target + 3) % 4];
            var nonlinear = additiveConstant switch
            {
                0 => (x & y) | (~x & z),
                0x5A827999 => (x & y) | (x & z) | (y & z),
                _ => x ^ y ^ z
            };
            var sum = unchecked(work[target] + nonlinear + words[order[i]] + additiveConstant);
            work[target] = (sum << shifts[i % 4]) | (sum >> (32 - shifts[i % 4]));
        }
    }
}
