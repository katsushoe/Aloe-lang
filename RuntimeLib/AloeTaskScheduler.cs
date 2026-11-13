using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

/// <summary>Public diagnostic snapshot; no exception object or stack trace is exposed.</summary>
public sealed record AloeTaskFailure(long TaskId, string TaskType, string MethodName, string ExceptionType, string Message);

/// <summary>Single-coordinator Task phase. Internal bindings await source bytecode lowering.</summary>
internal sealed class AloeTaskScheduler
{
    private sealed record Binding(EnumValueKind[] Parameters, Action<long, IReadOnlyList<AloeValue>> Execute);
    private sealed record Notification(string Method, AloeValue[] Arguments);
    private readonly AloeTaskPool _pool;
    private readonly Dictionary<(string Type, string Method), Binding> _bindings = new();
    private readonly Dictionary<long, Queue<Notification>> _pending = new();
    private Dictionary<long, Queue<Notification>>? _snapshot;
    private IReadOnlyList<AloeValue>? _activeArguments;
    private bool _inTick;
    private bool _faulted;

    internal AloeTaskScheduler(AloeTaskPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
        pool.Deleted += RemoveNotifications;
    }

    internal AloeTaskFailure? LastFailure { get; private set; }

    internal void Bind(string type, string method, Action<long, IReadOnlyList<AloeValue>> execute,
        params EnumValueKind[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!_pool.HasType(type)) throw new VmException("Unknown Task type: " + type);
        if (method is "start" or "stop" || method == "run" && parameters.Length != 0)
            throw new VmException("Invalid reserved Task method binding.");
        if (parameters.Any(kind => !AloeTaskPool.IsSupportedKind(kind)))
            throw new VmException("Task parameters require scalar or ordinary Object kinds.");
        if (!_bindings.TryAdd((type, method), new Binding((EnumValueKind[])parameters.Clone(), execute)))
            throw new VmException("Task method is already bound.");
    }

    internal void Enqueue(long identity, string method, params AloeValue[] arguments)
    {
        if (_faulted) throw new VmException("Task scheduler is faulted; reset is required.");
        var type = _pool.LocationOf(identity).TypeName;
        if (method is "run" or "start" or "stop") throw new VmException("Reserved Task methods cannot be queued.");
        ArgumentNullException.ThrowIfNull(arguments);
        var binding = RequireBinding(type, method);
        if (binding.Parameters.Length != arguments.Length) throw new VmException("Task argument count mismatch.");
        for (var index = 0; index < arguments.Length; index++)
            _pool.ValidateValue(binding.Parameters[index], arguments[index]);
        var notification = new Notification(method, (AloeValue[])arguments.Clone());
        if (!_pending.TryGetValue(identity, out var queue))
        {
            queue = new Queue<Notification>();
            _pending.Add(identity, queue);
        }
        queue.Enqueue(notification);
    }

    internal void Tick()
    {
        if (_inTick) throw new VmException("Nested Task Tick is not supported.");
        if (_faulted) throw new VmException("Task scheduler is faulted; reset is required.");
        var tasks = _pool.CaptureTasks();
        var running = _pool.CaptureRunningTasks().ToHashSet();
        // Detach the entire notification set before invoking any entry.
        var snapshot = new Dictionary<long, Queue<Notification>>(_pending);
        _pending.Clear();
        _snapshot = snapshot;
        _inTick = true;
        try
        {
            foreach (var identity in tasks)
            {
                if (snapshot.TryGetValue(identity, out var queue))
                    while (_pool.IsLive(identity) && queue.TryDequeue(out var notification))
                        Invoke(identity, notification.Method, notification.Arguments);
                if (_pool.IsLive(identity) && running.Contains(identity))
                    Invoke(identity, "run", Array.Empty<AloeValue>());
            }
        }
        catch
        {
            _faulted = true;
            _pending.Clear();
            snapshot.Clear();
            throw;
        }
        finally
        {
            _snapshot = null;
            _inTick = false;
        }
    }

    internal IReadOnlyList<long> GetObjectRoots()
    {
        var roots = new List<long>();
        AddQueueRoots(_pending, roots);
        if (_snapshot != null) AddQueueRoots(_snapshot, roots);
        if (_activeArguments != null)
            foreach (var value in _activeArguments)
                if (value.IsObject) roots.Add(value.AsObjectId);
        return roots;
    }

    internal void Reset()
    {
        if (_inTick) throw new InvalidOperationException("An active Task scheduler cannot be reset.");
        _pool.ResetInstances();
        _pending.Clear();
        LastFailure = null;
        _faulted = false;
    }

    internal void DiscardPending() => _pending.Clear();

    private void Invoke(long identity, string method, IReadOnlyList<AloeValue> arguments)
    {
        var type = _pool.LocationOf(identity).TypeName;
        _pool.BeginExecution(identity);
        _activeArguments = arguments;
        try
        {
            RequireBinding(type, method).Execute(identity, arguments);
        }
        catch (Exception exception)
        {
            LastFailure = new AloeTaskFailure(identity, type, method, exception.GetType().Name, exception.Message);
            throw;
        }
        finally
        {
            _activeArguments = null;
            _pool.EndExecution(identity);
        }
    }

    private Binding RequireBinding(string type, string method)
        => _bindings.TryGetValue((type, method), out var binding) ? binding
            : throw new VmException("Unknown Task method: " + type + "." + method);

    private void RemoveNotifications(long identity)
    {
        _pending.Remove(identity);
        _snapshot?.Remove(identity);
    }

    private void AddQueueRoots(Dictionary<long, Queue<Notification>> queues, List<long> roots)
    {
        foreach (var pair in queues)
            if (_pool.IsLive(pair.Key))
                foreach (var notification in pair.Value)
                    foreach (var argument in notification.Arguments)
                        if (argument.IsObject) roots.Add(argument.AsObjectId);
    }
}
