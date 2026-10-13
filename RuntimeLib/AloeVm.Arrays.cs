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
        if (id == EnumSyscall.ArrayLength)
        {
            Push(AloeValue.FromInt(entry.FieldCount));
            return;
        }
        if (!index.IsInt || index.AsInt < 0 || index.AsInt >= entry.FieldCount)
            throw new VmException("Array index must be an int within 0..Length-1.");
        if (id == EnumSyscall.ArrayGet)
        {
            var element = _committedQueryDepth > 0 ? Heap.GetCommittedField(array.AsObjectId, (int)index.AsInt) : Heap.GetField(array.AsObjectId, (int)index.AsInt);
            ValidateArrayElement(entry.TypeName!, element);
            Push(element);
        }
        else
        {
            if (_committedQueryDepth > 0) throw new VmException("Array writes are forbidden during committed property queries.");
            ValidateArrayElement(entry.TypeName!, value);
            if (_tickActive && _committedQueryDepth == 0) Heap.SetVolatileField(array.AsObjectId, (int)index.AsInt, value);
            else Heap.SetField(array.AsObjectId, (int)index.AsInt, value);
        }
    }

    private void ValidateArrayElement(string typeName, AloeValue value)
    {
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
        while (name != null && name.EndsWith("[][]", StringComparison.Ordinal)) name = name[..^2];
        return name is "int[]" or "bool[]" or "byte[]" or "char[]" or "float[]" or "decimal[]" or "string[]" or "object[]" || TryGetClassArrayTypes(name, out _) ||
            IsNamedArrayName(name, "enum:") || IsNamedArrayName(name, "struct:");
    }
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
