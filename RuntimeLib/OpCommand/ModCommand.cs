using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;


namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class ModCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var right = vm.Pop();
            var left = vm.Pop();


            if (!left.IsInt || !right.IsInt)
            {
                throw new VmException(
                    $"Mod is defined only for int operands " +
                    $"(left={left.Kind}, right={right.Kind}).");
            }


            if (right.AsInt == 0)
                throw new Aloe.CommonLib.Exceptions.ZeroDivisionException(
                    "Modulo by zero.");


            vm.Push(AloeValue.FromInt(left.AsInt % right.AsInt));
        }
    }
}