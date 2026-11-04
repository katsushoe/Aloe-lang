using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceInferredDecimalFlowTests
{
    [TestCase("var computed = 1:d + 2:d;")]
    [TestCase("var computed = 3:d - 2:d;")]
    [TestCase("var computed = 2:d * 3:d;")]
    [TestCase("var computed = 6:d / 2:d;")]
    [TestCase("var computed = 1:d / 3:d;")]
    [TestCase("try { var computed = 79228162514264337593543950335:d + 1:d; } catch(OverflowException) { }")]
    [TestCase("try { var computed = 0.0000000000000000000000000001:d * 0.0000000000000000000000000001:d; } catch(OverflowException) { }")]
    [TestCase("try { var computed = 1:d / 0:d; } catch(ZeroDivisionException) { }")]
    [TestCase("var alias = 1:d + 2:d; var copy = alias;")]
    [TestCase("if(flag) { var alias = 1:d + 2:d; } var copy = input;")]
    [TestCase("var alias = 1:d + 2:d; while(flag) { var copy = alias; break; }")]
    [TestCase("while(flag) { var alias = 1:d + 2:d; var copy = alias; flag = false; }")]
    [TestCase("if(flag) { var alias = 1:d + 2:d; var copy = alias; } else { var copy = input; }")]
    public void InferredDecimalConstant_SafeOrHandled_RetainsInterface(string statements)
    {
        foreach (var flag in new[] { "false", "true" })
        {
            var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
            var module = new AloeCompiler().Compile(Program(statements, flag));
            var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
            var output = new List<string>(); vm.OutputWriter = output.Add; vm.RunFromEntryPoint();
            Assert.That(output, Is.EqualTo(new[] { "safe" }));
            Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
            Assert.That(vm.Pop().AsInt, Is.EqualTo(0));
        }
    }

    [TestCase("var computed = 1:d / 0:d;")]
    [TestCase("var computed = input + 1:d;")]
    [TestCase("var computed = 1:d + 2;")]
    [TestCase("var computed = unknown();")]
    [TestCase("var alias = 1:d + 2:d; if(true) { let alias: object = boxed; let copy: decimal = alias; }")]
    public void UnknownOrThrowingInferredPath_RetainsCatch(string statements)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(statements, "true")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string statements, string flag) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function unknown(): decimal { return 1:d / 0:d; }
function main(args: string[]): int {
 let value: object = new C(); let boxed: object = new Plain(); var input = 2:d; var flag = {{flag}};
 if(value is I) { try { {{statements}} } catch { value = new Plain(); } value.emit(); }
 tick(); return 0;
}
""";
}
