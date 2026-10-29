using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceNonthrowingWhileFlowTests
{
    [TestCase("while(flag) { let same: bool = left == right; break; }")]
    [TestCase("while(not flag) { let same: bool = left != right; break; }")]
    [TestCase("while(left == right) { let number: int = 1 + 2; break; }")]
    [TestCase("while(true) { let same: bool = left == right; break; }")]
    [TestCase("while(false) { let result: int = fail(); }")]
    [TestCase("while(false and dangerous()) { let result: int = fail(); }")]
    [TestCase("while(flag) { while(true) { let number: int = 2; break; } break; }")]
    [TestCase("while(flag) { while(false) { let result: int = fail(); } break; }")]
    [TestCase("while(flag) { let flag: bool = false; while(flag) { continue; } break; }")]
    [TestCase("while(flag) { break; let result: int = fail(); } let number: int = 3;")]
    public void ProvenWhile_ExcludesUnreachableCatchWrites(string body)
    {
        Assert.That(Run(Program(body, "value = new Plain();", "value.emit();")),
            Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("while(dangerous()) { break; }", "caught\ncaught\n")]
    [TestCase("while(flag and (1 / 0 == 0)) { break; }", "safe\ncaught\n")]
    [TestCase("while(flag) { let result: int = fail(); break; }", "safe\ncaught\n")]
    [TestCase("while(flag) { let result: int = 2147483647 + 1; break; }", "safe\ncaught\n")]
    [TestCase("while(flag) { while(true) { let result: int = fail(); break; } break; }", "safe\ncaught\n")]
    [TestCase("while(flag) { break; } let result: int = fail();", "caught\ncaught\n")]
    [TestCase("while(false) { break; } let result: int = 1 / 0;", "caught\ncaught\n")]
    [TestCase("while(flag) { return fail(); }", "safe\ncaught\n")]
    [TestCase("while(flag) { let same: bool = left == source(); break; }", "safe\ncaught\n")]
    [TestCase("while(flag) { let number: int = maximum + 1; break; }", "safe\ncaught\n")]
    public void PossibleException_KeepsCatchWriteReachable(string body, string expected)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(body, "value = new Plain();", "value.emit();")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
        Assert.That(Run(Program(body + " print(\"safe\");", "print(\"caught\");", "")),
            Is.EqualTo(expected));
    }

    [TestCase("while(flag) { while(true) { break; } continue; let result: int = fail(); }")]
    [TestCase("while(flag) { continue; let result: int = fail(); }")]
    public void Continue_DoesNotMakeFollowingBodyStatementReachable(string body)
    {
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(
            Program(body, "value = new Plain();", "value.emit();")));
    }

    [Test]
    public void UnreachableCatch_RemainsTypeChecked()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program("while(false) { break; }", "let invalid: int = \"bad\";", "value.emit();")));
    }

    private static string Program(string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function fail(): int { return 1 / 0; }
function dangerous(): bool { let result: int = fail(); return true; }
function source(): string { let result: int = fail(); return "a"; }
function choose(flag: bool, left: string, right: string): int {
    let maximum: int = 2147483647;
    let value: object = new C();
    if(value is I) {
        try { {{body}} } catch { {{handler}} }
        {{after}}
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(false, "a", "a") + choose(true, "a", "b");
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
