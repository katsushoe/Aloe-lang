using System.Globalization;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;

namespace Aloe.RuntimeLib;

public sealed partial class AloeVm
{
    // Decision 0063: run the getter (argument count 0, Committed read) or setter (argument count 1)
    // selected by the receiver's runtime class from a compiler-built "Class=functionIndex;..." table.
    private void InvokeInterfacePropertyDispatch()
    {
        var argumentCountValue = Pop();
        var descriptorIndexValue = Pop();
        if (!argumentCountValue.IsInt || argumentCountValue.AsInt is not (0 or 1))
            throw new VmException("Interface property dispatch expects argument count 0 (get) or 1 (set).");
        if (!descriptorIndexValue.IsInt || descriptorIndexValue.AsInt < 0 || descriptorIndexValue.AsInt >= _module.Constants.Count)
            throw new VmException("Interface property dispatch expects a valid dispatch descriptor constant index.");
        var descriptor = _module.Constants[(int)descriptorIndexValue.AsInt];
        if (!descriptor.IsString)
            throw new VmException("Interface property dispatch descriptor must be a string constant.");

        var isSetter = argumentCountValue.AsInt == 1;
        var arguments = isSetter ? new[] { Pop() } : Array.Empty<AloeValue>();
        var target = Pop();
        if (!target.IsObject)
            throw new VmException($"Interface property dispatch expects an object target; found {target.Kind}.");

        var typeName = Heap.GetEntry(target.AsObjectId).TypeName
            ?? throw new VmException("Interface property dispatch target has no runtime class type name.");
        var functionIndex = FindDispatchFunction(descriptor.AsString, typeName);
        InvokeInstanceFunctionSynchronously(target.AsObjectId, functionIndex, arguments, committedQuery: !isSetter);
    }

    private int FindDispatchFunction(string descriptor, string typeName)
    {
        foreach (var entry in descriptor.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.LastIndexOf('=');
            if (separator <= 0 || !int.TryParse(entry[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw new VmException("Interface property dispatch descriptor is malformed.");
            if (!string.Equals(entry[..separator], typeName, StringComparison.Ordinal))
                continue;
            if ((uint)index >= (uint)_module.Functions.Count)
                throw new VmException($"Interface property dispatch function index is out of range: {index}.");
            return index;
        }
        throw new VmException($"No interface property implementation is registered for runtime type '{typeName}'.");
    }
}
