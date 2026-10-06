using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    /// <summary>
    /// 定数プールから 1 つ値を取り出して評価スタックに積む。
    ///
    /// Operand:
    ///   - 定数プールのインデックス (int)
    /// </summary>
    public sealed class PushConstCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            var module = frame.Module
                         ?? throw new VmException("PushConst: CallFrame.Module is null.");

            var constIndex = instruction.Operand;

            var constants = module.Constants;
            if (constIndex < 0 || constIndex >= constants.Count)
            {
                throw new VmException(
                    $"PushConst: constant index out of range. index={constIndex}, count={constants.Count}");
            }

            var value = constants[constIndex];

            // 評価スタックに積む
            vm.ValueStack.Push(value);
        }
    }
}
