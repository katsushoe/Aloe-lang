using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace Aloe.RuntimeLib.OpCommand
{
    public sealed class SyscallCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // オペランド0をシステムコールIDとして解釈
            vm.InvokeSyscall(instruction.Operand0);

            // ここでは Ip を変更しない。
            // Run() 側で「同じフレーム & Ip 変更なし」を検知して自動で +1 してくれる。
        }
    }
}
