using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 条件が false の場合にジャンプする命令。
    ///
    /// Stack: [..., cond] → [...]
    /// cond が false なら IP をオペランド位置へ書き換える。
    /// </summary>
    public sealed class JumpIfFalseCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // スタックトップから条件を取得
            var cond = vm.Pop();

            bool value;
            try
            {
                // ★ AsBool はプロパティなので () を付けない
                value = cond.AsBool;
            }
            catch (VmException ex)
            {
                throw new VmException(
                    $"JumpIfFalse expects Bool condition, but got {cond.Kind}.", ex);
            }

            // false のときだけジャンプ
            if (!value)
            {
                var target = instruction.Operand;

                if (target < 0)
                {
                    throw new VmException($"JumpIfFalse: invalid jump target {target}.");
                }

                // AloeVm.Run 側で frame.Ip++ 済みを想定して、
                // ここでは絶対 IP として上書き
                frame.Ip = target;
            }
            // true の場合は何もしない（Run ループ側の Ip++ のまま次の命令へ）
        }
    }
}
