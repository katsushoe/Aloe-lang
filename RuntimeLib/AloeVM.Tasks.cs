using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using System.Runtime.ExceptionServices;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private readonly Dictionary<string, AloeTaskTypeInfo> _taskDefinitions = new(StringComparer.Ordinal);
    private readonly List<VmThreadState> _taskEntryParents = new();

    private void RegisterModuleTaskTypes()
    {
        foreach (var type in _module.TaskTypes)
        {
            _taskDefinitions.Add(type.Name, type);
            TaskPool.RegisterType(type.Name, type.Fields.ToArray());
            foreach (var method in type.Methods)
                TaskScheduler.Bind(type.Name, method.Name,
                    (identity, arguments) => InvokeTaskEntry(identity, method.FunctionIndex, arguments), method.Parameters.ToArray());
        }
    }

    private void InvokeTaskSyscall(EnumSyscall syscall)
    {
        if (syscall == EnumSyscall.TaskConstruct)
        {
            var count = PopTaskArgumentCount();
            var name = Pop();
            if (!name.IsString || !_taskDefinitions.TryGetValue(name.AsString, out var type))
                throw new VmException("Task construction requires a module-registered type name.");
            var arguments = PopTaskArguments(count, type.ConstructorParameters);
            var identity = TaskPool.Create(type.Name);
            try
            {
                if (type.ConstructorIndex is int constructor) InvokeTaskEntry(identity, constructor, arguments);
            }
            catch
            {
                TaskPool.Delete(identity);
                throw;
            }
            Push(AloeValue.FromTask(identity));
            return;
        }
        if (syscall == EnumSyscall.TaskInvoke)
        {
            var count = PopTaskArgumentCount();
            var name = Pop();
            if (!name.IsString || name.AsString is "run" or "start" or "stop")
                throw new VmException("Task invocation requires a non-reserved method name.");
            if (count >= _valueStack.Count) throw new VmException("Task invocation is missing its receiver or arguments.");
            // The receiver is below the explicit arguments; validate the declared signature before copying them.
            var receiver = _valueStack.ElementAt(count);
            if (!receiver.IsTask || !TaskPool.IsLive(receiver.AsTaskId))
                throw new InvalidReferenceException("Task invocation target is invalid.");
            if (!_taskDefinitions.TryGetValue(TaskPool.LocationOf(receiver.AsTaskId).TypeName, out var type))
                throw new VmException("Task invocation requires module-registered entry bindings.");
            var method = type.Methods.FirstOrDefault(candidate => candidate.Name == name.AsString)
                ?? throw new VmException("Unknown Task method: " + name.AsString);
            var arguments = PopTaskArguments(count, method.Parameters);
            var identity = PopLiveTask();
            if (CurrentThreadState.TaskIdentity == identity) InvokeTaskEntry(identity, method.FunctionIndex, arguments);
            else TaskScheduler.Enqueue(identity, method.Name, arguments);
            return;
        }
        // A low-level allocation is deliberately separate from language construction.
        if (syscall == EnumSyscall.TaskAllocate)
        {
            var type = Pop();
            if (!type.IsString) throw new VmException("Task allocation expects a registered type name string.");
            Push(AloeValue.FromTask(TaskPool.Create(type.AsString)));
            return;
        }
        if (syscall == EnumSyscall.TaskFieldSet)
        {
            var value = Pop();
            var field = PopTaskFieldIndex();
            TaskPool.SetField(PopLiveTask(), field, value);
            return;
        }
        if (syscall == EnumSyscall.TaskFieldGet)
        {
            var field = PopTaskFieldIndex();
            Push(TaskPool.GetField(PopLiveTask(), field));
            return;
        }
        if (syscall == EnumSyscall.TaskDelete)
        {
            var value = Pop();
            if (value.IsNull) return;
            if (!value.IsTask) throw new VmException("Task deletion expects a Task identity or invalid marker.");
            if (CurrentThreadState.TaskIdentity == value.AsTaskId)
                throw new VmException("Task self-deletion bytecode unwinding is not implemented yet.");
            TaskPool.Delete(value.AsTaskId);
            return;
        }
        var liveIdentity = PopLiveTask();
        switch (syscall)
        {
            case EnumSyscall.TaskStart: TaskPool.Start(liveIdentity); break;
            case EnumSyscall.TaskStop: TaskPool.Stop(liveIdentity); break;
            case EnumSyscall.TaskRequireLive: Push(AloeValue.FromTask(liveIdentity)); break;
            default: throw new VmException("Unknown Task syscall: " + syscall);
        }
    }

    private long PopLiveTask()
    {
        var value = Pop();
        if (value.IsNull) throw new InvalidReferenceException("Task reference is deleted or invalid.");
        if (!value.IsTask) throw new VmException("Task operation requires a Task identity.");
        if (!TaskPool.IsLive(value.AsTaskId)) throw new InvalidReferenceException("Task reference is deleted or invalid.");
        return value.AsTaskId;
    }

    private int PopTaskFieldIndex()
    {
        var index = Pop();
        if (!index.IsInt || index.AsInt < 0 || index.AsInt > int.MaxValue)
            throw new VmException("Task field index must be a non-negative int32.");
        return (int)index.AsInt;
    }

    private int PopTaskArgumentCount()
    {
        var count = Pop();
        if (!count.IsInt || count.AsInt < 0 || count.AsInt > int.MaxValue)
            throw new VmException("Task argument count must be a non-negative int32.");
        return (int)count.AsInt;
    }

    private AloeValue[] PopTaskArguments(int count, IReadOnlyList<EnumValueKind> parameters)
    {
        if (count != parameters.Count || count > _valueStack.Count)
            throw new VmException("Task argument count does not match its signature.");
        var arguments = new AloeValue[count];
        for (var index = count - 1; index >= 0; index--) arguments[index] = Pop();
        for (var index = 0; index < count; index++) TaskPool.ValidateValue(parameters[index], arguments[index]);
        return arguments;
    }

    private void InvokeTaskEntry(long identity, int functionIndex, IReadOnlyList<AloeValue> arguments)
    {
        var function = _module.Functions[functionIndex];
        var parent = CurrentThreadState;
        var previous = _threadState.Value;
        var state = new VmThreadState { TaskIdentity = identity, SynchronousCallDepth = parent.SynchronousCallDepth + 1 };
        var locals = new AloeValue[function.LocalCount];
        locals[0] = AloeValue.FromTask(identity);
        for (var index = 0; index < arguments.Count; index++) locals[index + 1] = arguments[index];
        _taskEntryParents.EnsureCapacity(_taskEntryParents.Count + 1);
        TaskPool.BeginExecution(identity);
        _taskEntryParents.Add(parent);
        _threadState.Value = state;
        ExceptionDispatchInfo? failure = null;
        try
        {
            _callStack.Push(new CallFrame(_module, function, function.EntryIp, locals: locals));
            while (!_haltRequested && !_callStack.IsEmpty) ExecuteOneInstruction();
            if (_valueStack.Count != 0) throw new VmException("Task entries must return void with a balanced operand stack.");
        }
        catch (Exception error)
        {
            // Restore the caller's VM state before its CLR exception filter dispatches Aloe catch handlers.
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            _threadState.Value = previous;
            _taskEntryParents.RemoveAt(_taskEntryParents.Count - 1);
            TaskPool.EndExecution(identity);
        }
        failure?.Throw();
    }

    private void AddTaskParentRoots(List<long> roots)
    {
        foreach (var parent in _taskEntryParents)
        {
            foreach (var value in parent.ValueStack)
                if (value.IsObject) roots.Add(value.AsObjectId);
            foreach (var frame in parent.CallStack.EnumerateFrames())
            {
                foreach (var value in frame.Locals)
                    if (value.IsObject) roots.Add(value.AsObjectId);
                foreach (var handler in frame.ExceptionHandlers)
                    foreach (var value in handler.ValueStackTopFirst)
                        if (value.IsObject) roots.Add(value.AsObjectId);
            }
        }
    }
}
