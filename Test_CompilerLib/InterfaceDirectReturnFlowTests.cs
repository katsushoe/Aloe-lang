using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceDirectReturnFlowTests
{
    public static IEnumerable<TestCaseData> Types()
    {
        yield return new TestCaseData("int", "7");
        yield return new TestCaseData("bool", "true");
        yield return new TestCaseData("byte", "255");
        yield return new TestCaseData("char", "'字'");
        yield return new TestCaseData("float", "7.0");
        yield return new TestCaseData("decimal", "7:d");
        yield return new TestCaseData("string", "\"returned-value\"");
    }

    [TestCaseSource(nameof(Types))]
    public void ParameterReturn_ExcludesUnreachableCatch(string type, string literal)
        => AssertReturns(Source(type, literal, "try { return (input); } catch { value=new Plain(); }"));

    [TestCaseSource(nameof(Types))]
    public void NestedParameterReturn_ExcludesOuterCatch(string type, string literal)
        => AssertReturns(Source(type, literal,
            "try { try { return input; } catch { } } catch { value=new Plain(); }"));

    [TestCaseSource(nameof(Types))]
    public void SafeLocalAliasReturn_UsesSpeculativeType(string type, string literal)
        => AssertReturns(Source(type, literal,
            $"try {{ let alias: {type}=input; return ((alias)); }} catch {{ value=new Plain(); }}"));

    [TestCaseSource(nameof(Types))]
    public void SameTypeShadowReturn_UsesInnerBinding(string type, string literal)
        => AssertReturns(Source(type, literal,
            $"try {{ if(true) {{ let input: {type}=input; return input; }} }} catch {{ value=new Plain(); }}"));

    [TestCase("char", "'字'")]
    [TestCase("string", "\"returned-value\"")]
    public void SameTypeLiteralReturn_IsNonthrowing(string type, string literal)
        => AssertReturns(Source(type, literal,
            $"try {{ return {literal}; }} catch {{ value=new Plain(); }}"));

    [TestCaseSource(nameof(Types))]
    public void UnboxingReturn_RetainsExceptionalCatch(string type, string literal)
        => AssertRejected(Source(type, literal,
            "try { return boxed; } catch { value=new Plain(); }"));

    [TestCaseSource(nameof(Types))]
    public void CallReturn_RetainsUnknownExceptionalCatch(string type, string literal)
        => AssertRejected(Source(type, literal,
            "try { return unknown(input); } catch { value=new Plain(); }"));

    [TestCaseSource(nameof(Types))]
    public void ObjectShadowReturn_DoesNotUseOuterPrimitiveType(string type, string literal)
        => AssertRejected(Source(type, literal,
            "try { if(true) { let input: object=new Plain(); return input; } } catch { value=new Plain(); }"));

    [TestCaseSource(nameof(Types))]
    public void IndexedReturn_RetainsPossibleBoundsException(string type, string literal)
        => AssertRejected(Source(type, literal,
            "try { return items[0]; } catch { value=new Plain(); }"));

    private static string Source(string type, string literal, string frame) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("unsafe"); } }
class Plain { }
function unknown(input: {{type}}): {{type}} { return input; }
function choose(input: {{type}}): {{type}} {
 let boxed: object=new Plain(); let items: {{type}}[]=[input]; let value: object=new C();
 if(value is I) { {{frame}} value.emit(); }
 return {{literal}};
}
function main(args: string[]): int {
 let result: {{type}}=choose({{literal}}); tick();
 if(result != {{literal}}) { return 9; }
 print("returned"); return 0;
}
""";

    private static void AssertRejected(string source)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void AssertReturns(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(output, Is.EqualTo(new[] { "returned" }));
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }
}
