using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceFloatExceptionFlowTests
{
    private const string Large = "200000000000000000000000000000000000000.0";
    private const string Tiny = "0.0000000000000000000000000000000000000000000014";
    public static IEnumerable<TestCaseData> TypedCases()
    {
        (string Expression, string Matching, string Unrelated)[] failures = [
            ("1.0 / 0.0", "ZeroDivisionException", "OverflowException"),
            ("1.0 / -0.0", "ZeroDivisionException", "OverflowException"),
            (Large + " + " + Large, "OverflowException", "ZeroDivisionException"),
            (Tiny + " * " + Tiny, "OverflowException", "ZeroDivisionException"),
            (Tiny + " / " + Large, "OverflowException", "ZeroDivisionException"),
            ("(1.0 / 0.0) + (" + Large + " + " + Large + ")", "ZeroDivisionException", "OverflowException"),
            ("(" + Large + " + " + Large + ") + (1.0 / 0.0)", "OverflowException", "ZeroDivisionException")
        ];
        foreach (var (expression, matching, unrelated) in failures)
            foreach (var declaration in new[] { "let computed: float", "var computed" })
                yield return new TestCaseData(expression, matching, unrelated, declaration);
    }

    [TestCaseSource(nameof(TypedCases))]
    public void FirstTypedFailure_VisitsOnlyMatchingHandler(string expression, string matching, string unrelated, string declaration)
    {
        var safe = $"try {{ {declaration} = {expression}; }} catch({unrelated}) {{ value=new Plain(); }} catch({matching}) {{ print(\"matched\"); }}";
        Assert.That(Run(Program(safe)), Is.EqualTo(new[] { "matched", "safe" }));
        var unsafeBody = $"try {{ {declaration} = {expression}; }} catch({unrelated}) {{ }} catch({matching}) {{ value=new Plain(); }}";
        AssertRejected(unsafeBody);
    }

    [TestCase("try { let computed: float=1.0 / 0.0; } catch(ZeroDivisionException) { }")]
    [TestCase("try { var computed=" + Large + " + " + Large + "; } catch(OverflowException) { }")]
    [TestCase("try { input=1.0 / 0.0; } catch(ZeroDivisionException) { }")]
    [TestCase("let alias: float=input; try { alias=" + Tiny + " * " + Tiny + "; } catch(OverflowException) { }")]
    [TestCase("try { input=1.0 / 0.0; } catch { input=1.0 + 2.0; }")]
    [TestCase("try { let computed: float=(1.0 / 0.0) + (" + Large + " + " + Large + "); } catch(OverflowException) { throw; } catch(ZeroDivisionException) { }")]
    public void SingleHandledFailure_RetainsOuterInterface(string body)
        => Assert.That(Run(Program("try { " + body + " } catch { value=new Plain(); }")), Is.EqualTo(new[] { "safe" }));

    [TestCase("try { input=1.0 / 0.0; } catch(OverflowException) { }")]
    [TestCase("try { input=1.0 / 0.0; } catch { throw; }")]
    [TestCase("try { input=1.0 / 0.0; } catch { input=1.0 / 0.0; }")]
    [TestCase("try { input=1.0 / 0.0; } catch { value=new Plain(); }")]
    [TestCase("try { input=input + " + Large + "; } catch { }")]
    [TestCase("if(flag) { let input: object=boxed; try { input=1.0 / 0.0; } catch { } }")]
    public void UnknownOrOutwardFailure_KeepsOuterCatch(string body)
        => AssertRejected("try { " + body + " } catch { value=new Plain(); }");

    [Test]
    public void HandledParameterAssignment_PreservesOldValueAndInterface()
    {
        const string source = """
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(input: float): int {
 let value: object=new C();
 if(value is I) {
  try { try { input=1.0 / 0.0; } catch(ZeroDivisionException) { } } catch { value=new Plain(); }
  if(input != 7.0) { return 9; }
  value.emit();
 }
 return 0;
}
function main(args: string[]): int { let result: int=choose(7.0); tick(); return result; }
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "safe" }));
    }

    private static void AssertRejected(string statements)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(statements)));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string statements) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function main(args: string[]): int {
 var input=7.0; var flag=true; let boxed: object=new Plain(); let value: object=new C();
 if(value is I) { {{statements}} value.emit(); }
 tick(); return 0;
}
""";

    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output;
    }
}

