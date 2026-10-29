using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceIntReadInitializerFlowTests
{
    [TestCase("let count: int = maximum;")]
    [TestCase("let count: int = ((maximum));")]
    [TestCase("let count: int = maximum; let next: int = count;")]
    [TestCase("if(flag) { let count: int = maximum; }")]
    [TestCase("while(flag) { let count: int = maximum; break; }")]
    [TestCase("let maximum: int = maximum; let count: int = maximum;")]
    [TestCase("if(flag) { let maximum: bool = false; } let count: int = maximum;")]
    [TestCase("while(flag) { let maximum: bool = false; break; } let count: int = maximum;")]
    public void DirectTypedIntRead_HasNoCatchEdge(string body)
    {
        Assert.That(Run(Program(body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("let count: int = maximum + 1;")]
    [TestCase("let count: int = fail();")]
    [TestCase("if(flag) { let count: int = fail(); }")]
    [TestCase("let count: int = maximum; let fault: int = 1 / 0;")]
    public void ArithmeticCallsAndLaterFault_KeepCatchEdge(string body) => AssertRejected(Program(body,"value = new Plain();","value.emit();"));

    [TestCase("1", "safe\nsafe\n")]
    [TestCase("new Plain()", "caught\ncaught\n")]
    public void ImplicitUnbox_RemainsConservative(string initializer, string expected)
    {
        string Source(string handler,string after) => Program("let count: int = boxed; print(\"safe\");",handler,after)
            .Replace("let maximum: int", $"let boxed: object = {initializer}; let maximum: int");
        // A print call must not obscure the initializer's own possible exception edge.
        AssertRejected(Source("value = new Plain();","value.emit();")
            .Replace("let count: int = boxed; print(\"safe\");", "let count: int = boxed;"));
        Assert.That(Run(Source("print(\"caught\");","")), Is.EqualTo(expected));
    }

    [TestCase("left")]
    [TestCase("flag")]
    public void OtherPrimitiveTypes_DoNotBecomeIntInitializers(string initializer)
    {
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Program($"let count: int = {initializer};","","")));
    }

    private static void AssertRejected(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function fail(): int { return 1 / 0; }
function dangerous(): bool { let result: int = fail(); return true; }
function source(): string { let result: int = fail(); return "a"; }
function choose(flag: bool, left: string, right: string): int {
    let maximum: int = 2147483647;
    let value: object = new C();
    if(value is I) {
        try { {{body}} } catch { {{handler}} }
        {{after}}
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(false, "a", "a") + choose(true, "a", "b");
    tick();
    return result;
}
""";

    private static string Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output.ToString().Replace("\r\n", "\n");
    }
}
