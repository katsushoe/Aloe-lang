using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private static bool IsReferenceArrayName(string? name)
        => IsSupportedArrayName(name) && (TryGetNestedCollectionChild(name, out _) || (name!.StartsWith("set:", StringComparison.Ordinal)
            ? IsReferenceArrayName(name[4..])
            : name.StartsWith("list:", StringComparison.Ordinal)
            ? IsReferenceArrayName(name[5..])
            : name.EndsWith("[][]", StringComparison.Ordinal) || name == "object[]" || IsBoxArrayName(name) || TryGetClassArrayTypes(name, out _)));

    private void InvokeReferenceSlot(EnumSyscall id)
    {
        var index = Pop();
        var owner = Pop();
        RequireLiveReference(owner);
        var ownerEntry = Heap.GetEntry(owner.AsObjectId);
        var validLength = _committedQueryDepth > 0 ? ownerEntry.CommittedFieldCount : ownerEntry.FieldCount;
        if (!index.IsInt || index.AsInt < 0 || index.AsInt >= validLength)
            throw new VmException("Reference slot index is outside its owner fields.");
        var isArray = id == EnumSyscall.ReferenceArrayInvalidate;
        lock (Heap.SyncRoot)
        {
            var entry = Heap.GetEntry(owner.AsObjectId);
            if (isArray && !IsReferenceArrayName(entry.TypeName))
                throw new VmException("Only reference-array elements can be invalidated.");
            var value = _committedQueryDepth > 0
                ? entry.GetCommittedField((int)index.AsInt, allowUninitialized: true)
                : entry.GetCurrentField((int)index.AsInt, allowUninitialized: true);
            if (!value.IsObject && !value.IsNull) throw new VmException("Reference slot contains a primitive value.");
            if (id == EnumSyscall.ReferenceFieldGet) { Push(value); return; }
            if (_committedQueryDepth > 0) throw new VmException("Reference slot invalidation is forbidden during committed queries.");
            if (_tickActive) Heap.SetVolatileField(owner.AsObjectId, (int)index.AsInt, AloeValue.Null);
            else Heap.SetField(owner.AsObjectId, (int)index.AsInt, AloeValue.Null);
        }
    }
}
