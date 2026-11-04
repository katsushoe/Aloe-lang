using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceNumericReturnFlowTests
{
    private static (string Type, string One, string Zero, string Large, string Tiny)[] Domains =>
    [
        ("float", "1.0", "0.0", "200000000000000000000000000000000000000.0", "0.0000000000000000000000000000000000000000000014"),
        ("decimal", "1:d", "0:d", "79228162514264337593543950335:d", "0.0000000000000000000000000001:d")
    ];

    public static IEnumerable<TestCaseData> NormalCases()
    {
        foreach (var d in Domains)
        {
            var suffix = d.Type == "float" ? ".0" : ":d";
            foreach (var (expr, expected) in new[] {
                ("1"+suffix+" + 2"+suffix,"3"+suffix),
                ("3"+suffix+" - 2"+suffix,"1"+suffix),
                ("2"+suffix+" * 3"+suffix,"6"+suffix),
                ("6"+suffix+" / 2"+suffix,"3"+suffix),
                (d.One+" / 3"+suffix,d.Type=="float"?"0.33333334":"0."+new string('3',28)+":d"),
                ("(-2"+suffix+" + 1"+suffix+") * 3"+suffix,"-3"+suffix)
            })
                yield return new TestCaseData(d.Type, expr, expected);
        }
        yield return new TestCaseData("int", "1 + 2", "3");
        yield return new TestCaseData("bool", "true", "true");
    }

    public static IEnumerable<TestCaseData> FailureCases()
    {
        foreach (var d in Domains)
            foreach (var (expr, matching) in new[] {
                (d.One+" / "+d.Zero,"ZeroDivisionException"),
                (d.One+" / -"+d.Zero,"ZeroDivisionException"),
                (d.Large+" + "+d.Large,"OverflowException"),
                (d.Tiny+" * "+d.Tiny,"OverflowException"),
                (d.Tiny+" / "+d.Large,"OverflowException"),
                ("("+d.One+" / "+d.Zero+") + ("+d.Large+" + "+d.Large+")","ZeroDivisionException"),
                ("("+d.Large+" + "+d.Large+") + ("+d.One+" / "+d.Zero+")","OverflowException")
            })
                yield return new TestCaseData(d.Type, expr, matching);
    }

    [TestCaseSource(nameof(NormalCases))]
    public void NonthrowingReturn_ExcludesCatch(string type, string expression, string expected)
        => Assert.That(Run(Program(type, $"try {{ return {expression}; }} catch {{ value=new Plain(); }}", expected)), Is.EqualTo(new[] { "returned" }));

    [TestCaseSource(nameof(FailureCases))]
    public void FirstReturnFailure_VisitsOnlyMatchingCatch(string type, string expression, string matching)
    {
        var other = Other(matching);
        var frame = $"try {{ return {expression}; }} catch({other}) {{ value=new Plain(); }} catch({matching}) {{ print(\"matched\"); }}";
        Assert.That(Run(Program(type, frame, Fallback(type))), Is.EqualTo(new[] { "matched", "safe", "returned" }));
        AssertRejected(type, $"try {{ return {expression}; }} catch({other}) {{ }} catch({matching}) {{ value=new Plain(); }}");
    }

    [TestCase("float", "1.0 / 0.0", "ZeroDivisionException")]
    [TestCase("float", "200000000000000000000000000000000000000.0 + 200000000000000000000000000000000000000.0", "OverflowException")]
    [TestCase("decimal", "1:d / 0:d", "ZeroDivisionException")]
    [TestCase("decimal", "79228162514264337593543950335:d + 1:d", "OverflowException")]
    public void NestedHandledReturnFailure_HasNoOutwardException(string type, string expression, string matching)
    {
        var frame = $"try {{ try {{ return {expression}; }} catch({matching}) {{ }} }} catch {{ value=new Plain(); }}";
        Assert.That(Run(Program(type, frame, Fallback(type))), Is.EqualTo(new[] { "safe", "returned" }));
    }

    [TestCase("float", "1.0 / 0.0", "ZeroDivisionException")]
    [TestCase("float", "200000000000000000000000000000000000000.0 + 200000000000000000000000000000000000000.0", "OverflowException")]
    [TestCase("decimal", "1:d / 0:d", "ZeroDivisionException")]
    [TestCase("decimal", "79228162514264337593543950335:d + 1:d", "OverflowException")]
    public void RethrownReturnFailure_PreservesExceptionClass(string type, string expression, string matching)
    {
        var frame = $"try {{ try {{ return {expression}; }} catch({matching}) {{ throw; }} }} catch({Other(matching)}) {{ value=new Plain(); }} catch({matching}) {{ }}";
        Assert.That(Run(Program(type, frame, Fallback(type))), Is.EqualTo(new[] { "safe", "returned" }));
    }

    [TestCase("float", "1.0 / 0.0", "ZeroDivisionException")]
    [TestCase("float", "200000000000000000000000000000000000000.0 + 200000000000000000000000000000000000000.0", "OverflowException")]
    [TestCase("decimal", "1:d / 0:d", "ZeroDivisionException")]
    [TestCase("decimal", "79228162514264337593543950335:d + 1:d", "OverflowException")]
    public void BothBooleanBranches_RetainFirstFailure(string type, string expression, string matching)
    {
        var frame = $"try {{ if(flag) {{ return {expression}; }} else {{ return {expression}; }} }} catch({Other(matching)}) {{ value=new Plain(); }} catch({matching}) {{ print(\"matched\"); }}";
        foreach (var flag in new[] { "false", "true" })
            Assert.That(Run(Program(type, frame, Fallback(type), flag)), Is.EqualTo(new[] { "matched", "safe", "returned" }));
    }

    [TestCase("float", "input + 1.0")]
    [TestCase("decimal", "input + 1:d")]
    [TestCase("float", "unknown()")]
    [TestCase("decimal", "unknown()")]
    [TestCase("float", "boxed")]
    [TestCase("decimal", "boxed")]
    [TestCase("bool", "boxed")]
    [TestCase("object", "1.0 + 2.0")]
    [TestCase("object", "1:d + 2:d")]
    [TestCase("object", "1 + 2")]
    public void UnknownUnboxingAndBoxingReturn_KeepCatch(string type, string expression)
        => AssertRejected(type, $"try {{ return {expression}; }} catch {{ value=new Plain(); }}");

    [TestCase("256")]
    [TestCase("128 + 128")]
    public void ByteReturnBoundary_UsesOrdinaryTypeValidation(string expression)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function choose(): byte { return "+expression+"; } function main(args: string[]): int { let result: byte=choose(); return 0; }"));

    [TestCase("float", "1.0 / 0.0", "throw;")]
    [TestCase("decimal", "1:d / 0:d", "throw;")]
    [TestCase("float", "1.0 / 0.0", "let fail: int=1 / 0;")]
    [TestCase("decimal", "1:d / 0:d", "let fail: int=1 / 0;")]
    [TestCase("float", "1.0 / 0.0", "unmatched")]
    [TestCase("decimal", "1:d / 0:d", "unmatched")]
    public void NestedReturnWithoutSafeMatchingHandler_KeepsOuterCatch(string type, string expression, string handler)
    {
        var inner = handler == "unmatched"
            ? $"try {{ return {expression}; }} catch(OverflowException) {{ }}"
            : $"try {{ return {expression}; }} catch {{ {handler} }}";
        AssertRejected(type, "try { "+inner+" } catch { value=new Plain(); }");
    }

    [TestCase("float", "1.0 / 0.0")]
    [TestCase("decimal", "1:d / 0:d")]
    public void ReturningCatchHasNoContinuation_RestoresEntryForUnreachableChecks(string type, string expression)
    {
        var frame = $"try {{ return {expression}; }} catch {{ value=new Plain(); return {Fallback(type)}; }}";
        Assert.That(Run(Program(type, frame, Fallback(type))), Is.EqualTo(new[] { "returned" }));
    }

    [Test]
    public void NoContinuationTryBreak_PreservesRealLoopExitState()
        => AssertRejected("float", "while(true) { try { value=new Plain(); break; } catch { break; } }");

    private static string Other(string matching) => matching == "ZeroDivisionException" ? "OverflowException" : "ZeroDivisionException";

    [Test]
    public void BareVoidReturnWithNoContinuation_UsesEntryForUnreachableChecks()
    {
        const string source = """
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(): void {
 let value: object=new C();
 if(value is I) { try { return; } catch { value=new Plain(); } value.emit(); }
}
function main(args: string[]): int { choose(); tick(); print("returned"); return 0; }
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "returned" }));
    }
    private static string Fallback(string type) => type switch { "float" => "7.0", "decimal" => "7:d", "bool" => "false", "object" => "new Plain()", _ => "7" };
    private static void AssertRejected(string type, string frame)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(type, frame, "")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }
    private static string Program(string type, string frame, string expected, string flag = "true")
    {
        var helper = frame.Contains("unknown()", StringComparison.Ordinal)
            ? "function unknown(): "+type+" { return "+(type=="float"?"1.0 / 0.0":"1:d / 0:d")+"; }" : "";
        var check = expected.Length == 0 ? "" : "if(result != "+expected+") { return 9; }";
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
{{helper}}
function choose(flag: bool): {{type}} {
 let input: {{type}}={{Fallback(type)}}; let boxed: object=new Plain(); let value: object=new C();
 if(value is I) { {{frame}} value.emit(); }
 return {{Fallback(type)}};
}
function main(args: string[]): int {
 let result: {{type}}=choose({{flag}}); tick(); {{check}} print("returned"); return 0;
}
""";
    }
    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings(); settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.Zero);
        return output;
    }
}

