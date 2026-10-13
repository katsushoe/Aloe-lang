using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class StructArrayTests
{
    [Test]
    public void StructArrays_DocumentedCopiesElementReplacementAndForeach()
    {
        const string source = """
struct Point {
    field x: int; field y: int;
    method sum(): int { return this.x + this.y; }
    public property Sum: int { get { return this.x + this.y; } }
}
function identity(values: Point[]): Point[] { return values; }
function main(args: string[]): int {
    var original = Point { x: 1, y: 2 };
    let points: Point[] = [original];
    var copy = points[0]; copy.x = 20;
    identity(points)[0] = Point { x: 20, y: 22 };
    var total = 0;
    foreach (point in points) { total = total + point.Sum; point.x = 0; }
    if (original.x == 1 and copy.x == 20 and points[0].sum() == 42) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrays_NestedFieldsEnumsConstructorsEmptyContextsAndGc()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Feature { Read, Write }
struct Point {
    field x: int; field y: int;
    construct(x: int, y: int) { this.x = x; this.y = y; }
    public property Copy: Point { get { return Point { x: this.x, y: this.y }; } }
}
struct Item { field point: Point; field color: Color; field features: Feature; }
struct Empty { }
function items(): Item[] { return [Item { point: new Point(20, 22), color: Color.Blue, features: Feature.Read | Feature.Write }]; }
function main(args: string[]): int {
    let values: Item[] = []; foreach (value in values) { return 0; }
    values = items(); var alias = values;
    var snapshot = alias[0]; snapshot.point.x = 0;
    var empty = [Empty { }, Empty { }];
    GC.require(); GC.finish();
    if (values[0].point.Copy.x == 20 and values[0].point.y == 22 and snapshot.point.x == 0 and
        values[0].color == Color.Blue and values[0].features == (Feature.Read | Feature.Write) and empty.Length == 2) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrays_ClassAsyncWithAndTickUseSnapshotsAndSharedArrayHandles()
    {
        const string source = """
struct Point { field x: int; }
class Box {
    field values: Point[];
    construct(values: Point[]) { this.values = values; }
    public property First: Point { get { return this.values[0]; } }
    public property Values: Point[] { get { return this.values; } }
    public async method update(values: Point[], query: Box): void {
        values[0] = Point { x: 42 }; var first = query.First; print(first.x); print(values[0].x);
    }
}
function main(args: string[]): int {
    var values = [Point { x: 1 }]; var snapshot = values[0]; var box = new Box(values);
    with (box) { .Values[0] = Point { x: 2 }; }
    values[0] = snapshot; box.update(values, box);
    GC.require(); GC.finish(); tick();
    var first = box.First;
    if (first.x == 42 and values[0].x == 42 and snapshot.x == 1) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void StructArrays_RejectOtherStructsReferencesShapesAndInvalidFields()
    {
        const string declarations = "struct Point { field x: int; } struct Other { field x: int; }";
        string[] statements =
        [
            "var values = [Point { x: 1 }, Other { x: 1 }];", "var values = [Point { x: 1 }, 1];",
            "let values: Point[] = [Other { x: 1 }];", "let values: Point[] = [1];",
            "var values = [Point { x: 1 }]; values[0] = Other { x: 1 };", "var values = [Point { x: 1 }]; values[0] = 1;",
            "var values = [Point { x: 1 }]; let others: Other[] = values;",
            "var values = [Point { x: 1 }]; let other: Other = values[0];",
            "foreach (point in [Point { x: 1 }]) { let other: Other = point; }",
            "let values: Point[][] = [[Other { x: 1 }]];", "var values = [Point { x: 1 }]; values[0].missing = 2;",
            "var values = [Point { x: 1 }]; values.Length = 2;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function wrong(): Other[] { return [Point { x: 1 }]; } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function wrong(values: Other[]): int { return 0; } function main(args: string[]): int { return wrong([Point { x: 1 }]); }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("struct Bad { field values: int[]; } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations + " function main(args: string[]): int { var values = [Point { x: 1 }]; return 0; }"));
    }

    [Test]
    public void StructArrays_GetterWritesRemainForbidden()
    {
        const string header = "struct Point { field x: int; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(header + " class Box { field values: Point[]; construct(values: Point[]) { this.values = values; } public property First: Point { get { this.values[0] = Point { x: 42 }; return this.values[0]; } } } function main(args: string[]): int { return 0; }"));
        const string source = """
struct Point { field x: int; }
function change(values: Point[]): Point { values[0] = Point { x: 42 }; return values[0]; }
class Box {
    field values: Point[];
    construct(values: Point[]) { this.values = values; }
    public property First: Point { get { return change(this.values); } }
}
function main(args: string[]): int {
    var box = new Box([Point { x: 1 }]); var first = box.First; return first.x;
}
""";
        Assert.Throws<VmException>(() => Run(source));
    }

    [Test]
    public void StructArrays_ForeachCopiesObserveLaterReplacementAndRetainCapturedTarget()
    {
        const string source = """
struct Point { field x: int; }
function main(args: string[]): int {
    var values = [Point { x: 1 }, Point { x: 2 }]; var alias = values;
    var total = 0; var count = 0;
    foreach (point in values) {
        total = total + point.x; point.x = 0;
        if (count == 0) { alias[1] = Point { x: 41 }; values = [Point { x: 0 }]; }
        count = count + 1; GC.require(); GC.finish();
    }
    if (alias[0].x == 1 and alias[1].x == 41 and values.Length == 1 and count == 2) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrays_TargetIndexValueAndLiteralElementsEvaluateOnceInSourceOrder()
    {
        const string source = """
struct Point { field x: int; }
function value(number: int): Point { print(number); return Point { x: number }; }
function target(): Point[] { print(1); return [value(2), value(3)]; }
function index(): int { print(4); return 0; }
function main(args: string[]): int {
    target()[index()] = value(5);
    var values = [value(20), value(22)];
    return values[0].x + values[1].x;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n3\n4\n5\n20\n22\n"));
    }

    [Test]
    public void StructArrays_RuntimeRejectsWrongNamesKindsMalformedDescriptorsAndBounds()
    {
        var point = AloeValue.FromStruct(new AloeStructValue("Point", new[] { AloeValue.FromInt(1) }));
        var other = AloeValue.FromStruct(new AloeStructValue("Other", new[] { AloeValue.FromInt(1) }));
        foreach (var name in new[] { "struct:[]", "struct:Point[[]]", "struct:Point", "struct:1Point[]", "struct:Point.Other[]" })
        {
            var vm = CreateVm(); vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(0));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate), name);
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        foreach (var value in new[] { other, AloeValue.FromInt(1), AloeValue.Null })
        {
            var vm = CreateVm(); vm.Push(value); vm.Push(AloeValue.FromString("struct:Point[]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        var valid = CreateVm(); valid.Push(point); valid.Push(AloeValue.FromString("struct:Point[]")); valid.Push(AloeValue.FromInt(1));
        valid.InvokeSyscall(EnumSyscall.ArrayCreate); var array = valid.Pop();
        Assert.That(valid.Heap.GetEntry(array.AsObjectId).TypeName, Is.EqualTo("struct:Point[]"));
        foreach (var value in new[] { other, AloeValue.FromInt(1) })
        {
            valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.Push(value);
            Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArraySet));
        }
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.InvokeSyscall(EnumSyscall.ArrayGet);
        var copy = valid.Pop().AsStruct.WithField(0, AloeValue.FromInt(42));
        Assert.That(copy.GetField(0).AsInt, Is.EqualTo(42));
        Assert.That(point.AsStruct.GetField(0).AsInt, Is.EqualTo(1));
        valid.Push(array); valid.Push(AloeValue.FromInt(1)); Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArrayGet));
        valid.Heap.SetField(array.AsObjectId, 0, other);
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArrayGet));
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
