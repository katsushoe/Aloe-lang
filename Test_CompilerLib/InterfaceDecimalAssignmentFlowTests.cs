using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceDecimalAssignmentFlowTests
{
    [TestCase("input = 1:d + 2:d;")]
    [TestCase("input = 3:d - 2:d;")]
    [TestCase("input = 2:d * 3:d;")]
    [TestCase("input = 6:d / 2:d;")]
    [TestCase("input = 1:d / 3:d;")]
    [TestCase("input = ((-2:d + 1:d) * 3:d);")]
    [TestCase("let alias: decimal = input; alias = 1:d + 2:d;")]
    [TestCase("var alias = input; alias = 1:d + 2:d;")]
    [TestCase("if(flag) { input = 1:d + 2:d; } else { input = 3:d - 2:d; }")]
    [TestCase("while(flag) { input = 1:d + 2:d; flag = false; }")]
    [TestCase("if(flag) { let input: decimal = 0:d; input = 2:d * 3:d; } input = 6:d / 2:d;")]
    [TestCase("try { input = 1:d + 2:d; } catch { value = new Plain(); }")]
    public void NonthrowingConstantDecimalAssignment_RetainsInterface(string statements)
    {
        foreach (var flag in new[] { "false", "true" })
            Assert.That(Run(Program(statements, flag)), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase("input = 1:d / 0:d;")]
    [TestCase("input = 79228162514264337593543950335:d + 1:d;")]
    [TestCase("input = 0.0000000000000000000000000001:d * 0.0000000000000000000000000001:d;")]
    [TestCase("input = input + 1:d;")]
    [TestCase("input = boxed;")]
    [TestCase("if(flag) { let input: object = boxed; input = 1:d + 2:d; }")]
    public void ThrowingUnknownOrObjectAssignment_PreservesCatch(string statements)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(statements, "true")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [Test]
    public void SuccessfulAssignment_StoresComputedValue()
    {
        const string source = """
function main(args: string[]): int {
 var input = 0:d; input = (1:d + 2:d) * 3:d;
 if(input == 9:d) { print("stored"); return 0; }
 return 9;
}
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "stored" }));
    }

    [TestCase("1:d / 0:d")]
    [TestCase("79228162514264337593543950335:d + 1:d")]
    [TestCase("0.0000000000000000000000000001:d * 0.0000000000000000000000000001:d")]
    public void ExceptionalAssignment_PreservesOldValue(string expression)
    {
        var source = $$"""
function main(args: string[]): int {
 var input = 7:d;
 try { input = {{expression}}; } catch { print("caught"); }
 if(input == 7:d) { return 0; }
 return 9;
}
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "caught" }));
    }

    private static string Program(string statements, string flag) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function main(args: string[]): int {
 let value: object = new C(); let boxed: object = new Plain(); var input = 2:d; var flag = {{flag}};
 if(value is I) { try { {{statements}} } catch { value = new Plain(); } value.emit(); }
 tick(); return 0;
}
""";

    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output;
    }
}

