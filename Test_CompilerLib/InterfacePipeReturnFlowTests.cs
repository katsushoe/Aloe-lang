using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfacePipeReturnFlowTests
{
    public static IEnumerable<TestCaseData> Types()
    {
        yield return new TestCaseData("int", "7");
        yield return new TestCaseData("bool", "true");
        yield return new TestCaseData("byte", "255");
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
    public void LocalPipeBindingReturn_ExcludesUnreachableCatch(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, "return item;"), empty);

    [TestCaseSource(nameof(Inputs))]
    public void AliasReturn_PreservesPipeElementKind(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, $"let alias: {type}=item; return (alias);"), empty);

    [TestCaseSource(nameof(Inputs))]
    public void ParameterPipeReturn_UsesDeclaredContract(string type, string literal, bool empty)
        => AssertReturns(Source(type, literal, empty, "return item;", parameter: true), empty);

    [TestCaseSource(nameof(Types))]
    public void ObjectShadow_RetainsPossibleUnboxingFailure(string type, string literal)
        => AssertRejected(Source(type, literal, false, "let item: object=new Plain(); return item;"));

    [TestCaseSource(nameof(Types))]
    public void CallReturn_RetainsUnknownFailure(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return unknown(item);"));

    [TestCaseSource(nameof(Types))]
    public void BreakWrite_IsJoinedAtPipeExit(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;", beforeTry: "if(flag) { value=new Plain(); break; }", harmlessCatch: true));

    [TestCaseSource(nameof(Types))]
    public void ContinueWrite_IsCarriedIntoTheNextRead(string type, string literal)
        => AssertRejected(Source(type, literal, false, "return item;", beforeTry: "if(flag) { value=new Plain(); continue; }", harmlessCatch: true));

    [Test]
    public void OuterTry_RetainsPipeReadExceptionalEdges()
        => AssertRejected(Source("int", "7", false, "return item;", harmlessCatch: true)
            .Replace("  foreach(", "  try { foreach(")
            .Replace("  value.emit();", " } catch { value=new Plain(); } value.emit();"));

    [Test]
    public void StructPipeFieldRead_RemainsConservative()
        => AssertRejected(Source("int", "7", false, "return item.key;")
            .Replace("interface I", "struct Row { field key: int; } interface I")
            .Replace("pipe<int>", "pipe<Row>")
            .Replace("let element: int=7", "let element: Row=Row { key:7 }"));

    [Test]
    public void StringArrayPipeIndex_RetainsBoundsFailure()
        => AssertRejected(Source("string", "\"value\"", false, "return item[0];")
            .Replace("pipe<string>", "pipe<string[]>")
            .Replace("let element: string=\"value\"", "let element: string[]=[]"));

    private static string Source(string type, string literal, bool empty, string body,
        bool parameter = false, string beforeTry = "", bool harmlessCatch = false)
    {
        var setup = $"let input: pipe<{type}> = pipe<{type}>.create(1); " +
            (empty ? "" : $"let element: {type}={literal}; input.write(element); ") + "input.close();";
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("after-empty"); } }
class Plain { }
function unknown(input: {{type}}): {{type}} { return input; }
function choose(flag: bool{{(parameter ? ", input: pipe<"+type+">" : "")}}): {{type}} {
 {{(parameter ? "" : setup)}}
 let value: object=new C();
 if(value is I) {
  foreach(item in input) {
   {{beforeTry}}
   try { {{body}} } catch { {{(harmlessCatch ? "" : "value=new Plain();")}} }
  }
  value.emit();
 }
 return {{literal}};
}
function main(args: string[]): int {
 {{(parameter ? setup : "")}}
 let result: {{type}}=choose(true{{(parameter ? ", input" : "")}}); tick();
 if(result != {{literal}}) { return 9; } print("returned"); return 0;
}
""";
    }

    private static void AssertRejected(string source)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void AssertReturns(string source, bool empty)
    {
        var settings = new AloeVmSettings(); settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add; vm.RunFromEntryPoint();
        Assert.That(output, Is.EqualTo(empty ? new[] { "after-empty", "returned" } : new[] { "returned" }));
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.Zero);
    }
}
