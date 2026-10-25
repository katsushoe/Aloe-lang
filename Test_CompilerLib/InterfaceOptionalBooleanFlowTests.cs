using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceOptionalBooleanFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
function mayThrow(): bool { return (1 / 0) == 0; }
""";

    [TestCase("let n: bool = flag and (1 / 0 == 0);", "zero")]
    [TestCase("let n: bool = flag or (1 / 0 == 0);", "zero")]
    [TestCase("let n: bool = not (flag and (1 / 0 == 0));", "zero")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) == true;", "zero")]
    [TestCase("let n: bool = true == (flag or (1 / 0 == 0));", "zero")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) != (flag or (2147483647 + 1 > 0));", "both")]
    [TestCase("let n: bool = flag and (1 / 0 == 0) or (2147483647 + 1 > 0);", "both")]
    [TestCase("let n: bool = flag or (2147483647 + 1 > 0) and (1 / 0 == 0);", "overflow")]
    [TestCase("let n: bool = false and mayThrow() or (flag and (1 / 0 == 0));", "zero")]
    [TestCase("let n: bool = (1 / 0 == 0) == mayThrow();", "zero")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) and mayThrow();", "zero")]
    [TestCase("let n: bool = (flag or (1 / 0 == 0)) or mayThrow();", "zero")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) == (2147483647 + 1 > 0);", "both")]
    [TestCase("let n: bool = (flag or (2147483647 + 1 > 0)) != (1 / 0 == 0);", "both")]
    [TestCase("let n: bool = flag and (1 / 0 == 0); let m: int = 2147483647 + 1;", "both")]
    [TestCase("let n: bool = flag or (2147483647 + 1 > 0); let m: int = 1 / 0;", "both")]
    [TestCase("flag = flag and (1 / 0 == 0);", "zero")]
    [TestCase("flag = flag or (2147483647 + 1 > 0);", "overflow")]
    [TestCase("if (flag and (1 / 0 == 0)) { }", "zero")]
    [TestCase("if (flag or (2147483647 + 1 > 0)) { let m: int = 1 / 0; }", "both")]
    [TestCase("while (flag and (1 / 0 == 0)) { break; }", "zero")]
    [TestCase("while (flag or (2147483647 + 1 > 0)) { break; }", "overflow")]
    public void OptionalFaults_RetainOnlyPossibleArithmeticFamilies(string body, string families)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{(families == "overflow" ? "value = new Plain();" : "")}} }
        catch (OverflowException) { {{(families == "zero" ? "value = new Plain();" : "")}} }
        catch { value = new Plain(); }
        value.emit("finite");
    }
    return 0;
}
function main(args: string[]): int { let n: int = choose(false) + choose(true); tick(); return n; }
""");
        Assert.That(result, Is.EqualTo((0L, "finite\nfinite\n")));
    }

    [TestCase("flag and (1 / 0 == 0)", "zero")]
    [TestCase("flag or (2147483647 + 1 > 0)", "overflow")]
    [TestCase("(flag and (1 / 0 == 0)) == (2147483647 + 1 > 0)", "both")]
    [TestCase("(flag or (2147483647 + 1 > 0)) != (1 / 0 == 0)", "both")]
    public void OptionalReturnFaults_PreserveNormalReturnAndExceptionalEdges(string expression, string families)
    {
        var result = Run($$"""
function choose(flag: bool): bool {
    let value: object = new C();
    if (value is I) {
        try { return {{expression}}; }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{(families == "overflow" ? "value = new Plain();" : "")}} }
        catch (OverflowException) { {{(families == "zero" ? "value = new Plain();" : "")}} }
        catch { value = new Plain(); }
        value.emit("return-fault");
    }
    return true;
}
function main(args: string[]): int { let a: bool = choose(false); let b: bool = choose(true); tick(); return 0; }
""");
        var expected = families == "both" ? "return-fault\nreturn-fault\n" : "return-fault\n";
        Assert.That(result, Is.EqualTo((0L, expected)));
    }

    [TestCase("let n: bool = mayThrow() and (1 / 0 == 0);")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) or mayThrow();")]
    [TestCase("let n: bool = (flag or (1 / 0 == 0)) and mayThrow();")]
    [TestCase("let n: bool = (flag and (1 / 0 == 0)) == mayThrow();")]
    [TestCase("let n: bool = mayThrow() == (1 / 0 == 0);")]
    [TestCase("if (flag and mayThrow()) { let n: int = 1 / 0; }")]
    [TestCase("while (flag or mayThrow()) { break; }")]
    [TestCase("let n: bool = flag and (1 / 0 == 0); let m: bool = mayThrow();")]
    public void ReachableUnknownCalls_KeepOtherExceptionHandlersReachable(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        value.emit("unknown");
    }
    return 0;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    private static (long ExitCode, string Output) Run(string body)
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
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
