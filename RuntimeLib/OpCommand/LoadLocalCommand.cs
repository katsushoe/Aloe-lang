using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// ローカル変数を評価スタックに積む命令。
    ///
    /// Operand: ローカル変数インデックス (0-based)
    /// Stack: [... ] -> [..., value]
    /// </summary>
    public sealed class LoadLocalCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var index = instruction.Operand;

            if (index < 0 || index >= frame.Locals.Length)
            {
                throw new VmException($"LoadLocal: invalid local index {index}.");
            }

            var value = frame.Locals[index];
            vm.Push(value);
        }
    }
}
