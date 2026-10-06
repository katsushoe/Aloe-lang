// Aloe.Runtime/Execution/NopCommand.cs
using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System;

namespace Aloe.RuntimeLib.OpCommand
{
    internal sealed class NopCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            // 何もしない
        }
    }
}
