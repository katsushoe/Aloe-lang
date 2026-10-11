using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib.OpCommand;

internal sealed class StructCommand : IOpcodeCommand
{
    public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
    {
        switch (instruction.Opcode)
        {
            case EnumOpcode.StructCreate:
                var count = instruction.Operand0;
                if (count < 0 || count >= vm.ValueStack.Count)
                    throw new VmException("StructCreate requires a type name and the declared number of fields.");
                var fields = new AloeValue[count];
                for (var i = count - 1; i >= 0; i--) fields[i] = vm.Pop();
                var name = vm.Pop();
                if (!name.IsString || string.IsNullOrEmpty(name.AsString))
                    throw new VmException("StructCreate requires a nonempty type name string.");
                vm.Push(AloeValue.FromStruct(new AloeStructValue(name.AsString, fields)));
                break;
            case EnumOpcode.StructGet:
                if (vm.ValueStack.Count < 1) throw new VmException("StructGet requires a struct value.");
                vm.Push(vm.Pop().AsStruct.GetField(instruction.Operand0));
                break;
            case EnumOpcode.StructSet:
                if (vm.ValueStack.Count < 2) throw new VmException("StructSet requires a struct and replacement value.");
                var value = vm.Pop();
                vm.Push(AloeValue.FromStruct(vm.Pop().AsStruct.WithField(instruction.Operand0, value)));
                break;
            default:
                throw new VmException($"Invalid struct opcode {instruction.Opcode}.");
        }
    }
}
