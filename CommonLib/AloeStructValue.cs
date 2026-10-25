using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.CommonLib;

/// <summary>An immutable value snapshot containing primitive values or nested structs.</summary>
public sealed class AloeStructValue
{
    private readonly AloeValue[] _fields;

    /// <summary>Construct a snapshot, copying the supplied field collection.</summary>
    public AloeStructValue(string typeName, IEnumerable<AloeValue> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(fields);
        TypeName = typeName;
        _fields = fields.ToArray();
        foreach (var field in _fields) ValidateField(field);
    }

    /// <summary>The nominal source type name.</summary>
    public string TypeName { get; }

    /// <summary>The number of stored fields.</summary>
    public int FieldCount => _fields.Length;

    /// <summary>Read a field without exposing mutable storage.</summary>
    public AloeValue GetField(int index)
    {
        if ((uint)index >= (uint)_fields.Length) throw new VmException("Struct field index is out of range.");
        return _fields[index];
    }

    /// <summary>Return a new snapshot with one field replaced; the original remains unchanged.</summary>
    public AloeStructValue WithField(int index, AloeValue value)
    {
        var previous = GetField(index);
        ValidateField(value);
        if (previous.Kind != value.Kind ||
            (previous.Kind == EnumValueKind.Struct && previous.AsStruct.TypeName != value.AsStruct.TypeName))
            throw new VmException("Struct field replacement must preserve its value type.");
        var fields = (AloeValue[])_fields.Clone();
        fields[index] = value;
        return new AloeStructValue(TypeName, fields);
    }

    private static void ValidateField(AloeValue value)
    {
        if (value.Kind is not (EnumValueKind.Int or EnumValueKind.Byte or EnumValueKind.Char or EnumValueKind.Float or
            EnumValueKind.Decimal or EnumValueKind.Bool or EnumValueKind.String or EnumValueKind.Struct))
            throw new VmException("Struct fields cannot contain reference values or uninitialized values.");
        if (value.IsInt && (value.AsInt < int.MinValue || value.AsInt > int.MaxValue))
            throw new VmException("Struct integer field must fit signed int32.");
    }
}
