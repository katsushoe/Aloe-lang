using Aloe.CommonLib.Exceptions;
using System;

namespace Aloe.CommonLib
{
    public class BytecodeReader
    {
        private readonly byte[] _code;

        public int Position { get; set; }

        public int Length => _code.Length;

        public BytecodeReader(byte[] code)
        {
            _code = code ?? throw new ArgumentNullException(nameof(code));
            Position = 0;
        }

        public byte ReadByte()
        {
            if (Position >= _code.Length)
            {
                throw new VmException("Unexpected end of bytecode while reading byte.");
            }

            return _code[Position++];
        }

        public int ReadInt32()
        {
            if (Position + 4 > _code.Length)
            {
                throw new VmException("Unexpected end of bytecode while reading int32.");
            }

            int value = BitConverter.ToInt32(_code, Position);
            Position += 4;
            return value;
        }
    }
}
