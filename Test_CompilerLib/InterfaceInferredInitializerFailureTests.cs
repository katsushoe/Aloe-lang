using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceInferredInitializerFailureTests
{
    [TestCase("try { var fail = 1 / 0; } catch (ZeroDivisionException) { }", true)]
    [TestCase("if(flag) { try { var fail = 1 / 0; } catch (ZeroDivisionException) { } }", true)]
    [TestCase("while(flag) { try { var fail = 1 / 0; } catch (ZeroDivisionException) { } break; }", true)]
    [TestCase("try { var fail = 2147483647 + 1; } catch (OverflowException) { }", true)]
    [TestCase("if(flag) { try { var fail = 2147483647 + 1; } catch (OverflowException) { } }", true)]
    [TestCase("while(flag) { try { var fail = 2147483647 + 1; } catch (OverflowException) { } break; }", true)]
    [TestCase("try { var value = 1 % 0; } catch (ZeroDivisionException) { }", true)]
    [TestCase("if(flag) { try { var value = 1 % 0; } catch (ZeroDivisionException) { } }", true)]
    [TestCase("while(flag) { try { var value = 1 % 0; } catch (ZeroDivisionException) { } break; }", true)]
    [TestCase("try { var fail = 1 / 0; } catch (OverflowException) { }", false)]
    [TestCase("try { var fail = 1 / 0; } catch { throw; }", false)]
    [TestCase("try { var fail = 1 / 0; } catch { input = 1 / 0; }", false)]
    [TestCase("try { var fail = 1 / 0; } catch { value = new Plain(); }", false)]
    [TestCase("try { var fail = input / 0; } catch { }", false)]
    [TestCase("try { var fail = (1 / 0 == 0); } catch { }", false)]
    [TestCase("try { var first = 1; var fail = 1 / 0; } catch { }", false)]
    [TestCase("try { var fail = 1 / 0; } catch { } input = 1 / 0;", false)]
    public void InferredIntInitializerFailure_HasBoundedHandledFlow(string body, bool accepted)
    {
        var source = Program("int", "1", body, "value = new Plain();", "value.emit();");
        if (accepted)
            Assert.That(Run(source), Is.EqualTo("safe\nsafe\n"));
        else
        {
            var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
            Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
        }
    }

    private static string Program(string type, string initial, string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function source(): string { let fail: int = 1 / 0; return "never"; }
function choose(flag: bool, input: {{type}}): int {
    let boxed: object = new Plain();
    let value: object = new C();
    if(value is I) {
        try { {{body}} } catch { {{handler}} }
        {{after}}
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(false, {{initial}}) + choose(true, {{initial}});
    tick();
    return result;
}
""";

    private static string Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output.ToString().Replace("\r\n", "\n");
    }
}
