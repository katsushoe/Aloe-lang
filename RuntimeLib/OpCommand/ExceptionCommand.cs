using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>Executes the synchronous exception-handling prototype opcodes.</summary>
    public sealed class ExceptionCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            switch (instruction.Opcode)
            {
                case EnumOpcode.TryBegin:
                    vm.BeginExceptionHandler(frame, instruction.Operand0,
                        instruction.Operand1 == 0 ? -1 : instruction.Operand1 - 1);
                    break;
                case EnumOpcode.TryEnd:
                    vm.EndExceptionHandler(frame);
                    break;
                case EnumOpcode.CatchEnd:
                    vm.EndCatch(frame);
                    break;
                case EnumOpcode.Rethrow:
                    vm.Rethrow(frame);
                    break;
                case EnumOpcode.TryCatch:
                    // Catch-table descriptors are skipped by normal control flow.
                    break;
                default:
                    throw new Aloe.CommonLib.Exceptions.VmException(
                        $"Unsupported exception opcode: {instruction.Opcode}.");
            }
        }
    }
}
