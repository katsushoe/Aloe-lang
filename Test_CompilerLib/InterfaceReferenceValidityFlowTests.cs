using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceReferenceValidityFlowTests
{
    public static IEnumerable<TestCaseData> ValidityComparisons()
    {
        foreach (var type in new[] { "object", "C", "I", "int[]", "int{}", "int()", "map<int>", "Integer" })
        foreach (var op in new[] { "==", "!=" })
        foreach (var reversed in new[] { false, true })
            yield return new(type, op, reversed);
    }

    [TestCaseSource(nameof(ValidityComparisons))]
    public void DirectValidityComparisonForLiveAndDeletedParameter(string type, string op, bool reversed)
    {
        var expression = reversed ? $"invalid {op} (((input)))" : $"(((input))) {op} invalid";
        AssertSafe(Source(type, $"try {{ return {expression}; }} catch {{ marker=new Plain(); }}", op));
    }

    [TestCase("try { let result: bool=input == invalid; return result; } catch { marker=new Plain(); }")]
    [TestCase("try { var result=invalid == input; return result; } catch { marker=new Plain(); }")]
    [TestCase("try { return not (input != invalid) and (invalid == input or false); } catch { marker=new Plain(); }")]
    [TestCase("try { if(input == invalid) { return true; } else { return false; } } catch { marker=new Plain(); }")]
    public void ValidityComparisonComposesWithBooleanFlow(string body)
        => AssertSafe(Source("object", body));

    [TestCase("try { return unknown(input) == invalid; } catch { marker=new Plain(); }")]
    [TestCase("try { return invalid == unknown(input); } catch { marker=new Plain(); }")]
    [TestCase("try { return refs[0] == invalid; } catch { marker=new Plain(); }")]
    [TestCase("try { return new C() == invalid; } catch { marker=new Plain(); }")]
    [TestCase("try { return (input as C) == invalid; } catch { marker=new Plain(); }")]
    [TestCase("try { return input != invalid and unknownBool(); } catch { marker=new Plain(); }")]
    [TestCase("try { if(input != invalid) { var fail=1/zero; } return false; } catch { marker=new Plain(); }")]
    [TestCase("try { marker=new Plain(); return input == invalid; } catch { }")]
    public void UnknownOperandsAndReachableExceptionsRetainCatch(string body)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("object",body)));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("var input=1;")]
    [TestCase("let input: bool=true;")]
    [TestCase("let input: string=\"text\";")]
    [TestCase("var input=pipe<int>.create();")]
    public void InvalidComparisonTypesAreStillRejected(string declaration)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("object",
            $"try {{ {declaration} return input == invalid; }} catch {{ }}")));

    private static string Source(string type,string body,string op="==")
    {
        var value=type switch { "int[]"=>"[7]", "int{}"=>"{7}", "int()"=>"(7,8)",
            "map<int>"=>"[\"key\":7]", "Integer"=>"new Integer(7)", _=>"new C()" };
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(value: object): object { return value; }
function unknownBool(): bool { return true; }
function choose(input: {{type}},drop: bool): bool {
 if(drop) { delete input; } let marker: object=new C(); let refs: object[]=[]; var zero=0;
 if(marker is I) { {{body}} marker.emit(); } return false;
}
function main(args: string[]): int {
 let input: {{type}} = {{value}};
 if(choose(input,false) == {{(op=="=="?"false":"true")}} and choose(input,true) == {{(op=="=="?"true":"false")}}) { return 42; }
 return 0;
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
