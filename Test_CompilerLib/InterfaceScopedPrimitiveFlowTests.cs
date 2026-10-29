using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceScopedPrimitiveFlowTests
{
    [TestCase("let alias: string = left; let same: bool = alias == right;")]
    [TestCase("let alias: string = \"a\"; let same: bool = alias == left;")]
    [TestCase("let alias: string = ((left)); let same: bool = alias == (\"a\");")]
    [TestCase("let alias: string = left; let second: string = alias; let same: bool = second != right;")]
    [TestCase("let alias: string = left; if(alias == \"a\") { let same: bool = alias == right; }")]
    [TestCase("let alias: string = left; while(alias == \"a\") { let same: bool = alias == right; break; }")]
    [TestCase("if(flag) { let alias: string = left; let same: bool = alias == right; }")]
    [TestCase("if(flag) { let alias: string = left; let same: bool = alias == right; } else { let alias: string = right; let same: bool = alias == left; }")]
    [TestCase("while(flag) { let alias: string = left; let same: bool = alias == right; break; }")]
    [TestCase("let alias: string = left; if(flag) { let alias: bool = true; let same: bool = alias; } let same: bool = alias == right;")]
    [TestCase("let alias: string = left; while(flag) { let alias: bool = true; let same: bool = alias; break; } let same: bool = alias == right;")]
    [TestCase("if(flag) { let left: int = 1; let right: int = 2; let same: bool = left < right; } let same: bool = left == right;")]
    [TestCase("let left: int = 1; let right: int = 2; let same: bool = left < right;")]
    [TestCase("let left: bool = true; let right: bool = false; let same: bool = left == right;")]
    [TestCase("let left: bool = true; while(left) { let same: bool = left; break; }")]
    [TestCase("let maximum: bool = true; let same: bool = maximum;")]
    [TestCase("if(flag) { let maximum: bool = true; let same: bool = maximum; } let same: bool = maximum == 2147483647;")]
    [TestCase("while(flag) { let maximum: bool = true; let same: bool = maximum; break; } let same: bool = maximum == 2147483647;")]
    [TestCase("if(true) { let alias: string = left; let same: bool = alias == \"a\"; } else { let alias: string = source(); }")]
    [TestCase("let alias: string = left; while(flag) { if(alias == right) { let same: bool = alias == \"a\"; } break; }")]
    [TestCase("if(true) { let alias: string = left; let same: bool = alias == right; }")]
    [TestCase("let left: string = left; let same: bool = left == right;")]
    [TestCase("if(flag) { let flag: string = left; let same: bool = flag == \"a\"; } if(flag) { let same: bool = left == right; }")]
    [TestCase("while(flag) { let flag: string = left; if(flag == \"a\") { let same: bool = flag == right; } break; }")]
    public void ScopedPrimitiveDeclarations_HaveIndependentNonthrowingProof(string body)
    {
        Assert.That(Run(Program(body + " print(\"safe\");", "print(\"caught\");", "")), Is.EqualTo("safe\nsafe\n"));
        Assert.That(Run(Program(body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("let alias: string = source();", "caught\ncaught\n")]
    [TestCase("if(flag) { let alias: string = source(); }", "safe\ncaught\n")]
    [TestCase("while(flag) { let alias: string = source(); break; }", "safe\ncaught\n")]
    [TestCase("let alias: string = left; let number: int = fail();", "caught\ncaught\n")]
    [TestCase("let alias: string = left; if(flag) { let number: int = maximum + 1; }", "safe\ncaught\n")]
    [TestCase("let alias: string = left; while(flag and (1 / 0 == 0)) { break; }", "safe\ncaught\n")]
    [TestCase("let alias: string = value as string;", "caught\ncaught\n")]
    [TestCase("let alias: string = left; let same: bool = alias == source();", "caught\ncaught\n")]
    public void ScopedInitializerOrLaterFault_KeepsCatchEdge(string body, string expected)
    {
        AssertRejected(body);
        Assert.That(Run(Program(body + " print(\"safe\");", "print(\"caught\");", "")), Is.EqualTo(expected));
    }

    [TestCase("let alias: string = left; let count: int = maximum + 0;")]
    [TestCase("let alias: string = left; alias = value as string;")]
    [TestCase("let alias: string = left; alias = source();")]
    [TestCase("let alias: string = left; if(flag) { let inner: object = new Plain(); }")]
    public void UnsupportedInitializationAndWrites_RemainConservative(string body) => AssertRejected(body);

    [TestCase("let alias: string = 1;")]
    [TestCase("let alias: string = left; if(true) { } else { let bad: int = \"bad\"; }")]
    public void InvalidInitializationAndUnselectedBranch_RemainTypeChecked(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program(body, "", "")));
    }

    private static void AssertRejected(string body)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(body, "value = new Plain();", "value.emit();")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
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
