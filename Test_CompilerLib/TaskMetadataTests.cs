using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskMetadataTests
{
    [TestCase(EnumValueKind.Unknown)]
    [TestCase(EnumValueKind.Null)]
    [TestCase(EnumValueKind.Struct)]
    [TestCase(EnumValueKind.Task)]
    public void TaskLayoutsRejectUnsupportedFieldKinds(EnumValueKind kind)
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(fields: new[] { kind }) })));

    [Test]
    public void DuplicateTaskTypesAreRejected()
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(), Type() })));

    [Test]
    public void MissingRunAndDuplicateMethodsAreRejected()
    {
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(methods: Array.Empty<AloeTaskMethodInfo>()) })));
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(methods: new[] { Method(), Method() }) })));
    }

    [TestCase("start")]
    [TestCase("stop")]
    [TestCase("construct")]
    public void ReservedMethodNamesCannotBeBound(string name)
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(methods: new[] { Method(), new AloeTaskMethodInfo(name, 1, Array.Empty<EnumValueKind>()) }) })));

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(2)]
    public void TaskMethodIndexMustTargetNonEntryFunction(int index)
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(methods: new[] { Method(index) }) })));

    [Test]
    public void TaskConstructorRequiresValidIndexAndDeclaredParameters()
    {
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { new AloeTaskTypeInfo("Worker", Array.Empty<EnumValueKind>(), null, new[] { EnumValueKind.Int }, new[] { Method() }) })));
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { new AloeTaskTypeInfo("Worker", Array.Empty<EnumValueKind>(), 2, Array.Empty<EnumValueKind>(), new[] { Method() }) })));
    }

    [Test]
    public void TaskBindingNameAndParameterCountMustMatchFunction()
    {
        var task = Type();
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { task }, new FunctionInfo("Other.run", 1, 1, 1))));
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { task }, new FunctionInfo("Worker.run", 1, 0, 1))));
    }

    [TestCase(EnumValueKind.Object)]
    [TestCase(EnumValueKind.Int)]
    public void TypedTaskEntryRequiresTaskReceiver(EnumValueKind receiver)
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type() }, new FunctionInfo("Worker.run", 1, new[] { receiver }, new[] { receiver }, null))));

    [Test]
    public void TypedTaskEntryMustReturnVoid()
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type() }, new FunctionInfo("Worker.run", 1, new[] { EnumValueKind.Task }, new[] { EnumValueKind.Task }, EnumValueKind.Int))));

    [Test]
    public void RunCannotAcceptExplicitArguments()
        => Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(Module(new[] { Type(methods: new[] { new AloeTaskMethodInfo("run", 1, new[] { EnumValueKind.Int }) }) })));

    [Test]
    public void ExistingBytecodeWithoutTasksKeepsIdenticalSerialization()
    {
        var original = new AloeCompiler().Compile("function main(args: string[]): int { return 42; }");
        var explicitEmpty = new Module(original.Constants, original.Code, original.Functions, original.EntryPointIndex, Array.Empty<AloeTaskTypeInfo>());
        Assert.That(AloeBcCodec.Write(explicitEmpty), Is.EqualTo(AloeBcCodec.Write(original)));
        Assert.That(AloeBcCodec.Read(AloeBcCodec.Write(original)).TaskTypes, Is.Empty);
    }

    [Test]
    public void TaskSectionIsCore05AndDuplicateSectionsAreRejected()
    {
        var bytes = AloeBcCodec.Write(Module(new[] { Type() })); var offset = TaskOffset(bytes);
        Assert.That(bytes[offset], Is.EqualTo(5));
        Assert.Throws<VmException>(() => AloeBcCodec.Read(bytes.Concat(bytes[offset..]).ToArray()));
    }

    [Test]
    public void TaskPayloadTrailingBytesAndTruncationAreRejected()
    {
        var bytes = AloeBcCodec.Write(Module(new[] { Type() })); var offset = TaskOffset(bytes);
        var expanded = bytes.Concat(new byte[] { 0 }).ToArray(); BitConverter.GetBytes(bytes.Length - offset - 5 + 1).CopyTo(expanded, offset + 1);
        Assert.Throws<VmException>(() => AloeBcCodec.Read(expanded)); Assert.Throws<VmException>(() => AloeBcCodec.Read(bytes[..^1]));
    }

    [Test]
    public void TaskMetadataCopiesCallerOwnedVectors()
    {
        var fields = new[] { EnumValueKind.Int }; var parameters = new[] { EnumValueKind.Int };
        var method = new AloeTaskMethodInfo("send", 2, parameters); var methods = new[] { Method() };
        var type = new AloeTaskTypeInfo("Worker", fields, null, Array.Empty<EnumValueKind>(), methods);
        fields[0] = EnumValueKind.Task; parameters[0] = EnumValueKind.Task; methods[0] = method;
        Assert.That(type.Fields[0], Is.EqualTo(EnumValueKind.Int)); Assert.That(type.Methods[0].Name, Is.EqualTo("run")); Assert.That(method.Parameters[0], Is.EqualTo(EnumValueKind.Int));
    }

    private static AloeTaskMethodInfo Method(int index = 1) => new("run", index, Array.Empty<EnumValueKind>());
    private static AloeTaskTypeInfo Type(EnumValueKind[]? fields = null, AloeTaskMethodInfo[]? methods = null)
        => new("Worker", fields ?? Array.Empty<EnumValueKind>(), null, Array.Empty<EnumValueKind>(), methods ?? new[] { Method() });
    private static Module Module(AloeTaskTypeInfo[] types, FunctionInfo? run = null)
        => new(Array.Empty<AloeValue>(), new[] { new Instruction(EnumOpcode.Return), new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0), run ?? new FunctionInfo("Worker.run", 1, 1, 1) }, 0, types);
    private static int TaskOffset(byte[] bytes)
    {
        var offset = 10;
        while (bytes[offset] != 5) offset += 5 + BitConverter.ToInt32(bytes, offset + 1);
        return offset;
    }
}
