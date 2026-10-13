using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ArrayTests
{
    [Test]
    public void Arrays_DocumentedLiteralIndexLengthAndForeachExample()
    {
        const string source = """
function main(args: string[]): int {
    let values: int[] = [10, 20, 30];
    var alias = values;
    alias[1] = 2;
    var total = 0;
    foreach (value in values) { total = total + value; }
    if (values.Length == 3 and values[1] == 2) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Arrays_EvaluateOnceInOrderAndShareAcrossFunctionBoundaries()
    {
        const string source = """
function mark(value: int): int { print(value); return value; }
function change(values: int[]): int[] { values[0] = 42; return values; }
function main(args: string[]): int {
    var values = [mark(1), mark(2)];
    values[mark(0)] = mark(3);
    var returned = change(values);
    let limits: int[] = [-2147483648, 2147483647];
    GC.require(); GC.finish();
    if (returned[0] == 42 and values[0] == 42 and values[1] == 2 and limits[0] == -2147483648 and limits[1] == 2147483647) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n0\n3\n"));
    }

    [Test]
    public void Arrays_ForeachCapturesTargetObservesUpdatesAndHandlesControlFlowAndEmptyBoolArrays()
    {
        const string source = """
function main(args: string[]): int {
    var original = [1, 2, 3, 4]; var source = original;
    var total = 0;
    foreach (value in source) {
        if (value == 1) { source = [9]; original[1] = 5; }
        if (value == 5) { continue; }
        if (value == 4) { break; }
        total = total + value;
    }
    let empty: bool[] = []; var flags = [true, false]; flags[1] = true;
    foreach (flag in empty) { return 0; }
    var count = 0;
    foreach (flag in flags) { if (flag) { foreach (number in original) { count = count + 1; } } }
    empty = [];
    if (total == 4 and count == 8 and empty.Length == 0 and flags[1] and source.Length == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Arrays_ClassFieldsQueuedReferencesAndGcKeepSharedHandles()
    {
        const string source = """
class Box {
    field values: int[];
    construct(values: int[]) { this.values = values; }
    public property Values: int[] { get { return this.values; } }
    public property First: int { get { var values = this.values; return values[0]; } }
    public async method set(values: int[], query: Box): void {
        var current = this.values; current[0] = values[0]; print(query.First); print(current[0]);
    }
}
function main(args: string[]): int {
    var values = [1]; var box = new Box(values);
    var pending = [3]; box.set(pending, box); pending = [9];
    GC.require(); GC.finish(); tick();
    var stored = box.Values;
    stored[0] = 42;
    if (values[0] == 42 and box.First == 42 and pending[0] == 9) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n3\n"));
    }

    [Test]
    public void Arrays_RejectUnsupportedTypesMixedElementsInvalidIndexesAndLengths()
    {
        string[] statements =
        [
            "var empty = [];", "var mixed = [1, true];", "let wrong: bool[] = [1];",
            "let wrong: int[] = [false];", "var floats = [1.0, true];", "var strings = [\"a\", 1];",
            "let nested: int[][] = [[true]];", "let bytes: byte[] = [256];", "let strings: string[] = [1];",
            "let flags: bool[] = []; flags = [1];", "var values = [1]; values[true] = 2;",
            "var values = [1]; var result = values[false];", "var values = [1]; values[0] = false;",
            "var flags = [true]; flags[0] = 1;", "var values = [1]; values.Length = 2;",
            "var values = [1]; var result = values.Count;", "var values = [1]; var result = values.Length();",
            "var values = [1,];", "var values = [1]; let flag: bool = values[0];",
            "var value = 1; value[0] = 1;", "let values: int[] = [1]; values = [true];"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("struct S { field values: int[]; } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { var values = [1]; return values[0]; }"));
        foreach (var statement in new[] { "return values[-1];", "return values[1];", "values[2147483648] = 2; return 0;", "values[0] = 2147483648; return 0;" })
            Assert.Throws<VmException>(() => Run("function main(args: string[]): int { var values = [1]; " + statement + " }"), statement);
        Assert.Throws<VmException>(() => Run("function main(args: string[]): int { var values = [2147483648]; return 0; }"));
        Assert.Throws<VmException>(() => Run("function main(args: string[]): int { let values: int[] = []; return values[0]; }"));
    }

    [Test]
    public void Arrays_GetterQueriesRejectDirectAndTransitiveWrites()
    {
        const string type = "class Box { field values: int[]; construct(values: int[]) { this.values = values; } ";
        const string main = "function main(args: string[]): int { var values = [1]; var box = new Box(values); return box.First; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(type +
            "public property First: int { get { var values = this.values; values[0] = 2; return values[0]; } } }" + main));
        Assert.Throws<VmException>(() => Run("function mutate(values: int[]): int { values[0] = 2; return 2; }" + type +
            "public property First: int { get { return mutate(this.values); } } }" + main));
    }

    [Test]
    public void Arrays_RuntimeRejectsMalformedSyscallsAndNonArrayHandles()
    {
        var vm = CreateVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"));
        vm.Push(AloeValue.FromBool(true)); vm.Push(AloeValue.FromString("int[]")); vm.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        vm.ValueStack.Clear();
        vm.Push(AloeValue.FromInt(1)); vm.Push(AloeValue.FromString("bool[]")); vm.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        vm.ValueStack.Clear();
        vm.Push(AloeValue.FromString("int[]")); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.ArrayCreate);
        var empty = vm.Pop();
        vm.Push(empty); vm.Push(AloeValue.FromInt(0));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayGet));
        vm.ValueStack.Clear();
        var obj = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Other"));
        vm.Push(obj);
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayLength));
        vm.ValueStack.Clear();
        vm.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayLength));
    }

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new StringBuilder();
        var vm = CreateVm(module);
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
