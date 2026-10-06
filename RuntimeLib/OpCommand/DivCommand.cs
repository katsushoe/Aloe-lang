using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 除算命令。
    ///
    /// Stack: [..., left, right] -> [..., (left / right)]
    /// AloeValue の operator / が ZeroDivisionException を投げる。
    /// </summary>
    public sealed class DivCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // スタックから右・左の順に取り出す（left / right）
            var right = vm.Pop();
            var left = vm.Pop();

            // AloeValue.operator / 内で数値チェック & ゼロ除算チェック
            var result = left / right;

            vm.Push(result);
        }
    }
}
