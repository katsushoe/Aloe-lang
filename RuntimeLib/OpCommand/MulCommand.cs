using System;
using Aloe.CommonLib;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 乗算命令。
    ///
    /// Stack: [..., left, right] -> [..., (left * right)]
    /// 数値でない場合は AloeValue 側で例外になる想定。
    /// </summary>
    public sealed class MulCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // スタックから右・左の順に取り出す（left * right）
            var right = vm.Pop();
            var left = vm.Pop();

            // AloeValue.operator * に任せる
            var result = left * right;

            vm.Push(result);
        }
    }
}
