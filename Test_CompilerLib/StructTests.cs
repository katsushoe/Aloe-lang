using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class StructTests
{
    [Test]
    public void StructMethods_ReadFieldsCallOtherMethodsAndReturnValueCopies()
    {
        const string source = """
struct Point {
    field x: int; field y: int;
    method sum(offset: int): int { return this.x + this.y + offset; }
    method total(): int { return sum(0); }
    method shifted(delta: int): Point { return Point { x: this.x + delta, y: this.y + delta }; }
    method noop(): void { return; }
}
function main(args: string[]): int {
    var point = Point { x: 10, y: 20 };
    var moved = point.shifted(2);
    point.noop();
    if (point.sum(10) == 40 and point.total() == 30 and moved.x == 12 and moved.y == 22 and point.x == 10) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructMethods_RejectMutationInvalidCallsAndUnsupportedTypes()
    {
        string[] sources =
        [
            "struct S { field x: int; method set(value: int): void { this.x = value; } }",
            "struct S { field x: int; method set(value: int): void { this.x.y = value; } }",
            "struct S { method same(): int { return 1; } method same(): int { return 2; } }",
            "struct S { method bad(value: string): int { return 1; } }",
            "struct S { method value(): int { return 1; } } function main(args: string[]): int { var s = S { }; s.value(true); return 0; }",
            "struct S { method value(): int { return 1; } } function main(args: string[]): int { var s = S { }; s.missing(); return 0; }",
            "struct S { method value(): int { return 1; } } function main(args: string[]): int { var s = S { }; s.value(); return 0; }",
            "struct S { method value(): void { return; } } function main(args: string[]): int { var s = S { }; return s.value(); }"
        ];
        foreach (var source in sources)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source + (source.Contains("function main", StringComparison.Ordinal) ? "" : " function main(args: string[]): int { return 0; }")), source);
    }

    [Test]
    public void StructProperties_GettersReadFieldsAndReturnIndependentStructCopies()
    {
        const string source = """
struct Point {
    field x: int; field y: int;
    public property Sum: int { get { return this.x + this.y; } }
    public property Shifted: Point { get { return Point { x: this.x + 1, y: this.y + 1 }; } }
    method total(): int { return this.Sum + this.Sum; }
}
function main(args: string[]): int {
    var point = Point { x: 10, y: 20 };
    var copy = point.Shifted; copy.x = 100;
    if (point.Sum == 30 and point.total() == 60 and point.x == 10 and copy.x == 100 and point.Shifted.x == 11) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructProperties_RejectSettersInvalidTypesAndDuplicateMembers()
    {
        string[] sources =
        [
            "struct S { public property Value: int { get { return 1; } set { } } }",
            "struct S { public property Value: string { get { return \"x\"; } } }",
            "struct S { field Value: int; public property Value: int { get { return 1; } } }",
            "struct S { public property Value: int { get { return 1; } } method Value(): int { return 2; } }",
            "struct S { public property Value: int { get { return 1; } } } function main(args: string[]): int { var s = S { }; s.Value = 2; return 0; }"
        ];
        foreach (var source in sources)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source + (source.Contains("function main", StringComparison.Ordinal) ? "" : " function main(args: string[]): int { return 0; }")), source);
    }

    [Test]
    public void StructValues_LocalAssignmentsAndFunctionBoundariesAreIndependent()
    {
        const string source = """
struct Point { field x: int; field y: int; }
function mutate(point: Point): void { point.x = 100; }
function changed(point: Point): Point { point.x = 10; return point; }
function main(args: string[]): int {
    var first = Point { y: 2, x: 1 };
    var second = first; second.y = 20;
    mutate(first); let third: Point = changed(first);
    first.x = 3;
    if (first.x == 3 and first.y == 2 and second.x == 1 and second.y == 20 and third.x == 10 and third.y == 2) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructValues_NestedAssignmentsPreserveUnchangedCopies()
    {
        const string source = """
struct Pair { field left: Point; field right: Point; }
struct Point { field x: int; field y: int; }
function echo(pair: Pair): Pair { return pair; }
function main(args: string[]): int {
    var first = Pair { left: Point { x: 1, y: 2 }, right: Point { x: 3, y: 4 } };
    var second = echo(first); second.left.x = 10; first.left.y = 20;
    var leaf = second.left; leaf.y = 30;
    second.right = leaf; leaf.x = 40;
    GC.require(); GC.finish();
    if (first.left.x == 1 and first.left.y == 20 and second.left.x == 10 and second.left.y == 2 and
        first.right.x == 3 and second.right.x == 10 and second.right.y == 30 and leaf.x == 40) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructWith_LocalFieldAccessAndNestedWritesPreserveValueCopies()
    {
        const string source = """
struct Point { field x: int; field y: int; }
struct Segment { field start: Point; field end: Point; }
function main(args: string[]): int {
    var segment = Segment { start: Point { x: 1, y: 2 }, end: Point { x: 3, y: 4 } };
    var copy = segment;
    with (segment) {
        let startX: int = .start.x;
        .start.y = startX + .end.y;
        .end = Point { x: .start.y, y: 9 };
    }
    if (segment.start.x == 1 and segment.start.y == 5 and segment.end.x == 5 and segment.end.y == 9 and
        copy.start.x == 1 and copy.start.y == 2 and copy.end.x == 3 and copy.end.y == 4) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructWith_MultipleTargetsResolveMembersAndRejectAmbiguity()
    {
        const string source = """
struct Left { field left: int; }
struct Right { field right: int; }
function main(args: string[]): int {
    var first = Left { left: 0 }; var second = Right { right: 0 };
    with (first, second) { .left = 20; .right = .left + 22; }
    if (first.left == 20 and second.right == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "struct S { field x: int; } function main(args: string[]): int { var a = S { x: 0 }; var b = S { x: 0 }; with (a, b) { .x = 1; } return 0; }"));
    }

    [Test]
    public void StructValues_InitializersEvaluateOnceInWrittenOrderAndSupportPrimitiveFields()
    {
        const string source = """
sealed struct Empty { }
struct Values { field x: int; field y: int; field b: byte; field c: char; field f: float; field d: decimal; field flag: bool; field empty: Empty; }
function mark(value: int): int { print(value); return value; }
function main(args: string[]): int {
    var value = Values { y: mark(2), x: mark(1), b: 255, c: 'a', f: 1.5, d: 0.1:d, flag: true, empty: Empty { } };
    value.b = 0; value.c = 'z'; value.d = 0.2:d;
    if (value.x == 1 and value.y == 2 and value.b == 0 and value.c == 'z' and value.f == 1.5 and value.d == 0.2:d and value.flag) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("2\n1\n"));
    }

    [Test]
    public void StructValues_ClassFieldsStoreIndependentSnapshots()
    {
        const string source = """
struct Point { field x: int; }
class Box {
    field point: Point;
    construct(point: Point) { this.point = point; }
    public property Point: Point { get { return this.point; } }
    public async method set(point: Point): void { this.point = point; }
}
function main(args: string[]): int {
    var point = Point { x: 1 }; var box = new Box(point);
    point.x = 2; var copy = box.Point; copy.x = 3;
    box.set(point); point.x = 4; tick();
    var stored = box.Point;
    if (stored.x == 2 and copy.x == 3 and point.x == 4) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructValues_RejectInvalidDeclarationsLayoutsInitializersAndTypes()
    {
        string[] declarations =
        [
            "struct S { field x: string; }", "struct S { field x: object; }",
            "struct S { field x: pipe<int>; }", "struct S { field x: S; }",
            "struct S { field t: T; } struct T { field s: S; }", "struct S extends Other { }",
            "struct S { field x: int; field x: bool; }", "struct S { } struct S { }",
            "enum S { A } struct S { }", "struct S { } class S { }"
        ];
        foreach (var declaration in declarations)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declaration + " function main(args: string[]): int { return 0; }"), declaration);
        const string types = "struct S { field x: int; } struct T { field x: int; } ";
        string[] statements =
        [
            "var s = S { };", "var s = S { x: 1, x: 2 };", "var s = S { y: 1 };",
            "var s = S { x: true };", "let s: S = T { x: 1 };", "var s = S { x: 1 }; s = T { x: 2 };",
            "var s = S { x: 1 }; s.x = true;", "var s = S { x: 1 }; s.y = 2;",
            "var s = S { x: 1 }; print(s == s);", "var s = S { x: 1 }; print(s + s);",
            "var s = S { x: 1 }; print(s.x.y);", "var s = S { x: 1 }; with (S { x: 2 }) { .x = 3; }",
            "var s = S { x: 1 }; with (s) { .missing = 2; }", "with (S) { .x = 2; }"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(types + "function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(types + "function main(args: string[]): int { return 0; }"));
    }

    [Test]
    public void StructValues_ImmutableStorageAndRuntimeValidationEnforceValueSemantics()
    {
        var fields = new[] { AloeValue.FromInt(1) };
        var original = new AloeStructValue("Point", fields);
        fields[0] = AloeValue.FromInt(99);
        var changed = original.WithField(0, AloeValue.FromInt(2));
        Assert.That(original.GetField(0).AsInt, Is.EqualTo(1));
        Assert.That(changed.GetField(0).AsInt, Is.EqualTo(2));
        Assert.Throws<VmException>(() => original.WithField(0, AloeValue.FromBool(true)));
        Assert.Throws<VmException>(() => original.GetField(1));
        foreach (var value in new[] { AloeValue.FromString("ref"), AloeValue.FromObject(1), AloeValue.Null, AloeValue.FromInt(2147483648) })
            Assert.Throws<VmException>(() => new AloeStructValue("Invalid", new[] { value }));
        foreach (var opcode in new[] { EnumOpcode.StructCreate, EnumOpcode.StructGet, EnumOpcode.StructSet })
        {
            var module = new Module(Array.Empty<AloeValue>(), new[] { new Instruction(opcode, -1), new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0) }, 0);
            Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module))));
        }
    }

    [Test]
    public void StructOpcodes_RejectMalformedStacksNamesFieldsAndReplacements()
    {
        Instruction P(int index) => new(EnumOpcode.PushConst, index);
        var constants = new[] { AloeValue.FromString("S"), AloeValue.FromInt(1), AloeValue.FromBool(true), AloeValue.FromString(""), AloeValue.FromInt(2147483648) };
        Instruction[][] cases =
        [
            [P(0), new(EnumOpcode.StructCreate, int.MaxValue)],
            [P(3), new(EnumOpcode.StructCreate, 0)],
            [P(1), new(EnumOpcode.StructCreate, 0)],
            [P(0), P(4), new(EnumOpcode.StructCreate, 1)],
            [new(EnumOpcode.StructGet, 0)], [P(1), new(EnumOpcode.StructGet, 0)],
            [P(0), P(1), new(EnumOpcode.StructCreate, 1), new(EnumOpcode.StructGet, 1)],
            [P(0), P(1), new(EnumOpcode.StructCreate, 1), new(EnumOpcode.StructSet, 0)],
            [P(0), P(1), new(EnumOpcode.StructCreate, 1), P(2), new(EnumOpcode.StructSet, 0)],
            [P(0), P(1), new(EnumOpcode.StructCreate, 1), P(1), new(EnumOpcode.StructSet, 1)]
        ];
        foreach (var instructions in cases)
        {
            var module = new Module(constants, instructions.Append(new Instruction(EnumOpcode.Return)).ToArray(), new[] { new FunctionInfo("main", 0, 0, 0) }, 0);
            var settings = new AloeVmSettings();
            settings.Logging.FileEnabled = false;
            settings.Logging.ConsoleEnabled = false;
            var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
            Assert.Throws<VmException>(() => vm.RunFromEntryPoint());
        }
    }

    [Test]
    public void StructConstructors_NewEvaluatesArgumentsOnceAndPreservesCopies()
    {
        const string source = """
struct Pair {
    construct(first: int, second: int) {
        let offset: int = first + 1;
        this.left = new Point(offset);
        this.right = new Point(second + this.left.x);
    }
    field left: Point; field right: Point;
}
struct Point { field x: int; construct(x: int) { this.x = x; this.x = this.x + 1; } }
function mark(x: int): int { print(x); return x; }
function make(x: int): Pair { return new Pair(x, 2); }
function main(args: string[]): int {
    var first = new Pair(mark(1), mark(2)); var copy = first;
    copy.left.x = 20; var other = make(3);
    GC.require(); GC.finish();
    if (first.left.x == 3 and first.right.x == 6 and copy.left.x == 20 and other.left.x == 5) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n"));
    }

    [Test]
    public void StructConstructors_NewSupportsPrimitiveFieldsAndClassAsyncBoundaries()
    {
        const string source = """
sealed struct Empty { }
struct Data {
    field b: byte; field c: char; field f: float; field d: decimal; field ok: bool; field item: Point;
    construct(b: byte, c: char, f: float, d: decimal, ok: bool, item: Point) {
        item.x = item.x + 1;
        this.b = b; this.c = c; this.f = f; this.d = d; this.ok = ok; this.item = item;
    }
}
struct Point { field x: int; construct(x: int) { this.x = x; } }
class Box {
    field point: Point;
    construct(point: Point) { this.point = point; }
    public property Value: Point { get { return this.point; } }
    public async method set(point: Point): void { this.point = point; }
}
function main(args: string[]): int {
    var empty = new Empty(); var point = new Point(1);
    var value = new Data(255, 'z', 1.5, 0.2:d, true, point);
    var box = new Box(new Point(3)); box.set(new Point(4)); tick(); var stored = box.Value;
    if (point.x == 1 and value.item.x == 2 and value.b == 255 and value.c == 'z' and
        value.f == 1.5 and value.d == 0.2:d and value.ok and stored.x == 4) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructConstructors_EarlyReturnsAndNestedThisAssignmentsPreserveDefiniteInitialization()
    {
        const string source = """
struct Point { field value: int; construct(value: int) { this.value = value; } }
struct Result {
    field point: Point; field code: int;
    construct(point: Point, code: int) {
        this.point = point;
        if (code == 0) {
            this.code = code;
            return;
        } else {
            this.point.value = this.point.value + 1;
            this.code = code;
            return;
        }
    }
}
function main(args: string[]): int {
    var early = new Result(new Point(10), 0);
    var nested = new Result(new Point(20), 1);
    if (early.point.value == 10 and early.code == 0 and nested.point.value == 21 and nested.code == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StructConstructors_InvalidInitializationAndArgumentsAreRejected()
    {
        string[] declarations =
        [
            "struct S { field x: int; construct() { } }",
            "struct S { construct() { } construct(x: int) { } }",
            "struct S { field x: int; construct() { this.x = this.x; } }",
            "struct S { field x: int; construct() { this.y = 1; } }",
            "struct S { field x: int; construct() { this.x = true; } }",
            "struct S { field x: int; construct() { if (true) { this.x = 1; } } }",
            "struct S { field x: int; construct() { while (false) { this.x = 1; } } }",
            "struct S { field x: int; field y: int; construct() { this.x = 1; return; } }",
            "struct S { field x: int; construct(x: int) { return x; this.x = x; } }",
            "struct Point { field x: int; construct(x: int) { this.x = x; } } struct S { field child: Point; construct() { this.child.x = 1; this.child = new Point(2); } }",
            "struct S { construct(x: int, x: int) { } }",
            "struct S { construct(x: string) { } }",
            "struct S { construct(x: int,) { } }"
        ];
        foreach (var declaration in declarations)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declaration + " function main(args: string[]): int { return 0; }"), declaration);
        const string types = "struct S { field x: byte; construct(x: byte) { this.x = x; } } struct T { field s: S; construct(s: S) { this.s = s; } } struct U { } ";
        foreach (var expression in new[] { "new S()", "new S(1, 2)", "new S(true)", "new S(256)", "new S(1,)", "new T(new U())", "new U(1)" })
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(types + " function main(args: string[]): int { var value = " + expression + "; return 0; }"), expression);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("struct S { field x: int; } function main(args: string[]): int { var s = new S(); return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("struct S { construct() { } } function main(args: string[]): int { var s = new S(); return 0; }"));
        Assert.Throws<VmException>(() => Run("struct S { field x: int; construct(x: int) { this.x = x; } } function main(args: string[]): int { var s = new S(2147483648); return 0; }"));
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var output = new StringBuilder();
        var vm = new AloeVm(module, settings) { OutputWriter = line => output.AppendLine(line) };
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
