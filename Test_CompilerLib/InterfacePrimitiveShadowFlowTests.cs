using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfacePrimitiveShadowFlowTests
{
    [Test, Combinatorial]
    public void PrimitiveShadow_PreservesNonthrowingLoopProof(
        [Values("char", "float", "decimal")] string type,
        [Values("while", "array")] string loop,
        [Values(false, true)] bool indirect)
    {
        Assert.That(Run(Source(type, loop, "var alias = alias;", indirect)),
            Is.EqualTo("0\n0\nsafe\nsafe\nsafe\nsafe\n"));
    }

    [Test, Combinatorial]
    public void ObjectShadow_CannotBorrowOuterPrimitiveType(
        [Values("char", "float", "decimal")] string type,
        [Values("while", "array")] string loop,
        [Values("var alias = boxed;", "let alias: object = boxed;")] string declaration,
        [Values(false, true)] bool indirect)
    {
        AssertUnsafeRejected(Source(type, loop, declaration, indirect));
    }

    [Test, Combinatorial]
    public void ForeachObjectBinding_CannotBorrowOuterPrimitiveType(
        [Values("char", "float", "decimal")] string type,
        [Values(false, true)] bool indirect)
    {
        AssertUnsafeRejected(Source(type, "binding", "", indirect));
    }

    [Test, Combinatorial]
    public void ExpiredObjectShadow_RestoresOuterPrimitiveType(
        [Values("char", "float", "decimal")] string type,
        [Values("while", "array")] string loop,
        [Values(false, true)] bool indirect)
    {
        Assert.That(Run(Source(type, loop, "if(flag) { var alias = boxed; }", indirect)),
            Is.EqualTo("0\n0\nsafe\nsafe\nsafe\nsafe\n"));
    }

    private static void AssertUnsafeRejected(string source)
    {
        Assert.That(Run(source.Replace("value.emit();", "print(\"attempt\");")),
            Is.EqualTo("attempt\nattempt\n2\nattempt\nattempt\n2\n"));
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Source(string type, string loop, string declaration, bool indirect)
    {
        var literal = type switch { "char" => "'A'", "float" => "-1.5", _ => "2.5:d" };
        var header = loop switch
        {
            "while" => "while(n < 2)",
            "array" => "foreach(item in [1,2])",
            _ => "foreach(alias in objects)"
        };
        var read = indirect ? $"var copy = alias; let read: {type} = copy;" : $"let read: {type} = alias;";
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(flag: bool): int {
 let alias: {{type}} = {{literal}};
 let boxed: object = new Plain();
 let objects: object[] = [boxed,boxed];
 let value: object = new C();
 var caught = 0;
 if(value is I) {
  var n = 0;
  {{header}} {
   value.emit(); {{declaration}}
   try { {{read}} let equal: bool = read == {{literal}}; }
   catch { value = new Plain(); caught = caught + 1; }
   n = n + 1;
  }
 }
 print(caught); return 0;
}
function main(args: string[]): int { let result: int = choose(false) + choose(true); tick(); return result; }
""";
    }

    private static string Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output.ToString().Replace("\r\n", "\n");
    }
}
