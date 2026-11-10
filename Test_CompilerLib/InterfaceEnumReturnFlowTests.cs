using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceEnumReturnFlowTests
{
    [TestCase("try { return input; } catch { marker=new Plain(); }")]
    [TestCase("try { return (((input))); } catch { marker=new Plain(); }")]
    [TestCase("try { try { return input; } catch { } } catch { marker=new Plain(); }")]
    [TestCase("try { if(flag) { return input; } else { return input; } } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { try { return input; } catch { marker=new Plain(); } break; }")]
    [TestCase("try { return payload.value; } catch { marker=new Plain(); }")]
    [TestCase("try { return payload.inner.value; } catch { marker=new Plain(); }")]
    public void EnumLocalOrFieldReturnHasNoExceptionalEdge(string body)
        => AssertSafe(Source(body));

    [TestCase("try { var alias=input; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=payload.value; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=payload.inner.value; return alias; } catch { marker=new Plain(); }")]
    public void InferredEnumAliasesPreserveReturnKind(string body)
        => AssertSafe(Source(body));

    [TestCase("try { return unknown(input); } catch { marker=new Plain(); }")]
    [TestCase("try { return values[0]; } catch { marker=new Plain(); }")]
    [TestCase("try { return payload.Value; } catch { marker=new Plain(); }")]
    [TestCase("try { return payload.read(); } catch { marker=new Plain(); }")]
    [TestCase("try { var copy=make(); return copy.value; } catch { marker=new Plain(); }")]
    [TestCase("try { var input=unknown(input); return input; } catch { marker=new Plain(); }")]
    public void ExecutableEnumOperandsRetainCatch(string body)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("try { return 1; } catch { }")]
    [TestCase("try { var input=1; return input; } catch { }")]
    [TestCase("try { return Other.One; } catch { }")]
    [TestCase("try { return payload.missing; } catch { }")]
    public void EnumNominalTypeAndFieldChecksRemainEnforced(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));

    private static string Source(string body)
    {
        var nested=body.Contains("inner",StringComparison.Ordinal);
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
enum E { One, Two }
enum Other { One }
struct Payload { field value: E; public property Value: E { get { var fail=1/0; return E.One; } }
 method read(): E { var fail=1/0; return E.One; } }
struct Outer { field inner: Payload; }
function unknown(value: E): E { return value; }
function make(): Payload { return Payload { value: E.One }; }
function choose(input: E,payload: {{(nested?"Outer":"Payload")}},flag: bool): E {
 let marker: object=new C(); let values: E[]=[];
 if(marker is I) { {{body}} marker.emit(); } return E.One;
}
function main(args: string[]): int {
 var payload={{(nested?"Outer { inner: Payload { value: E.Two } }":"Payload { value: E.Two }")}};
 if(choose(E.Two,payload,true) == E.Two) { return 42; } return 0;
}
""";
    }

    private static void AssertSafe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
