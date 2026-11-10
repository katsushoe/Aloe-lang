using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceReferenceEqualityFlowTests
{
    public static IEnumerable<TestCaseData> Comparisons()
    {
        var references = new (string Type, string Value)[]
        {
            ("object", "new C()"), ("C", "new C()"), ("I", "new C()"),
            ("int[]", "[7]"), ("int{}", "{7}"), ("int()", "(7,8)"),
            ("map<int>", "[\"key\":7]"), ("Integer", "new Integer(7)")
        };
        foreach (var (type, value) in references)
        foreach (var op in new[] { "==", "!=" })
        foreach (var initializer in new[] { false, true })
            yield return new(type, value, op, initializer);
    }

    [TestCaseSource(nameof(Comparisons))]
    public void DirectIdentityComparison(string type, string value, string op, bool initializer)
    {
        var expression = $"(((left))) {op} (right)";
        var body = initializer ? $"let result: bool={expression}; return result;" : $"return {expression};";
        AssertSafe(Source(type, value, $"try {{ {body} }} catch {{ marker=new Plain(); }}", op));
    }

    [TestCase("try { if(left == right) { return true; } else { return false; } } catch { marker=new Plain(); }")]
    [TestCase("try { var alias=left; var result=alias == right; return result; } catch { marker=new Plain(); }")]
    [TestCase("try { return not (left != right) and (left == right or false); } catch { marker=new Plain(); }")]
    [TestCase("try { try { return left == right; } catch { } } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { var left=unknown(left); try { return left == right; } catch { marker=new Plain(); } break; }")]
    public void IdentityComparisonComposesWithExistingFlow(string body)
        => AssertSafe(Source("object", "new C()", body));

    [TestCase("==")]
    [TestCase("!=")]
    public void InvalidHandlesAreComparedWithoutHeapLookup(string op)
    {
        AssertSafe($$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function choose(left: object): bool {
 delete left; let marker: object=new C();
 if(marker is I) { try { return left {{op}} left; } catch { marker=new Plain(); } marker.emit(); } return false;
}
function main(args: string[]): int {
 let input: object=new Plain(); if(choose(input) == {{(op=="=="?"true":"false")}}) { return 42; } return 0;
}
""");
    }

    [TestCase("try { return unknown(left) == right; } catch { marker=new Plain(); }")]
    [TestCase("try { return left == unknown(right); } catch { marker=new Plain(); }")]
    [TestCase("try { return refs[0] == right; } catch { marker=new Plain(); }")]
    [TestCase("try { return new C() == right; } catch { marker=new Plain(); }")]
    [TestCase("try { return (left as C) == right; } catch { marker=new Plain(); }")]
    [TestCase("try { return left == 1; } catch { marker=new Plain(); }")]
    [TestCase("try { var left=1; return left == right; } catch { marker=new Plain(); }")]
    [TestCase("try { if(left == right) { var fail=1/zero; } return true; } catch { marker=new Plain(); }")]
    [TestCase("try { return left == right and unknownBool(); } catch { marker=new Plain(); }")]
    public void ExecutableOperandsUnboxingAndUnknownShadowsRetainCatch(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Source("object", "new C()", body)));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("left < right")]
    [TestCase("left == UnknownType.missing")]
    [TestCase("left == missing")]
    public void OrdinaryTypeAndNameChecksStillRejectInvalidExpressions(string expression)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Source("C", "new C()", $"try {{ return {expression}; }} catch {{ }}")));

    private static string Source(string type, string value, string body, string op = "==") => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(value: object): object { return value; }
function unknownBool(): bool { return true; }
function choose(left: {{type}},right: {{type}}): bool {
 let marker: object=new C(); let refs: object[]=[]; var flag=true; var zero=0;
 if(marker is I) { {{body}} marker.emit(); } return false;
}
function main(args: string[]): int {
 let left: {{type}} = {{value}}; let right: {{type}} = {{value}};
 if(choose(left,left) == {{(op=="=="?"true":"false")}} and choose(left,right) == {{(op=="=="?"false":"true")}}) { return 42; }
 return 0;
}
""";

    private static void AssertSafe(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm = new AloeVm(module, settings); vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
    }
}
