using System;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand
{
    public sealed class BlockCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
            => vm.EnterControl(frame, ControlFrameKind.Block, frame.Ip);
    }

    public sealed class LoopCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
            => vm.EnterControl(frame, ControlFrameKind.Loop, frame.Ip);
    }

    public sealed class IfCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
        {
            var cond = vm.Pop();
            bool value;
            try
            {
                value = cond.AsBool;
            }
            catch (VmException ex)
            {
                throw new VmException($"IF expects Bool condition, but got {cond.Kind}.", ex);
            }

            vm.EnterIf(frame, frame.Ip, value);
        }
    }

    public sealed class ElseCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
            => vm.EnterElse(frame, frame.Ip);
    }

    public sealed class EndCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
            => vm.LeaveControl(frame);
    }

    public sealed class BrCommand : IOpcodeCommand
    {
        public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
            => vm.Branch(frame, instruction.Operand0);
    }
}
