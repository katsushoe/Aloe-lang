using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class StructWithMemberTests
{
    [Test]
    public void StructWith_DocumentedExampleExecutesAfterAloeBcRoundTrip()
    {
        const string source = """
struct Point {
    field x: int; field y: int;
    public property Sum: int { get { return this.x + this.y; } }
    method shifted(delta: int): Point {
        return Point { x: this.x + delta, y: this.y + delta };
    }
    method report(value: int): void { print(value); }
}
function main(args: string[]): int {
    var point = Point { x: 10, y: 20 };
    with (point) {
        .x = .x + 1;
        .report(.Sum);
        return .shifted(1).Sum;
    }
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(33));
        Assert.That(result.Output, Is.EqualTo("31\n"));
    }

    [Test]
    public void StructWith_MethodsAndGettersObserveWritesAndKeepReturnedCopiesIndependent()
    {
        const string source = """
struct Point {
    field x: int; field y: int;
    public property Sum: int { get { return this.x + this.y; } }
    public property Copy: Point { get { print(3); return Point { x: this.x, y: this.y }; } }
    method shifted(first: byte, second: int): Point {
        return Point { x: this.x + first, y: this.y + second };
    }
    method report(value: int): void { print(value); }
    method reportPair(first: int, second: int): void { print(this.x + first + second); }
}
struct Pair { field point: Point; }
function mark(value: int): int { print(value); return value; }
function main(args: string[]): int {
    var point = Point { x: 10, y: 20 };
    var original = point;
    var pair = Pair { point: point };
    with (point, pair) {
        .x = .x + 1;
        var copy = .Copy.shifted(1, mark(2));
        copy.x = 99;
        .report(.shifted(2, 3).Sum);
        .reportPair(mark(4), mark(5));
        if (.Sum != 31 or .shifted(1, 2).x != 12 or .point.Sum != 30 or
            .point.shifted(1, 1).Sum != 32 or copy.x != 99 or copy.y != 22) { return 0; }
    }
    GC.require(); GC.finish();
    if (point.x == 11 and point.y == 20 and original.x == 10 and pair.point.x == 10) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("3\n2\n36\n4\n5\n20\n"));
    }

    [Test]
    public void StructWith_MixedTargetsAndNestedScopesResolveAndRestoreMembers()
    {
        const string source = """
enum Answer { Extra = 2 }
class Box { public property Count: int { get { return 5; } } }
struct Left {
    field x: int;
    public property Value: int { get { return this.x; } }
    method total(extra: int): int { return this.Value + extra; }
    method report(): void { print(this.Value); }
}
struct Right {
    field y: int;
    public property Value: int { get { return this.y; } }
    method total(extra: int): int { return this.Value + extra; }
    method report(): void { print(this.Value); }
}
function main(args: string[]): int {
    var first = Left { x: 10 }; var second = Right { y: 20 }; var box = new Box();
    with (first, box, Answer) {
        if (.Value != 10 or .total(.Count) != 15 or .Extra != Answer.Extra) { return 0; }
        with (second) {
            .report();
            if (.Value != 20 or .total(.Count) != 25 or .x != 10) { return 0; }
        }
        .report();
        if (.Value != 10 or .total(1) != 11) { return 0; }
    }
    return 42;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("20\n10\n"));
    }

    [Test]
    public void StructWith_RejectsAmbiguousMembersInvalidCallsAndPropertyAssignments()
    {
        const string declarations = """
struct S {
    field x: int;
    public property Value: int { get { return this.x; } }
    public property Copy: S { get { return S { x: this.x }; } }
    method plus(value: byte): int { return this.x + value; }
    method echo(value: S): S { return value; }
    method noop(): void { return; }
}
struct T { field y: int; }
class Box { public property Value: int { get { return 5; } } }
enum E { Value = 1, plus = 2 }
""";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { return 0; }"));
        string[] statements =
        [
            "with (a, b) { var n = .Value; }",
            "with (a, b) { var n = .plus(1); }",
            "with (a, b) { .noop(); }",
            "with (a, E) { var n = .Value; }",
            "with (a, E) { var n = .plus(1); }",
            "with (a, box) { var n = .Value; }",
            "with (a) { var n = .missing; }",
            "with (a) { .missing(); }",
            "with (a) { var n = .plus(); }",
            "with (a) { var n = .plus(1, 2); }",
            "with (a) { var n = .plus(true); }",
            "with (a) { var n = .plus(256); }",
            "with (a) { var n = .echo(t); }",
            "with (a) { var n = .noop(); }",
            "with (a) { .plus(1); }",
            "with (a) { var n = .plus; }",
            "with (a) { var n = .Value(); }",
            "with (a) { var n = .x(); }",
            "with (a) { .Value = 2; }",
            "with (a) { .Copy.x = 2; }",
            "with (a) { var n = .Value.x; }",
            "with (S) { var n = .Value; }",
            "with (S) { .noop(); }",
            "with (a) { with (E) { var n = .plus(1); } }",
            "with (a) { with (t) { var n = .y.Value; } }"
        ];
        foreach (var statement in statements)
        {
            var source = declarations + "function main(args: string[]): int { var a = S { x: 1 }; var b = a; var t = T { y: 0 }; var box = new Box(); " + statement + " return 0; }";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source), statement);
        }
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
