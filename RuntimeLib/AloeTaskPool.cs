using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

internal enum AloeTaskState { Stopped, Running, Deleted }

/// <summary>Coordinator-owned Task storage. No language Task bytecode is emitted yet.</summary>
internal sealed class AloeTaskPool
{
    private struct Slot
    {
        internal long Identity;
        internal AloeTaskState State;
        internal int ActiveEntries;
    }

    private sealed class Chunk
    {
        internal readonly Slot[] Slots;
        internal readonly AloeValue[] Fields;
        internal Chunk(int capacity, int fieldCount)
        {
            Slots = new Slot[capacity];
            Fields = new AloeValue[checked(capacity * fieldCount)];
            Array.Fill(Fields, AloeValue.Null);
        }
    }

    private sealed class TypePool
    {
        internal readonly string Name;
        internal readonly int Capacity;
        internal readonly EnumValueKind[] FieldKinds;
        internal readonly List<Chunk> Chunks = new();
        internal readonly Stack<int> Free = new();
        internal int NextSlot;
        internal TypePool(string name, int capacity, EnumValueKind[] fieldKinds) { Name = name; Capacity = capacity; FieldKinds = fieldKinds; }
    }

    private readonly record struct Address(TypePool Pool, int Index);
    private readonly Dictionary<string, TypePool> _types = new(StringComparer.Ordinal);
    private readonly List<TypePool> _orderedTypes = new();
    private readonly Dictionary<long, Address> _locations = new();
    private readonly Func<long, bool> _isLiveObject;
    private long _nextIdentity;
    private bool _identityExhausted;

    internal AloeTaskPool(Func<long, bool> isLiveObject, int chunkCapacity = 128, long initialIdentity = 1)
    {
        ArgumentNullException.ThrowIfNull(isLiveObject);
        if (chunkCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        if (initialIdentity <= 0) throw new ArgumentOutOfRangeException(nameof(initialIdentity));
        _isLiveObject = isLiveObject;
        DefaultChunkCapacity = chunkCapacity;
        _nextIdentity = initialIdentity;
    }

    internal int DefaultChunkCapacity { get; }
    internal int LiveCount { get; private set; }
    internal event Action<long>? Deleted;
    internal bool HasType(string name) => _types.ContainsKey(name);
    internal static bool IsSupportedKind(EnumValueKind kind) => kind is EnumValueKind.Int or EnumValueKind.Float or
        EnumValueKind.Decimal or EnumValueKind.Bool or EnumValueKind.String or EnumValueKind.Byte or EnumValueKind.Char or EnumValueKind.Object;

    internal void RegisterType(string name, params EnumValueKind[] fieldKinds)
        => RegisterType(name, DefaultChunkCapacity, fieldKinds);

    internal void RegisterType(string name, int chunkCapacity, params EnumValueKind[] fieldKinds)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(fieldKinds);
        if (chunkCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        if (_types.ContainsKey(name)) throw new ArgumentException("Task type is already registered.", nameof(name));
        if (fieldKinds.Any(kind => kind is not (EnumValueKind.Int or EnumValueKind.Float or EnumValueKind.Decimal or
            EnumValueKind.Bool or EnumValueKind.String or EnumValueKind.Byte or EnumValueKind.Char or EnumValueKind.Object)))
            throw new ArgumentException("Task fields require scalar or ordinary Object kinds.", nameof(fieldKinds));
        _ = checked(chunkCapacity * fieldKinds.Length);
        var pool = new TypePool(name, chunkCapacity, (EnumValueKind[])fieldKinds.Clone());
        _types.Add(name, pool);
        _orderedTypes.Add(pool);
    }

    internal long Create(string typeName)
    {
        if (_identityExhausted) throw new VmException("Task identity space is exhausted.");
        if (!_types.TryGetValue(typeName, out var pool)) throw new VmException("Unknown Task type: " + typeName);
        var reused = pool.Free.Count > 0;
        var index = reused ? pool.Free.Peek() : pool.NextSlot;
        if (!reused && index == int.MaxValue) throw new VmException("Task pool slot space is exhausted.");
        if (index / pool.Capacity == pool.Chunks.Count)
        {
            var capacity = checked((pool.Chunks.Count + 1) * pool.Capacity);
            pool.Free.EnsureCapacity(capacity);
            var chunk = new Chunk(pool.Capacity, pool.FieldKinds.Length);
            pool.Chunks.Add(chunk);
        }
        var identity = _nextIdentity;
        var address = new Address(pool, index);
        // Do not consume a reusable slot or identity until the lookup allocation succeeds.
        _locations.Add(identity, address);
        if (reused) pool.Free.Pop(); else pool.NextSlot++;
        ref var slot = ref SlotAt(address);
        slot.Identity = identity;
        slot.State = AloeTaskState.Stopped;
        slot.ActiveEntries = 0;
        LiveCount++;
        if (identity == long.MaxValue) _identityExhausted = true; else _nextIdentity++;
        return identity;
    }

    internal bool IsLive(long identity)
        => _locations.TryGetValue(identity, out var address) && SlotAt(address).State != AloeTaskState.Deleted;

    internal AloeTaskState StateOf(long identity)
        => _locations.TryGetValue(identity, out var address) ? SlotAt(address).State : AloeTaskState.Deleted;

    internal void Start(long identity) => SlotAt(RequireLive(identity)).State = AloeTaskState.Running;
    internal void Stop(long identity) => SlotAt(RequireLive(identity)).State = AloeTaskState.Stopped;

    internal void Delete(long identity)
    {
        if (!_locations.TryGetValue(identity, out var address)) return;
        ref var slot = ref SlotAt(address);
        if (slot.State == AloeTaskState.Deleted) return;
        slot.State = AloeTaskState.Deleted;
        LiveCount--;
        Deleted?.Invoke(identity);
        if (slot.ActiveEntries == 0) Release(identity, address);
    }

    internal void BeginExecution(long identity)
    {
        ref var slot = ref SlotAt(RequireLive(identity));
        slot.ActiveEntries = checked(slot.ActiveEntries + 1);
    }

    internal void EndExecution(long identity)
    {
        if (!_locations.TryGetValue(identity, out var address)) throw new InvalidOperationException("Task entry is not active.");
        ref var slot = ref SlotAt(address);
        if (slot.ActiveEntries == 0) throw new InvalidOperationException("Task entry is not active.");
        slot.ActiveEntries--;
        if (slot.ActiveEntries == 0 && slot.State == AloeTaskState.Deleted) Release(identity, address);
    }

    internal AloeValue GetField(long identity, int field)
    {
        var address = RequireLive(identity);
        CheckField(address, field);
        var value = FieldAt(address, field);
        if (value.IsNull && address.Pool.FieldKinds[field] != EnumValueKind.Object)
            throw new VmException("Task scalar field is uninitialized.");
        return value;
    }

    internal void SetField(long identity, int field, AloeValue value)
    {
        var address = RequireLive(identity);
        CheckField(address, field);
        var kind = address.Pool.FieldKinds[field];
        ValidateValue(kind, value);
        FieldAt(address, field) = value;
    }

    internal void ValidateValue(EnumValueKind kind, AloeValue value)
    {
        if (kind == EnumValueKind.Object)
        {
            if (!value.IsNull && (!value.IsObject || !_isLiveObject(value.AsObjectId)))
                throw new InvalidReferenceException("Task field requires a live ordinary Object reference or invalid marker.");
        }
        else if (value.Kind != kind || value.IsInt && (value.AsInt < int.MinValue || value.AsInt > int.MaxValue) ||
                 value.IsFloat && !double.IsFinite(value.AsFloat))
            throw new VmException("Task field value must preserve its declared scalar kind and domain.");
    }

    internal IReadOnlyList<long> CaptureTasks()
    {
        var identities = new List<long>();
        foreach (var pool in _orderedTypes)
            for (var index = 0; index < pool.NextSlot; index++)
            {
                ref var slot = ref SlotAt(new Address(pool, index));
                if (slot.Identity != 0 && slot.State != AloeTaskState.Deleted) identities.Add(slot.Identity);
            }
        return identities.ToArray();
    }

    internal IReadOnlyList<long> CaptureRunningTasks()
    {
        var identities = new List<long>();
        foreach (var pool in _orderedTypes)
            for (var index = 0; index < pool.NextSlot; index++)
            {
                ref var slot = ref SlotAt(new Address(pool, index));
                if (slot.Identity != 0 && slot.State == AloeTaskState.Running) identities.Add(slot.Identity);
            }
        return identities.ToArray();
    }

    internal IReadOnlyList<long> GetObjectRoots()
    {
        var roots = new List<long>();
        // Deleted-but-active entries retain their fields until finally/frame cleanup completes.
        foreach (var address in _locations.Values)
            for (var field = 0; field < address.Pool.FieldKinds.Length; field++)
            {
                var value = FieldAt(address, field);
                if (value.IsObject) roots.Add(value.AsObjectId);
            }
        return roots.ToArray();
    }

    internal (string TypeName, int SlotIndex) LocationOf(long identity)
    {
        var address = RequireLive(identity);
        return (address.Pool.Name, address.Index);
    }

    internal void ResetInstances()
    {
        if (_locations.Values.Any(address => SlotAt(address).ActiveEntries != 0))
            throw new InvalidOperationException("Active Task entries cannot be reset.");
        foreach (var pool in _orderedTypes)
        {
            foreach (var chunk in pool.Chunks)
            {
                Array.Clear(chunk.Slots);
                Array.Fill(chunk.Fields, AloeValue.Null);
            }
            pool.Free.Clear();
            pool.NextSlot = 0;
        }
        _locations.Clear();
        LiveCount = 0;
        // Type layouts and capacity are reusable; identities never restart during this VM instance.
    }

    private Address RequireLive(long identity)
    {
        if (!_locations.TryGetValue(identity, out var address) || SlotAt(address).State == AloeTaskState.Deleted)
            throw new InvalidReferenceException("Task reference is deleted or invalid.");
        return address;
    }

    private ref Slot SlotAt(Address address)
        => ref address.Pool.Chunks[address.Index / address.Pool.Capacity].Slots[address.Index % address.Pool.Capacity];

    private ref AloeValue FieldAt(Address address, int field)
        => ref address.Pool.Chunks[address.Index / address.Pool.Capacity].Fields[
            (address.Index % address.Pool.Capacity) * address.Pool.FieldKinds.Length + field];

    private static void CheckField(Address address, int field)
    {
        if ((uint)field >= (uint)address.Pool.FieldKinds.Length) throw new VmException("Task field index is out of range.");
    }

    private void Release(long identity, Address address)
    {
        var chunk = address.Pool.Chunks[address.Index / address.Pool.Capacity];
        Array.Fill(chunk.Fields, AloeValue.Null, (address.Index % address.Pool.Capacity) * address.Pool.FieldKinds.Length, address.Pool.FieldKinds.Length);
        ref var slot = ref SlotAt(address);
        slot.Identity = 0;
        slot.State = AloeTaskState.Deleted;
        slot.ActiveEntries = 0;
        _locations.Remove(identity);
        address.Pool.Free.Push(address.Index);
    }
}
