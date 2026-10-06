using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 評価スタックトップの値をローカル変数に書き込む命令。
    ///
    /// Operand: ローカル変数インデックス (0-based)
    /// Stack: [..., value] -> [...]
    /// </summary>
    public sealed class StoreLocalCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var index = instruction.Operand;

            if (index < 0 || index >= frame.Locals.Length)
            {
                throw new VmException($"StoreLocal: invalid local index {index}.");
            }

            // スタックから値を取り出してローカルに保存
            var value = vm.Pop();
            frame.Locals[index] = value;
        }
    }
}
