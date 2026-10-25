using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceIntComparisonFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
function unknown(): int { return 1; }
""";

    [TestCase("left < right")]
    [TestCase("left <= right")]
    [TestCase("left > right")]
    [TestCase("left >= right")]
    [TestCase("left == right")]
    [TestCase("left != right")]
    [TestCase("((left)) < ((right))")]
    [TestCase("left < 0")]
    [TestCase("0 >= right")]
    [TestCase("left == -2147483648")]
    [TestCase("left != (2147483646 + 1)")]
    [TestCase("not (left < right)")]
    [TestCase("(left < right) == flag")]
    [TestCase("(left < right) and (right != 0)")]
    public void PureIntComparisons_DoNotIntroduceExceptionalEdges(string expression)
    {
        var output = Run(Program($"let result: bool = {expression};", "catch { value = new Plain(); }"));
        Assert.That(output, Is.EqualTo((0L, "int-flow\nint-flow\n")));
    }

    [TestCase("(left < right) and (1 / 0 == 0)")]
    [TestCase("(left >= right) or (1 / 0 == 0)")]
    [TestCase("(left == right) and (1 / 0 == 0)")]
    [TestCase("(left != right) or (1 / 0 == 0)")]
    public void OptionalArithmeticFailure_RetainsItsExactFamily(string expression)
    {
        var output = Run(Program($"let result: bool = {expression};", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
"""));
        Assert.That(output, Is.EqualTo((0L, "int-flow\nint-flow\n")));
    }

    [TestCase("unknown() < right")]
    [TestCase("left == unknown()")]
    [TestCase("(left + 1) < right")]
    [TestCase("left < (right + 1)")]
    [TestCase("(left / right) == 0")]
    [TestCase("(left == left) and (unknown() == 1)")]
    [TestCase("(left != left) or (unknown() == 1)")]
    public void CallsAndVariableArithmetic_RemainConservative(string expression)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Declarations + Program($"let result: bool = {expression};", "catch { value = new Plain(); }")));
    }

    [TestCase("if (left < right) { let n: int = 1 / 0; } else { let n: int = 1 / 0; }")]
    [TestCase("while (left < right) { let n: int = 1 / 0; }")]
    [TestCase("let n: bool = false; n = left < right; let failure: int = 1 / 0;")]
    public void Comparisons_ComposeWithBranchesLoopsAndAssignments(string body)
    {
        var output = Run(Program(body, """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
"""));
        Assert.That(output, Is.EqualTo((0L, "int-flow\nint-flow\n")));
    }

    [TestCase("if (left < right) { value = new Plain(); }")]
    [TestCase("while (left < right) { value = new Plain(); break; }")]
    [TestCase("if (left != left) { value = new Plain(); }")]
    [TestCase("if (left < right) { let left: bool = flag; let result: bool = left and (unknown() == 1); }")]
    public void PossibleWritesAndReachableCalls_AreNotHiddenByIntProof(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Declarations + Program(body, "catch { value = new Plain(); }")));
    }

    private static string Program(string body, string handlers) => $$"""
function choose(left: int, right: int, flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        {{handlers}}
        value.emit("int-flow");
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(1, 2, false) + choose(2, 1, true);
    tick();
    return result;
}
""";

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
