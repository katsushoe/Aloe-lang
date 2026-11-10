using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceStructFieldFlowTests
{
    public static IEnumerable<TestCaseData> Scalars()
    {
        yield return new("int","42"); yield return new("bool","true"); yield return new("byte","255");
        yield return new("char","'字'"); yield return new("float","-1.5");
        yield return new("decimal","1.2300:d"); yield return new("string","\"Aloe字\"");
    }

    [TestCaseSource(nameof(Scalars))]
    public void DirectFieldReturn(string type,string literal)
        => AssertSafe(Source(type,literal,"try { return (((payload.value))); } catch { marker=new Plain(); }"));

    [TestCaseSource(nameof(Scalars))]
    public void TypedFieldInitializer(string type,string literal)
        => AssertSafe(Source(type,literal,$"try {{ let copy: {type}=payload.value; return copy; }} catch {{ marker=new Plain(); }}"));

    [TestCaseSource(nameof(Scalars))]
    public void InferredFieldInitializer(string type,string literal)
        => AssertSafe(Source(type,literal,"try { var copy=payload.value; return copy; } catch { marker=new Plain(); }"));

    [TestCaseSource(nameof(Scalars))]
    public void NestedStructFieldChain(string type,string literal)
        => AssertSafe(Source(type,literal,"try { return payload.inner.value; } catch { marker=new Plain(); }",nested:true));

    [TestCaseSource(nameof(Scalars))]
    public void ScalarFieldComparison(string type,string literal)
        => AssertSafe(Source(type,literal,$"try {{ if(payload.value == {literal}) {{ return payload.value; }} else {{ return payload.value; }} }} catch {{ marker=new Plain(); }}"));

    [TestCase("while(flag) { try { return payload.value; } catch { marker=new Plain(); } break; }")]
    [TestCase("try { if(flag) { return payload.value; } else { return payload.value; } } catch { marker=new Plain(); }")]
    [TestCase("try { try { var copy=payload.value; return copy; } catch { } } catch { marker=new Plain(); }")]
    public void ExistingStructBindingsComposeWithControlFlow(string body)
        => AssertSafe(Source("int","42",body));

    [TestCase("try { return payload.value/zero; } catch { marker=new Plain(); }")]
    [TestCase("try { return payload.Value; } catch { marker=new Plain(); }")]
    [TestCase("try { return payload.read(); } catch { marker=new Plain(); }")]
    [TestCase("try { return use(payload); } catch { marker=new Plain(); }")]
    [TestCase("try { var copy=make(); return copy.value; } catch { marker=new Plain(); }")]
    [TestCase("try { var copy=points[0]; return copy.value; } catch { marker=new Plain(); }")]
    [TestCase("try { var copy=Payload { value: 42 }; return copy.value; } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { var payload=new Box(); try { return payload.value; } catch { marker=new Plain(); } break; }")]
    [TestCase("try { var payload=new Box(); return payload.value; } catch { marker=new Plain(); }")]
    public void ThrowingCallsPropertiesIndexesAndShadowsStayConservative(string body)
        => AssertConservative(Source("int","42",body));

    [Test]
    public void EnumFieldPreservesItsNominalValueOnReturn()
        => AssertSafe(Source("E","E.One","try { return payload.value; } catch { marker=new Plain(); }"));

    [Test]
    public void ClassGetterThenStructFieldStaysConservative()
    {
        var source=Source("int","42","try { var copy=holder.Data; return copy.value; } catch { marker=new Plain(); }")
            .Replace("let points: Payload[]=[];", "var holder=new Holder(); let points: Payload[]=[];");
        AssertConservative(source);
    }

    [TestCase("try { return payload.missing; } catch { }")]
    [TestCase("try { return payload.value.missing; } catch { }")]
    public void InvalidFieldPathsRemainRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","42",body)));

    private static string Source(string type,string literal,string body,bool nested=false) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
enum E { One }
struct Payload { field value: {{type}}; public property Value: int { get { return 1/0; } } method read(): int { return 1/0; } }
struct Outer { field inner: Payload; }
class Box { public property value: int { get { return 1/0; } } }
class Holder { field data: Payload; construct() { this.data=Payload { value: {{literal}} }; }
 public property Data: Payload { get { return this.data; } } }
function make(): Payload { return Payload { value: {{literal}} }; }
function use(payload: Payload): int { return 42; }
function choose(payload: {{(nested?"Outer":"Payload")}},flag: bool): {{type}} {
 let points: Payload[]=[]; var zero=0; let marker: object=new C();
 if(marker is I) { {{body}} marker.emit(); } return {{literal}};
}
function main(args: string[]): int {
 var payload={{(nested?"Outer { inner: Payload { value: "+literal+" } }":"Payload { value: "+literal+" }")}};
 let result: {{type}}=choose(payload,true); if(result == {{literal}}) { return 42; } return 0;
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
