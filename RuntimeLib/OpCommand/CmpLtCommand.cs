using Aloe.CommonLib;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class CmpLtCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // 右側・左側のオペランドをスタックから取り出す
            var right = vm.Pop();
            var left = vm.Pop();

            vm.Push(AloeValue.FromBool(
                ComparisonSupport.CompareNumber(left, right) < 0));
        }
    }
}
