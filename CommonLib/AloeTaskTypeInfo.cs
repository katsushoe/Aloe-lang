using System;
using System.Collections.Generic;
using System.Linq;
using Aloe.CommonLib.Constants;

namespace Aloe.CommonLib;

/// <summary>Bytecode function binding for one Task notification or run method.</summary>
public sealed class AloeTaskMethodInfo
{
    public string Name { get; }
    public int FunctionIndex { get; }
    public IReadOnlyList<EnumValueKind> Parameters { get; }
    public AloeTaskMethodInfo(string name, int functionIndex, IReadOnlyList<EnumValueKind> parameters)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        FunctionIndex = functionIndex;
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToArray();
    }
}

/// <summary>Ordered Task layout and entry bindings carried by a Module.</summary>
public sealed class AloeTaskTypeInfo
{
    public string Name { get; }
    public IReadOnlyList<EnumValueKind> Fields { get; }
    public int? ConstructorIndex { get; }
    public IReadOnlyList<EnumValueKind> ConstructorParameters { get; }
    public IReadOnlyList<AloeTaskMethodInfo> Methods { get; }
    public AloeTaskTypeInfo(string name, IReadOnlyList<EnumValueKind> fields, int? constructorIndex,
        IReadOnlyList<EnumValueKind> constructorParameters, IReadOnlyList<AloeTaskMethodInfo> methods)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Fields = (fields ?? throw new ArgumentNullException(nameof(fields))).ToArray();
        ConstructorIndex = constructorIndex;
        ConstructorParameters = (constructorParameters ?? throw new ArgumentNullException(nameof(constructorParameters))).ToArray();
        Methods = (methods ?? throw new ArgumentNullException(nameof(methods))).ToArray();
    }
}
