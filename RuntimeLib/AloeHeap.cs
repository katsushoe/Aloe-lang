using System;
using System.Collections.Generic;
using Aloe.CommonLib;
using System.Linq;

namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Physical heap used by the C# reference VM.
    /// Heap Slot is fixed at 8 bytes. ObjectId stays stable when an object moves.
    /// </summary>
    public sealed class AloeHeap
    {
        public const int HeapSlotSize = 8;

        private readonly Dictionary<long, AloeObjectTableEntry> _objects = new();
        private readonly List<AloeHeapBank> _banks = new();
        private long _nextObjectId = 1;
        private int _nextBankId = 1;
        private long _allocationVersion;
        private long _referenceGraphVersion;
        private long _lifetimeVersion;

        public AloeHeap(int bankSlotCapacity = 128)
        {
            if (bankSlotCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(bankSlotCapacity));

            BankSlotCapacity = bankSlotCapacity;
        }

        public int BankSlotCapacity { get; }
        public IReadOnlyDictionary<long, AloeObjectTableEntry> ObjectTable => _objects;
        public IReadOnlyList<AloeHeapBank> Banks => _banks;

        /// <summary>Total bytes occupied by live Aloe Object slots.</summary>
        public long UsedBytes => _objects.Values.Sum(x => (long)x.AllocatedBytes);

        /// <summary>Total bytes reserved by currently allocated Aloe heap Banks.</summary>
        public long ReservedBytes => _banks.Sum(x => (long)x.SlotCapacity * HeapSlotSize);
        internal long AllocationVersion => _allocationVersion;
        internal long ReferenceGraphVersion => _referenceGraphVersion;
        internal long LifetimeVersion => _lifetimeVersion;

        public long Allocate(ReadOnlySpan<byte> data, long referenceGroupId = 0, int fieldCount = 0, string? typeName = null)
        {
            if (fieldCount < 0) throw new ArgumentOutOfRangeException(nameof(fieldCount));
            var physicalBytes = Math.Max(data.Length, fieldCount * HeapSlotSize);
            var slotCount = GetRequiredSlotCount(physicalBytes);
            var bank = FindBankWithCapacity(slotCount) ?? CreateBank();
            var startSlot = bank.Allocate(slotCount);
            bank.Write(startSlot, slotCount, data);

            var objectId = _nextObjectId++;
            var entry = new AloeObjectTableEntry(
                objectId,
                bank.BankId,
                startSlot,
                slotCount,
                data.Length,
                referenceGroupId,
                fieldCount,
                typeName);
            _objects.Add(objectId, entry);
            _allocationVersion++;
            _referenceGraphVersion++;
            _lifetimeVersion++;
            return objectId;
        }

        public byte[] ReadObject(long objectId)
        {
            var entry = GetEntry(objectId);
            return GetBank(entry.BankId).Read(entry.StartSlot, entry.DataSizeBytes);
        }

        public AloeObjectTableEntry GetEntry(long objectId)
        {
            if (!_objects.TryGetValue(objectId, out var entry))
                throw new KeyNotFoundException($"Unknown ObjectId: {objectId}.");
            return entry;
        }

        public AloeValue GetField(long objectId, int fieldIndex)
        {
            var entry = GetEntry(objectId);
            return entry.GetCurrentField(fieldIndex);
        }

        public AloeValue GetCommittedField(long objectId, int fieldIndex)
        {
            var entry = GetEntry(objectId);
            return entry.GetCommittedField(fieldIndex);
        }

        /// <summary>Write directly to Committed State. Used by construction and non-Tick synchronous execution.</summary>
        public void SetField(long objectId, int fieldIndex, AloeValue value)
        {
            var entry = GetEntry(objectId);
            var previous = entry.GetCommittedField(fieldIndex, allowUninitialized: true);

            if (previous.IsObject)
                RemoveReference(objectId, previous.AsObjectId);

            entry.SetCommittedField(fieldIndex, value);

            if (value.IsObject)
                AddReference(objectId, value.AsObjectId);

            _lifetimeVersion++;
        }

        /// <summary>Write Tick-local Volatile State while retaining the old Committed reference until commit.</summary>
        public void SetVolatileField(long objectId, int fieldIndex, AloeValue value)
        {
            var entry = GetEntry(objectId);

            if (entry.TryGetVolatileField(fieldIndex, out var previousVolatile) && previousVolatile.IsObject)
                RemoveReference(objectId, previousVolatile.AsObjectId);

            entry.SetVolatileField(fieldIndex, value);
            if (value.IsObject)
                AddReference(objectId, value.AsObjectId);

            _lifetimeVersion++;
        }

        /// <summary>Commit all dirty Volatile fields. The Volatile reference edge becomes the Committed edge.</summary>
        public int CommitVolatileFields()
        {
            var committed = 0;
            foreach (var entry in _objects.Values)
            {
                foreach (var fieldIndex in entry.GetDirtyFieldIndices())
                {
                    var previousCommitted = entry.GetCommittedField(fieldIndex, allowUninitialized: true);
                    if (previousCommitted.IsObject)
                        RemoveReference(entry.ObjectId, previousCommitted.AsObjectId);

                    entry.CommitVolatileField(fieldIndex);
                    committed++;
                }
            }

            if (committed > 0)
                _lifetimeVersion++;
            return committed;
        }

        public void SetDestructionCandidate(long objectId, bool value = true)
        {
            var entry = GetEntry(objectId);
            if (entry.IsDestructionCandidate == value) return;
            entry.IsDestructionCandidate = value;
            _lifetimeVersion++;
        }

        public void SetExternalReferenceCount(long objectId, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var entry = GetEntry(objectId);
            if (entry.ExternalReferenceCount == count) return;
            entry.ExternalReferenceCount = count;
            _lifetimeVersion++;
        }

        public void SetCallBufferStrongReferenceCount(long objectId, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var entry = GetEntry(objectId);
            if (entry.CallBufferStrongReferenceCount == count) return;
            entry.CallBufferStrongReferenceCount = count;
            _lifetimeVersion++;
        }

        /// <summary>Add one strong Object Reference edge from source to target.</summary>
        public void AddReference(long sourceObjectId, long targetObjectId)
        {
            var source = GetEntry(sourceObjectId);
            _ = GetEntry(targetObjectId);
            source.AddOutgoingReference(targetObjectId);
            source.ReferenceDirty = true;
            GetEntry(targetObjectId).ReferenceDirty = true;
            _referenceGraphVersion++;
            _lifetimeVersion++;
        }

        /// <summary>Remove one strong Object Reference edge from source to target.</summary>
        public bool RemoveReference(long sourceObjectId, long targetObjectId)
        {
            var source = GetEntry(sourceObjectId);
            if (!source.RemoveOutgoingReference(targetObjectId))
                return false;
            source.ReferenceDirty = true;
            if (_objects.TryGetValue(targetObjectId, out var target))
                target.ReferenceDirty = true;
            _referenceGraphVersion++;
            _lifetimeVersion++;
            return true;
        }

        /// <summary>Replace all strong outgoing references for an Object.</summary>
        public void SetReferences(long sourceObjectId, IEnumerable<long> targetObjectIds)
        {
            if (targetObjectIds == null) throw new ArgumentNullException(nameof(targetObjectIds));
            var source = GetEntry(sourceObjectId);
            var counts = new Dictionary<long, int>();
            foreach (var targetObjectId in targetObjectIds)
            {
                _ = GetEntry(targetObjectId);
                counts.TryGetValue(targetObjectId, out var count);
                counts[targetObjectId] = count + 1;
            }

            if (source.OutgoingReferences.Count == counts.Count &&
                source.OutgoingReferences.All(x => counts.TryGetValue(x.Key, out var c) && c == x.Value))
                return;

            var affectedTargets = source.OutgoingReferences.Keys.Concat(counts.Keys).Distinct().ToArray();
            source.ReplaceOutgoingReferences(counts);
            source.ReferenceDirty = true;
            foreach (var targetId in affectedTargets)
            {
                if (_objects.TryGetValue(targetId, out var target))
                    target.ReferenceDirty = true;
            }
            _referenceGraphVersion++;
            _lifetimeVersion++;
        }

        /// <summary>
        /// Replaces the runtime-temporary root counts used for the next GC step.
        /// Duplicate ObjectIds are counted as multiple temporary references.
        /// </summary>
        public void SetRuntimeTemporaryRoots(IEnumerable<long> objectIds)
        {
            if (objectIds == null) throw new ArgumentNullException(nameof(objectIds));
            var next = new Dictionary<long, int>();
            foreach (var objectId in objectIds)
            {
                if (_objects.ContainsKey(objectId))
                {
                    next.TryGetValue(objectId, out var count);
                    next[objectId] = count + 1;
                }
            }

            var changed = false;
            foreach (var entry in _objects.Values)
            {
                next.TryGetValue(entry.ObjectId, out var count);
                if (entry.RuntimeTemporaryReferenceCount != count)
                {
                    entry.RuntimeTemporaryReferenceCount = count;
                    changed = true;
                }
            }

            if (changed) _lifetimeVersion++;
        }

        internal static int GetRequiredSlotCount(int dataSizeBytes)
        {
            if (dataSizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(dataSizeBytes));
            return Math.Max(1, (dataSizeBytes + HeapSlotSize - 1) / HeapSlotSize);
        }

        internal AloeHeapBank GetBank(int bankId)
            => _banks.FirstOrDefault(x => x.BankId == bankId)
               ?? throw new KeyNotFoundException($"Unknown BankId: {bankId}.");

        internal AloeHeapBank CreateBank()
        {
            var bank = new AloeHeapBank(_nextBankId++, BankSlotCapacity);
            _banks.Add(bank);
            return bank;
        }

        internal void Sweep(long objectId)
        {
            var entry = GetEntry(objectId);
            GetBank(entry.BankId).Release(entry.StartSlot, entry.SlotCount);
            _objects.Remove(objectId);
            _referenceGraphVersion++;
            _lifetimeVersion++;
        }

        internal void Move(long objectId, int targetBankId, int targetStartSlot)
        {
            var entry = GetEntry(objectId);
            var source = GetBank(entry.BankId);
            var target = GetBank(targetBankId);

            if (!target.CanReserve(targetStartSlot, entry.SlotCount))
                throw new InvalidOperationException(
                    $"Target Bank {targetBankId} slot {targetStartSlot} is no longer available.");

            var bytes = source.ReadRaw(entry.StartSlot, entry.SlotCount);
            target.Reserve(targetStartSlot, entry.SlotCount);
            target.WriteRaw(targetStartSlot, entry.SlotCount, bytes);
            source.Release(entry.StartSlot, entry.SlotCount);

            entry.BankId = targetBankId;
            entry.StartSlot = targetStartSlot;
        }

        internal bool FreeBankIfEmpty(int bankId)
        {
            var bank = GetBank(bankId);
            if (bank.UsedSlotCount != 0)
                return false;

            _banks.Remove(bank);
            return true;
        }

        private AloeHeapBank? FindBankWithCapacity(int slotCount)
            => _banks.FirstOrDefault(x => x.TryFindFreeRange(slotCount, out _));
    }

    public sealed class AloeObjectTableEntry
    {
        internal AloeObjectTableEntry(
            long objectId,
            int bankId,
            int startSlot,
            int slotCount,
            int dataSizeBytes,
            long referenceGroupId,
            int fieldCount,
            string? typeName)
        {
            ObjectId = objectId;
            BankId = bankId;
            StartSlot = startSlot;
            SlotCount = slotCount;
            DataSizeBytes = dataSizeBytes;
            ReferenceGroupId = referenceGroupId;
            TypeName = typeName;
            _committedFields = Enumerable.Repeat(AloeValue.Null, fieldCount).ToArray();
            _volatileFields = Enumerable.Repeat(AloeValue.Null, fieldCount).ToArray();
            _volatileDirty = new bool[fieldCount];
        }

        private readonly Dictionary<long, int> _outgoingReferences = new();
        private readonly AloeValue[] _committedFields;
        private readonly AloeValue[] _volatileFields;
        private readonly bool[] _volatileDirty;

        public long ObjectId { get; }
        public int BankId { get; internal set; }
        public int StartSlot { get; internal set; }
        public int SlotCount { get; }
        public int DataSizeBytes { get; }
        public int AllocatedBytes => SlotCount * AloeHeap.HeapSlotSize;
        public int FieldCount => _committedFields.Length;
        public string? TypeName { get; }
        public long ReferenceGroupId { get; internal set; }
        public bool ReferenceDirty { get; internal set; } = true;
        public IReadOnlyDictionary<long, int> OutgoingReferences => _outgoingReferences;
        public int ReferenceCount { get; internal set; }
        /// <summary>Strong references entering this Object from outside the Aloe heap (host/native).</summary>
        public int ExternalReferenceCount { get; internal set; }
        public int GroupExternalReferenceCount { get; internal set; }
        public int RuntimeTemporaryReferenceCount { get; internal set; }
        public int CallBufferStrongReferenceCount { get; internal set; }
        public int GroupRuntimeTemporaryReferenceCount { get; internal set; }
        public int GroupCallBufferStrongReferenceCount { get; internal set; }
        public bool IsDestructionCandidate { get; set; }

        public bool IsReclaimable =>
            IsDestructionCandidate &&
            GroupExternalReferenceCount == 0 &&
            GroupRuntimeTemporaryReferenceCount == 0 &&
            GroupCallBufferStrongReferenceCount == 0;

        internal AloeValue GetCurrentField(int fieldIndex, bool allowUninitialized = false)
        {
            ValidateFieldIndex(fieldIndex);
            var value = _volatileDirty[fieldIndex]
                ? _volatileFields[fieldIndex]
                : _committedFields[fieldIndex];
            if (!allowUninitialized && value.IsNull)
                throw new InvalidOperationException($"Field {fieldIndex} of object {ObjectId} is uninitialized.");
            return value;
        }

        internal AloeValue GetCommittedField(int fieldIndex, bool allowUninitialized = false)
        {
            ValidateFieldIndex(fieldIndex);
            var value = _committedFields[fieldIndex];
            if (!allowUninitialized && value.IsNull)
                throw new InvalidOperationException($"Field {fieldIndex} of object {ObjectId} is uninitialized.");
            return value;
        }

        internal bool TryGetVolatileField(int fieldIndex, out AloeValue value)
        {
            ValidateFieldIndex(fieldIndex);
            if (_volatileDirty[fieldIndex])
            {
                value = _volatileFields[fieldIndex];
                return true;
            }
            value = AloeValue.Null;
            return false;
        }

        internal void SetCommittedField(int fieldIndex, AloeValue value)
        {
            ValidateFieldIndex(fieldIndex);
            _committedFields[fieldIndex] = value;
        }

        internal void SetVolatileField(int fieldIndex, AloeValue value)
        {
            ValidateFieldIndex(fieldIndex);
            _volatileFields[fieldIndex] = value;
            _volatileDirty[fieldIndex] = true;
        }

        internal IReadOnlyList<int> GetDirtyFieldIndices()
        {
            var result = new List<int>();
            for (var i = 0; i < _volatileDirty.Length; i++)
            {
                if (_volatileDirty[i]) result.Add(i);
            }
            return result;
        }

        internal void CommitVolatileField(int fieldIndex)
        {
            ValidateFieldIndex(fieldIndex);
            if (!_volatileDirty[fieldIndex]) return;
            _committedFields[fieldIndex] = _volatileFields[fieldIndex];
            _volatileFields[fieldIndex] = AloeValue.Null;
            _volatileDirty[fieldIndex] = false;
        }

        private void ValidateFieldIndex(int fieldIndex)
        {
            if ((uint)fieldIndex >= (uint)_committedFields.Length)
                throw new IndexOutOfRangeException($"Field index {fieldIndex} is out of range for object {ObjectId}.");
        }

        internal void AddOutgoingReference(long targetObjectId)
        {
            _outgoingReferences.TryGetValue(targetObjectId, out var count);
            _outgoingReferences[targetObjectId] = count + 1;
        }

        internal bool RemoveOutgoingReference(long targetObjectId)
        {
            if (!_outgoingReferences.TryGetValue(targetObjectId, out var count))
                return false;
            if (count <= 1) _outgoingReferences.Remove(targetObjectId);
            else _outgoingReferences[targetObjectId] = count - 1;
            return true;
        }

        internal void ReplaceOutgoingReferences(IReadOnlyDictionary<long, int> references)
        {
            _outgoingReferences.Clear();
            foreach (var pair in references)
                _outgoingReferences.Add(pair.Key, pair.Value);
        }
    }

    public sealed class AloeHeapBank
    {
        private readonly byte[] _memory;
        private readonly bool[] _occupied;

        internal AloeHeapBank(int bankId, int slotCapacity)
        {
            BankId = bankId;
            SlotCapacity = slotCapacity;
            _memory = new byte[slotCapacity * AloeHeap.HeapSlotSize];
            _occupied = new bool[slotCapacity];
        }

        public int BankId { get; }
        public int SlotCapacity { get; }
        public int UsedSlotCount => _occupied.Count(x => x);
        public int AvailableSlotCount => SlotCapacity - UsedSlotCount;
        public double Occupancy => SlotCapacity == 0 ? 0 : (double)UsedSlotCount / SlotCapacity;

        internal int Allocate(int slotCount)
        {
            if (!TryFindFreeRange(slotCount, out var startSlot))
                throw new InvalidOperationException($"Bank {BankId} has no contiguous range of {slotCount} slots.");
            Reserve(startSlot, slotCount);
            return startSlot;
        }

        internal bool TryFindFreeRange(int slotCount, out int startSlot)
        {
            if (slotCount <= 0 || slotCount > SlotCapacity)
            {
                startSlot = -1;
                return false;
            }

            var run = 0;
            for (var i = 0; i < _occupied.Length; i++)
            {
                if (!_occupied[i])
                {
                    run++;
                    if (run == slotCount)
                    {
                        startSlot = i - slotCount + 1;
                        return true;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            startSlot = -1;
            return false;
        }

        internal bool CanReserve(int startSlot, int slotCount)
        {
            if (startSlot < 0 || slotCount <= 0 || startSlot + slotCount > SlotCapacity)
                return false;

            for (var i = startSlot; i < startSlot + slotCount; i++)
            {
                if (_occupied[i]) return false;
            }
            return true;
        }

        internal void Reserve(int startSlot, int slotCount)
        {
            if (!CanReserve(startSlot, slotCount))
                throw new InvalidOperationException($"Bank {BankId} range is not available.");

            for (var i = startSlot; i < startSlot + slotCount; i++)
                _occupied[i] = true;
        }

        internal void Release(int startSlot, int slotCount)
        {
            for (var i = startSlot; i < startSlot + slotCount; i++)
                _occupied[i] = false;

            Array.Clear(_memory, startSlot * AloeHeap.HeapSlotSize, slotCount * AloeHeap.HeapSlotSize);
        }

        internal void Write(int startSlot, int slotCount, ReadOnlySpan<byte> data)
        {
            if (data.Length > slotCount * AloeHeap.HeapSlotSize)
                throw new ArgumentException("Data does not fit in allocated Heap Slots.", nameof(data));

            data.CopyTo(_memory.AsSpan(startSlot * AloeHeap.HeapSlotSize));
        }

        internal byte[] Read(int startSlot, int dataSizeBytes)
        {
            var result = new byte[dataSizeBytes];
            _memory.AsSpan(startSlot * AloeHeap.HeapSlotSize, dataSizeBytes).CopyTo(result);
            return result;
        }

        internal byte[] ReadRaw(int startSlot, int slotCount)
        {
            var result = new byte[slotCount * AloeHeap.HeapSlotSize];
            _memory.AsSpan(startSlot * AloeHeap.HeapSlotSize, result.Length).CopyTo(result);
            return result;
        }

        internal void WriteRaw(int startSlot, int slotCount, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != slotCount * AloeHeap.HeapSlotSize)
                throw new ArgumentException("Raw move size must match the Heap Slot allocation.", nameof(bytes));
            bytes.CopyTo(_memory.AsSpan(startSlot * AloeHeap.HeapSlotSize));
        }
    }
}
