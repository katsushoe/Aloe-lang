using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // Catch-table constants naming a declared exception type use this prefix (decision 0059).
    private const string ThrownExceptionCatchPrefix = "exception:";

    private static bool IsThrownExceptionCatchDescriptor(string descriptor, out string typeName)
    {
        var matched = descriptor.StartsWith(ThrownExceptionCatchPrefix, StringComparison.Ordinal);
        typeName = matched ? descriptor[ThrownExceptionCatchPrefix.Length..] : string.Empty;
        return matched;
    }

    // Stack: exception struct value, type-chain constant index ("Thrown|Base|...|BuiltinRoot").
    private void ThrowExceptionValue()
    {
        var chainIndex = Pop();
        var value = Pop();
        if (!chainIndex.IsInt || chainIndex.AsInt < 0 || chainIndex.AsInt >= _module.Constants.Count ||
            !_module.Constants[(int)chainIndex.AsInt].IsString)
            throw new VmException("Exception throw expects a type-chain string constant index.");
        if (value.Kind != EnumValueKind.Struct || value.AsStruct.FieldCount < 2)
            throw new VmException("Exception throw expects an exception struct value.");
        var chain = _module.Constants[(int)chainIndex.AsInt].AsString.Split('|');
        if (!AloeExceptionTypes.TryResolve(chain[^1], out var builtinBase) ||
            !string.Equals(chain[0], value.AsStruct.TypeName, StringComparison.Ordinal))
            throw new VmException("Exception throw type chain does not match the thrown value.");
        throw new AloeThrownException(value, chain, builtinBase);
    }

    // Stack: catch type-name constant index, field count. Pushes the caught value narrowed to that type.
    private void PushCaughtExceptionValue()
    {
        var fieldCount = Pop();
        var typeIndex = Pop();
        var frame = CurrentFrame;
        if (frame == null || frame.CaughtExceptions.Count == 0 ||
            frame.CaughtExceptions.Peek().SourceException is not AloeThrownException thrown)
            throw new VmException("Caught exception value requires an active catch of a thrown exception value.");
        if (!typeIndex.IsInt || typeIndex.AsInt < 0 || typeIndex.AsInt >= _module.Constants.Count ||
            !_module.Constants[(int)typeIndex.AsInt].IsString || !fieldCount.IsInt ||
            fieldCount.AsInt < 2 || fieldCount.AsInt > thrown.Value.AsStruct.FieldCount)
            throw new VmException("Caught exception value expects a type-name constant and a valid field count.");
        var source = thrown.Value.AsStruct;
        var fields = new AloeValue[(int)fieldCount.AsInt];
        for (var index = 0; index < fields.Length; index++)
            fields[index] = source.GetField(index);
        Push(AloeValue.FromStruct(new AloeStructValue(_module.Constants[(int)typeIndex.AsInt].AsString, fields)));
    }
}
