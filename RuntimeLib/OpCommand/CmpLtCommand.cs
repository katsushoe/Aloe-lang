using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class CmpLtCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // 右側・左側のオペランドをスタックから取り出す
            var right = vm.Pop();
            var left = vm.Pop();

            // 数値以外には対応しない
            if (!left.IsNumber || !right.IsNumber)
            {
                throw new VmException(
                    $"CmpLt is only defined for numeric operands (left={left.Kind}, right={right.Kind}).");
            }

            bool result;

            // どちらかが Float なら浮動小数で比較
            if (left.IsFloat || right.IsFloat)
            {
                // ★ ここがポイント: プロパティなので () を付けない
                result = left.AsFloat < right.AsFloat;
            }
            else
            {
                // 両方 Int のときは整数比較
                result = left.AsInt < right.AsInt;
            }

            vm.Push(AloeValue.FromBool(result));
        }
    }
}
