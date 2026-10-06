using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using System;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 加算命令 (ADD)。
    ///
    /// - Int + Int           => Int
    /// - Decimal + Decimal   => Decimal
    /// - どちらか String     => 文字列連結
    /// それ以外の組み合わせは VmException。
    /// </summary>
    public sealed class AddCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // 右オペランド、左オペランドの順でポップ
            var right = vm.Pop();
            var left = vm.Pop();

            // int + int
            if (left.Kind == EnumValueKind.Int && right.Kind == EnumValueKind.Int)
            {
                // AsInt は long 前提
                long sum = left.AsInt + right.AsInt;
                vm.Push(AloeValue.FromInt(checked((int)sum)));
                return;
            }

            // decimal + decimal
            if (left.Kind == EnumValueKind.Decimal && right.Kind == EnumValueKind.Decimal)
            {
                decimal result = left.AsDecimal + right.AsDecimal;
                vm.Push(AloeValue.FromDecimal(result));
                return;
            }

            // どちらかが文字列なら文字列連結
            if (left.Kind == EnumValueKind.String || right.Kind == EnumValueKind.String)
            {
                string result = left.ToString() + right.ToString();
                vm.Push(AloeValue.FromString(result));
                return;
            }

            // それ以外の組み合わせは今のところ未対応
            throw new VmException(
                $"ADD: unsupported operand types {left.Kind} and {right.Kind}.");
        }
    }
}
