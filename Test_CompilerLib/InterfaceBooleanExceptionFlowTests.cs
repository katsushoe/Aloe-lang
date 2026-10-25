using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceBooleanExceptionFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
""";

    [TestCase("true")]
    [TestCase("flag")]
    [TestCase("not flag")]
    [TestCase("flag and true")]
    [TestCase("flag == false")]
    [TestCase("(1 + 2) < 4")]
    [TestCase("false and mayThrow()")]
    [TestCase("true or mayThrow()")]
    [TestCase("false and (1 / 0 == 0)")]
    [TestCase("true or (2147483647 + 1 > 0)")]
    public void NonthrowingBoolDeclaration_ExcludesUnreachableHandler(string expression)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { let safe: bool = {{expression}}; }
        catch { value = new Plain(); }
        value.emit("safe");
    }
    return 21;
}
function main(args: string[]): int { let n: int = choose(false) + choose(true); tick(); return n; }
""");
        Assert.That(result, Is.EqualTo((42L, "safe\nsafe\n")));
    }

    [TestCase("let safe: bool = flag; let n: int = 1 / 0;", true)]
    [TestCase("var safe = not flag; let n: int = 2147483647 + 1;", false)]
    [TestCase("flag = not flag; let n: int = 1 / 0;", true)]
    [TestCase("let safe: bool = (1 / 0) == 0;", true)]
    [TestCase("let safe: bool = (2147483647 + 1) > 0;", false)]
    [TestCase("let safe: bool = true and (1 / 0 == 0);", true)]
    public void BoolExpression_PreservesExactArithmeticFamily(string body, bool zero)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{(zero ? "" : "value = new Plain();")}} }
        catch (OverflowException) { {{(zero ? "value = new Plain();" : "")}} }
        catch { value = new Plain(); }
        value.emit("exact");
    }
    return 21;
}
function main(args: string[]): int { let n: int = choose(false) + choose(true); tick(); return n; }
""");
        Assert.That(result, Is.EqualTo((42L, "exact\nexact\n")));
    }

    [TestCase("return (1 / 0) == 0;", true)]
    [TestCase("return (2147483647 + 1) > 0;", false)]
    public void BoolReturnOperand_FaultOccursBeforeReturn(string body, bool zero)
    {
        var result = Run($$"""
function choose(): bool {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{(zero ? "" : "value = new Plain();")}} }
        catch (OverflowException) { {{(zero ? "value = new Plain();" : "")}} }
        catch { value = new Plain(); }
        value.emit("return-fault");
    }
    return true;
}
function main(args: string[]): int { if (choose()) { tick(); return 42; } return 0; }
""");
        Assert.That(result, Is.EqualTo((42L, "return-fault\n")));
    }

    [TestCase("mayThrow()")]
    [TestCase("mayThrow() and false")]
    [TestCase("mayThrow() or true")]
    [TestCase("flag and mayThrow()")]
    [TestCase("flag or mayThrow()")]
    [TestCase("mayThrow() and (1 / 0 == 0)")]
    [TestCase("mayThrow() or (2147483647 + 1 > 0)")]
    [TestCase("flag and (1 / 0 == 0)")]
    [TestCase("flag or (2147483647 + 1 > 0)")]
    public void PotentiallyExecutedCall_RemainsConservative(string expression)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): bool { return true; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { let result: bool = {{expression}}; }
        catch { value = new Plain(); }
        value.emit("unsafe");
    }
    return 0;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    private static (long ExitCode, string Output) Run(string body)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Declarations + "function mayThrow(): bool { return true; }\n" + body)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
