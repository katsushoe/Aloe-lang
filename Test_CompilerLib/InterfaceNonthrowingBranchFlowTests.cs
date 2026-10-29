using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceNonthrowingBranchFlowTests
{
    [TestCase("if(flag) { let number: int = 1 + 2; }")]
    [TestCase("if(not flag) { let same: bool = left == right; }")]
    [TestCase("if(flag) { let number: int = 1; } else { let number: int = 2; }")]
    [TestCase("if(left == right) { let same: bool = flag; } else { let different: bool = not flag; }")]
    [TestCase("if(false) { let number: int = fail(); }")]
    [TestCase("if(true) { let number: int = 1; } else { let number: int = fail(); }")]
    [TestCase("if(false and dangerous()) { let number: int = fail(); } else { let safe: bool = flag; }")]
    [TestCase("if(true) { } else if(dangerous()) { let number: int = fail(); }")]
    [TestCase("if(flag) { } else if(true) { let number: int = 2; } else if(dangerous()) { let number: int = fail(); }")]
    [TestCase("if(flag) { if(not flag) { let number: int = 3; } else { let same: bool = left == right; } }")]
    [TestCase("if(flag) { while(true) { let same: bool = left == right; break; } }")]
    [TestCase("while(flag) { if(flag) { break; } else { continue; } }")]
    [TestCase("while(flag) { if(flag) { let same: bool = left == right; } else { let number: int = 0; } break; }")]
    [TestCase("if(flag) { let flag: bool = false; if(flag) { let number: int = 1; } }")]
    [TestCase("if(flag) { } else if(not flag) { } else { let number: int = 1; } let safe: bool = left != right;")]
    [TestCase("if(maximum < 1) { let number: int = 1; } else { let number: int = 2; }")]
    public void ProvenBranches_ExcludeUnreachableCatchWrites(string body)
    {
        Assert.That(Run(Program(body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("if(dangerous()) { }", "caught\ncaught\n")]
    [TestCase("if(dangerous() and false) { }", "caught\ncaught\n")]
    [TestCase("if(flag and (1 / 0 == 0)) { }", "safe\ncaught\n")]
    [TestCase("if(flag) { let number: int = fail(); }", "safe\ncaught\n")]
    [TestCase("if(flag) { } else { let number: int = fail(); }", "caught\nsafe\n")]
    [TestCase("if(flag) { } else if(dangerous()) { }", "caught\nsafe\n")]
    [TestCase("if(flag) { if(not flag) { } else { let number: int = fail(); } }", "safe\ncaught\n")]
    [TestCase("if(flag) { while(true) { let number: int = fail(); break; } }", "safe\ncaught\n")]
    [TestCase("while(flag) { if(flag) { let number: int = fail(); } break; }", "safe\ncaught\n")]
    [TestCase("if(flag) { } else { } let number: int = fail();", "caught\ncaught\n")]
    [TestCase("if(true) { } else { let number: int = fail(); } let number: int = 1 / 0;", "caught\ncaught\n")]
    [TestCase("if(flag) { return fail(); }", "safe\ncaught\n")]
    [TestCase("if(flag) { let number: int = maximum + 1; }", "safe\ncaught\n")]
    [TestCase("if(flag) { let same: bool = left == source(); }", "safe\ncaught\n")]
    public void PossibleBranchException_KeepsCatchReachable(string body, string expected)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(body, "value = new Plain();", "value.emit();")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
        Assert.That(Run(Program(body + " print(\"safe\");", "print(\"caught\");", "")), Is.EqualTo(expected));
    }

    [TestCase("if(flag) { value = new Plain(); }")]
    [TestCase("if(flag) { } else { value = new Plain(); }")]
    public void ReachableBranchWrite_InvalidatesInterfaceFact(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(body, "", "value.emit();")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [Test]
    public void UnreachableElse_RemainsTypeChecked()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program("if(true) { } else { let bad: int = \"bad\"; }", "", "value.emit();")));
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
