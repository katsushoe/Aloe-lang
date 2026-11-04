using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceFloatConstantFlowTests
{
    private const string Large = "200000000000000000000000000000000000000.0";
    private const string Tiny = "0.0000000000000000000000000000000000000000000014";
    public static IEnumerable<TestCaseData> SafeCases()
    {
        foreach (var expression in new[] { "1.0 + 2.0", "3.0 - 2.0", "2.0 * 3.0", "6.0 / 2.0",
                     "1.0 / 3.0", "(-2.0 + 1.0) * 3.0", Tiny + " / 1.0" })
            foreach (var form in new[] { "let", "var", "assignment", "comparison", "if", "while" })
                yield return new TestCaseData(expression, form);
    }

    public static IEnumerable<TestCaseData> UnsafeCases()
    {
        foreach (var expression in new[] { "1.0 / 0.0", "1.0 / -0.0", Large + " + " + Large,
                     Tiny + " * " + Tiny, Tiny + " / " + Large, "input + " + Large, "floatUnknown()" })
            foreach (var form in new[] { "let", "var", "assignment", "comparison" })
                yield return new TestCaseData(expression, form);
    }

    [TestCaseSource(nameof(SafeCases))]
    public void NonthrowingFloatConstant_RetainsInterface(string expression, string form)
    {
        foreach (var flag in new[] { "false", "true" })
            Assert.That(Run(Program(Body(expression, form), flag)), Is.EqualTo(new[] { "safe" }));
    }

    [TestCaseSource(nameof(UnsafeCases))]
    public void ThrowingOrUnknownFloatExpression_RetainsCatch(string expression, string form)
        => AssertRejected(Body(expression, form));

    [TestCase("if(flag) { let input: object = boxed; let computed: float = input; }")]
    [TestCase("try { let computed: float = floatUnknown(); } catch { }")]
    [TestCase("if((1.0 + 2.0) == 3.0) { value = new Plain(); }")]
    public void UnboxingMixingHandledFailureAndTruthPruning_RemainOutsideProof(string body)
        => AssertRejected(body);

    [Test]
    public void FloatDecimalMixing_RemainsRejected()
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program("let computed: float = 1.0 + 2:d;", "true")));

    [TestCase("let alias: float = 1.0 + 2.0;")]
    [TestCase("var alias = 1.0 + 2.0;")]
    public void SpeculativeLoopShadow_UsesOnlyNonthrowingFloatType(string declaration)
    {
        var source = $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("loop"); } }
class Plain { }
function main(args: string[]): int {
 let value: object=new C(); var flag=true;
 if(value is I) { while(flag) { {{declaration}} try { var copy=alias; } catch { value=new Plain(); } value.emit(); flag=false; } }
 tick(); return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "loop" }));
    }

    [Test]
    public void Float32Rounding_MatchesRuntime()
    {
        const string source = """
function main(args: string[]): int {
 var computed=16777216.0 + 1.0;
 if(computed == 16777216.0) { print("rounded"); return 0; }
 return 9;
}
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "rounded" }));
    }

    private static string Body(string expression, string form) => form switch
    {
        "let" => $"let computed: float = {expression};",
        "var" => $"var computed = {expression};",
        "assignment" => $"var computed = 0.0; computed = {expression};",
        "comparison" => $"let computed: bool = ({expression}) == 0.0;",
        "if" => $"if(({expression}) == 0.0) {{ var copy=input; }}",
        _ => $"while(({expression}) == 0.0) {{ var copy=input; break; }}"
    };

    private static void AssertRejected(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(body, "true")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string body, string flag) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function floatUnknown(): float { return 1.0 / 0.0; }
function main(args: string[]): int {
 var input=2.0; var flag={{flag}}; let boxed: object=new Plain(); let value: object=new C();
 if(flag) { input=200000000000000000000000000000000000000.0; }
 if(value is I) { try { {{body}} } catch { value=new Plain(); } value.emit(); }
 tick(); return 0;
}
""";

    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output;
    }
}

