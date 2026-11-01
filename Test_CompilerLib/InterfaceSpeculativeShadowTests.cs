using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceSpeculativeShadowTests
{
    [Test, Combinatorial]
    public void LocalObjectShadow_CannotBorrowOuterIntForLoopProof(
        [Values("while", "array")] string loop,
        [Values("var alias = boxed;", "let alias: object = boxed;")] string declaration,
        [Values("var copy = alias; let number: int = copy;", "let number: int = alias;")] string initializer)
    {
        AssertUnsafeLoopRejected(loop, declaration, initializer);
    }

    [TestCase("var copy = alias; let number: int = copy;")]
    [TestCase("let number: int = alias;")]
    public void ForeachBindingShadow_CannotBorrowOuterIntForLoopProof(string initializer)
    {
        AssertUnsafeLoopRejected("binding", "", initializer);
    }

    [Test]
    public void UnknownOuterShadow_DoesNotPreventIndependentConstantInitializerProof()
    {
        Assert.That(Run(Source("while", "var alias = boxed;", "var number = 1 + 2;")), Is.EqualTo("0\nsafe\nsafe\n"));
    }

    private static void AssertUnsafeLoopRejected(string loop, string declaration, string initializer)
    {
        var source = Source(loop, declaration, initializer);
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
        // Independently confirm that unboxing really reaches the handler on both iterations.
        Assert.That(Run(source.Replace("value.emit();", "print(\"attempt\");")), Is.EqualTo("attempt\nattempt\n2\n"));
    }

    private static string Source(string loop, string declaration, string initializer)
    {
        var header = loop switch
        {
            "while" => "while(n < 2)",
            "array" => "foreach(item in [1,2])",
            _ => "foreach(alias in objects)"
        };
        return $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function main(args: string[]): int {
 let alias: int = 1;
 let boxed: object = new Plain();
 let objects: object[] = [boxed,boxed];
 let value: object = new C();
 var caught = 0;
 if(value is I) {
  var n = 0;
  {{header}} {
   value.emit();
   {{declaration}}
   try { {{initializer}} }
   catch { value = new Plain(); caught = caught + 1; }
   n = n + 1;
  }
 }
 print(caught); tick(); return 0;
}
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
