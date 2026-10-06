// Aloe.Runtime/Execution/NopCommand.cs
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class SubCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            if (vm == null) throw new ArgumentNullException(nameof(vm));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            // 旧: vm.OperandStack.Pop() などを使っていた想定
            var right = vm.Pop();
            var left = vm.Pop();

            // AloeValue に演算子 - が定義されている前提
            AloeValue result;
            try
            {
                result = left - right;
            }
            catch (Exception ex)
            {
                // 型不一致などで落ちたときに VM 例外にラップ
                throw new VmException(
                    $"SubCommand failed: cannot subtract {right.Kind} from {left.Kind}.", ex);
            }

            vm.Push(result);
        }
    }
}
