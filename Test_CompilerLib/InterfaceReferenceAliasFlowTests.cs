using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceReferenceAliasFlowTests
{
    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void InferredAlias(string inputType,string unused,string initializer)
        => AssertSafe(Source(inputType,initializer,"try { var alias=input; return alias; } catch { marker=new Plain(); }"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void TypedObjectAlias(string inputType,string unused,string initializer)
        => AssertSafe(Source(inputType,initializer,"try { let alias: object=input; return alias; } catch { marker=new Plain(); }"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void ChainedAliases(string inputType,string unused,string initializer)
        => AssertSafe(Source(inputType,initializer,"try { var first=input; let alias: object=first; var last=alias; return (((last))); } catch { marker=new Plain(); }"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void NestedHandlerAlias(string inputType,string unused,string initializer)
        => AssertSafe(Source(inputType,initializer,"try { try { var alias=input; return alias; } catch { } } catch { marker=new Plain(); }"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void ShadowInitializerReadsOuterReferenceBeforeBinding(string inputType,string unused,string initializer)
        => AssertSafe(Source(inputType,initializer,"try { if(flag) { var input=input; return input; } else { return input; } } catch { marker=new Plain(); }"));

    [TestCase("C", "new C()")]
    [TestCase("I", "new C()")]
    [TestCase("Plain", "new Plain()")]
    [TestCase("Integer", "new Integer(7)")]
    public void TypedNominalAlias(string type,string initializer)
        => AssertSafe(Source(type,initializer,"try { let alias: " + type + "=input; return alias; } catch { marker=new Plain(); }"));

    [TestCase("try { let alias: object=1; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=unknown(input); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=new Plain(); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=refs[0]; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=input as C; return alias; } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { var input=1; try { var alias=input; return alias; } catch { marker=new Plain(); } break; }")]
    [TestCase("while(flag) { const input: int=1; try { var alias=input; return alias; } catch { marker=new Plain(); } break; }")]
    public void UnknownOrAllocatingInitializersStayConservative(string frame)
        => AssertConservative(Source("object","new Plain()",frame));

    [Test]
    public void BoxedReferenceCreatedBeforeTryCanBeCopiedWithoutAnotherAllocation()
        => AssertSafe(Source("object","new Plain()",
            "while(flag) { let input: object=1; try { var alias=input; return alias; } catch { marker=new Plain(); } break; }")
            .Replace("if(result == input)", "if((result as int) == 1)"));

    [Test]
    public void PipeAliasRemainsOutsideReferenceProof()
        => AssertConservative(Source("object","new Plain()",
            "var stream=pipe<int>.create(); try { var alias=stream; return alias; } catch { marker=new Plain(); }"));

    [Test]
    public void MultiTokenTypedArrayAliasRemainsConservative()
        => AssertConservative(Source("int[]","[7]",
            "try { let alias: int[]=input; return alias; } catch { marker=new Plain(); }"));

    [Test]
    public void NominalAliasAssignabilityRemainsChecked()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            Source("Plain","new Plain()","try { let alias: C=input; return alias; } catch { }")));
        Assert.That(error!.Message, Does.Contain("Type mismatch"));
    }

    [Test]
    public void ReferenceAliasThenRealFailureRetainsCatch()
        => AssertConservative(Source("object","new Plain()",
            "try { var alias=input; var failure=1/0; return alias; } catch { marker=new Plain(); }"));

    private static string Source(string type,string initializer,string frame) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(value: object): object { return value; }
function choose(input: {{type}},flag: bool): object {
 let refs: object[]=[]; let marker: object=new C();
 if(marker is I) { {{frame}} marker.emit(); }
 return input;
}
function main(args: string[]): int {
 let input: {{type}} = {{initializer}}; let result: object=choose(input,true);
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
