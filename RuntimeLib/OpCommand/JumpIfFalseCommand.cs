using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    public sealed class BrIfCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var cond = vm.Pop();
            bool value;
            try
            {
                value = cond.AsBool;
            }
            catch (VmException ex)
            {
                throw new VmException($"BR_IF expects Bool condition, but got {cond.Kind}.", ex);
            }

            if (value)
                vm.Branch(frame, instruction.Operand0);
        }
    }
}
