using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    private void InvokeObjectCast(EnumSyscall id)
    {
        var descriptor = Pop();
        var value = Pop();
        if (!descriptor.IsString) throw new VmException("Object cast requires a target descriptor string.");
        var name = descriptor.AsString;
        var classes = Array.Empty<string>();
        EnumValueKind? boxKind = null;
        string? arrayName = null;
        if (name.StartsWith("map:", StringComparison.Ordinal))
        {
            if (!IsSupportedMapName(name)) throw new VmException("Object cast requires a supported Map descriptor.");
            arrayName = name;
        }
        else if (name.StartsWith("array:", StringComparison.Ordinal))
        {
            arrayName = name[6..];
            if (!IsSupportedArrayName(arrayName)) throw new VmException("Object cast requires a supported array descriptor.");
        }
        else if (name.StartsWith("class:", StringComparison.Ordinal))
        {
            if (!TryGetClassArrayTypes(name + "[]", out classes)) throw new VmException("Object cast requires valid unique class names.");
        }
        else if (name.StartsWith("box:", StringComparison.Ordinal)) boxKind = PrimitiveCastKind(name[4..]);
        else if (name != "object") throw new VmException("Object cast target descriptor is unsupported.");

        var success = value.IsObject && Heap.ContainsObject(value.AsObjectId);
        if (success && name != "object")
        {
            var actualName = Heap.GetEntry(value.AsObjectId).TypeName;
            success = boxKind != null
                ? TryReadBoxPrimitive(value, name[4..], boxKind.Value, out _)
                : arrayName != null
                ? string.Equals(actualName, arrayName, StringComparison.Ordinal)
                : classes.Contains(actualName, StringComparer.Ordinal);
        }
        if (id == EnumSyscall.ObjectTryCast)
        {
            Push(success ? value : AloeValue.Null);
            Push(AloeValue.FromBool(success));
        }
        else if (success) Push(value);
        else throw new InvalidCastException("Object is not a live reference compatible with the requested type.");
    }
}
