using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceStructReturnFlowTests
{
    [TestCase("try { return payload; } catch { marker=new Plain(); }")]
    [TestCase("try { return (((payload))); } catch { marker=new Plain(); }")]
    [TestCase("try { if(flag) { return payload; } else { return payload; } } catch { marker=new Plain(); }")]
    [TestCase("try { try { return payload; } catch { } } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { try { return payload; } catch { marker=new Plain(); } break; }")]
    [TestCase("let alias: Leaf=payload; try { return alias; } catch { marker=new Plain(); }")]
    public void ExistingStructValueReturnExcludesUnreachableCatch(string body)
        => AssertSafe(Source(body));

    [TestCase("try { return payload.inner; } catch { marker=new Plain(); }")]
    [TestCase("try { return (((payload.inner))); } catch { marker=new Plain(); }")]
    [TestCase("try { if(flag) { return payload.inner; } else { return payload.inner; } } catch { marker=new Plain(); }")]
    [TestCase("var alias=payload.inner; try { return alias; } catch { marker=new Plain(); }")]
    public void ExistingNestedStructFieldReturnExcludesUnreachableCatch(string body)
        => AssertSafe(Source(body,"Wrap","Wrap { inner: Leaf { value: 42 } }"));

    [Test]
    public void DeepStructFieldChainRetainsItsNominalValue()
        => AssertSafe(Source("try { return payload.outer.inner; } catch { marker=new Plain(); }","Top","Top { outer: Wrap { inner: Leaf { value: 42 } } }"));

    [TestCase("try { return make(); } catch { marker=new Plain(); }")]
    [TestCase("try { return use(payload); } catch { marker=new Plain(); }")]
    [TestCase("try { return Leaf { value: 42 }; } catch { marker=new Plain(); }")]
    [TestCase("try { return refs[0]; } catch { marker=new Plain(); }")]
    [TestCase("var box=new Box(); try { return box.inner; } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { var payload=new Box(); try { return payload.inner; } catch { marker=new Plain(); } break; }")]
    [TestCase("foreach(payload in boxes) { try { return payload.inner; } catch { marker=new Plain(); } }")]
    public void CallsAllocationIndexPropertyAndShadowsRemainConservative(string body)
        => AssertConservative(Source(body));

    [Test]
    public void StructToObjectConversionRemainsRejected()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("try { return payload; } catch { marker=new Plain(); }",resultType:"object")));
        Assert.That(error!.Message,Does.Contain("Type mismatch"));
    }

    [Test]
    public void DifferentNominalStructReturnIsRejected()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("try { return payload; } catch { }",resultType:"Other")));
        Assert.That(error!.Message,Does.Contain("type mismatch").IgnoreCase);
    }

    [TestCase("try { return payload.missing; } catch { }")]
    [TestCase("try { return payload.value.missing; } catch { }")]
    public void InvalidFieldChainsRemainRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));

    private static string Source(string body,string inputType="Leaf",string initializer="Leaf { value: 42 }",string resultType="Leaf") => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
struct Leaf { field value: int; }
struct Other { field value: int; }
struct Wrap { field inner: Leaf; }
struct Top { field outer: Wrap; }
class Box { public property inner: Leaf { get { var bad=1/0; return Leaf { value: 42 }; } } }
function make(): Leaf { var bad=1/0; return Leaf { value: 42 }; }
function use(value: Leaf): Leaf { return value; }
function choose(payload: {{inputType}},flag: bool): {{resultType}} {
 let refs: Leaf[]=[]; let boxes: Box[]=[]; let marker: object=new C();
 if(marker is I) { {{body}} marker.emit(); }
 return {{(resultType=="Other"?"Other { value: 0 }":"Leaf { value: 0 }")}};
}
function main(args: string[]): int {
 var payload={{initializer}}; let result: {{resultType}}=choose(payload,true);
 {{(resultType=="object"?"return 0;":"if(result.value == 42) { return 42; } return 0;")}}
}
""";

    private static void AssertConservative(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void AssertSafe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
