using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 無条件ジャンプ命令。
    ///
    /// IP をオペランドで指定された位置に書き換える。
    /// </summary>
    public sealed class JumpCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var target = instruction.Operand;

            if (target < 0)
            {
                throw new VmException($"Jump: invalid jump target {target}.");
            }

            // Run ループ側で Ip++ 済みを想定しているので、
            // ここでは絶対 IP として上書きする。
            frame.Ip = target;
        }
    }
}
