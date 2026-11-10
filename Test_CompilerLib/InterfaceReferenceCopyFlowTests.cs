using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceReferenceCopyFlowTests
{
    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void TypedDeclarationInsideTry(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"try {{ let alias: {returnType} = input; return alias; }} catch {{ marker=new Plain(); }}"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void TypedAliasChain(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"try {{ let alias: {returnType} = (((input))); let copy: {returnType} = alias; return copy; }} catch {{ marker=new Plain(); }}"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void ExistingReferenceAssignment(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"let alias: {returnType} = input; try {{ alias=(((input))); return alias; }} catch {{ marker=new Plain(); }}"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void TypedShadowInsideLoop(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"while(flag) {{ let input: {returnType} = input; try {{ let alias: {returnType} = input; return alias; }} catch {{ marker=new Plain(); }} break; }}"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void NestedHandlersAfterReferenceAssignment(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"let alias: {returnType} = input; try {{ try {{ alias=input; return alias; }} catch {{ }} }} catch {{ marker=new Plain(); }}"));

    [TestCaseSource(typeof(InterfaceReferenceReturnFlowTests), nameof(InterfaceReferenceReturnFlowTests.References))]
    public void BranchReferenceAssignments(string inputType, string returnType, string initializer)
        => AssertSafe(Source(inputType, returnType, initializer,
            $"let alias: {returnType} = input; try {{ if(flag) {{ alias=input; return alias; }} else {{ alias=input; return alias; }} }} catch {{ marker=new Plain(); }}"));

    [TestCase("object", "new Plain()")]
    [TestCase("int[]", "[7]")]
    [TestCase("int{}", "{7}")]
    [TestCase("int()", "(7,8)")]
    [TestCase("map<int>", "[\"key\":7]")]
    [TestCase("Integer", "new Integer(7)")]
    public void InvalidHandleCopyDoesNotDereference(string type, string initializer)
        => AssertSafe(Source(type, type, initializer,
            $"delete input; let alias: {type} = input; try {{ alias=input; return alias; }} catch {{ marker=new Plain(); }}")
            .Replace("if(result == input)", "if(result == invalid)"));

    [TestCase("try { let alias: object=1; return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: object=input; try { alias=1; return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: object=input; try { alias=unknown(input); return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: object=input; try { alias=new Plain(); return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: object=input; try { alias=refs[0]; return alias; } catch { marker=new Plain(); }")]
    [TestCase("let alias: object=input; try { alias=input as C; return alias; } catch { marker=new Plain(); }")]
    [TestCase("while(flag) { let input: int=1; try { var alias=input; return alias; } catch { marker=new Plain(); } break; }")]
    [TestCase("while(flag) { let input: int[]=unknownArray(); try { let alias: object=input; var fail=1/0; return alias; } catch { marker=new Plain(); } break; }")]
    [TestCase("try { let alias: object=input; var fail=1/0; return alias; } catch { marker=new Plain(); }")]
    [TestCase("try { marker=new Plain(); return input; } catch { }")]
    [TestCase("try { let alias: object=input; } catch { } marker=new Plain();")]
    [TestCase("var stream=pipe<int>.create(); try { var alias=stream; return alias; } catch { marker=new Plain(); }")]
    public void UnsafeOrUnknownPathsRemainConservative(string frame)
        => AssertConservative(Source("object", "object", "new Plain()", frame));

    [Test]
    public void TypedReferenceMismatchRemainsRejected()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            Source("int[]", "int[]", "[7]", "try { let alias: string[]=input; return input; } catch { }")));
        Assert.That(error!.Message, Does.Contain("Type mismatch"));
    }

    private static string Source(string inputType, string returnType, string initializer, string frame) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(value: object): object { return value; }
function unknownArray(): int[] { return [7]; }
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

    private static void AssertSafe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1)); Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }

    private static void AssertConservative(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }
}
