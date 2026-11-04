using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceHandledDecimalAssignmentFlowTests
{
    [TestCase("try { input = 1:d / 0:d; } catch(ZeroDivisionException) { }")]
    [TestCase("try { input = 79228162514264337593543950335:d + 1:d; } catch(OverflowException) { }")]
    [TestCase("try { input = 0.0000000000000000000000000001:d * 0.0000000000000000000000000001:d; } catch(OverflowException) { }")]
    [TestCase("try { input = 0.0000000000000000000000000001:d / 79228162514264337593543950335:d; } catch(OverflowException) { }")]
    [TestCase("let alias: decimal = input; try { alias = 1:d / 0:d; } catch { }")]
    [TestCase("try { input = 1:d / 0:d; } catch { input = 1:d + 2:d; }")]
    [TestCase("if(flag) { try { input = 1:d / 0:d; } catch { } }")]
    [TestCase("while(flag) { try { input = 1:d / 0:d; } catch { } break; }")]
    [TestCase("try { input = 79228162514264337593543950335:d + 1:d; } catch(ZeroDivisionException) { throw; } catch(OverflowException) { }")]
    [TestCase("try { input = 1:d / 0:d; } catch(OverflowException) { throw; } catch(ZeroDivisionException) { }")]
    [TestCase("try { input = (1:d / 0:d) + (79228162514264337593543950335:d + 1:d); } catch(OverflowException) { throw; } catch(ZeroDivisionException) { }")]
    [TestCase("try { input = (79228162514264337593543950335:d + 1:d) + (1:d / 0:d); } catch(ZeroDivisionException) { throw; } catch(OverflowException) { }")]
    public void SingleHandledFailure_RetainsOuterInterface(string body)
    {
        foreach (var flag in new[] { "false", "true" })
            Assert.That(Run(Program(body, flag)), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase("try { input = 1:d / 0:d; } catch(OverflowException) { }")]
    [TestCase("try { input = 1:d / 0:d; } catch { throw; }")]
    [TestCase("try { input = 1:d / 0:d; } catch { input = 79228162514264337593543950335:d + 1:d; }")]
    [TestCase("try { input = 1:d / 0:d; } catch { value = new Plain(); }")]
    [TestCase("try { input = input + 1:d; } catch { }")]
    [TestCase("try { input = boxed; } catch { }")]
    [TestCase("try { input = 1:d; input = 1:d / 0:d; } catch { }")]
    [TestCase("if(flag) { let input: object = boxed; try { input = 1:d / 0:d; } catch { } }")]
    public void UnknownOrOutwardFailure_RemainsConservative(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(body, "true")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [Test]
    public void HandledFailure_OnDecimalParameter_RetainsInterface()
    {
        const string source = """
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(input: decimal): int {
 let value: object=new C();
 if(value is I) { try { try { input=1:d / 0:d; } catch(ZeroDivisionException) { } } catch { value=new Plain(); } value.emit(); }
 return 0;
}
function main(args: string[]): int { let result: int=choose(7:d); tick(); return result; }
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase("1:d / 0:d")]
    [TestCase("79228162514264337593543950335:d + 1:d")]
    public void FailedAssignment_PreservesExistingParameter(string expression)
    {
        var source = $$"""
function check(input: decimal): int {
 try { input = {{expression}}; } catch { }
 if(input == 7:d) { print("old"); return 0; }
 return 9;
}
function main(args: string[]): int { return check(7:d); }
""";
        Assert.That(Run(source), Is.EqualTo(new[] { "old" }));
    }

    private static string Program(string body, string flag) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function main(args: string[]): int {
 var input=7:d; var flag={{flag}}; let boxed: object=new Plain(); let value: object=new C();
 if(value is I) { try { {{body}} } catch { value=new Plain(); } value.emit(); }
 tick(); return 0;
}
""";

    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>(); vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output;
    }
}

