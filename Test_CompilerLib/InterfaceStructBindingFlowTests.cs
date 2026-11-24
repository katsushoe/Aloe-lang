using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceStructBindingFlowTests
{
    [TestCase("try { var alias=payload; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: Leaf=(((payload))); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var first=payload; let second: Leaf=first; var third=second; return third; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=payload; alias=payload; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: Leaf=payload; if(flag) { alias=payload; } return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=payload; try { return alias; } catch { } } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { try { var alias=payload; return alias; } catch { marker=new Plain(); } break; }")]
    [TestCase("try { var payload=payload; return payload; } catch { marker=new Plain(); }")]
    [TestCase("try { if(flag) { var alias=payload; } return payload; } catch { marker=new Plain(); }")]
    [TestCase("var alias=payload; try { alias=payload; return alias; } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { let alias: Leaf=payload; try { return alias; } catch { marker=new Plain(); } break; }")]
    [TestCase("if(flag) { try { var alias=payload; return alias; } catch { marker=new Plain(); } }")]
    public void ExistingImmutableStructBindingsPreserveReturnFlow(string body)
        => Safe(Source(body));

    [TestCase("try { var alias=payload.inner; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: Leaf=payload.inner; alias=payload.inner; return alias; } catch { marker=new Plain(); }")]
    public void DeclaredNestedStructValueCanBeCopiedIntoALocal(string body)
        => Safe(Source(body,"Wrap","Wrap { inner: Leaf { value: 42 } }"));

    [TestCase("try { var alias=make(); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=refs[0]; return alias; } catch { marker=new Plain(); }")]
    [TestCase("var box=new Box(); try { let alias: Leaf=box.inner; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=Leaf { value: 42 }; return alias; } catch { marker=new Plain(); }")]
    [TestCase("var alias=payload; while(flag) { var alias=new Box(); try { return alias.inner; } catch { marker=new Plain(); } break; }")]
    public void CallsAllocationIndexPropertyAndShadowsRemainConservative(string body)
        => Conservative(Source(body));

    [TestCase("var alias=payload; return alias.inner;")]
    [TestCase("let alias: Wrap=payload; var second=alias; return (((second.inner)));")]
    [TestCase("var alias=payload; if(flag) { return alias.inner; } return payload.inner;")]
    [TestCase("var alias=payload; if(flag) { var alias=1; } return alias.inner;")]
    [TestCase("var payload=payload; return payload.inner;")]
    public void ProvenLocalCopiesRetainNominalFieldShape(string body)
        => Safe(Source("try { "+body+" } catch { marker=new Plain(); }","Wrap","Wrap { inner: Leaf { value: 42 } }"));

    [Test]
    public void ThrowingGetterOnCopiedStructRetainsExceptionalEdge()
        => Conservative(Source("try { var alias=payload; return alias.Fault; } catch { marker=new Plain(); }",
            "Wrap","Wrap { inner: Leaf { value: 42 } }"));

    [TestCase("try { let alias: Other=payload; return payload; } catch { }")]
    [TestCase("var alias=Other { value: 1 }; try { alias=payload; return payload; } catch { }")]
    public void NominalMismatchRemainsRejected(string body)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));
        Assert.That(error!.Message,Does.Contain("type mismatch").IgnoreCase);
    }

    [Test]
    public void StructConstRemainsUnsupported()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("try { const alias: Leaf=payload; return alias; } catch { }")));
        Assert.That(error!.Message,Does.Contain("const supports local scalar types only"));
    }

    [TestCase("try { var alias=payload; return alias.missing; } catch { }")]
    [TestCase("try { if(flag) { var alias=payload; } return alias; } catch { }")]
    public void InvalidFieldsAndEscapedBindingsRemainRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));

    private static string Source(string body,string inputType="Leaf",string initial="Leaf { value: 42 }") => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
struct Leaf { field value: int; }
struct Other { field value: int; }
struct Wrap { field inner: Leaf; public property Fault: Leaf { get { var bad=1/0; return this.inner; } } }
class Box { public property inner: Leaf { get { var bad=1/0; return Leaf { value: 42 }; } } }
function make(): Leaf { var bad=1/0; return Leaf { value: 42 }; }
function choose(payload: {{inputType}},flag: bool): Leaf {
 let refs: Leaf[]=[]; let marker: object=new C();
 if(marker is I) { {{body}} marker.emit(); }
 return Leaf { value: 0 };
}
function main(args: string[]): int { var input={{initial}}; var result=choose(input,true); return result.value; }
""";

    private static void Conservative(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void Safe(string source)
    {
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source))),settings);
        vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
