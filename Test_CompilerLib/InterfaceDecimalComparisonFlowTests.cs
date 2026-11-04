using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceDecimalComparisonFlowTests
{
    public static IEnumerable<TestCaseData> SafeCases()
    {
        string[] conditions = [
            "(1:d + 2:d) == 3:d", "(1:d / 3:d) != 0:d",
            "(2:d * 3:d) < 7:d", "(1:d + 2:d) <= 3:d",
            "(6:d / 2:d) > 2:d", "(3:d - 2:d) >= 1:d",
            "input < (1:d + 2:d)"
        ];
        foreach (var condition in conditions)
            foreach (var form in new[] { "let", "var", "assignment", "if", "while" })
                yield return new TestCaseData(condition, form);
    }

    [TestCaseSource(nameof(SafeCases))]
    public void SafeDecimalComparison_RetainsInterface(string condition, string form)
    {
        var body = form switch
        {
            "let" => $"let computed: bool = {condition};",
            "var" => $"var computed = {condition};",
            "assignment" => $"var computed = false; computed = {condition};",
            "if" => $"if({condition}) {{ var read = input; }}",
            _ => $"while({condition}) {{ var read = input; break; }}"
        };
        AssertSafe(body);
    }

    [TestCase("(1:d + 2:d) == 3")]
    [TestCase("(1:d + 2:d) == (2:d + 1:d)")]
    public void ExistingNumericPromotionAndTwoConstantOperands_RetainInterface(string condition)
        => AssertSafe($"let computed: bool = {condition};");

    [TestCase("(1:d / 0:d) == 0:d")]
    [TestCase("(79228162514264337593543950335:d + 1:d) > 0:d")]
    [TestCase("(0.0000000000000000000000000001:d * 0.0000000000000000000000000001:d) == 0:d")]
    [TestCase("(input + 1:d) == 3:d")]
    [TestCase("(boxed as decimal) == 1:d")]
    [TestCase("decimalUnknown() == 1:d")]
    public void ThrowingOrUnknownOperand_KeepsCatchEdge(string condition)
        => AssertRejected($"let computed: bool = {condition};");

    [TestCase("(1:d + 2:d) == 3.0")]
    [TestCase("(1:d + 2:d) == 'A'")]
    public void IncompatibleOperandTypes_RemainRejected(string condition)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program($"let computed: bool = {condition};", "true")));
    }

    [TestCase("(1:d + 2:d) == 3:d")]
    [TestCase("(1:d + 2:d) != 3:d")]
    public void ComparisonDoesNotIntroduceConstantTruthPruning(string condition)
        => AssertRejected($"if({condition}) {{ value = new Plain(); }}");

    private static void AssertSafe(string body)
    {
        foreach (var flag in new[] { "false", "true" })
        {
            var settings = new AloeVmSettings();
            settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
            var module = new AloeCompiler().Compile(Program(body, flag));
            var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
            var output = new List<string>(); vm.OutputWriter = output.Add;
            vm.RunFromEntryPoint();
            Assert.That(output, Is.EqualTo(new[] { "safe" }));
            Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
            Assert.That(vm.Pop().AsInt, Is.Zero);
        }
    }

    private static void AssertRejected(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(body, "true")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string body, string flag) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function decimalUnknown(): decimal { return 1:d / 0:d; }
function main(args: string[]): int {
 var input=2:d; var flag={{flag}}; let boxed: object=new Plain(); let value: object=new C();
 if(flag) { input=79228162514264337593543950335:d; }
 if(value is I) { try { {{body}} } catch { value=new Plain(); } value.emit(); }
 tick(); return 0;
}
""";
}

