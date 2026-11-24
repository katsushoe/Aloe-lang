using Aloe.CommonLib;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // VM-only payload in the existing caught-context stack. Never thrown as a CLR control-flow exception.
    private sealed class TaskDeletionSignal : Exception { }

    private sealed class TaskDeletionState
    {
        internal ExceptionDispatchInfo? Failure;
        internal CallFrame? CleanupFrame;
        internal int CleanupDepth;
    }

    // Weak keys keep abandoned frames from extending the lifetime of caught exceptions.
    private readonly ConditionalWeakTable<CallFrame, Stack<bool>> _caughtCleanupKinds = new();

    private void PushCaughtContext(CallFrame frame, Exception exception, bool cleanup)
    {
        var kinds = _caughtCleanupKinds.GetOrCreateValue(frame);
        while (kinds.Count < frame.CaughtExceptions.Count) kinds.Push(false);
        frame.CaughtExceptions.Push(ExceptionDispatchInfo.Capture(exception));
        kinds.Push(cleanup);
    }

    private void PopCaughtContext(CallFrame frame)
    {
        frame.CaughtExceptions.Pop();
        if (_caughtCleanupKinds.TryGetValue(frame, out var kinds) && kinds.Count > 0) kinds.Pop();
    }

    private ExceptionDispatchInfo? PendingCleanupFailure()
    {
        foreach (var frame in _callStack.EnumerateFrames())
        {
            if (!_caughtCleanupKinds.TryGetValue(frame, out var kinds)) continue;
            var contexts = frame.CaughtExceptions.ToArray();
            var flags = kinds.ToArray();
            for (var index = 0; index < Math.Min(contexts.Length, flags.Length); index++)
                if (flags[index] && contexts[index].SourceException is not TaskDeletionSignal) return contexts[index];
        }
        return null;
    }

    private void ContinueTaskDeletion(ExceptionDispatchInfo? failure = null)
    {
        var state = CurrentThreadState;
        state.TaskDeletion ??= new TaskDeletionState { Failure = PendingCleanupFailure() };
        if (failure != null) state.TaskDeletion.Failure = failure;
        DispatchException(state.TaskDeletion.Failure?.SourceException ?? new TaskDeletionSignal());
    }
}
