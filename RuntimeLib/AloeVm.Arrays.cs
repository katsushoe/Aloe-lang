using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void InvokeArraySyscall(EnumSyscall id)
    {
        if (id == EnumSyscall.ArrayCreate)
        {
            var count = Pop();
            var type = Pop();
            if (!count.IsInt || count.AsInt < 0 || count.AsInt > int.MaxValue || count.AsInt > ValueStack.Count)
                throw new VmException("Array creation expects a non-negative int count and that many elements.");
            if (!type.IsString || !IsSupportedArrayName(type.AsString))
                throw new VmException("Array creation expects a supported array type name.");
            var values = new AloeValue[(int)count.AsInt];
            for (var i = values.Length - 1; i >= 0; i--)
            {
                values[i] = Pop();
                ValidateArrayElement(type.AsString, values[i]);
            }
            if (type.AsString.StartsWith("set:", StringComparison.Ordinal))
                values = values.Where((value, index) => !values.Take(index).Any(previous => SetValuesEqual(previous, value))).ToArray();
            var handle = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: values.Length, typeName: type.AsString);
            for (var i = 0; i < values.Length; i++) Heap.SetField(handle, i, values[i]);
            Push(AloeValue.FromObject(handle));
            return;
        }
        var value = id == EnumSyscall.ArraySet ? Pop() : AloeValue.Null;
        var index = id == EnumSyscall.ArrayLength ? AloeValue.Null : Pop();
        var array = Pop();
        if (!array.IsObject || !Heap.ContainsObject(array.AsObjectId))
            throw new VmException("Array operation expects a live array handle.");
        var entry = Heap.GetEntry(array.AsObjectId);
        if (!IsSupportedArrayName(entry.TypeName))
            throw new VmException("Array operation expects a supported array handle.");
        if (id == EnumSyscall.ArraySet && entry.TypeName!.StartsWith("set:", StringComparison.Ordinal))
            throw new VmException("Set elements cannot be assigned by index.");
        if (id == EnumSyscall.ArrayLength)
        {
            Push(AloeValue.FromInt(_committedQueryDepth > 0 ? entry.CommittedFieldCount : entry.FieldCount));
            return;
        }
        var accessLength = _committedQueryDepth > 0 ? entry.CommittedFieldCount : entry.FieldCount;
        if (!index.IsInt || index.AsInt < 0 || index.AsInt >= accessLength)
            throw new VmException("Array index must be an int within 0..Length-1.");
        if (id == EnumSyscall.ArrayGet)
        {
            lock (Heap.SyncRoot)
            {
                var referenceArray = IsReferenceArrayName(entry.TypeName);
                var element = _committedQueryDepth > 0 ? entry.GetCommittedField((int)index.AsInt, allowUninitialized: referenceArray) : entry.GetCurrentField((int)index.AsInt, allowUninitialized: referenceArray);
                if (!element.IsNull || !referenceArray) ValidateArrayElement(entry.TypeName!, element);
                Push(element);
            }
        }
        else
        {
            if (_committedQueryDepth > 0) throw new VmException("Array writes are forbidden during committed property queries.");
            ValidateArrayElement(entry.TypeName!, value);
            if (_tickActive && _committedQueryDepth == 0) Heap.SetVolatileField(array.AsObjectId, (int)index.AsInt, value);
            else Heap.SetField(array.AsObjectId, (int)index.AsInt, value);
        }
    }

    private void InvokeListMutation(EnumSyscall id)
    {
        var operand = Pop();
        var owner = Pop();
        if (_committedQueryDepth > 0)
            throw new VmException("List mutations are forbidden during committed property queries.");
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("List mutation expects a live List handle.");
        var entry = Heap.GetEntry(owner.AsObjectId);
        if (entry.TypeName is not { } typeName ||
            !typeName.StartsWith("list:", StringComparison.Ordinal) && !typeName.StartsWith("nested:list:", StringComparison.Ordinal))
            throw new VmException("List mutation expects a List handle.");
        if (entry.FieldCount == int.MaxValue)
            throw new VmException("List length exceeds the supported maximum.");

        if (id == EnumSyscall.ListAdd)
        {
            ValidateArrayElement(entry.TypeName!, operand);
            var values = Enumerable.Range(0, entry.FieldCount).Select(i => entry.GetCurrentField(i, allowUninitialized: IsReferenceArrayName(entry.TypeName))).Append(operand).ToArray();
            Heap.ResizeCollection(owner.AsObjectId, values, _tickActive);
            return;
        }

        if (!operand.IsInt || operand.AsInt < 0 || operand.AsInt >= entry.FieldCount)
            throw new VmException("List.RemoveAt index must be within 0..Length-1.");
        var removedIndex = (int)operand.AsInt;
        var next = new AloeValue[entry.FieldCount - 1];
        for (int source = 0, target = 0; source < entry.FieldCount; source++)
        {
            if (source == removedIndex) continue;
            next[target++] = entry.GetCurrentField(source, allowUninitialized: IsReferenceArrayName(entry.TypeName));
        }
        Heap.ResizeCollection(owner.AsObjectId, next, _tickActive);
    }

    private void InvokeSetMutation(EnumSyscall id)
    {
        var value = Pop();
        var owner = Pop();
        if (_committedQueryDepth > 0)
            throw new VmException("Set mutations are forbidden during committed property queries.");
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("Set mutation expects a live Set handle.");
        var entry = Heap.GetEntry(owner.AsObjectId);
        if (entry.TypeName is not { } typeName || !typeName.StartsWith("set:", StringComparison.Ordinal))
            throw new VmException("Set mutation expects a Set handle.");
        ValidateArrayElement(typeName[4..], value);
        var values = Enumerable.Range(0, entry.FieldCount)
            .Select(i => entry.GetCurrentField(i, allowUninitialized: IsReferenceArrayName(typeName)))
            .ToList();
        var existing = values.FindIndex(item => SetValuesEqual(item, value));
        if (id == EnumSyscall.SetAdd)
        {
            if (existing < 0 && entry.FieldCount == int.MaxValue)
                throw new VmException("Set count exceeds the supported maximum.");
            if (existing < 0) values.Add(value);
        }
        else if (existing >= 0)
        {
            values.RemoveAt(existing);
        }
        if (values.Count != entry.FieldCount)
            Heap.ResizeCollection(owner.AsObjectId, values, _tickActive);
    }

    private void InvokeSetContains()
    {
        var value = Pop();
        var owner = Pop();
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("Set.Contains expects a live Set handle.");
        var entry = Heap.GetEntry(owner.AsObjectId);
        if (entry.TypeName is not { } typeName || !typeName.StartsWith("set:", StringComparison.Ordinal))
            throw new VmException("Set.Contains expects a Set handle.");
        ValidateArrayElement(typeName[4..], value);
        var count = _committedQueryDepth > 0 ? entry.CommittedFieldCount : entry.FieldCount;
        var found = Enumerable.Range(0, count).Any(i => SetValuesEqual(
            _committedQueryDepth > 0 ? entry.GetCommittedField(i) : entry.GetCurrentField(i), value));
        Push(AloeValue.FromBool(found));
    }

    private void InvokeSetClear()
    {
        var owner = Pop();
        if (_committedQueryDepth > 0)
            throw new VmException("Set mutations are forbidden during committed property queries.");
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("Set.Clear expects a live Set handle.");
        var entry = Heap.GetEntry(owner.AsObjectId);
        if (entry.TypeName is not { } typeName || !typeName.StartsWith("set:", StringComparison.Ordinal))
            throw new VmException("Set.Clear expects a Set handle.");
        if (entry.FieldCount != 0)
            Heap.ResizeCollection(owner.AsObjectId, Array.Empty<AloeValue>(), _tickActive);
    }

    private void InvokeSetAlgebra(EnumSyscall id)
    {
        var other = Pop();
        var owner = Pop();
        if (_committedQueryDepth > 0)
            throw new VmException("Set mutations are forbidden during committed property queries.");
        if (!owner.IsObject || !Heap.ContainsObject(owner.AsObjectId))
            throw new VmException("Set operation expects a live destination Set handle.");
        if (!other.IsObject || !Heap.ContainsObject(other.AsObjectId))
            throw new VmException("Set operation expects a live source Set handle.");
        var destination = Heap.GetEntry(owner.AsObjectId);
        var source = Heap.GetEntry(other.AsObjectId);
        if (destination.TypeName is not { } typeName || !typeName.StartsWith("set:", StringComparison.Ordinal) ||
            !string.Equals(source.TypeName, typeName, StringComparison.Ordinal))
            throw new VmException("Set operations require Sets with the exact same element descriptor.");
        var left = Enumerable.Range(0, destination.FieldCount)
            .Select(i => destination.GetCurrentField(i, allowUninitialized: IsReferenceArrayName(typeName))).ToList();
        var right = Enumerable.Range(0, source.FieldCount)
            .Select(i => source.GetCurrentField(i, allowUninitialized: IsReferenceArrayName(typeName))).ToList();
        foreach (var value in left.Concat(right)) ValidateArrayElement(typeName[4..], value);
        switch (id)
        {
            case EnumSyscall.SetUnionWith:
                foreach (var value in right)
                    if (!left.Any(existing => SetValuesEqual(existing, value))) left.Add(value);
                break;
            case EnumSyscall.SetIntersectWith:
                left.RemoveAll(value => !right.Any(candidate => SetValuesEqual(value, candidate)));
                break;
            case EnumSyscall.SetExceptWith:
                left.RemoveAll(value => right.Any(candidate => SetValuesEqual(value, candidate)));
                break;
            default:
                throw new VmException($"Unsupported Set operation {id}.");
        }
        if (left.Count != destination.FieldCount)
            Heap.ResizeCollection(owner.AsObjectId, left, _tickActive);
    }

    private static bool SetValuesEqual(AloeValue left, AloeValue right)
    {
        if (left.Kind != right.Kind) return false;
        return left.Kind switch
        {
            EnumValueKind.Int => left.AsInt == right.AsInt,
            EnumValueKind.Byte => left.AsByte == right.AsByte,
            EnumValueKind.Char => left.AsChar == right.AsChar,
            EnumValueKind.Bool => left.AsBool == right.AsBool,
            EnumValueKind.Float => left.AsFloat.Equals(right.AsFloat),
            EnumValueKind.Decimal => left.AsDecimal == right.AsDecimal,
            EnumValueKind.String => string.Equals(left.AsString, right.AsString, StringComparison.Ordinal),
            EnumValueKind.Object => left.AsObjectId == right.AsObjectId,
            _ => false
        };
    }


    private void ValidateArrayElement(string typeName, AloeValue value)
    {
        if (TryGetNestedCollectionChild(typeName, out var child))
        {
            if (!value.IsObject || !Heap.ContainsObject(value.AsObjectId) || Heap.GetEntry(value.AsObjectId).TypeName != child)
                throw new VmException("Nested collection element must be a live handle with the exact child descriptor.");
            return;
        }
        if (typeName.StartsWith("list:", StringComparison.Ordinal)) typeName = typeName[5..];
        if (typeName.StartsWith("set:", StringComparison.Ordinal)) typeName = typeName[4..];
        var valid = typeName.EndsWith("[][]", StringComparison.Ordinal)
            ? value.IsObject && Heap.ContainsObject(value.AsObjectId) &&
                string.Equals(Heap.GetEntry(value.AsObjectId).TypeName, typeName[..^2], StringComparison.Ordinal)
            : typeName switch
        {
            "int[]" => value.IsInt && value.AsInt >= int.MinValue && value.AsInt <= int.MaxValue,
            "bool[]" => value.IsBool,
            "byte[]" => value.IsByte,
            "char[]" => value.IsChar,
            "float[]" => value.IsFloat && double.IsFinite(value.AsFloat),
            "decimal[]" => value.IsDecimal,
            "string[]" => value.IsString,
            "object[]" => value.IsObject && Heap.ContainsObject(value.AsObjectId),
            _ when IsBoxArrayName(typeName) => TryReadBoxPrimitive(value, typeName[4..^2], PrimitiveCastKind(typeName[4..^2]), out _),
            _ when TryGetClassArrayTypes(typeName, out var types) => value.IsObject && Heap.ContainsObject(value.AsObjectId) &&
                types.Contains(Heap.GetEntry(value.AsObjectId).TypeName, StringComparer.Ordinal),
            _ when IsNamedArrayName(typeName, "enum:") => value.IsInt && value.AsInt >= int.MinValue && value.AsInt <= int.MaxValue,
            _ when IsNamedArrayName(typeName, "struct:") => value.Kind == EnumValueKind.Struct &&
                string.Equals(value.AsStruct.TypeName, typeName[7..^2], StringComparison.Ordinal),
            _ => false
        };
        if (!valid) throw new VmException("Array element must match its declared element type and range.");
    }

    private static bool IsSupportedArrayName(string? name)
    {
        while (TryGetNestedCollectionChild(name, out var child)) name = child;
        if (name?.StartsWith("list:", StringComparison.Ordinal) == true)
            return !name[5..].StartsWith("list:", StringComparison.Ordinal) && !name.EndsWith("[][]", StringComparison.Ordinal) && IsSupportedArrayName(name[5..]);
        if (name?.StartsWith("set:", StringComparison.Ordinal) == true)
            return IsSupportedArrayName(name[4..]);
        while (name != null && name.EndsWith("[][]", StringComparison.Ordinal)) name = name[..^2];
        return name is "int[]" or "bool[]" or "byte[]" or "char[]" or "float[]" or "decimal[]" or "string[]" or "object[]" || IsBoxArrayName(name) || TryGetClassArrayTypes(name, out _) ||
            IsNamedArrayName(name, "enum:") || IsNamedArrayName(name, "struct:");
    }
    private static bool TryGetNestedCollectionChild(string? name, out string child)
    {
        child = "";
        if (name?.StartsWith("nested:list:", StringComparison.Ordinal) == true) child = name[12..];
        else if (name?.StartsWith("nested:array:", StringComparison.Ordinal) == true) child = name[13..];
        else return false;
        return true;
    }
    private static bool IsBoxArrayName(string? name)
        => name is "box:int[]" or "box:byte[]" or "box:char[]" or "box:float[]" or "box:decimal[]" or "box:string[]" or "box:bool[]";
    private static bool IsNamedArrayName(string? name, string prefix)
    {
        if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith("[]", StringComparison.Ordinal) || name.Length <= prefix.Length + 2) return false;
        return IsArrayTypeIdentifier(name.AsSpan(prefix.Length, name.Length - prefix.Length - 2));
    }

    private static bool TryGetClassArrayTypes(string? name, out string[] types)
    {
        types = Array.Empty<string>();
        if (name == null || !name.StartsWith("class:", StringComparison.Ordinal) || !name.EndsWith("[]", StringComparison.Ordinal))
            return false;
        types = name[6..^2].Split('|');
        return types.All(type => IsArrayTypeIdentifier(type.AsSpan())) && types.Distinct(StringComparer.Ordinal).Count() == types.Length;
    }

    private static bool IsArrayTypeIdentifier(ReadOnlySpan<char> identifier)
    {
        if (identifier.IsEmpty || !char.IsLetter(identifier[0]) && identifier[0] != '_') return false;
        foreach (var character in identifier)
            if (!char.IsLetterOrDigit(character) && character != '_') return false;
        return true;
    }
}
