using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceJoinFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
interface Copy { method emit(message: string): void; }
interface Left extends I { method left(): void; }
interface Right extends I { method right(): void; }
interface Mid extends I { method shared(): void; }
interface DeepLeft extends Mid { }
interface DeepRight extends Mid { }
interface A { method alpha(message: string): void; }
interface B { method beta(): void; }
interface X extends A, B { }
interface Y extends A, B { }
interface Z extends A { }
interface Diamond extends A, B { }
interface DiamondLeft extends Diamond { }
interface DiamondRight extends Diamond { }
class L implements Left {
    public async method emit(message: string): void { print(message); }
    public async method left(): void { print("left"); }
}
class R implements Right {
    public async method emit(message: string): void { print(message); }
    public async method right(): void { print("right"); }
}
class Specific implements DeepLeft {
    public async method emit(message: string): void { print(message); }
    public async method shared(): void { print("shared"); }
}
class Multi implements X {
    public async method alpha(message: string): void { print(message); }
    public async method beta(): void { print("beta"); }
}
class Both implements X, Y {
    public async method alpha(message: string): void { print(message); }
    public async method beta(): void { print("beta"); }
}
class Plain { }
class DiamondValue implements DiamondLeft {
    public async method alpha(message: string): void { print(message); }
    public async method beta(): void { print("beta"); }
}
""";

    [Test]
    public void AlternativeSiblingTestsNarrowToTheirCommonParentAndDispatchBothClasses()
    {
        var result = Run("""
function route(value: object): void {
    if (value is Left or value is Right) { value.emit("parent"); }
    if (not (value is Left) and not (value is Right)) { }
    else { value.emit("false-and"); }
}
function main(args: string[]): int {
    route(new L()); route(new R()); route(new Plain());
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "parent", "false-and", "parent", "false-and" }));
        Assert.That(result.ExitCode, Is.Zero);
    }

    [Test]
    public void AlternativeTestsChooseUniqueMostSpecificCommonAncestor()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new Specific();
    if (value is DeepLeft or value is DeepRight) { value.shared(); value.emit("root"); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "shared", "root" }));
    }

    [TestCase("value is X or value is Y or value is Z")]
    [TestCase("value is Z or value is X or value is Y")]
    [TestCase("value is Y or value is Z or value is X")]
    [TestCase("not (not (value is X) and not (value is Y) and not (value is Z))")]
    [TestCase("(value is X or value is Y) or value is Z")]
    [TestCase("value is Z or (value is X or value is Y)")]
    [TestCase("((value is X or value is Y) or value is X) or value is Z")]
    [TestCase("not ((not (value is X) and not (value is Y)) and not (value is Z))")]
    [TestCase("not (not (value is Z) and (not (value is X) and not (value is Y)))")]
    [TestCase("(not (not (value is X) and not (value is Y))) or value is Z")]
    public void AlternativeChainsIntersectAllAncestorsBeforeSelectingAUniqueResult(string condition)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new Multi();
    if (CONDITION) { value.alpha("common-a"); }
    tick(); return 0;
}
""".Replace("CONDITION", condition));
        Assert.That(result.Output, Is.EqualTo(new[] { "common-a" }));
    }

    [Test]
    public void DiamondCommonAncestorRetainsItsContractsFromBothParents()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new DiamondValue();
    if (value is DiamondLeft or value is DiamondRight) { value.alpha("diamond"); value.beta(); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "diamond", "beta" }));
    }

    [Test]
    public void CommonParentWhileProofsReestablishAfterSiblingReassignment()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new L(); var count = 0;
    while ((value is Left or value is Right) and count < 2) {
        value.emit("loop"); count = count + 1;
        if (count == 1) { value = new R(); continue; }
        value = new Plain();
    }
    GC.require(); GC.finish(); tick(); return count;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "loop", "loop" }));
        Assert.That(result.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void NestedFalseAndJoinAppliesToElseIfAndRestoresAfterWrites()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new Multi();
    if ((not (value is X) and not (value is Y)) and not (value is Z)) { }
    else if (true) {
        value.alpha("before"); value = new Plain();
        if ((value is X or value is Y) or value is Z) { value.alpha("unreachable"); }
    }
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "before" }));
    }

    [TestCase("(value is X or value is Y) and true or value is Z")]
    [TestCase("((value is X or value is Y) and (value is X or value is Y)) or value is Z")]
    [TestCase("(value is X and value is Y) or value is Z")]
    [TestCase("((value is X or value is Y) and value is A) or value is Z")]
    [TestCase("not ((not (value is X) or not (value is Y)) and not (value is Z))")]
    [TestCase("(not (not (value is X or value is Y) or false)) or value is Z")]
    public void MixedSimultaneousAndAlternativeProofsRetainGuaranteedAncestors(string condition)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new Both();
    if (CONDITION) { value.alpha("mixed"); }
    tick(); return 0;
}
""".Replace("CONDITION", condition));
        Assert.That(result.Output, Is.EqualTo(new[] { "mixed" }));
    }

    [Test]
    public void MixedFalseOrProofsApplyToElseAndWhileAndInvalidateAfterWrite()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new Both();
    if ((not (value is X) or not (value is Y)) and not (value is Z)) { }
    else { value.alpha("else"); }
    while ((value is X and value is Y) or value is Z) {
        value.alpha("loop"); value = new Plain();
    }
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "else", "loop" }));
    }

    [TestCase("if (value is Left or value is Right) { value.left(); }")]
    [TestCase("if (value is Left or value is Right) { value = new Plain(); value.emit(\"bad\"); }")]
    [TestCase("if (value is Left or value is Right or true) { value.emit(\"bad\"); }")]
    [TestCase("if (value is Left or value is A) { value.emit(\"bad\"); }")]
    [TestCase("if (value is Left or value is Copy) { value.emit(\"structural\"); }")]
    [TestCase("if (value is X or value is Y) { value.alpha(\"ambiguous\"); }")]
    [TestCase("if (value is X or value is Y) { value.beta(); }")]
    [TestCase("if ((value is X or value is Y) or value is Z) { value.beta(); }")]
    [TestCase("if ((value is X or value is Y) or true) { value.alpha(\"missing\"); }")]
    [TestCase("if ((value is X or value is Y) or value is Left) { value.alpha(\"no-common\"); }")]
    [TestCase("if ((value is X or value is Y) or value is Z) { delete value; value.alpha(\"invalidated\"); }")]
    [TestCase("if ((value is X or value is Y) and true) { value.alpha(\"still-ambiguous\"); }")]
    [TestCase("if ((value is X or value is Y) and true or true) { value.alpha(\"missing\"); }")]
    [TestCase("if ((value is X or value is Y) and true or value is Z) { value.beta(); }")]
    [TestCase("if ((value is X or value is Y) and true or value is Z) { value = new Plain(); value.alpha(\"changed\"); }")]
    [TestCase("let peer: object = value; if ((value is X and peer is Y) or peer is Z) { value.alpha(\"wrong-local\"); }")]
    [TestCase("if (((value is X or value is Y) and unbox value) or value is Z) { value.alpha(\"consumed\"); }")]
    [TestCase("let peer: object = value; if ((value is X or value is Y) or peer is Z) { value.alpha(\"wrong-local\"); }")]
    [TestCase("if (value is Left and value is Right) { value.emit(\"intersection\"); }")]
    [TestCase("if (value is Left or value is Right) { } value.emit(\"scope\");")]
    [TestCase("let peer: object = value; if (value is Left or peer is Right) { value.emit(\"wrong-local\"); }")]
    public void MissingAmbiguousOrUnsupportedFactsRemainCompileErrors(string body)
    {
        var source = Declarations + "function main(args: string[]): int { let value: object = new L(); " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    private static (long ExitCode, string[] Output) Run(string body)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Declarations + body)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
