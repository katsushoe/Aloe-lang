using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void InvokeTaskSyscall(EnumSyscall syscall)
    {
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
            TaskPool.Delete(value.AsTaskId);
            return;
        }
        var identity = PopLiveTask();
        switch (syscall)
        {
            case EnumSyscall.TaskStart: TaskPool.Start(identity); break;
            case EnumSyscall.TaskStop: TaskPool.Stop(identity); break;
            case EnumSyscall.TaskRequireLive: Push(AloeValue.FromTask(identity)); break;
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
}
