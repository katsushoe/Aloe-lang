using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceForeachBreakFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
class C implements I {
    public async method emit(message: string): void { print(message); }
}
class Plain {}
""";

    private static readonly object[] Collections =
    [
        new object[] { "let items: int[] = [0, 1];", "item in items" },
        new object[] { "let items: int() = (0, 1);", "item in items" },
        new object[] { "let items: int{} = {0, 1};", "item in items" },
        new object[] { "let items: map<int/int> = [0: 0, 1: 1];", "item in items" },
        new object[] { "let items: map<int/int> = [0: 0, 1: 1];", "item, entry in items" },
        new object[] { "let items: pipe<int> = pipe<int>.create(); items.write(0); items.write(1); items.close();", "item in items" }
    ];

    [TestCase("let items: int[] = [];", "item in items")]
    [TestCase("let items: int() = ();", "item in items")]
    [TestCase("let items: int{} = {};", "item in items")]
    [TestCase("let items: map<int/int> = [];", "item in items")]
    [TestCase("let items: map<int/int> = [];", "item, entry in items")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); items.close();", "item in items")]
    public void ReturningForeachBodyPreservesZeroIterationExit(string setup, string binding)
    {
        var result = Run($$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        {{setup}}
        foreach ({{binding}}) { value = new Plain(); return 0; }
        value.emit("zero-iteration");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "zero-iteration" }));
    }

    [TestCaseSource(nameof(Collections))]
    public void NoBackEdgeForeachPreservesFirstIterationReads(string setup, string binding)
    {
        var result = Run($$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        {{setup}}
        foreach ({{binding}}) {
            value.emit("first-iteration");
            value = new Plain();
            break;
        }
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "first-iteration" }));
    }

    [TestCaseSource(nameof(Collections))]
    public void BreakOnlyWritesPreserveRepeatedForeachEntry(string setup, string binding)
    {
        var result = Run($$"""
function main(args: string[]): int {
let value: object = new C();
if (value is I) {
    {{setup}}
    foreach ({{binding}}) {
        value.emit("foreach-break-only");
        if (item == 1) { value = new Plain(); break; }
    }
}
tick(); return 0;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.Output, Is.EqualTo(new[] { "foreach-break-only", "foreach-break-only" }));
    }

    [TestCaseSource(nameof(Collections))]
    public void BreakOnlyWritesInvalidateForeachExit(string setup, string binding)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function main(args: string[]): int {
let value: object = new C();
if (value is I) {
    {{setup}}
    foreach ({{binding}}) {
        if (item == 1) { value = new Plain(); break; }
    }
    value.emit("must-not-narrow");
}
return 0;
}
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (item == 0) {} else { value = new Plain(); break; }")]
    [TestCase("if (item == 1) { value = new Plain(); break; } else { continue; }")]
    [TestCase("if (item == 1) { foreach (inner in [0]) { break; } value = new Plain(); break; }")]
    public void BreakOnlyAlternativesPreserveEntry(string tail)
    {
        var result = Run($$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("entry"); {{tail}} }
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "entry", "entry" }));
    }

    [TestCase("value = new Plain(); break;")]
    [TestCase("foreach (inner in [0]) { value = new Plain(); break; } break;")]
    [TestCase("while (true) { value = new Plain(); break; } break;")]
    public void UnconditionalAndNestedBreakWritesInvalidateExit(string body)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        foreach (item in [0]) { {{body}} }
        value.emit("must-not-narrow");
    }
    return 0;
}
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (item == 0) { value = new Plain(); continue; }")]
    [TestCase("while (true) { if (item == 0) { value = new Plain(); break; } break; }")]
    [TestCase("foreach (inner in [0]) { if (item == 0) { value = new Plain(); break; } }")]
    public void WritesThatCanReachLaterIterationsStillInvalidateEntry(string tail)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("must-not-narrow"); {{tail}} }
    }
    return 0;
}
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("return;")]
    [TestCase("break;")]
    public void SourceConsumptionIsRetainedAtZeroIterationExit(string terminal)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function items(flag: bool): int[] { return []; }
function route(value: object): void {
    if (value is I) {
        foreach (item in items(unbox value)) { {{terminal}} }
        value.emit("must-not-narrow");
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ShortCircuitedSourceConsumerPreservesZeroIterationExit()
    {
        var result = Run("""
function items(flag: bool): int[] { return []; }
function route(value: object): void {
    if (value is I) {
        foreach (item in items(true or unbox value)) { value = new Plain(); return; }
        value.emit("source-short-circuit");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "source-short-circuit" }));
    }

    private static (long ExitCode, string[] Output) Run(string body)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Declarations + body)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
