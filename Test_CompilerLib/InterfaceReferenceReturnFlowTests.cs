using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceReferenceReturnFlowTests
{
    public static IEnumerable<TestCaseData> References()
    {
        yield return new("object", "object", "new Plain()");
        yield return new("C", "C", "new C()");
        yield return new("C", "I", "new C()");
        yield return new("I", "I", "new C()");
        yield return new("C", "object", "new C()");
        yield return new("int[]", "int[]", "[7]");
        yield return new("int{}", "int{}", "{7}");
        yield return new("int()", "int()", "(7,8)");
        yield return new("map<int>", "map<int>", "[\"key\":7]");
        yield return new("Integer", "Integer", "new Integer(7)");
    }

    [TestCaseSource(nameof(References))]
    public void ParsedParameterReturnExcludesUnreachableCatch(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType,returnType,initializer,"try { return (((input))); } catch { marker=new Plain(); }"));

    [TestCaseSource(nameof(References))]
    public void AlreadyParsedAliasReturnExcludesUnreachableCatch(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType,returnType,initializer,"let alias: " + inputType + " = input; try { return alias; } catch { marker=new Plain(); }"));

    [TestCaseSource(nameof(References))]
    public void NestedTryReturnExcludesOuterCatch(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType,returnType,initializer,"try { try { return input; } catch { } } catch { marker=new Plain(); }"));

    [TestCaseSource(nameof(References))]
    public void BothPrimitiveConditionBranchesReturnExistingReference(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType,returnType,initializer,"try { if(flag) { return input; } else { return input; } } catch { marker=new Plain(); }"));

    [TestCase("object")]
    [TestCase("int[]")]
    public void LoadingInvalidHandleDoesNotDereferenceIt(string type)
    {
        var source=$$"""
function choose(input: {{type}}): {{type}} { delete input; try { return input; } catch { return input; } }
function main(args: string[]): int { let input: {{type}} = {{(type=="object"?"new Plain()":"[7]")}}; let result: {{type}}=choose(input); if(result == invalid) { return 42; } return 0; }
class Plain { }
""";
        AssertSafe(source);
    }

    [TestCase("try { const input: int=1; return input; } catch { marker=new Plain(); }")]
    [TestCase("try { var input=1; return input; } catch { marker=new Plain(); }")]
    [TestCase("try { let input: int=1; return input; } catch { marker=new Plain(); }")]
    [TestCase("try { return unknown(input); } catch { marker=new Plain(); }")]
    [TestCase("try { return new Plain(); } catch { marker=new Plain(); }")]
    [TestCase("try { return refs[0]; } catch { marker=new Plain(); }")]
    [TestCase("try { return input as C; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: object=1; return alias; } catch { marker=new Plain(); }")]
    public void UnknownOrAllocatingReturnOperandsRetainCatch(string frame)
        => AssertConservative(Source("object","object","new Plain()",frame));

    [TestCase("const input: int=1;")]
    [TestCase("var input=1;")]
    [TestCase("let input: int=1;")]
    public void UnparsedPrimitiveShadowCannotBorrowOuterReferenceKind(string declaration)
        => AssertConservative(Source("object","object","new Plain()",
            "while(flag) { " + declaration + " try { return input; } catch { marker=new Plain(); } break; }"));

    [Test]
    public void UnparsedForeachBindingCannotBorrowOuterReferenceKind()
        => AssertConservative(Source("object","object","new Plain()",
            "foreach(input in [1]) { try { return input; } catch { marker=new Plain(); } }"));

    [Test]
    public void ParsedReferenceCannotProvePrimitiveUnboxingReturnSafe()
    {
        var source=Source("object","object","new Plain()","try { return input; } catch { marker=new Plain(); }")
            .Replace("function choose(input: object,flag: bool): object", "function choose(input: object,flag: bool): int")
            .Replace(" return input;\n}", " return 0;\n}")
            .Replace("let result: object=choose(input,true); if(result == input)", "let result: int=choose(input,true); if(result == 0)");
        AssertConservative(source);
    }

    [Test]
    public void NominalAssignabilityIsStillValidated()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            Source("Plain","C","new Plain()","try { return input; } catch { }")));
        Assert.That(error!.Message, Does.Contain("Type mismatch"));
    }

    private static string Source(string inputType, string returnType, string initializer, string frame) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(value: object): object { return value; }
function choose(input: {{inputType}},flag: bool): {{returnType}} {
 let refs: object[]=[]; let marker: object=new C();
 if(marker is I) { {{frame}} marker.emit(); }
 return input;
}
function main(args: string[]): int {
 let input: {{inputType}} = {{initializer}}; let result: {{returnType}} = choose(input,true);
 if(result == input) { return 42; } return 0;
}
""";

    private static void AssertConservative(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }
    private static void AssertSafe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
    }
}
