using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void InvokePrimitiveCast(EnumSyscall id)
    {
        var descriptor = Pop();
        var value = Pop();
        if (!descriptor.IsString) throw new VmException("Primitive cast requires a target descriptor string.");
        var kind = PrimitiveCastKind(descriptor.AsString);
        if (value.IsObject && TryReadBoxPrimitive(value, descriptor.AsString, kind, out var payload)) value = payload;
        var success = value.Kind == kind && IsValidBoxPrimitive(value);
        if (id == EnumSyscall.PrimitiveTryCast)
        {
            Push(success ? value : AloeValue.Null);
            Push(AloeValue.FromBool(success));
        }
        else if (success) Push(value);
        else throw new InvalidCastException("Value or box does not contain a valid primitive of the requested type; numeric conversion is unsupported.");
    }

    private static EnumValueKind PrimitiveCastKind(string name)
        => name switch
        {
            "int" => EnumValueKind.Int,
            "byte" => EnumValueKind.Byte,
            "char" => EnumValueKind.Char,
            "float" => EnumValueKind.Float,
            "decimal" => EnumValueKind.Decimal,
            "string" => EnumValueKind.String,
            "bool" => EnumValueKind.Bool,
            _ => throw new VmException("Primitive cast target descriptor is unsupported.")
        };
    private bool TryReadBoxPrimitive(AloeValue value, string name, EnumValueKind kind, out AloeValue payload)
    {
        payload = AloeValue.Null;
        if (!value.IsObject) return false;
        lock (Heap.SyncRoot)
        {
            if (!Heap.ContainsObject(value.AsObjectId)) return false;
            var entry = Heap.GetEntry(value.AsObjectId);
            if (entry.TypeName != "box:" + name || entry.FieldCount != 1) return false;
            payload = _committedQueryDepth > 0
                ? entry.GetCommittedField(0, allowUninitialized: true)
                : entry.GetCurrentField(0, allowUninitialized: true);
        }
        return payload.Kind == kind && IsValidBoxPrimitive(payload);
    }

    private void InvokePrimitiveBox()
    {
        var value = Pop();
        if (!IsValidBoxPrimitive(value)) throw new VmException("Boxing requires a supported primitive in its valid range.");
        var name = value.Kind switch
        {
            EnumValueKind.Int => "int", EnumValueKind.Byte => "byte", EnumValueKind.Char => "char",
            EnumValueKind.Float => "float", EnumValueKind.Decimal => "decimal", EnumValueKind.String => "string",
            EnumValueKind.Bool => "bool", _ => throw new VmException("Unsupported box value kind.")
        };
        lock (Heap.SyncRoot)
        {
            var handle = Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: 1, typeName: "box:" + name);
            Heap.SetField(handle, 0, value);
            Push(AloeValue.FromObject(handle));
        }
    }

    private static bool IsValidBoxPrimitive(AloeValue value)
        => value.Kind switch
        {
            EnumValueKind.Int => value.AsInt is >= int.MinValue and <= int.MaxValue,
            EnumValueKind.Float => double.IsFinite(value.AsFloat),
            EnumValueKind.Byte or EnumValueKind.Char or EnumValueKind.Decimal or EnumValueKind.String or EnumValueKind.Bool => true,
            _ => false
        };

    private void RequireLiveReference(AloeValue value)
    {
        if (!value.IsObject || !Heap.ContainsObject(value.AsObjectId))
            throw new InvalidReferenceException("Reference variable is invalid or does not identify a live heap object.");
    }

    private void InvokeConsumingUnbox()
    {
        var descriptor = Pop();
        var reference = Pop();
        if (!descriptor.IsString) throw new VmException("Consuming unbox requires a primitive target descriptor.");
        var kind = PrimitiveCastKind(descriptor.AsString);
        RequireLiveReference(reference);
        if (!TryReadBoxPrimitive(reference, descriptor.AsString, kind, out var payload))
            throw new InvalidCastException("Reference does not contain a valid box of the expected primitive type.");
        Push(payload);
    }
}
