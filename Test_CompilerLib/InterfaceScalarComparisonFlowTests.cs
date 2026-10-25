using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceScalarComparisonFlowTests
{
    [Test]
    public void RawFloatComparisons_NonfiniteAndSignedZeroHaveNoExceptionEdge()
    {
        double[] numbers = [double.NegativeInfinity, double.MinValue, -0.0, 0.0,
            double.MaxValue, double.PositiveInfinity, double.NaN];
        EnumOpcode[] operators = [EnumOpcode.CmpLt, EnumOpcode.CmpLe, EnumOpcode.CmpGt,
            EnumOpcode.CmpGe, EnumOpcode.CmpEq, EnumOpcode.CmpNe];
        foreach (var left in numbers)
        foreach (var right in numbers)
        foreach (var op in operators)
        {
            // Raw VM values exercise boundaries that source literals/JSON do not admit.
            var module = new Module([AloeValue.FromFloat(left), AloeValue.FromFloat(right)],
                [new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
                    new Instruction(op), new Instruction(EnumOpcode.Return)],
                [new FunctionInfo("main", 0, 0, 0)], 0);
            var settings = new AloeVmSettings();
            settings.Logging.FileEnabled = false;
            settings.Logging.ConsoleEnabled = false;
            var vm = new AloeVm(module, settings);
            vm.RunFromEntryPoint();
            Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
            Assert.That(vm.Pop().IsBool, Is.True);
        }
    }

    private static readonly (string Type, string Low, string High)[] Scalars =
        [("byte", "1", "255"), ("char", "'a'", "'z'"), ("float", "-1.5", "2.5")];

    public static IEnumerable<TestCaseData> Comparisons()
    {
        foreach (var scalar in Scalars)
        foreach (var op in new[] { "<", "<=", ">", ">=", "==", "!=" })
        foreach (var locals in new[] { false, true })
            yield return new TestCaseData(scalar.Type, scalar.Low, scalar.High, op, locals);
    }

    [TestCaseSource(nameof(Comparisons))]
    public void DirectSameTypeReads_HaveNoCatchEdge(string type, string low, string high, string op, bool locals)
    {
        var expression = locals ? $"((first)) {op} ((second))" : $"left {op} right";
        Assert.That(Run(Program(type, low, high, $"let result: bool = {expression};",
            "catch { value = new Plain(); }")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    public void OptionalFailure_PreservesExactExceptionFamily(string type, string low, string high)
    {
        Assert.That(Run(Program(type, low, high,
            "let result: bool = not (left >= right) and (1 / 0 == 0);", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
""")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    public void ReachableCall_RemainsConservative(string type, string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(type, low, high, "let result: bool = left < source();", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    public void PossibleBranchWrite_RemainsConservative(string type, string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(type, low, high, "if (left < right) { value = new Plain(); }", "catch { }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string type, string low, string high, string body, string handlers) => $$"""
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
function source(): {{type}} { return {{low}}; }
function choose(left: {{type}}, right: {{type}}): int {
    let first: {{type}} = left;
    let second: {{type}} = right;
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        {{handlers}}
        value.emit("scalar-flow");
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose({{low}}, {{high}}) + choose({{high}}, {{low}});
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
