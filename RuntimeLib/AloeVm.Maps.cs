using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private static bool IsSupportedMapName(string? name)
    {
        if (name?.StartsWith("map:", StringComparison.Ordinal) != true) return false;
        if (!TrySplitMapDescriptor(name, out var key, out var value)) return false;
        return IsSupportedMapKeyName(key) && IsSupportedMapValueName(value);
    }

    private static bool TrySplitMapDescriptor(string name, out string key, out string value)
    {
        // Both class keys and class values may contain their own '|'-separated allowed-type lists.
        for (var separator = name.IndexOf('|', 4); separator > 4; separator = name.IndexOf('|', separator + 1))
        {
            var candidateKey = name[4..separator];
            var candidateValue = name[(separator + 1)..];
            if (!IsSupportedMapKeyName(candidateKey) || !IsSupportedMapValueName(candidateValue)) continue;
            key = candidateKey;
            value = candidateValue;
            return true;
        }
        key = value = "";
        return false;
    }

    private static bool IsSupportedMapScalarName(string name)
        => name is "int[]" or "bool[]" or "byte[]" or "char[]" or "float[]" or "decimal[]" or "string[]" ||
           IsNamedArrayName(name, "enum:");

    private static bool IsSupportedMapKeyName(string name)
        => IsSupportedMapScalarName(name) || TryGetClassArrayTypes(name, out _);

    private static bool IsSupportedMapValueName(string name)
        => IsSupportedMapScalarName(name) || IsSupportedMapReferenceValueName(name);

    private static bool IsSupportedMapReferenceValueName(string name)
        => name == "object[]" || IsBoxArrayName(name) || TryGetClassArrayTypes(name, out _);

    private void InvokeMapSyscall(EnumSyscall id)
    {
        if (id == EnumSyscall.MapCreate)
        {
            var count = Pop();
            var descriptor = Pop();
            if (!count.IsInt || count.AsInt < 0 || count.AsInt > int.MaxValue / 2 || count.AsInt > ValueStack.Count / 2)
                throw new VmException("Map creation requires an int count and that many key/value pairs.");
            if (!descriptor.IsString || !IsSupportedMapName(descriptor.AsString))
                throw new VmException("Map creation requires a supported descriptor.");
            var source = new AloeValue[(int)count.AsInt * 2];
            var createTypes = GetMapDescriptorParts(descriptor.AsString);
            for (var i = source.Length - 2; i >= 0; i -= 2)
            {
                source[i + 1] = Pop();
                source[i] = Pop();
                ValidateArrayElement(createTypes.Key, source[i]);
                ValidateArrayElement(createTypes.Value, source[i + 1]);
            }
            var fields = new List<AloeValue>();
            for (var i = 0; i < source.Length; i += 2)
            {
                var existing = FindMapKey(fields, source[i], createTypes.Key);
                if (existing >= 0) fields[existing + 1] = source[i + 1];
                else { fields.Add(source[i]); fields.Add(source[i + 1]); }
            }
            var handle = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: fields.Count, typeName: descriptor.AsString);
            for (var i = 0; i < fields.Count; i++) Heap.SetField(handle, i, fields[i]);
            Push(AloeValue.FromObject(handle));
            return;
        }
        if (id == EnumSyscall.MapKeyAt)
        {
            var keyIndex = Pop();
            var keyOwner = Pop();
            if (!keyOwner.IsObject || !Heap.ContainsObject(keyOwner.AsObjectId))
                throw new VmException("Map key iteration requires a live Map handle.");
            var keyEntry = Heap.GetEntry(keyOwner.AsObjectId);
            if (!IsSupportedMapName(keyEntry.TypeName)) throw new VmException("Map key iteration requires a supported Map descriptor.");
            var fields = _committedQueryDepth > 0 ? keyEntry.CommittedFieldCount : keyEntry.FieldCount;
            if (fields % 2 != 0) throw new VmException("Map fields must contain key/value pairs.");
            if (!keyIndex.IsInt || keyIndex.AsInt < 0 || keyIndex.AsInt >= fields / 2)
                throw new VmException("Map key index must be an int within 0..Count-1.");
            TrySplitMapDescriptor(keyEntry.TypeName!, out var keyType, out _);
            var keyValue = _committedQueryDepth > 0
                ? keyEntry.GetCommittedField(checked((int)keyIndex.AsInt * 2))
                : keyEntry.GetCurrentField(checked((int)keyIndex.AsInt * 2));
            ValidateArrayElement(keyType, keyValue);
            Push(keyValue);
            return;
        }
        var value = id == EnumSyscall.MapSet ? Pop() : AloeValue.Null;
        var key = id == EnumSyscall.MapCount ? AloeValue.Null : Pop();
        var owner = Pop();
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("Map operation requires a live Map handle.");
        var entry = Heap.GetEntry(owner.AsObjectId);
        if (!IsSupportedMapName(entry.TypeName)) throw new VmException("Map operation requires a supported Map descriptor.");
        var mutating = id is EnumSyscall.MapSet or EnumSyscall.MapRemove;
        if (mutating && _committedQueryDepth > 0)
            throw new VmException("Map mutations are forbidden during committed property queries.");
        var countFields = _committedQueryDepth > 0 ? entry.CommittedFieldCount : entry.FieldCount;
        if (countFields % 2 != 0) throw new VmException("Map fields must contain key/value pairs.");
        if (id == EnumSyscall.MapCount) { Push(AloeValue.FromInt(countFields / 2)); return; }
        var typeParts = GetMapDescriptorParts(entry.TypeName!);
        ValidateArrayElement(typeParts.Key, key);
        var fieldsNow = Enumerable.Range(0, countFields)
            .Select(i => _committedQueryDepth > 0 ? entry.GetCommittedField(i) : entry.GetCurrentField(i)).ToList();
        for (var i = 0; i < fieldsNow.Count; i += 2)
        {
            ValidateArrayElement(typeParts.Key, fieldsNow[i]);
            ValidateArrayElement(typeParts.Value, fieldsNow[i + 1]);
        }
        var index = FindMapKey(fieldsNow, key, typeParts.Key);
        if (id == EnumSyscall.MapContainsKey) { Push(AloeValue.FromBool(index >= 0)); return; }
        if (id == EnumSyscall.MapGet)
        {
            if (index < 0) throw new VmException("Map key was not found.");
            Push(fieldsNow[index + 1]);
            return;
        }
        if (id == EnumSyscall.MapSet)
        {
            ValidateArrayElement(typeParts.Value, value);
            if (index >= 0) fieldsNow[index + 1] = value;
            else
            {
                if (countFields > int.MaxValue - 2) throw new VmException("Map count exceeds the supported maximum.");
                fieldsNow.Add(key);
                fieldsNow.Add(value);
            }
        }
        else
        {
            if (index < 0) return;
            fieldsNow.RemoveRange(index, 2);
        }
        Heap.ResizeCollection(owner.AsObjectId, fieldsNow, _tickActive);
    }

    private static (string Key, string Value) GetMapDescriptorParts(string name)
    {
        if (!TrySplitMapDescriptor(name, out var key, out var value))
            throw new VmException("Map descriptor must declare key and value types.");
        return (key, value);
    }

    private int FindMapKey(IReadOnlyList<AloeValue> fields, AloeValue key, string keyType)
    {
        for (var i = 0; i < fields.Count; i += 2)
        {
            ValidateArrayElement(keyType, fields[i]);
            if (SetValuesEqual(fields[i], key)) return i;
        }
        return -1;
    }
}
