using System;
using System.Collections.Generic;
using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceDecimalConstantFlowTests
{
    private const string Max = "79228162514264337593543950335:d";
    private const string Tiny = "0.0000000000000000000000000001:d";

    [TestCase("1:d + 2:d")]
    [TestCase("3:d - 2:d")]
    [TestCase("2:d * 3:d")]
    [TestCase("6:d / 2:d")]
    [TestCase("(-2:d + 3:d) * (4:d / 2:d)")]
    [TestCase("1:d / 3:d")]
    [TestCase("0:d * -0.0:d")]
    [TestCase(Max + " - " + Max)]
    [TestCase("-" + Max + " + " + Max)]
    [TestCase(Tiny + " * 1:d")]
    public void ExplicitDecimalConstant_NoFailure_RetainsInterface(string expression)
    {
        var body = $"try {{ let computed: decimal = {expression}; }} catch {{ value = new Plain(); }}";
        Assert.That(Run(Program(body)), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase(Max + " + 1:d", "OverflowException", "ZeroDivisionException")]
    [TestCase("-" + Max + " - 1:d", "OverflowException", "ZeroDivisionException")]
    [TestCase(Max + " * 2:d", "OverflowException", "ZeroDivisionException")]
    [TestCase(Tiny + " * " + Tiny, "OverflowException", "ZeroDivisionException")]
    [TestCase(Tiny + " / 2:d", "OverflowException", "ZeroDivisionException")]
    [TestCase("1:d / 0:d", "ZeroDivisionException", "OverflowException")]
    [TestCase("0:d / -0.0:d", "ZeroDivisionException", "OverflowException")]
    [TestCase("(" + Max + " + 1:d) + (1:d / 0:d)", "OverflowException", "ZeroDivisionException")]
    [TestCase("(1:d / 0:d) + (" + Max + " + 1:d)", "ZeroDivisionException", "OverflowException")]
    public void ExplicitDecimalFailure_OnlyMatchingHandlerContinues(string expression, string matching, string unrelated)
    {
        var body = $"try {{ let computed: decimal = {expression}; }} catch({unrelated}) {{ value = new Plain(); }} catch({matching}) {{ }}";
        Assert.That(Run(Program(body)), Is.EqualTo(new[] { "safe" }));
        var unsafeBody = $"try {{ let computed: decimal = {expression}; }} catch({unrelated}) {{ }} catch({matching}) {{ value = new Plain(); }}";
        AssertRejected(Program(unsafeBody));
        Assert.That(Run(Program(unsafeBody, dispatch: false)), Is.Empty);
    }

    [TestCase("let computed: decimal = input + 1:d;")]
    [TestCase("let computed: decimal = 1:d + 2;")]
    [TestCase("input = (1:d / 0:d) + unknown();")]
    [TestCase("let computed: decimal = unknown() + (" + Max + " + 1:d);")]
    [TestCase("let computed: decimal = (1:d / 0:d) + unknown();")]
    public void OutsideConstantInitializerSubset_DoesNotEraseReachableCatch(string statement)
    {
        AssertRejected(Program("try { " + statement + " } catch { value = new Plain(); }"));
    }

    [TestCase("1:d + 2:d")]
    [TestCase(Max + " + 1:d")]
    [TestCase("1:d / 0:d")]
    public void NestedTry_HandledDecimalInitializer_HasNoOuterFailure(string expression)
    {
        var body = $"try {{ try {{ let computed: decimal = {expression}; }} catch {{ }} }} catch {{ value = new Plain(); }}";
        Assert.That(Run(Program(body)), Is.EqualTo(new[] { "safe" }));
    }

    [Test]
    public void Rethrow_PreservesDecimalExceptionClass()
    {
        var body = $"try {{ try {{ let computed: decimal = {Max} + 1:d; }} catch(OverflowException) {{ throw; }} }} catch(ZeroDivisionException) {{ value = new Plain(); }} catch(OverflowException) {{ }}";
        Assert.That(Run(Program(body)), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase("if(flag) { let x: decimal = 1:d + 2:d; } else { let x: decimal = 3:d * 4:d; }")]
    [TestCase("while(flag) { let x: decimal = (6:d / 2:d); break; }")]
    [TestCase("let input: decimal = 1:d + 2:d; let copy: decimal = input;")]
    public void LexicalControlFlow_NonthrowingInitializer_RetainsInterface(string statements)
    {
        foreach (var flag in new[] { "false", "true" })
            Assert.That(Run(Program("var flag = " + flag + "; try { " + statements + " } catch { value = new Plain(); }")), Is.EqualTo(new[] { "safe" }));
    }

    [TestCase("try { let x: decimal = 1:d / 0:d; } catch(ZeroDivisionException) { throw; }")]
    [TestCase("try { let x: decimal = " + Max + " + 1:d; } catch(OverflowException) { let fail: int = 1 / 0; }")]
    [TestCase("let input: object = new Plain(); try { let x: decimal = input; } catch { throw; }")]
    public void NestedThrowingOrUnboxingPath_RetainsOuterCatch(string statement)
        => AssertRejected(Program("try { " + statement + " } catch { value = new Plain(); }"));

    private static string Program(string body, bool dispatch = true) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function unknown(): decimal { return 1:d / 0:d; }
function main(args: string[]): int {
 let value: object = new C(); var input = 1:d;
 if(value is I) { {{body}} {{(dispatch ? "value.emit();" : "")}} }
 tick(); return 0;
}
""";

    private static void AssertRejected(string source)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static List<string> Run(string source)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var module = new AloeCompiler().Compile(source);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)), settings);
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        return output;
    }
}
