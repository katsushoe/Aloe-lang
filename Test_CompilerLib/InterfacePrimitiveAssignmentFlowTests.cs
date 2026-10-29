using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfacePrimitiveAssignmentFlowTests
{
    [TestCase("int", "2147483647")]
    [TestCase("bool", "true")]
    [TestCase("string", "\"text\"")]
    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    public void SameTypeLocalAssignment_RetainsInterfaceFact(string type, string initial)
    {
        var body = $"let alias: {type} = input; alias = ((input)); if(flag) {{ alias = input; }} while(flag) {{ alias = input; break; }}";
        if (type == "string") body += " alias = ((\"literal\"));";
        Assert.That(Run(Program(type, initial, body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("alias = source();")]
    [TestCase("alias = boxed;")]
    [TestCase("alias = boxed as string;")]
    public void UnknownAssignment_StillIncludesCatchEdge(string assignment)
    {
        var source = Program("string", "\"text\"", "let alias: string = input; " + assignment, "value = new Plain();", "value.emit();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [Test]
    public void InvalidObjectUnbox_ActuallyReachesCatch()
    {
        var source = Program("string", "\"text\"", "let alias: string = input; alias = boxed;", "print(\"caught\");", "");
        Assert.That(Run(source), Is.EqualTo("caught\ncaught\n"));
    }

    [TestCase("input = true;")]
    [TestCase("input = ((false));")]
    [TestCase("let alias: bool = input; alias = false;")]
    [TestCase("let alias: bool = input; if(flag) { let alias: bool = true; alias = false; } alias = true;")]
    [TestCase("while(flag) { input = false; break; }")]
    public void BoolLiteralAssignment_RetainsInterfaceFact(string body)
    {
        Assert.That(Run(Program("bool", "true", body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("let alias: bool = input; alias = boxed as bool;")]
    [TestCase("input = (1 / 0 == 0);")]
    [TestCase("input = flag and (1 / 0 == 0);")]
    public void BoolAssignmentWithPossibleException_KeepsCatchEdge(string body)
    {
        var source = Program("bool", "true", body, "value = new Plain();", "value.emit();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("input = not flag;")]
    [TestCase("input = flag and input;")]
    [TestCase("input = flag or input;")]
    [TestCase("input = flag == input;")]
    [TestCase("input = true or (1 / 0 == 0);")]
    [TestCase("input = false and (1 / 0 == 0);")]
    [TestCase("if(flag) { input = not input; }")]
    [TestCase("while(flag) { input = input != flag; break; }")]
    public void NonthrowingBoolExpressionAssignment_RetainsInterfaceFact(string body)
    {
        Assert.That(Run(Program("bool", "true", body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("input = flag and (1 / 0 == 0);")]
    [TestCase("input = flag or (1 / 0 == 0);")]
    [TestCase("input = not (boxed as bool);")]
    public void ThrowingBoolExpressionAssignment_KeepsCatchEdge(string body)
    {
        var source = Program("bool", "true", body, "value = new Plain();", "value.emit();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("input = 0;")]
    [TestCase("input = -2147483648;")]
    [TestCase("input = 2147483647;")]
    [TestCase("input = (7 + 5) * 2;")]
    [TestCase("input = 12 / 3;")]
    [TestCase("input = 13 % 5;")]
    [TestCase("if(flag) { input = 1 + 2; }")]
    [TestCase("while(flag) { input = 2 * 3; break; }")]
    public void NonthrowingConstantIntAssignment_RetainsInterfaceFact(string body)
    {
        Assert.That(Run(Program("int", "1", body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("input = 1 / 0;")]
    [TestCase("input = 2147483647 + 1;")]
    [TestCase("input = input + 1;")]
    [TestCase("input = boxed;")]
    public void ThrowingOrUnknownIntAssignment_KeepsCatchEdge(string body)
    {
        var source = Program("int", "1", body, "value = new Plain();", "value.emit();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("try { } catch { value = new Plain(); }")]
    [TestCase("try { input = 1 + 2; } catch { value = new Plain(); }")]
    [TestCase("try { if(flag) { input = 2; } } catch { value = new Plain(); }")]
    [TestCase("try { try { input = 3; } catch { value = new Plain(); } } catch { value = new Plain(); }")]
    [TestCase("try { input = 2; } catch { value = new Plain(); } input = 4;")]
    public void NonthrowingNestedTry_RetainsOuterInterfaceFact(string body)
    {
        Assert.That(Run(Program("int", "1", body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("try { let fail: int = 1 / 0; } catch (ZeroDivisionException) { }")]
    [TestCase("try { let fail: int = 1 / 0; } catch { input = 1; }")]
    [TestCase("try { let fail: int = 2147483647 + 1; } catch (OverflowException) { }")]
    [TestCase("if(flag) { try { let fail: int = 1 / 0; } catch (ZeroDivisionException) { } }")]
    [TestCase("try { let fail: int = 1 / 0; } catch (OverflowException) { throw; } catch (ZeroDivisionException) { }")]
    [TestCase("try { let fail: int = 2147483647 + 1; } catch (ZeroDivisionException) { throw; } catch (OverflowException) { }")]
    public void HandledConstantNestedFailure_RetainsOuterInterfaceFact(string body)
    {
        Assert.That(Run(Program("int", "1", body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("try { let fail: int = 1 / 0; } catch (OverflowException) { }")]
    [TestCase("try { let fail: int = 1 / 0; } catch { throw; }")]
    [TestCase("try { let fail: int = 1 / 0; } catch { let other: int = 1 / 0; }")]
    [TestCase("try { let fail: int = input / 0; } catch { }")]
    [TestCase("try { let fail: int = 2147483647 + 1; } catch (ZeroDivisionException) { }")]
    [TestCase("try { let fail: int = 1 / 0; } catch (ZeroDivisionException) { throw; } catch { }")]
    [TestCase("try { input = 1 / 0; } catch { input = 1; }")]
    [TestCase("try { input = input + 1; } catch { input = 1; }")]
    [TestCase("try { input = boxed; } catch { input = 1; }")]
    public void UnknownNestedTry_RemainsConservative(string body)
    {
        var source = Program("int", "1", body, "value = new Plain();", "value.emit();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
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
