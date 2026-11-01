using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceScalarLiteralFlowTests
{
    [TestCase("char", "'B'")]
    [TestCase("char", "'あ'")]
    [TestCase("char", "'\\n'")]
    [TestCase("float", "0.0")]
    [TestCase("float", "2.5")]
    [TestCase("float", "-1.5")]
    [TestCase("decimal", "2.5:d")]
    [TestCase("decimal", "-2.5:d")]
    [TestCase("decimal", "79228162514264337593543950335.0:d")]
    public void LiteralReads_PreserveInterfaceAcrossInitializersAssignmentsAndComparisons(string type, string literal)
    {
        foreach (var declaration in new[] { $"let scalar: {type} = (({literal}));", $"var scalar = (({literal}));" })
        {
            var body = declaration + $" scalar = {literal}; if(flag) {{ let equal: bool = scalar == {literal}; }} while(flag) {{ scalar = (({literal})); break; }}";
            Assert.That(Run(Source(type, literal, body)), Is.EqualTo("safe\nsafe\n"));
        }
    }

    [TestCase("float", "1.5", "let scalar: float = input / 0.0;")]
    [TestCase("decimal", "1.5:d", "let scalar: decimal = input / 0.0:d;")]
    [TestCase("float", "1.5", "var scalar = boxed as float;")]
    [TestCase("decimal", "1.5:d", "var scalar = boxed as decimal;")]
    [TestCase("float", "1.5", "let scalar: float = input + 1.5;")]
    public void OtherOperations_RemainConservative(string type, string initial, string body)
    {
        var source = Source(type, initial, body);
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source.Replace("value.emit();", "print(\"continued\");")));
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [Test]
    public void DecimalFloatLiteralMix_StillFailsOrdinaryTypeCheck()
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Source("float", "1.5", "let equal: bool = input == 1.0:d;")));
        Assert.That(error!.Message, Does.Contain("Cannot compare Float and Decimal"));
    }

    [TestCase("char", "''")]
    [TestCase("char", "'AB'")]
    [TestCase("decimal", "79228162514264337593543950336.0:d")]
    public void InvalidLiterals_StillFailOrdinaryCompilation(string type, string literal)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            $"function main(args: string[]): int {{ let scalar: {type} = {literal}; return 0; }}"));
    }

    private static string Source(string type, string initial, string body) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(flag: bool,input: {{type}}): int {
 let boxed: object = new Plain();
 let value: object = new C();
 if(value is I) {
  try { {{body}} } catch { value = new Plain(); }
  value.emit();
 }
 return 0;
}
function main(args: string[]): int { let result: int = choose(false,{{initial}}) + choose(true,{{initial}}); tick(); return result; }
""";

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
