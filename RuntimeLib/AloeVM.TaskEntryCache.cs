namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private const int TaskEntryStateCacheCapacity = 16;
    private readonly object _taskEntryStateGate = new();
    private readonly Stack<VmThreadState> _taskEntryStates = new(TaskEntryStateCacheCapacity);

    private VmThreadState RentTaskEntryState()
    {
        lock (_taskEntryStateGate)
            return _taskEntryStates.Count == 0 ? new VmThreadState() : _taskEntryStates.Pop();
    }

    private void ReturnTaskEntryState(VmThreadState state)
    {
        // Detach all references and execution flags before another entry can rent this state.
        // Frames/locals remain independently allocated; retained public frame snapshots are not reused.
        state.ValueStack.Clear();
        state.CallStack.Clear();
        state.RunningContext = null;
        state.ContextSuspendRequested = false;
        state.SuspendCurrentInstruction = false;
        state.SynchronousCallDepth = 0;
        state.CommittedQueryDepth = 0;
        state.TaskIdentity = null;
        state.TaskDeletion = null;
        lock (_taskEntryStateGate)
            if (_taskEntryStates.Count < TaskEntryStateCacheCapacity) _taskEntryStates.Push(state);
    }
}
