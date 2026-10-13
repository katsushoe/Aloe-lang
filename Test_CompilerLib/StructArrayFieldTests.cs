using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class StructArrayFieldTests
{
    [Test]
    public void StructArrayFields_DocumentedNestedWritebackPreservesSnapshotsAndAliases()
    {
        const string source = """
struct Point { field x: int; field y: int; }
struct Item { field point: Point; }
function identity(values: Item[]): Item[] { return values; }
function main(args: string[]): int {
    var values = [Item { point: Point { x: 1, y: 22 } }];
    var alias = values; var before = values[0];
    identity(values)[0].point.x = 20;
    if (before.point.x == 1 and alias[0].point.y == 22) { return values[0].point.x + values[0].point.y; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrayFields_PreserveByteEnumAndNestedStructTypes()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Flags { Read, Write }
struct Point { field x: int; }
struct Item { field number: byte; field color: Color; field flags: Flags; field point: Point; }
function main(args: string[]): int {
    var values = [Item { number: 1, color: Color.Red, flags: Flags.Read, point: Point { x: 1 } }];
    var before = values[0];
    values[0].number = 42; values[0].color = Color.Blue;
    values[0].flags = values[0].flags | Flags.Write;
    values[0].point = Point { x: 42 };
    if (values[0].number == 42 and values[0].color == Color.Blue and values[0].flags == (Flags.Read | Flags.Write) and
        values[0].point.x == 42 and before.number == 1 and before.color == Color.Red and before.point.x == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrayFields_TargetIndexRhsEvaluateOnceAndSurviveGc()
    {
        const string source = """
struct Point { field x: int; field y: int; }
function target(values: Point[]): Point[] { print(1); return values; }
function index(): int { print(2); return 0; }
function value(): int { print(3); return 42; }
function main(args: string[]): int {
    var values = [Point { x: 1, y: 2 }];
    target(values)[index()].x = value();
    ([Point { x: 1, y: 2 }])[0].y = 42;
    if (values[0].x == 42 and values[0].y == 2) { return 42; }
    return 0;
}
""";
        var collections = 0;
        var result = Run(source, (vm, line) => { if (line == "2" || line == "3") { vm.RequireGc(); vm.FinishGc(); collections++; } });
        Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo("1\n2\n3\n"));
        Assert.That(collections, Is.EqualTo(2));
    }

    [Test]
    public void StructArrayFields_RebuildCapturedSnapshotAfterEffectfulRhs()
    {
        const string source = """
struct Point { field x: int; field y: int; }
function change(values: Point[]): int { values[0] = Point { x: 100, y: 200 }; return 42; }
function main(args: string[]): int {
    var values = [Point { x: 1, y: 2 }]; var before = values[0];
    values[0].x = change(values);
    if (values[0].x == 42 and values[0].y == 2 and before.x == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructArrayFields_ThisAndWithWritesPreserveTickCommittedQueries()
    {
        const string source = """
struct Point { field x: int; field y: int; }
class Box {
    field values: Point[];
    construct(values: Point[]) { this.values = values; }
    public property First: Point { get { return this.values[0]; } }
    public property Values: Point[] { get { return this.values; } }
    public async method update(query: Box): void {
        this.values[0].x = 20; with (this) { .Values[0].y = 22; }
        var committed = query.First; print(committed.x); print(this.values[0].x);
    }
}
function main(args: string[]): int {
    var values = [Point { x: 1, y: 2 }]; var before = values[0]; var box = new Box(values);
    box.update(box); GC.require(); GC.finish(); tick();
    if (values[0].y == 22 and before.x == 1) { return values[0].x + values[0].y; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n20\n"));
    }

    [Test]
    public void StructArrayFields_RejectWrongTypesReadonlyPathsGetterWritesAndBoundsBeforeRhs()
    {
        const string declarations = "enum Color { Red } enum Other { Red } struct Point { field x: int; public property X: int { get { return this.x; } } } struct Item { field point: Point; field color: Color; field number: byte; }";
        string[] statements =
        [
            "var values = [Point { x: 1 }]; values[0].x = true;",
            "var values = [Point { x: 1 }]; values[0].X = 2;",
            "var values = [Point { x: 1 }]; values[0].x.missing = 2;",
            "var values = [Item { point: Point { x: 1 }, color: Color.Red, number: 1 }]; values[0].color = Other.Red;",
            "var values = [Item { point: Point { x: 1 }, color: Color.Red, number: 1 }]; values[0].number = 256;",
            "var values = [Item { point: Point { x: 1 }, color: Color.Red, number: 1 }]; values[0].point = 1;",
            "var values = [1]; values[0].x = 2;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("struct Point { field x: int; } class Box { field values: Point[]; construct(values: Point[]) { this.values = values; } public property X: int { get { this.values[0].x = 42; return 0; } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<VmException>(() => Run("struct Point { field x: int; } function mutate(values: Point[]): int { values[0].x = 42; return 0; } class Box { field values: Point[]; construct(values: Point[]) { this.values = values; } public property X: int { get { return mutate(this.values); } } } function main(args: string[]): int { var box = new Box([Point { x: 1 }]); return box.X; }"));
        var output = new StringBuilder();
        Assert.Throws<VmException>(() => Run("struct Point { field x: int; } function value(): int { print(99); return 42; } function main(args: string[]): int { var values = [Point { x: 1 }]; values[1].x = value(); return 0; }", (_, line) => output.AppendLine(line)));
        Assert.That(output.Length, Is.EqualTo(0));
    }

    private static (long ExitCode, string Output) Run(string source, Action<AloeVm, string>? onOutput = null)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings); var output = new StringBuilder();
        vm.OutputWriter = line => { output.AppendLine(line); onOutput?.Invoke(vm, line); };
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
