using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ArrayForeachExpressionTests
{
    [Test]
    public void ArrayForeachExpressions_DocumentedFunctionAndLiteralIteration()
    {
        const string source = """
function values(): int[] { return [10, 20, 12]; }
function main(args: string[]): int {
    var total = 0;
    foreach (value in values()) { total = total + value; }
    foreach (flag in [true, false]) { if (flag) { return total; } }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ArrayForeachExpressions_EvaluateFunctionAndGetterOnceRetainTemporaryArraysAndNestLoops()
    {
        const string source = """
function make(): int[] { print(10); return [1, 2, 3, 4]; }
class Box { public property Values: int[] { get { print(20); return [10, 20]; } } }
function main(args: string[]): int {
    var total = 0;
    foreach (value in make()) {
        GC.require(); GC.finish();
        if (value == 2) { continue; }
        if (value == 4) { break; }
        total = total + value;
    }
    var box = new Box(); var nested = 0;
    foreach (number in (box.Values)) {
        GC.require(); GC.finish();
        foreach (flag in [true, false, true]) { if (flag) { nested = nested + 1; } }
        total = total + number;
    }
    foreach (value in ([0, 0])) { total = total + value; }
    if (nested == 4) { return total + 8; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("10\n20\n"));
    }

    [Test]
    public void ArrayForeachExpressions_CaptureSharedReferenceObserveLaterWritesAndHandleEmptyTargets()
    {
        const string source = """
function identity(values: int[]): int[] { return values; }
function empty(): bool[] { let result: bool[] = []; return result; }
struct Counter {
    field total: int;
    construct() {
        this.total = 0;
        foreach (value in [1, 2, 3]) { this.total = this.total + value; }
    }
}
function main(args: string[]): int {
    var original = [1, 2, 3]; var alias = original; var total = 0;
    foreach (value in identity(alias)) {
        if (value == 1) { alias = [9]; original[1] = 5; }
        total = total + value;
    }
    foreach (flag in empty()) { return 0; }
    var counter = new Counter();
    if (total == 9 and counter.total == 6 and alias[0] == 9) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ArrayForeachExpressions_RejectNonArraysInvalidScopesAndUninitializedConstructorFields()
    {
        const string declarations = "function values(): int[] { return [1]; } function scalar(): int { return 1; } ";
        string[] statements =
        [
            "foreach (value in scalar()) { }", "foreach (value in values().Length) { }",
            "foreach (value in values()[0]) { }", "foreach (value in 1) { }",
            "foreach (value in [1, true]) { }", "foreach (value in []) { }",
            "foreach (flag in [true]) { let number: int = flag; }",
            "foreach (value in values()) { } return value;",
            "foreach (value in values()) { } break;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "struct S { field x: int; construct() { foreach (value in [1]) { this.x = value; } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations +
            "function main(args: string[]): int { foreach (value in values()) { return value; } return 0; }"));
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
