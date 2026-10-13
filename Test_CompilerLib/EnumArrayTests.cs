using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class EnumArrayTests
{
    [Test]
    public void EnumArrays_DocumentedTypesWritesAndForeach()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Feature { Read, Write }
function identity(values: Color[]): Color[] { return values; }
function main(args: string[]): int {
    let colors: Color[] = [Color.Red, Color.Blue];
    identity(colors)[0] = Color.Blue;
    var features = [Feature.Read, Feature.Read | Feature.Write];
    features[0] = features[0] | Feature.Write;
    var count = 0;
    foreach (color in colors) { if (color == Color.Blue) { count = count + 1; } }
    if (count == 2 and features[0] == features[1]) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void EnumArrays_EmptyContextsBoundariesAliasesAndGc()
    {
        const string source = """
enum Number { Low = -2147483648, High = 2147483647 }
bitfield enum Flags { One = 1, High = 1 << 31, All = -1 }
function values(): Number[] { return [Number.Low, Number.High]; }
function main(args: string[]): int {
    let empty: Number[] = []; empty = [Number.Low];
    var alias = empty; alias[0] = Number.High;
    let emptyFlags: Flags[] = []; foreach (flag in emptyFlags) { return 0; }
    var flags = [Flags.One, Flags.High, Flags.All];
    flags[0] = flags[0] << 31;
    GC.require(); GC.finish();
    var count = 0; foreach (number in values()) { if (number == Number.Low or number == Number.High) { count = count + 1; } }
    if (empty[0] == Number.High and flags[0] == Flags.High and flags[2] == Flags.All and count == 2) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void EnumArrays_ClassAsyncGetterWithAndTickPreserveTypesAndSharedState()
    {
        const string source = """
enum Color { Red, Blue }
class Box {
    field values: Color[];
    construct(values: Color[]) { this.values = values; }
    public property First: Color { get { return this.values[0]; } }
    public property Values: Color[] { get { return this.values; } }
    public async method update(values: Color[], query: Box): void {
        values[0] = Color.Blue;
        if (query.First == Color.Red) { print(1); }
        if (values[0] == Color.Blue) { print(2); }
    }
}
function main(args: string[]): int {
    var colors = [Color.Red]; var box = new Box(colors);
    with (box) { .Values[0] = Color.Blue; }
    colors[0] = Color.Red; box.update(colors, box);
    GC.require(); GC.finish(); tick();
    if (box.First == Color.Blue and colors[0] == Color.Blue) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n"));
    }

    [Test]
    public void EnumArrays_RejectOtherEnumsIntegersAndUnsupportedShapes()
    {
        const string declarations = "enum Color { Red, Blue } enum Other { Red, Blue } bitfield enum Flags { One }";
        string[] statements =
        [
            "var values = [Color.Red, Other.Red];", "var values = [Color.Red, 0];",
            "let values: Color[] = [Other.Red];", "let values: Color[] = [0];",
            "var values = [Color.Red]; values[0] = Other.Red;", "var values = [Color.Red]; values[0] = 0;",
            "var values = [Color.Red]; let other: Other[] = values;", "var values = [Color.Red]; let ints: int[] = values;",
            "var values = [Color.Red]; let other: Other = values[0];", "var values = [Color.Red]; let number: int = values[0];",
            "foreach (value in [Color.Red]) { let other: Other = value; }",
            "let values: Color[][] = [[Other.Red]];", "var values = [Color.Red]; values[0] = Flags.One;",
            "var values = [Flags.One]; values[0] = Color.Red;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function take(values: Other[]): int { return 0; } function main(args: string[]): int { return take([Color.Red]); }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function wrong(): Other[] { return [Color.Red]; } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations + " function main(args: string[]): int { var values = [Color.Red]; return 0; }"));
    }

    [Test]
    public void EnumArrays_GetterWritesRemainForbidden()
    {
        const string header = "enum Color { Red, Blue }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(header + " class Box { field values: Color[]; construct(values: Color[]) { this.values = values; } public property First: Color { get { this.values[0] = Color.Blue; return Color.Red; } } } function main(args: string[]): int { return 0; }"));
        const string source = """
enum Color { Red, Blue }
function change(values: Color[]): Color { values[0] = Color.Blue; return values[0]; }
class Box {
    field values: Color[];
    construct(values: Color[]) { this.values = values; }
    public property First: Color { get { return change(this.values); } }
}
function main(args: string[]): int {
    var box = new Box([Color.Red]);
    if (box.First == Color.Blue) { return 42; }
    return 0;
}
""";
        Assert.Throws<VmException>(() => Run(source));
    }

    [Test]
    public void EnumArrays_RuntimeValidatesDescriptorKindRangeAndBounds()
    {
        foreach (var name in new[] { "enum:[]", "enum:Color[[]]", "enum:Color", "enum:1Color[]", "enum:Color.Other[]" })
        {
            var vm = CreateVm(); vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(0));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate), name);
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        foreach (var value in new[] { AloeValue.FromBool(true), AloeValue.FromInt((long)int.MaxValue + 1), AloeValue.FromInt((long)int.MinValue - 1) })
        {
            var vm = CreateVm(); vm.Push(value); vm.Push(AloeValue.FromString("enum:Color[]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        var valid = CreateVm(); valid.Push(AloeValue.FromInt(int.MinValue)); valid.Push(AloeValue.FromString("enum:Color[]")); valid.Push(AloeValue.FromInt(1));
        valid.InvokeSyscall(EnumSyscall.ArrayCreate); var array = valid.Pop();
        Assert.That(valid.Heap.GetEntry(array.AsObjectId).TypeName, Is.EqualTo("enum:Color[]"));
        foreach (var value in new[] { AloeValue.FromBool(true), AloeValue.FromInt((long)int.MaxValue + 1) })
        {
            valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.Push(value);
            Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArraySet));
        }
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.InvokeSyscall(EnumSyscall.ArrayGet);
        Assert.That(valid.Pop().AsInt, Is.EqualTo(int.MinValue));
        valid.Push(array); valid.InvokeSyscall(EnumSyscall.ArrayLength);
        Assert.That(valid.Pop().AsInt, Is.EqualTo(1));
        valid.Push(array); valid.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArrayGet));
    }

    private static AloeVm CreateVm(Module? module = null)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module ?? new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new StringBuilder(); var vm = CreateVm(module); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
