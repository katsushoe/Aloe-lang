using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class NestedArrayTests
{
    [Test]
    public void NestedArrays_DocumentedRowsShareReferencesAndForeach()
    {
        const string source = """
function identity(values: int[][]): int[][] { return values; }
function main(args: string[]): int {
    let matrix: int[][] = [[1, 2], [3]];
    var row = matrix[0]; row[0] = 20;
    identity(matrix)[1] = [22];
    var total = 0;
    foreach (values in matrix) { foreach (value in values) { total = total + value; } }
    GC.require(); GC.finish();
    if (matrix[0][0] == 20 and matrix[0].Length == 2 and total == 44) { return matrix[0][0] + matrix[1][0]; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedArrays_TypedEmptyContextsJaggedLengthsAndAllBaseKinds()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Flags { Read, Write }
struct Point { field x: int; }
function main(args: string[]): int {
    let values: int[][][] = [[[], [1]]]; values[0][0] = [42];
    let bytes: byte[][] = [[0, 255], []]; bytes[0][0] = 42;
    var chars = [['a']]; var floats = [[1.5]]; var decimals = [[0.1:d]]; var bools = [[true]];
    let colors: Color[][] = [[], [Color.Red]]; colors[1][0] = Color.Blue;
    var flags = [[Flags.Read]]; flags[0][0] = flags[0][0] | Flags.Write;
    var points = [[Point { x: 1 }]]; var before = points[0][0]; points[0][0].x = 42;
    values[0][1] = []; GC.require(); GC.finish();
    if (values[0][0][0] == 42 and values[0][1].Length == 0 and bytes[0][0] == 42 and chars[0][0] == 'a' and
        floats[0][0] == 1.5 and decimals[0][0] == 0.1:d and bools[0][0] and colors[1][0] == Color.Blue and
        flags[0][0] == (Flags.Read | Flags.Write) and points[0][0].x == 42 and before.x == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedArrays_OnlyOuterRootKeepsChildrenAliveAcrossGcAndCapturedForeach()
    {
        const string source = """
function make(): int[][][] { return [[[20]], [[22]]]; }
function main(args: string[]): int {
    var values = make(); GC.require(); GC.finish(); var alias = values;
    var count = 0; var total = 0;
    foreach (middle in values) {
        foreach (row in middle) { total = total + row[0]; }
        if (count == 0) { values = [[[0]]]; alias[1] = [[22]]; }
        count = count + 1; GC.require(); GC.finish();
    }
    if (count == 2 and values[0][0][0] == 0) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NestedArrays_ClassAsyncGetterWritesPreserveCommittedAndCurrentHandles()
    {
        const string source = """
class Box {
    field values: int[][];
    construct(values: int[][]) { this.values = values; }
    public property First: int { get { return this.values[0][0]; } }
    public property Values: int[][] { get { return this.values; } }
    public async method update(query: Box): void {
        this.values[0] = [42]; print(query.First); print(this.values[0][0]);
    }
}
function main(args: string[]): int {
    var values = [[1]]; var old = values[0]; var box = new Box(values);
    with (box) { .Values[0][0] = 2; } old[0] = 1;
    box.update(box); GC.require(); GC.finish(); tick();
    if (box.First == 42 and values[0][0] == 42 and old[0] == 1) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void NestedArrays_RejectRankBaseNominalNullAndQueryMutation()
    {
        const string declarations = "enum Color { Red } enum Other { Red } struct Point { field x: int; } struct OtherPoint { field x: int; }";
        string[] statements =
        [
            "let values: int[][] = [[true]];", "let values: int[][] = [1];", "let values: int[][][] = [[1]];",
            "var values = [[1], [true]];", "var values = [[1]]; values[0] = [true];",
            "var values = [[1]]; values[0] = 1;",
            "let values: Color[][] = [[Other.Red]];", "let values: Point[][] = [[OtherPoint { x: 1 }]];",
            "let values: byte[][] = [[256]];", "var values = [[]];", "let values: string[][] = [[1]];",
            "foreach (row in [[1]]) { let wrong: bool[] = row; }"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<InvalidCastException>(() => Run("function main(args:string[]):int {var values=[[1]];values[0][0]=[1];return 0;}"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { var values = [[1]]; return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("class Box { field values: int[][]; construct(values: int[][]) { this.values = values; } public property First: int { get { this.values[0][0] = 42; return 0; } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<VmException>(() => Run("function mutate(values: int[][]): int { values[0] = [42]; return 0; } class Box { field values: int[][]; construct(values: int[][]) { this.values = values; } public property First: int { get { return mutate(this.values); } } } function main(args: string[]): int { var box = new Box([[1]]); return box.First; }"));
    }

    [Test]
    public void NestedArrays_RuntimeValidatesLiveExactChildDescriptorsAndReferenceEdges()
    {
        var vm = CreateVm(); var first = CreateArray(vm, "int[]", AloeValue.FromInt(1));
        var wrong = CreateArray(vm, "bool[]", AloeValue.FromBool(true));
        foreach (var value in new[] { AloeValue.Null, AloeValue.FromInt(1), wrong, AloeValue.FromObject(long.MaxValue) })
        {
            vm.Push(value); vm.Push(AloeValue.FromString("int[][]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        }
        var outer = CreateArray(vm, "int[][]", first, first); vm.Heap.SetExternalReferenceCount(outer.AsObjectId, 1);
        Assert.That(vm.Heap.GetEntry(outer.AsObjectId).OutgoingReferences[first.AsObjectId], Is.EqualTo(2));
        vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(first.AsObjectId), Is.True);
        foreach (var value in new[] { wrong, AloeValue.Null, outer })
        {
            vm.Push(outer); vm.Push(AloeValue.FromInt(0)); vm.Push(value);
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArraySet));
        }
        var second = CreateArray(vm, "int[]", AloeValue.FromInt(42));
        vm.Push(outer); vm.Push(AloeValue.FromInt(0)); vm.Push(second); vm.InvokeSyscall(EnumSyscall.ArraySet);
        Assert.That(vm.Heap.GetEntry(outer.AsObjectId).OutgoingReferences[first.AsObjectId], Is.EqualTo(1));
        vm.Push(outer); vm.Push(AloeValue.FromInt(1)); vm.Push(second); vm.InvokeSyscall(EnumSyscall.ArraySet);
        Assert.That(vm.Heap.GetEntry(outer.AsObjectId).OutgoingReferences.ContainsKey(first.AsObjectId), Is.False);
        vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(first.AsObjectId), Is.False);
        Assert.That(vm.Heap.ContainsObject(second.AsObjectId), Is.True);
    }

    [Test]
    public void NestedArrays_VolatileReplacementRetainsBothChildrenUntilCommitAndGuardsReads()
    {
        var vm = CreateVm();
        var oldRow = CreateArray(vm, "int[]", AloeValue.FromInt(1));
        var outer = CreateArray(vm, "int[][]", oldRow);
        vm.Heap.SetExternalReferenceCount(outer.AsObjectId, 1);
        var newRow = CreateArray(vm, "int[]", AloeValue.FromInt(42));
        vm.Heap.SetVolatileField(outer.AsObjectId, 0, newRow);
        vm.RequireGc(); vm.FinishGc();
        Assert.That(vm.Heap.ContainsObject(oldRow.AsObjectId), Is.True);
        Assert.That(vm.Heap.ContainsObject(newRow.AsObjectId), Is.True);
        Assert.That(vm.Heap.GetCommittedField(outer.AsObjectId, 0).AsObjectId, Is.EqualTo(oldRow.AsObjectId));
        Assert.That(vm.Heap.GetField(outer.AsObjectId, 0).AsObjectId, Is.EqualTo(newRow.AsObjectId));
        vm.Heap.CommitVolatileFields(); vm.RequireGc(); vm.FinishGc();
        Assert.That(vm.Heap.ContainsObject(oldRow.AsObjectId), Is.False);
        Assert.That(vm.Heap.ContainsObject(newRow.AsObjectId), Is.True);
        var wrongRow = CreateArray(vm, "bool[]", AloeValue.FromBool(true));
        vm.Heap.SetField(outer.AsObjectId, 0, wrongRow);
        vm.Push(outer); vm.Push(AloeValue.FromInt(0));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayGet));
    }

    private static AloeValue CreateArray(AloeVm vm, string name, params AloeValue[] values)
    {
        foreach (var value in values) vm.Push(value);
        vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(values.Length)); vm.InvokeSyscall(EnumSyscall.ArrayCreate);
        return vm.Pop();
    }
    private static AloeVm CreateVm(Module? module = null)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module ?? new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }
    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
