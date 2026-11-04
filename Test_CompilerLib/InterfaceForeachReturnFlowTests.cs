using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceForeachReturnFlowTests
{
    public static IEnumerable<TestCaseData> Types()
    {
        yield return new TestCaseData("int", "7");
        yield return new TestCaseData("bool", "true");
        yield return new TestCaseData("byte", "255");
        yield return new TestCaseData("char", "'字'");
        yield return new TestCaseData("float", "7.0");
        yield return new TestCaseData("decimal", "7:d");
        yield return new TestCaseData("string", "\"value\"");
    }

    public static IEnumerable<TestCaseData> Inputs()
    {
        foreach (var row in Types())
            foreach (var empty in new[] { false, true })
                yield return new TestCaseData(row.Arguments[0], row.Arguments[1], empty);
    }

    [TestCaseSource(nameof(Inputs))]
    public void DirectArrayBindingReturn_ExcludesCatch(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, "return (item);"), empty);

    [TestCaseSource(nameof(Inputs))]
    public void BindingAliasReturn_UsesLexicalPrimitiveType(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, $"let alias: {type}=item; return ((alias));"), empty);

    [TestCaseSource(nameof(Inputs))]
    public void ArrayParameterReturn_UsesDeclaredElementType(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, "return item;", parameterArray: true), empty);

    [TestCaseSource(nameof(Types))]
    public void SourceNameCanBeShadowedByItsOwnBinding(string type, string literal)
        => AssertReturns(Source(type, literal, false, "return values;", binding: "values"), false);

    [TestCaseSource(nameof(Types))]
    public void ExpiredObjectShadow_DoesNotEscapeItsBlock(string type, string literal)
        => AssertReturns(Source(type, literal, false, "return item;",
            beforeTry: "if(true) { let item: object=new Plain(); }"), false);

    [TestCaseSource(nameof(Types))]
    public void ObjectShadowInsideTry_RetainsCatch(string type, string literal)
        => AssertRejected(Source(type, literal, false, "let item: object=new Plain(); return item;"));

    [TestCaseSource(nameof(Types))]
    public void CallReturn_RetainsUnknownExceptionalEdge(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return unknown(item);"));

    [TestCaseSource(nameof(Types))]
    public void IndexedReturn_RetainsPossibleFailure(string type, string literal)
        => AssertRejected(Source(type, literal, true, "return values[0];", header: "trigger in triggers"));

    [TestCaseSource(nameof(Types))]
    public void LiveBreakWrite_IsJoinedAtForeachExit(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;", harmlessCatch: true,
            beforeTry: "if(flag) { value=new Plain(); break; }"));

    [TestCaseSource(nameof(Types))]
    public void LiveContinueWrite_IsCarriedIntoTheNextIteration(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;", harmlessCatch: true,
            beforeTry: "if(flag) { value=new Plain(); continue; }"));

    [TestCaseSource(nameof(Types))]
    public void ObjectArraySourceShadow_DoesNotReuseOuterElementType(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;")
            .Replace("foreach(item in values)", "if(true) { let values: object[]=[new Plain()]; foreach(item in values)")
            .Replace("  value.emit();", " } value.emit();"));

    [TestCaseSource(nameof(Types))]
    public void FunctionCallHeader_RemainsConservative(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;", header: "item in make()"));

    [Test]
    public void ClosedIntPipeBinding_PreservesEntry()
        => AssertReturns(Source("int", "7", false, "return item;", header: "item in source"), true);

    [Test]
    public void OuterTry_StillTreatsTheForeachHeaderAsPotentiallyThrowing()
        => AssertRejected(Source("float", "7.0", false, "return item;", harmlessCatch: true)
            .Replace("  foreach(", "  try { foreach(")
            .Replace("  value.emit();", " } catch { value=new Plain(); } value.emit();"));

    private static string Source(string type, string literal, bool empty, string body,
        string binding = "item", string? header = null, string beforeTry = "", bool harmlessCatch = false,
        bool parameterArray = false) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("after-empty"); } }
class Plain { }
function unknown(input: {{type}}): {{type}} { return input; }
function make(): {{type}}[] { return [{{literal}}]; }
function choose(flag: bool{{(parameterArray ? ", values: "+type+"[]" : "")}}): {{type}} {
 let value: object=new C(); {{(parameterArray ? "" : "let values: "+type+"[]="+(empty ? "[]" : "["+literal+"]")+";")}}
 let triggers: int[]=[1]; let source: pipe<int>=pipe<int>.create(); source.close();
 if(value is I) {
  foreach({{header ?? binding+" in values"}}) {
   {{beforeTry}}
   try { {{body}} } catch { {{(harmlessCatch ? "" : "value=new Plain();")}} }
  }
  value.emit();
 }
 return {{literal}};
}
function main(args: string[]): int {
 let result: {{type}}=choose(true{{(parameterArray ? ", "+(empty ? "[]" : "["+literal+"]") : "")}}); tick(); if(result != {{literal}}) { return 9; } print("returned"); return 0;
}
""".Replace("pipe<int>=", "pipe<int> =");

    private static void AssertRejected(string source)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void AssertReturns(string source, bool empty)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(output, Is.EqualTo(empty ? new[] { "after-empty", "returned" } : new[] { "returned" }));
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }
}
