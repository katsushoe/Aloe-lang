using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceEnumBindingFlowTests
{
    [TestCase("try { let alias: E=input; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=(((input))); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=payload.value; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=input; let copy: E=alias; return copy; } catch { marker=new Plain(); }")]
    [TestCase("let alias: E=input; try { alias=input; return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: E=input; try { alias=payload.value; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let input: E=input; input=payload.value; return input; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=input; if(flag) { alias=input; return alias; } else { alias=payload.value; return alias; } } catch { marker=new Plain(); }")]
    [TestCase("try { try { let alias: E=input; alias=input; return alias; } catch { } } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { let alias: E=input; try { alias=input; return alias; } catch { marker=new Plain(); } break; }")]
    public void DirectEnumBindingsHaveNoExceptionalEdge(string body) => AssertSafe(Source(body));

    [TestCase("try { let alias: E=unknown(input); return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: E=input; try { alias=unknown(input); return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=values[0]; return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: E=input; try { alias=values[0]; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=input; var fail=1/zero; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { let alias: E=input; } catch { } marker=new Plain();")]
    public void UnknownAndReachableFailuresRetainCatch(string body)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("try { let alias: Other=input; return input; } catch { }")]
    [TestCase("try { let alias: E=Other.One; return alias; } catch { }")]
    [TestCase("let alias: E=input; try { alias=Other.One; return alias; } catch { }")]
    [TestCase("try { let alias: E=1; return alias; } catch { }")]
    [TestCase("let alias: E=input; try { alias=1; return alias; } catch { }")]
    [TestCase("try { const alias: E=input; return alias; } catch { }")]
    public void NominalEnumAndConstBoundariesRemainEnforced(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source(body)));

    private static string Source(string body) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
enum E { One, Two }
enum Other { One }
struct Payload { field value: E; }
function unknown(value: E): E { return value; }
function choose(input: E,payload: Payload,flag: bool): E {
 let marker: object=new C(); let values: E[]=[]; var zero=0;
 if(marker is I) { {{body}} marker.emit(); } return E.One;
}
function main(args: string[]): int {
 var payload=Payload { value: E.Two };
 if(choose(E.Two,payload,true) == E.Two) { return 42; } return 0;
}
""";

    private static void AssertSafe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
