using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class BitwiseCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = PopInt32(vm);
            if (instruction.Opcode is EnumOpcode.BitShiftLeft or EnumOpcode.BitShiftRight && (right < 0 || right > 31))
                throw new VmException("Bitfield shift count must be from 0 to 31.");
            var result = instruction.Opcode == EnumOpcode.BitNot
                ? ~right
                : ApplyBinary(instruction.Opcode, PopInt32(vm), right);
            vm.Push(AloeValue.FromInt(result));
        }

        private static int PopInt32(AloeVm vm)
        {
            var value = vm.Pop();
            if (!value.IsInt || value.AsInt < int.MinValue || value.AsInt > int.MaxValue)
                throw new VmException("Bitwise opcode requires signed int32 operands.");
            return (int)value.AsInt;
        }

        private static int ApplyBinary(EnumOpcode opcode, int left, int right) => opcode switch
        {
            EnumOpcode.BitAnd => left & right,
            EnumOpcode.BitOr => left | right,
            EnumOpcode.BitXor => left ^ right,
            EnumOpcode.BitShiftLeft => unchecked(left << right),
            EnumOpcode.BitShiftRight => left >> right,
            _ => throw new VmException($"Invalid bitwise opcode {opcode}.")
        };
    }
}
