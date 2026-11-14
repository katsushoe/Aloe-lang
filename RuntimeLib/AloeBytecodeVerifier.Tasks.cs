using System;
using System.Collections.Generic;
using System.Linq;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;

namespace Aloe.RuntimeLib;

public static partial class AloeBytecodeVerifier
{
    private static void VerifyTaskTypes(Module module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in module.TaskTypes)
        {
            if (task == null || string.IsNullOrEmpty(task.Name) || !names.Add(task.Name))
                throw Error(-1, "Task type names must be nonempty and unique.");
            if (task.Fields.Any(kind => !AloeTaskPool.IsSupportedKind(kind)))
                throw Error(-1, "Task fields require scalar or ordinary Object kinds.");
            if (task.ConstructorIndex is int constructor)
                VerifyTaskEntry(module, task.Name + ".construct", constructor, task.ConstructorParameters);
            else if (task.ConstructorParameters.Count != 0)
                throw Error(-1, "A Task without a constructor cannot declare constructor parameters.");
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in task.Methods)
            {
                if (method == null || string.IsNullOrEmpty(method.Name) || !methods.Add(method.Name) || method.Name is "start" or "stop" or "construct")
                    throw Error(-1, "Task method names must be unique and cannot shadow lifecycle operations.");
                if (method.Name == "run" && method.Parameters.Count != 0)
                    throw Error(-1, "Task run requires zero explicit parameters.");
                VerifyTaskEntry(module, task.Name + "." + method.Name, method.FunctionIndex, method.Parameters);
            }
            if (!methods.Contains("run")) throw Error(-1, "Task metadata must bind run.");
        }
    }

    private static void VerifyTaskEntry(Module module, string name, int index, IReadOnlyList<EnumValueKind> parameters)
    {
        if ((uint)index >= (uint)module.Functions.Count || index == module.EntryPointIndex)
            throw Error(-1, "Task entry function index is invalid.");
        if (parameters.Any(kind => !AloeTaskPool.IsSupportedKind(kind)))
            throw Error(-1, "Task entries accept scalar and ordinary Object parameters only.");
        var function = module.Functions[index];
        if (function.Name != name || function.ParameterCount != parameters.Count + 1)
            throw Error(-1, "Task entry name or parameter count does not match its binding.");
        if (function.HasTypeMetadata && (function.ReturnType != null || function.ParameterTypes[0] != EnumValueKind.Task ||
            !function.ParameterTypes.Skip(1).SequenceEqual(parameters)))
            throw Error(-1, "Task entries require an implicit Task receiver, declared parameters and void result.");
    }
}
