using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using Aloe.RuntimeLib.OpCommand;

public sealed class CallCommand : IOpcodeCommand
{
    public void Execute(AloeVm vm, CallFrame frame, in Instruction instruction)
    {
        var module = vm.Module;
        var functions = module.Functions;

        int functionIndex = instruction.Operand0;

        if (functionIndex < 0 || functionIndex >= functions.Count)
        {
            throw new VmException(
                $"CALL: function index out of range. index={functionIndex}, count={functions.Count}");
        }

        var callee = functions[functionIndex];

        int parameterCount = callee.ParameterCount;
        int localCount = callee.LocalCount;

        if (localCount < parameterCount)
        {
            localCount = parameterCount;
        }

        AloeValue[] locals = localCount > 0
            ? new AloeValue[localCount]
            : Array.Empty<AloeValue>();

        for (int i = parameterCount - 1; i >= 0; i--)
        {
            locals[i] = vm.Pop();
        }

        int entryIp = callee.EntryIp;

        // 🔴 ここが重要：呼び出し元フレームの IP を「次の命令」に進めておく
        frame.Ip = frame.Ip + 1;

        var newFrame = new CallFrame(
            module: module,
            function: callee,
            ip: entryIp,
            locals: locals
        );

        vm.CallStack.Push(newFrame);
    }
}
