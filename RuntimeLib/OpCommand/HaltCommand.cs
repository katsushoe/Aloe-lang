// Aloe.Runtime/Execution/NopCommand.cs
using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class HaltCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // VM に停止要求フラグを立てるだけ。
            // メインループ側は HaltRequested を見てループを抜ける。
            vm.RequestHalt();
        }
    }
}
