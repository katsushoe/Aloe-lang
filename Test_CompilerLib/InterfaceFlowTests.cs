using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
interface J extends I { method extra(): void; }
interface K { method other(): void; }
class C implements J {
    public property Value: int { get { return 7; } }
    public async method emit(message: string): void { print(message); }
    public async method extra(): void { print("extra"); }
}
class Other implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
""";

    [Test]
    public void InterfaceFlow_ParenthesesAndConjunctionsNarrowStableLocalsToMostSpecificInterface()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = value; var enabled = true;
    if (enabled and (((value is I))) and (peer is I)) { value.emit("value"); peer.emit("peer"); }
    if ((value is I) and ((value is J))) { value.extra(); }
    if ((peer is I or false) and value is I) { value.emit("guard"); }
    let bad: object = new Plain(); if ((bad is I) and enabled) { bad.emit("unreachable"); }
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("value\npeer\nextra\nguard\n"));
        Assert.That(result.ExitCode, Is.Zero);
    }

    [Test]
    public void InterfaceFlow_ReassignmentUsesOriginalDeclaredTypeAndClearsFacts()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (((value is I)) and true) { value = new Plain(); }
    print(value is I); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("false\n"));
        Assert.That(() => new AloeCompiler().Compile(Declarations + " function main(args: string[]): int { let value: C = new C(); if (value is I) { value = new Other(); } return 0; }"),
            Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void InterfaceFlow_AlreadyConformingClassRetainsClassMembers()
    {
        Assert.That(Run("function main(args: string[]): int { let value: C = new C(); if ((value is I) and true) { print(value.Value); } return 0; }").Output,
            Is.EqualTo("7\n"));
    }

    [Test]
    public void InterfaceFlow_DisjunctionsRetainOnlyFactsGuaranteedByEveryBranch()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if ((value is I and false) or (value is J and true)) { value.emit("or"); }
    if (value is J or true and value is I) { value.emit("precedence"); }
    if ((value is I or value is J) and value is J) { value.extra(); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("or\nprecedence\nextra\n"));
    }

    [Test]
    public void InterfaceFlow_ConstantArithmeticExcludesImpossibleDisjunctionArms()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (1 + 1 != 2 or value is I) { value.emit("arithmetic-or"); }
    if (2 * 3 != 6 or value is J) { value.extra(); }
    if (1 + (2 * 3) != 7 or value is I) { value.emit("operator-precedence"); }
    if (((16 / 4) - 3) != 1 or value is J) { value.extra(); }
    if (-3 * 2 != -6 or value is I) { value.emit("signed-arithmetic"); }
    if (-2147483648 % -1 != 0 or value is J) { value.extra(); }
    if (false == true or value is I) { value.emit("boolean-equality"); }
    if (true != false and value is J) { value.extra(); }
    if (1 + 1 < 3 and value is I) { value.emit("less-than-constant"); }
    if (2 * 3 <= 6 and value is J) { value.extra(); }
    if (2 + 3 > 6 and value is I) { value.emit("greater-than-constant"); }
    if (2 + 3 >= 5 and value is J) { value.extra(); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("arithmetic-or\nextra\noperator-precedence\nextra\nsigned-arithmetic\nextra\nboolean-equality\nextra\nless-than-constant\nextra\nextra\n"));
    }

    [Test]
    public void InterfaceFlow_OverflowAndDivisionByZeroRemainUnknown()
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (2147483647 + 1 != 0 or value is I) { value.emit("overflow"); }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (2147483648 != 0 or value is I) { value.emit("out-of-range-literal"); }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (1 / 0 == 0 or value is I) { value.emit("division-by-zero"); }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (-2147483648 / -1 == 0 or value is I) { value.emit("division-overflow"); }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void InterfaceFlow_KnownOverflowOnlyReachesCompatibleTypedCatch()
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            let safe: int = 2 * 3;
            if (true) { let overflow: int = 2147483647 + 1; }
            else { let unreachable: int = 1 / 0; }
        }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("overflow-does-not-reach-zero-division-catch");
    }
    return 0;
}
"""), Throws.Nothing);

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { while (true) { let overflow: int = 2147483647 + 1; } }
        catch (ZeroDivisionException) { value = new Plain(); }
    }
    return 0;
}
"""), Throws.Nothing);

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            if (false) { let unreachable: int = 1 / 0; }
            while (false) { let alsoUnreachable: int = 1 / 0; }
            let overflow: int = 2147483647 + 1;
        }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("false-body-does-not-hide-overflow-edge");
    }
    return 0;
}
"""), Throws.Nothing);

        Assert.That(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { if (false) { let unreachable: int = 1 / 0; } else { let overflow: int = 2147483647 + 1; } }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("false-condition-selects-overflow-branch");
    }
    return 0;
}
"""), Throws.Nothing);

        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let overflow: int = 2147483647 + 1; }
        catch (OverflowException) { value = new Plain(); }
        value.emit("reachable-overflow-catch-invalidates-interface-fact");
    }
    return 0;
}
"""));

        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let overflow: int = 2147483647 + 1; }
        catch (SystemException) { value = new Plain(); }
        value.emit("system-exception-matches-overflow");
    }
    return 0;
}
"""));

        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function mayThrow(): int { return 1; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let unknown: int = mayThrow(); let overflow: int = 2147483647 + 1; }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("unknown-operation-keeps-catch-reachable");
    }
    return 0;
}
"""));

        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function main(args: string[]): int {
    let value: object = new C(); var flag = true;
    if (value is I) {
          try { if (flag) { let overflow: int = 2147483647 + 1; } else { let zero: int = 1 / 0; } }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("unknown-branch-keeps-catch-reachable");
    }
    return 0;
}
"""));
    }

    [TestCase("flag", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("not flag", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("(flag and true) or false", "let safe: int = 2 * 3;", "let safe: int = 7;", "let failure: int = 2147483647 + 1;")]
    [TestCase("flag or true", "let failure: int = 2147483647 + 1;", "let failure: int = 1 / 0;", "")]
    [TestCase("flag and false", "let failure: int = 1 / 0;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("(flag and false) or true", "let failure: int = 2147483647 + 1;", "let failure: int = 1 / 0;", "")]
    [TestCase("flag == true", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("false != flag", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("(flag == false) or false", "let safe: int = 1;", "let safe: int = 2;", "let failure: int = 2147483647 + 1;")]
    [TestCase("(not flag) == true", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    public void InterfaceFlow_NonthrowingBoolBranchesJoinExactArithmeticException(
        string condition, string thenBody, string elseBody, string continuation)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { if ({{condition}}) { {{thenBody}} } else { {{elseBody}} } {{continuation}} }
        catch (ZeroDivisionException) { value = new Plain(); }
        catch (OverflowException) { }
        value.emit("preserved");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("preserved\npreserved\n"));
    }

    [TestCase("flag", "let failure: int = 1 / 0;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("ready()", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("flag and ready()", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("flag", "let safe: int = 1;", "let failure: int = 2147483647 + 1;", "let failure: int = 1 / 0;")]
    [TestCase("ready() == true", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("flag == ready()", "let failure: int = 2147483647 + 1;", "let failure: int = 2147483647 + 1;", "")]
    [TestCase("flag != false", "let failure: int = 1 / 0;", "let failure: int = 2147483647 + 1;", "")]
    public void InterfaceFlow_BranchJoinRetainsMixedAndUnknownExceptionEdges(
        string condition, string thenBody, string elseBody, string continuation)
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + $$"""
function ready(): bool { return true; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { if ({{condition}}) { {{thenBody}} } else { {{elseBody}} } {{continuation}} }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("must-retain-possible-zero-division");
    }
    return 0;
}
function main(args: string[]): int { return choose(false); }
"""), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("flag and false", "let skipped: int = 1 / 0;")]
    [TestCase("flag or true", "let failure: int = 2147483647 + 1;")]
    [TestCase("not (flag and false)", "let failure: int = 2147483647 + 1;")]
    [TestCase("(flag == true) and false", "let skipped: int = 1 / 0;")]
    [TestCase("(flag != false) or true", "let failure: int = 2147483647 + 1;")]
    public void InterfaceFlow_NonthrowingBoolLoopConditionsKeepExactFailurePaths(string condition, string body)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { while ({{condition}}) { {{body}} } let failure: int = 2147483647 + 1; }
        catch (ZeroDivisionException) { value = new Plain(); }
        catch (OverflowException) { }
        value.emit("preserved-loop");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("preserved-loop\npreserved-loop\n"));
    }

    [TestCase("flag")]
    [TestCase("flag and ready()")]
    public void InterfaceFlow_PossiblySkippedOrThrowingLoopConditionRetainsMixedExceptions(string condition)
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + $$"""
function ready(): bool { return true; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { while ({{condition}}) { let failure: int = 2147483647 + 1; } let failure: int = 1 / 0; }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("possible-zero-iteration-zero-division");
    }
    return 0;
}
function main(args: string[]): int { return choose(false); }
"""), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if (true) { let safe: int = 2 * 3; }")]
    [TestCase("if (true) { let safe: int = 2 * 3; } else { let skipped: int = mayThrow(); }")]
    [TestCase("if (false) { let skipped: int = mayThrow(); } else { let safe: int = 2 * 3; }")]
    [TestCase("if (true) { if (false) { let skipped: int = 1 / 0; } else { let safe: int = 6; } }")]
    [TestCase("if (false) { let skipped: int = mayThrow(); } else if (true) { let safe: int = 6; } else { let skipped: int = mayThrow(); }")]
    [TestCase("if (false) { let skipped: int = mayThrow(); } else if (false) { let skipped: int = mayThrow(); } else { let safe: int = 6; }")]
    [TestCase("if (true) { let safe: int = 6; } else if (mayThrow() > 0) { let skipped: int = mayThrow(); }")]
    [TestCase("if (true) { let safe: int = 6; } else if (2147483647 + 1 > 0) { let skipped: int = 1 / 0; }")]
    [TestCase("if (false) { let skipped: int = mayThrow(); } else if (false) { let skipped: int = mayThrow(); }")]
    public void InterfaceFlow_ArithmeticCatchClassificationContinuesAfterSelectedSafeBlocks(string prefix)
    {
        foreach (var (failure, matchingCatch, unrelatedCatch) in new[]
        {
            ("2147483647 + 1", "OverflowException", "ZeroDivisionException"),
            ("1 / 0", "ZeroDivisionException", "OverflowException")
        })
        {
            var source = $$"""
function mayThrow(): int { return 1; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            {{prefix}}
            let failure: int = {{failure}};
        }
        catch ({{unrelatedCatch}}) { value = new Plain(); }
        catch ({{matchingCatch}}) { print("handled"); }
        value.emit("preserved");
    }
    tick(); return 0;
}
""";
            var result = Run(source);
            Assert.That(result.Output, Is.EqualTo("handled\npreserved\n"));
            Assert.That(() => new AloeCompiler().Compile(Declarations + source.Replace(
                $"catch ({matchingCatch}) {{ print(\"handled\"); }}",
                $"catch ({matchingCatch}) {{ value = new Plain(); }}")),
                Throws.TypeOf<AloeCompileException>());
        }
    }

    [TestCase("while (true) { let safe: int = 2 * 3; }")]
    [TestCase("if (true) { return 0; }")]
    [TestCase("if (true) { let unknown: int = mayThrow(); }")]
    [TestCase("if (false) { let safe: int = 1; } else if (mayThrow() > 0) { let safe: int = 2; }")]
    public void InterfaceFlow_ArithmeticCatchClassificationDoesNotPassUnknownOrNoncontinuingBlocks(string prefix)
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 1; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { {{prefix}} let failure: int = 2147483647 + 1; }
        catch (ZeroDivisionException) { value = new Plain(); }
        value.emit("must-not-infer-later-overflow");
    }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if", "2147483647 + 1 > 0", "OverflowException", "ZeroDivisionException")]
    [TestCase("while", "1 / 0 > 0", "ZeroDivisionException", "OverflowException")]
    [TestCase("if", "0 < (2147483647 + 1)", "OverflowException", "ZeroDivisionException")]
    [TestCase("while", "not (0 < (1 / 0))", "ZeroDivisionException", "OverflowException")]
    [TestCase("if", "true and (2147483647 + 1 > 0)", "OverflowException", "ZeroDivisionException")]
    [TestCase("while", "false or (1 / 0 > 0)", "ZeroDivisionException", "OverflowException")]
    [TestCase("if", "(2147483647 + 1) > (1 / 0)", "OverflowException", "ZeroDivisionException")]
    [TestCase("if", "(1 / 0) > (2147483647 + 1)", "ZeroDivisionException", "OverflowException")]
    [TestCase("if", "true or false and (1 / 0 > 0)", "OverflowException", "ZeroDivisionException")]
    [TestCase("while", "false and (1 / 0 > 0)", "OverflowException", "ZeroDivisionException")]
    [TestCase("if", "(2147483647 + 1) > mayThrow()", "OverflowException", "ZeroDivisionException")]
    public void InterfaceFlow_ConditionFailureReachesOnlyCompatibleCatch(
        string statement, string condition, string matchingCatch, string unrelatedCatch)
    {
        var source = $$"""
function mayThrow(): int { return 1 / 0; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            {{statement}} ({{condition}}) { let safe: int = 1; }
            let later: int = 2147483647 + 1;
        }
        catch ({{unrelatedCatch}}) { value = new Plain(); }
        catch ({{matchingCatch}}) { print("handled"); }
        value.emit("preserved");
    }
    tick(); return 0;
}
""";
        Assert.That(Run(source).Output, Is.EqualTo("handled\npreserved\n"));
        Assert.That(() => new AloeCompiler().Compile(Declarations + source.Replace(
            $"catch ({matchingCatch}) {{ print(\"handled\"); }}",
            $"catch ({matchingCatch}) {{ value = new Plain(); }}")),
            Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("mayThrow() > (1 / 0)")]
    [TestCase("ready() and (1 / 0 > 0)")]
    [TestCase("ready() or (1 / 0 > 0)")]
    [TestCase("true and ready() and (1 / 0 > 0)")]
    public void InterfaceFlow_UnknownConditionOperandKeepsEarlierExceptionTypesReachable(string condition)
    {
        Assert.That(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 2147483647 + 1; }
function ready(): bool { return mayThrow() > 0; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { if ({{condition}}) { let safe: int = 1; } }
        catch (OverflowException) { value = new Plain(); }
        value.emit("earlier-call-can-overflow");
    }
    return 0;
}
"""), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void InterfaceFlow_WhileConditionsNarrowStableLocalsForEveryIteration()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    while ((value is I or value is J) and count < 2) {
        value.emit("loop"); count = count + 1; continue;
    }
    while (value is I) { value.emit("break"); break; }
    let bad: object = new Plain(); while (bad is I) { bad.emit("unreachable"); break; }
    GC.require(); GC.finish(); tick(); return count;
}
""");
        Assert.That(result.Output, Is.EqualTo("loop\nloop\nbreak\n"));
        Assert.That(result.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void InterfaceFlow_NegationAndElseUseFactsForTheSelectedTruthBranch()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = value; var count = 0;
    if (not (value is I)) { print("bad"); } else { value.emit("else"); }
    if (not (not (value is J))) { value.extra(); }
    if (not ((not (value is I)) or false)) { value.emit("demorgan"); }
    if ((not (value is I)) or false) { print("bad"); } else { value.emit("or-else"); }
    if ((not (value is I)) and (not (value is J))) { print("bad"); } else { value.emit("join-else"); }
    if (not ((value is I) and (peer is I))) { print("bad"); }
    else { value.emit("both"); peer.emit("peer"); }
    while ((not (not (value is I))) and count < 1) {
        value.emit("loop"); count = count + 1;
    }
    tick(); return count;
}
""");
        Assert.That(result.Output, Is.EqualTo("else\nextra\ndemorgan\nor-else\njoin-else\nboth\npeer\nloop\n"));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [Test]
    public void InterfaceFlow_ElseStabilityIsIndependentOfWritesInThenBranch()
    {
        Assert.That(Run("function main(args: string[]): int { let value: object = new C(); if (not (value is I)) { value = new Plain(); } else { value.emit(\"else\"); } tick(); return 0; }").Output,
            Is.EqualTo("else\n"));
        Assert.That(Run("function main(args: string[]): int { let value: object = new C(); if (not (value is I)) { } else { value = new Plain(); } print(value is I); return 0; }").Output,
            Is.EqualTo("false\n"));
    }

    [Test]
    public void InterfaceFlow_ElseIfChainsInheritFalseFactsAndRefineTheirOwnBranches()
    {
        var result = Run("""
function route(value: object): void {
    if (not (value is I)) { print("plain"); }
    else if (not (value is J)) { value.emit("parent"); }
    else { value.extra(); }
}
function main(args: string[]): int {
    route(new C()); route(new Other()); route(new Plain());
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Does.StartWith("plain\n"));
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1),
            Is.EquivalentTo(new[] { "parent", "extra" }));
    }

    [Test]
    public void InterfaceFlow_InheritedElseIfFactsApplyToConditionsAndRestoreAfterChain()
    {
        var result = Run("""
function ready(value: I): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { print("bad"); }
    else if (false) { value.emit("bad"); }
    else if (ready(value)) { value.emit("condition"); }
    else { value.emit("bad"); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("condition\n"));
    }

    [Test]
    public void InterfaceFlow_WritesInElseIfTailPreserveOriginalAssignmentType()
    {
        Assert.That(Run("function main(args: string[]): int { let value: object = new C(); if (not (value is I)) { } else if (true) { value = new Plain(); } else { } print(value is I); return 0; }").Output,
            Is.EqualTo("false\n"));
    }

    [Test]
    public void InterfaceFlow_DirectWritesAllowEarlierReadsAndRequireFreshTestsAfterward()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        value.emit("before"); value = new Other();
        if (value is I) { value.emit("fresh"); }
    }
    print(value is I); GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Does.StartWith("true\n"));
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1),
            Is.EquivalentTo(new[] { "before", "fresh" }));
    }

    [Test]
    public void InterfaceFlow_DirectWhileWriteClearsFactsBeforeTheNextIteration()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    while (value is I) { value.emit("loop"); value = new Plain(); count = count + 1; }
    tick(); return count;
}
""");
        Assert.That(result.Output, Is.EqualTo("loop\n"));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [Test]
    public void InterfaceFlow_DirectDeleteClearsFactsAndKeepsQueuedTargetAlive()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) { value.emit("before-delete"); delete (value); }
    print(value == invalid); GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("true\nbefore-delete\n"));
    }

    [Test]
    public void InterfaceFlow_AssignmentToNarrowedChildUsesDeclaredParentContract()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: I = new C();
    if (value is J) { value.extra(); value = new Other(); value.emit("parent"); }
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(new[] { "extra", "parent" }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InterfaceFlow_NestedConditionalWritesKeepEarlierAndSiblingReads(bool change)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        value.emit("before");
        if (CHANGE) { value.emit("then"); value = new Plain(); }
        else { value.emit("else"); }
        if (value is I) { value.emit("fresh"); }
    }
    print(value is I); GC.require(); GC.finish(); tick(); return 0;
}
""".Replace("CHANGE", change ? "true" : "false"));
        Assert.That(result.Output, Is.EqualTo(change ? "false\nbefore\nthen\n" : "true\nbefore\nelse\nfresh\n"));
    }

    [Test]
    public void InterfaceFlow_ElseIfWritesDoNotSuppressReadsInOtherBranches()
    {
        var result = Run("""
function route(value: object, selected: int): void {
    if (not (value is I)) { print("plain"); }
    else if (selected == 0) { value.emit("first"); }
    else if (selected == 1) { value.emit("second"); delete value; }
    else { value.emit("last"); value = new Plain(); }
}
function main(args: string[]): int {
    route(new C(), 0); route(new C(), 1); route(new C(), 2);
    GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(new[] { "first", "second", "last" }));
    }

    [Test]
    public void InterfaceFlow_ConditionalJoinPreservesFactsOnlyOnUnchangedFallthroughPaths()
    {
        var result = Run("""
function route(value: object, leave: bool): void {
    if (value is I) {
        if (leave) { value = new Plain(); return; }
        value.emit("survivor");
        if (true) { let value: object = new Plain(); value = new Other(); }
        value.emit("outer");
        if (value is J) { value.extra(); }
        value.emit("parent");
    }
}
function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("survivor\nouter\nextra\nparent\n"));
    }

    [Test]
    public void InterfaceFlow_NestedWritesRetainDeclaredParentAndUnaffectedLocals()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: I = new C(); let peer: object = new C();
    if (value is J and peer is I) {
        if (true) { value.extra(); value = new Other(); }
        else { value.extra(); }
        value.emit("parent"); peer.emit("peer");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(new[] { "extra", "parent", "peer" }));
    }

    [TestCase("value = new Plain();", "false")]
    [TestCase("delete value;", "true")]
    public void InterfaceFlow_WithWritesInvalidateOriginalButRetainCapturedTarget(string write, string invalid)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        with (value as C) {
            value.emit("before"); WRITE
            print(value == invalid); GC.require(); GC.finish();
            .extra();
        }
    }
    tick(); return 0;
}
""".Replace("WRITE", write));
        Assert.That(result.Output, Is.EqualTo(invalid + "\nbefore\nextra\n"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InterfaceFlow_WithConditionalWritesUseSiblingStatesAndFreshTests(bool change)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        with (new C()) {
            if (CHANGE) { value.emit("change"); value = new Other(); }
            else { value.emit("same"); }
            if (value is I) { value.emit("fresh"); }
        }
    }
    tick(); return 0;
}
""".Replace("CHANGE", change ? "true" : "false"));
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(change ? new[] { "change", "fresh" } : new[] { "same", "fresh" }));
    }

    [Test]
    public void InterfaceFlow_WithShadowLocalsAndReturningWritesPreserveOuterFacts()
    {
        var result = Run("""
function route(value: object, leave: bool): void {
    if (value is I) {
        with (new C()) {
            if (leave) { with (new Integer(1)) { value = new Plain(); return; } }
            with (new Integer(2)) { let value: object = new Plain(); delete value; }
            value.emit("inside");
        }
        value.emit("outside");
    }
}
function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("inside\noutside\n"));
    }

    [Test]
    public void InterfaceFlow_WithFailedConsumptionRetainsRuntimeReference()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    try {
        if (value is I) { with (new C()) { value.emit("before"); let number: int = unbox value; } }
    } catch { print(value is I); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("true\nbefore\n"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InterfaceFlow_TryWritesAllowEarlierReadsAndCatchRetesting(bool writeFirst)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = new C();
    if (value is I and peer is I) {
        try {
            value.emit("before"); FIRST
            let number: int = 1 / 0; LAST
        } catch {
            peer.emit("peer");
            if (value is I) { value.emit("fresh"); }
        }
    }
    print(value is I); GC.require(); GC.finish(); tick(); return 0;
}
""".Replace("FIRST", writeFirst ? "value = new Plain();" : "")
    .Replace("LAST", writeFirst ? "" : "value = new Plain();"));
        Assert.That(result.Output, Does.StartWith(writeFirst ? "false\n" : "true\n"));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.That(lines, Is.EquivalentTo(writeFirst ? new[] { "before", "peer" } : new[] { "before", "peer", "fresh" }));
        if (!writeFirst) Assert.That(Array.IndexOf(lines, "before"), Is.LessThan(Array.IndexOf(lines, "fresh")));
    }

    [Test]
    public void InterfaceFlow_RelationalConstantBranchesDoNotPolluteExceptionalEntry()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            if (1 + 2 > 3) { value = new Plain(); }
            let number: int = 1 / 0;
        } catch { value.emit("catch"); }
        value.emit("after");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("catch\nafter\n"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InterfaceFlow_TryCatchRetainsDeclaredParentContractAfterChildWrite(bool fail)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: I = new C();
    if (value is J) {
        try { value.extra(); value = new Other(); FAIL }
        catch { value.emit("parent"); }
        value.emit("after");
    }
    tick(); return 0;
}
""".Replace("FAIL", fail ? "let number: int = 1 / 0;" : ""));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Is.EquivalentTo(fail ? new[] { "extra", "parent", "after" } : new[] { "extra", "after" }));
        Assert.That(lines.Where(line => line is "parent" or "after"),
            Is.EqualTo(fail ? new[] { "parent", "after" } : new[] { "after" }));
    }

    [Test]
    public void InterfaceFlow_ReturningCatchWritesDoNotInvalidateNormalContinuation()
    {
        var result = Run("""
function route(value: object, fail: bool): void {
    if (value is I) {
        try { if (fail) { let number: int = 1 / 0; } }
        catch { value.emit("catch"); delete value; return; }
        value.emit("normal");
    }
}
function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }
""");
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EquivalentTo(new[] { "catch", "normal" }));
    }

    [Test]
    public void InterfaceFlow_TryShadowWritesLeaveOuterFactsAvailableInCatchAndAfterward()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let value: object = new Plain(); delete value; let number: int = 1 / 0; }
        catch { value.emit("outer-catch"); }
        value.emit("outer-after");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("outer-catch\nouter-after\n"));
    }

    [Test]
    public void InterfaceFlow_TryFailedConsumptionNeedsRetestButRetainsRuntimeReference()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { value.emit("before"); let number: int = unbox value; }
        catch { if (value is I) { value.emit("fresh"); } }
    }
    print(value is I); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("true\nbefore\nfresh\n"));
    }

    [Test]
    public void InterfaceFlow_WhileRechecksFactsAfterNestedWritesAndContinue()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = new C(); var count = 0;
    if (value is I and peer is I) {
        value.emit("before-loop");
        while (value is I) {
            peer.emit("peer"); count = count + 1;
            if (count == 1) { value.emit("first"); value = new Other(); continue; }
            with (new C()) { value.emit("second"); value = new Plain(); }
        }
        peer.emit("after-loop");
    }
    print(value is I); GC.require(); GC.finish(); tick(); return count;
}
""");
        Assert.That(result.Output, Does.StartWith("false\n"));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.That(lines, Is.EquivalentTo(new[] { "before-loop", "peer", "first", "peer", "second", "after-loop" }));
        Assert.That(lines.Where(line => line is "peer" or "after-loop"), Is.EqualTo(new[] { "peer", "peer", "after-loop" }));
        Assert.That(Array.IndexOf(lines, "before-loop"), Is.LessThan(Array.IndexOf(lines, "first")));
        Assert.That(result.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void InterfaceFlow_WhileDeleteInsideTryKeepsQueuedTargetAndExitsByBreak()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    while (value is I) {
        try { value.emit("before-delete"); delete value; } catch { }
        break;
    }
    print(value == invalid); GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("true\nbefore-delete\n"));
    }

    [TestCase("let items: int[] = [1, 2];")]
    [TestCase("let items: int() = (1, 2);")]
    [TestCase("let items: map<int/string> = [1: \"one\", 2: \"two\"];")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); items.write(1); items.write(2); items.close();")]
    public void InterfaceFlow_ForeachUsesFreshBodyTestsAfterLoopCarriedWrites(string setup)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = new C(); SETUP
    if (value is I and peer is I) {
        value.emit("before");
        foreach (item in items) {
            peer.emit("peer");
            if (value is I) { value.emit("fresh"); value = new Other(); }
        }
        peer.emit("after");
    }
    tick(); return 0;
}
""".Replace("SETUP", setup));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Is.EquivalentTo(new[] { "before", "peer", "fresh", "peer", "fresh", "after" }));
        Assert.That(lines.Where(line => line is "peer" or "after"), Is.EqualTo(new[] { "peer", "peer", "after" }));
    }

    [Test]
    public void InterfaceFlow_InnerLoopWritesRequireFreshProofInOuterIteration()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    while (value is I) {
        value.emit("outer");
        while (value is I) { value.emit("inner"); value = new Plain(); }
        if (value is I) { value.emit("unreachable"); }
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("outer\ninner\n"));
    }

    [Test]
    public void InterfaceFlow_MapPairForeachClearsLoopCarriedFactsAndRetainsUnchangedPeers()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); let peer: object = new C();
    let items: map<int/string> = [1: "one", 2: "two"];
    if (value is I and peer is I) {
        value.emit("before");
        foreach (key, text in items) {
            peer.emit(text);
            if (value is I) { value.emit("first"); delete value; }
        }
        peer.emit("after");
    }
    print(value == invalid); GC.require(); GC.finish(); tick(); return 0;
}
""");
        Assert.That(result.Output, Does.StartWith("true\n"));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.That(lines, Is.EquivalentTo(new[] { "before", "one", "first", "two", "after" }));
        Assert.That(lines.Where(line => line is "one" or "two" or "after"), Is.EqualTo(new[] { "one", "two", "after" }));
        Assert.That(Array.IndexOf(lines, "before"), Is.LessThan(Array.IndexOf(lines, "first")));
    }

    [TestCase("let value: object = value;", "new Plain()")]
    [TestCase("var value = value as object;", "new Plain()")]
    [TestCase("var value: object;", "new Plain()")]
    [TestCase("var value = value;", "new Other()")]
    public void InterfaceFlow_LoopShadowWritesPreserveOuterReadsBeforeDeclarationAndAfterLoop(string declaration, string replacement)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    if (value is I) {
        while (count < 1) {
            value.emit("before-shadow"); DECLARATION
            value = REPLACEMENT; delete value; count = count + 1;
        }
        value.emit("after-loop");
    }
    tick(); return count;
}
""".Replace("DECLARATION", declaration).Replace("REPLACEMENT", replacement));
        Assert.That(result.Output, Is.EqualTo("before-shadow\nafter-loop\n"));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [TestCase("let items: int[] = [1, 2];", "value")]
    [TestCase("let items: int{} = {1, 2};", "value")]
    [TestCase("let items: map<int/int> = [1: 10, 2: 20];", "value")]
    [TestCase("let items: map<int/int> = [1: 10, 2: 20];", "key, value")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); items.write(1); items.close();", "value")]
    public void InterfaceFlow_ForeachBindingsShadowOuterReferenceOnlyInsideBody(string setup, string binding)
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); SETUP
    if (value is I) {
        foreach (BINDING in items) { value = value + 1; }
        value.emit("outer");
    }
    tick(); return 0;
}
""".Replace("SETUP", setup).Replace("BINDING", binding));
        Assert.That(result.Output, Is.EqualTo("outer\n"));
    }

    [Test]
    public void InterfaceFlow_ShadowScopeEndsBeforeSiblingAndLaterOuterReads()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    if (value is I) {
        while (count < 1) {
            if (true) { let value: object = new Plain(); delete value; }
            else { value.emit("unreachable"); }
            value.emit("after-branch"); count = count + 1;
        }
        value.emit("after-loop");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("after-branch\nafter-loop\n"));
    }

    [Test]
    public void InterfaceFlow_ForeachHeaderConsumptionStillTargetsOuterLocal()
    {
        var source = Declarations + """
function items(flag: bool): int[] { return [1]; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        foreach (value in items(unbox value)) { value = 2; }
        value.emit("bad");
    }
    tick(); return 0;
}
""";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        Assert.That(() => new AloeCompiler().Compile(source.Replace("value.emit(\"bad\");", "")), Throws.Nothing);
    }

    [TestCase("while (value is I) { if (true) { value = new Plain(); } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { let items: int[] = [1, 2]; foreach (item in items) { value.emit(\"bad\"); value = new Plain(); } }")]
    [TestCase("if (value is I) { let items: int[] = []; foreach (item in items) { delete value; } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { while (unbox value) { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { try { value = new Plain(); } catch { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { try { delete value; } catch { } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { try { let zero: int = 1 / 0; } catch { delete value; } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { try { let number: int = unbox value; } catch { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { try { if (true) { value = new Plain(); return 0; } } catch { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { try { try { delete value; } catch { throw; } } catch { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { try { try { let number: int = 1 / 0; } catch { delete value; throw; } } catch { value.emit(\"bad\"); } }")]
    [TestCase("try { if (value is I) { value.emit(\"before\"); } } catch { value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { with (new C()) { value = new Plain(); value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { with (new C()) { delete value; } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { with (new C()) { let number: int = unbox value; value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { with (new C()) { if (true) { value = new Plain(); } } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { with (value) { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { with (new Integer(unbox value)) { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { if (true) { value = new Plain(); } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (false) { } else { delete value; } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (value is J) { value = new Other(); } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (true) { value = value; } else { value = new C(); } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (unbox value) { } else { value.emit(\"bad\"); } }")]
    [TestCase("if (value is I) { if (true) { let number: int = unbox value; } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (true) { value = new Plain(); } else { value = new Other(); } if (value is I) { } value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else if (true) { value = new Plain(); } else { value.emit(\"ok\"); } value.emit(\"bad\");")]
    [TestCase("if (value is I and unbox value) { value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (true) { if (true) { delete value; } } value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { if (true) { value = new Plain(); } else if (value is J) { } value.emit(\"bad\"); }")]
    [TestCase("let parent: I = new C(); if (parent is J) { parent = new Other(); parent.extra(); }")]
    [TestCase("if (value is I) { value = value; value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { value = new C(); value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { value = new Other(); if (value is I) { } value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else if (unbox value) { value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else if (true) { } value.emit(\"bad\");")]
    [TestCase("if (value is I) { } else if (true) { value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I) and false) { } else { value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else { value = new Plain(); value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else { delete value; value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else { let number: int = unbox value; value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { } else { } value.emit(\"bad\");")]
    [TestCase("while (not (value is I)) { value.emit(\"bad\"); break; }")]
    [TestCase("if ((value is I) or (value is J)) { value.extra(); }")]
    [TestCase("if (value is I or value is K) { value.emit(\"bad\"); }")]
    [TestCase("let peer: object = value; if (value is I or peer is I) { value.emit(\"bad\"); }")]
    [TestCase("while ((value is I) and false) { } value.emit(\"bad\");")]
    [TestCase("while (value is I or value is J) { delete value; value.emit(\"bad\"); }")]
    [TestCase("while (value is I) { let number: int = unbox value; value.emit(\"bad\"); }")]
    [TestCase("if (value is I or true and value is K) { value.emit(\"bad\"); }")]
    [TestCase("if (value is I or value is J or true) { value.emit(\"bad\"); }")]
    [TestCase("if (value is I or true) { value.emit(\"bad\"); }")]
    [TestCase("if (not (value is I)) { value.emit(\"bad\"); }")]
    [TestCase("if ((value is I) == true) { value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { value = new Plain(); value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { delete (value); value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { let number: int = unbox (value); value.emit(\"bad\"); }")]
    [TestCase("if (value is I) { } else { value.emit(\"bad\"); }")]
    [TestCase("if (((value is I)) and true) { } value.emit(\"bad\");")]
    [TestCase("if (value is I and value is K) { value.emit(\"bad\"); }")]
    public void InterfaceFlow_RejectsUnprovenOrMutableNarrowing(string body)
    {
        var source = Declarations + " function main(args: string[]): int { let value: object = new C(); " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if (value is I) { while (false) { let value: int = unbox value; } value.emit(\"after\"); }")]
    [TestCase("if (value is I) { while (false) { if (true) { let value: object = new Plain(); delete value; } value = new Plain(); } value.emit(\"after\"); }")]
    [TestCase("if (value is I) { while (false) { value = new Plain(); } value.emit(\"after\"); }")]
    [TestCase("while (value is I) { while (false) { delete value; } value.emit(\"after\"); }")]
    [TestCase("if (value is I) { with (new C()) { while (false) { value.emit(\"inside\"); value = new Plain(); } } }")]
    [TestCase("if (not (value is I)) { } else if (true) { while (false) { value = new Plain(); } value.emit(\"after\"); }")]
    [TestCase("if (value is I) { while (false) { value.emit(\"inside\"); value = new Plain(); } }")]
    public void UnreachableWhileWritesPreserveProvenFacts(string body)
    {
        var source = Declarations + " function main(args: string[]): int { let value: object = new C(); " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.Nothing);
    }

    [TestCase("delete value;")]
    [TestCase("let number: int = unbox value;")]
    [TestCase("value = new Plain();")]
    public void ProvenUnreachableCatchWritesPreserveFollowingInterfaceFact(string catchBody)
    {
        var result = Run($$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {} catch { {{catchBody}} }
        value.emit("after-empty-try");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("after-empty-try\n"));
    }

    [Test]
    public void ProvenUnreachableCatchStillReceivesTypeChecking()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + "function main(args: string[]): int { try {} catch { let invalid: int = true; } return 0; }"));
    }

    [TestCase("delete value;")]
    [TestCase("let number: int = unbox value;")]
    [TestCase("value = new Plain();")]
    public void TypeShadowedCatchConsumersPreserveFollowingFact(string catchBody)
    {
        var result = Run($$"""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let zero: int = 1 / 0; }
        catch (Exception) {}
        catch (ZeroDivisionException) { {{catchBody}} }
        value.emit("after-type-shadowed-catch");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("after-type-shadowed-catch\n"));
    }

    [Test]
    public void TypeShadowedCatchStillReceivesTypeChecking()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + "function main(args: string[]): int { try { let zero: int = 1 / 0; } catch (Exception) {} catch (ZeroDivisionException) { let invalid: int = true; } return 0; }"));
    }

    [Test]
    public void InterfaceFlow_ElseIfBranchesJoinAtTheirCommonInterface()
    {
        var result = Run("""
interface K2 extends I { }
class KValue implements K2 { public async method emit(message: string): void { print(message); } }
function route(value: object, first: bool, second: bool): void {
    if (first) {
        if (not (value is J)) { return; }
    } else if (second) {
        if (not (value is K2)) { return; }
    } else { return; }
    value.emit("else-if-join");
}
function main(args: string[]): int {
    route(new C(), true, false);
    route(new KValue(), false, true);
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("else-if-join\nelse-if-join\n"));
    }

    [Test]
    public void InterfaceFlow_StringCharAndNumericConstantBranchesDoNotPolluteCatchEntry()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if ("left" == "right") { value = new Plain(); }
        if ('z' < 'a') { value = new Plain(); }
        if (2.5 < 1.5) { value = new Plain(); }
        if (16777216.0 == 16777217) { value = new Plain(); }
        if (16777216.0 > 16777217) { value = new Plain(); }
        if (16777216.0 >= 16777217) { value = new Plain(); }
        if (not (16777216.0 != 16777217)) { value = new Plain(); }
        if (not (16777216.0 <= 16777217)) { value = new Plain(); }
        if (16777217 < 16777216.0) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("literal-catch-entry");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("literal-catch-entry\n"));
    }

    [Test]
    public void InterfaceFlow_MixedNumericOrderingKeepsCatchWriteReachable()
    {
        Assert.That(Run("function main(args: string[]): int { print(16777216.0 < 16777217); print(16777217 > 16777216.0); return 0; }").Output,
            Is.EqualTo("true\ntrue\n"));
        var source = Declarations + """
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (16777217 > 16777216.0) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("must-not-be-narrowed");
    }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void InterfaceFlow_GuaranteedDivisionInIfConditionDoesNotExposeBodyWriteToCatch()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (1 / 0 > 0) { value = new Plain(); }
    } catch {
        value.emit("condition-catch");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("condition-catch\n"));
    }

    [Test]
    public void InterfaceFlow_UnknownShortCircuitBeforeDivisionKeepsCatchConservative()
    {
        foreach (var condition in new[]
        {
            "ready() or (1 / 0 > 0)",
            "ready() and true or (1 / 0 > 0)",
            "ready() or true and (1 / 0 > 0)"
        })
        {
            var source = Declarations + $$"""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if ({{condition}}) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("must-remain-conservative");
    }
    return 0;
}
""";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source), condition);
        }
    }

    [Test]
    public void InterfaceFlow_UnknownAndFalseSkipsBranchWritesBeforeLaterException()
    {
        var source = """
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (ready() and false) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("unknown-and-false-catch");
    }
    tick(); return 0;
}
""";
        var result = Run(source);
        Assert.That(result.Output, Is.EqualTo("unknown-and-false-catch\n"));
    }

    [Test]
    public void InterfaceFlow_IfBodyIsUnreachableWhenShortCircuitCannotBeTrue()
    {
        var result = Run("""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (ready() and (1 / 0 > 0)) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("if-condition-catch");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("if-condition-catch\n"));
    }

    [Test]
    public void InterfaceFlow_NegatedShortCircuitUsesComplementaryOutcomes()
    {
        var safe = Run("""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (not (ready() or (1 / 0 > 0))) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch { value.emit("negated-condition-catch"); }
    tick(); return 0;
}
""");
        Assert.That(safe.Output, Is.EqualTo("negated-condition-catch\n"));

        var unsafeSource = Declarations + """
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (not (ready() and (1 / 0 > 0))) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch { value.emit("must-remain-conservative"); }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(unsafeSource));
    }

    [Test]
    public void InterfaceFlow_NegatedShortCircuitControlsWhileReachability()
    {
        var safe = Run("""
function ready(): bool { return false; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while (not (ready() or (1 / 0 > 0))) { value = new Plain(); }
    } catch { value.emit("negated-loop-catch"); }
    tick(); return 0;
}
""");
        Assert.That(safe.Output, Is.EqualTo("negated-loop-catch\n"));

        var unsafeSource = Declarations + """
function ready(): bool { return false; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while (not (ready() and (1 / 0 > 0))) { value = new Plain(); break; }
        let zero: int = 1 / 0;
    } catch { value.emit("must-remain-conservative"); }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(unsafeSource));
    }

    [Test]
    public void InterfaceFlow_ConditionOutcomeTableRespectsNotAndShortCircuitPrecedence()
    {
        var cases = new (string Condition, bool ThenCanRun)[]
        {
            ("not false and (1 / 0 > 0)", false),
            ("not true or (1 / 0 > 0)", false),
            ("not false or (1 / 0 > 0)", true),
            ("not (ready() or (1 / 0 > 0))", false),
            ("not (ready() and (1 / 0 > 0))", true),
            ("not (ready()) and (1 / 0 > 0)", false),
            ("not (ready()) or (1 / 0 > 0)", true),
            ("not (ready() and false) and (1 / 0 > 0)", false),
            ("not (ready() and true) or (1 / 0 > 0)", true),
            ("ready() and false or (1 / 0 > 0)", false),
            ("ready() or false and (1 / 0 > 0)", true)
        };

        foreach (var (condition, thenCanRun) in cases)
        {
            var source = Declarations + $$"""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if ({{condition}}) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch { value.emit("condition-table-catch"); }
    return 0;
}
""";
            TestDelegate compile = () => { _ = new AloeCompiler().Compile(source); };
            if (thenCanRun)
                Assert.Throws<AloeCompileException>(compile, condition);
            else
                Assert.DoesNotThrow(compile, condition);
        }
    }

    [Test]
    public void InterfaceFlow_NoTrueIfConditionKeepsReachableElseWrites()
    {
        var source = Declarations + """
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (ready() and (1 / 0 > 0)) { } else { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("must-retain-else-write");
    }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void InterfaceFlow_LoopBodyIsUnreachableWhenShortCircuitCannotBeTrue()
    {
        var result = Run("""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while (ready() and (1 / 0 > 0)) { value = new Plain(); }
    } catch {
        value.emit("loop-condition-catch");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("loop-condition-catch\n"));
    }

    [Test]
    public void InterfaceFlow_WhileWithPossibleTrueShortCircuitKeepsBodyWriteInCatch()
    {
        foreach (var condition in new[]
        {
            "ready() and true or (1 / 0 > 0)",
            "ready() or true and (1 / 0 > 0)"
        })
        {
            var source = Declarations + $$"""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while ({{condition}}) { value = new Plain(); break; }
        let zero: int = 1 / 0;
    } catch {
        value.emit("must-remain-conservative");
    }
    return 0;
}
""";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source), condition);
        }
    }

    [Test]
    public void InterfaceFlow_NestedNoTrueLoopDoesNotPolluteOuterLoopFacts()
    {
        var result = Run("""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while (true) {
            while (ready() and (1 / 0 > 0)) { value = new Plain(); }
            value.emit("after-inner-loop");
            break;
        }
    } catch {
        value.emit("nested-loop-catch");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("nested-loop-catch\n"));
    }

    [Test]
    public void InterfaceFlow_GuaranteedDivisionInWhileConditionDoesNotExposeBodyWriteToCatch()
    {
        foreach (var condition in new[]
        {
            "1 / 0 > 0",
            "true and (1 / 0 > 0)",
            "false or (1 / 0 > 0)",
            "ready() and false or (1 / 0 > 0)"
        })
        {
            var result = Run($$"""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        while ({{condition}}) { value = new Plain(); }
    } catch {
        value.emit("while-condition-catch");
    }
    tick(); return 0;
}
""");
            Assert.That(result.Output, Is.EqualTo("while-condition-catch\n"), condition);
        }
    }

    [Test]
    public void InterfaceFlow_GuaranteedDivisionInEvaluatedShortCircuitOperandDoesNotExposeBodyWriteToCatch()
    {
        foreach (var condition in new[]
        {
            "true and (1 / 0 > 0)",
            "(true and (1 / 0 > 0))",
            "false or (1 / 0 > 0)",
            "(false or (1 / 0 > 0))",
            "true and true and (1 / 0 > 0)",
            "false or false or (1 / 0 > 0)",
            "true and false or (1 / 0 > 0)",
            "false and (1 / 0 > 0) or (1 / 0 > 0)",
            "1 / 0 > 0 or 2 / 0 > 0",
            "1 > (1 / 0)",
            "1 == 1 and (1 / 0 > 0)",
            "ready() and false or (1 / 0 > 0)",
            "0 != 1 and (1 / 0 > 0)",
            "2 >= 2 and (1 / 0 > 0)",
            "1 < 0 or (1 / 0 > 0)",
            "\"aloe\" == \"aloe\" and (1 / 0 > 0)",
            "\"aloe\" != \"aloe\" or (1 / 0 > 0)",
            "not false and (1 / 0 > 0)",
            "0 == (1 / 0)",
            "0 != (1 / 0)",
            "0 < (1 / 0)",
            "0 <= (1 / 0)",
            "0 > (1 / 0)",
            "0 >= (1 / 0)"
        })
        {
            var result = Run($$"""
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if ({{condition}}) { value = new Plain(); }
    } catch {
        value.emit("short-circuit-catch");
    }
    tick(); return 0;
}
""");
            Assert.That(result.Output, Is.EqualTo("short-circuit-catch\n"), condition);
        }
    }

    [Test]
    public void InterfaceFlow_SkippedShortCircuitDivisionKeepsReachableBodyWriteInCatchAnalysis()
    {
        var source = Declarations + """
function ready(): bool { return true; }
function main(args: string[]): int {
    let value: object = new C();
    if (not (value is I)) { return 0; }
    try {
        if (true or (1 / 0 > 0)) { value = new Plain(); }
        if ((true or (1 / 0 > 0))) { value = new Plain(); }
        if (true or false and (1 / 0 > 0)) { value = new Plain(); }
        if (1 > 0 or (1 / 0 > 0)) { value = new Plain(); }
        if (ready() or true) { value = new Plain(); }
        let zero: int = 1 / 0;
    } catch {
        value.emit("must-remain-conservative");
    }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [TestCase("if (flag) { let failure: int = 1 / 0; } else { let failure: int = 2147483647 + 1; }")]
    [TestCase("if (flag) { let safe: int = 1; } else { let failure: int = 2147483647 + 1; } let failure: int = 1 / 0;")]
    [TestCase("while (flag) { let failure: int = 2147483647 + 1; } let failure: int = 1 / 0;")]
    public void InterfaceFlow_FiniteArithmeticExceptionSetExcludesUnrelatedAndAlreadyHandledCatches(string body)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        catch (Exception) { value = new Plain(); }
        value.emit("finite-exceptions");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("finite-exceptions\nfinite-exceptions\n"));
    }

    [TestCase("if (flag) { let failure: int = 1 / 0; } else { let failure: int = mayThrow(); }")]
    [TestCase("if (ready()) { let failure: int = 1 / 0; } else { let failure: int = 2147483647 + 1; }")]
    [TestCase("if (flag) { let failure: int = 1 / 0; } let later: int = mayThrow();")]
    public void InterfaceFlow_FiniteArithmeticExceptionSetRetainsUnknownOperationCatches(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function ready(): bool { return true; }
function mayThrow(): int { return 1; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        value.emit("must-retain-unknown-catch");
    }
    return 0;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    [TestCase("if (flag) { return 21; } return 1 / 0;")]
    [TestCase("if (flag) { return 1 / 0; } return 2147483647 + 1;")]
    [TestCase("if (flag) { return (1 + 2) * 7; } return (1 / 0) + (2147483647 + 1);")]
    public void InterfaceFlow_ArithmeticReturnExpressionsRetainExactExceptionSets(string body)
    {
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        catch (Exception) { value = new Plain(); }
        value.emit("return-exception");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Does.Contain("return-exception\n"));
    }

    [TestCase("if (flag) { return mayThrow(); } return 1 / 0;")]
    [TestCase("if (flag) { return 1 / 0; } return mayThrow();")]
    public void InterfaceFlow_UnknownReturnExpressionsKeepCatchWritesConservative(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 21; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        value.emit("must-retain-unknown-return");
    }
    return 21;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    [Test]
    public void InterfaceFlow_BareReturnDoesNotAddAnExceptionalEdge()
    {
        var result = Run("""
function route(flag: bool): void {
    let value: object = new C();
    if (value is I) {
        try { if (flag) { return; } let failure: int = 1 / 0; }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        value.emit("bare-return");
    }
}
function main(args: string[]): int {
    route(false); route(true); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("bare-return\n"));
    }

    [TestCase("while (true) { if (flag) { break; } let failure: int = 1 / 0; } let failure: int = 2147483647 + 1;", true)]
    [TestCase("while (flag) { break; } let failure: int = 1 / 0;", true)]
    [TestCase("while (true) { while (true) { break; } if (flag) { break; } let failure: int = 1 / 0; } let failure: int = 2147483647 + 1;", true)]
    [TestCase("while (flag) { continue; } let failure: int = 1 / 0;", false)]
    [TestCase("while (true) { if (flag) { continue; } let failure: int = 1 / 0; }", false)]
    [TestCase("while (flag) { if (flag) { break; } continue; } let failure: int = 1 / 0;", true)]
    [TestCase("while (true) { if (flag) { let failure: int = 2147483647 + 1; } break; } let failure: int = 1 / 0;", true)]
    public void InterfaceFlow_LoopTransfersKeepFiniteArithmeticExceptionSets(string body, bool runBoth)
    {
        var calls = runBoth ? "choose(false) + choose(true)" : "choose(false)";
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        catch (Exception) { value = new Plain(); }
        value.emit("loop-transfer");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = {{calls}};
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(runBoth ? 42 : 21));
        Assert.That(result.Output, Is.EqualTo(runBoth ? "loop-transfer\nloop-transfer\n" : "loop-transfer\n"));
    }

    [Test]
    public void InterfaceFlow_InnerBreakDoesNotReachStatementsAfterOuterInfiniteLoop()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try {
            while (true) { while (true) { break; } let failure: int = 1 / 0; }
            let unreachable: int = 2147483647 + 1;
        }
        catch (OverflowException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        value.emit("inner-break");
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("inner-break\n"));
    }

    [TestCase("while (flag) { let n: int = mayThrow(); break; } let failure: int = 1 / 0;")]
    [TestCase("while (true) { if (flag) { break; } let n: int = mayThrow(); continue; } let failure: int = 1 / 0;")]
    public void InterfaceFlow_UnknownLoopTransferPathsRemainConservative(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 21; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        value.emit("must-retain-unknown-loop-path");
    }
    return 21;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { } let failure: int = 2147483647 + 1;", "overflow")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let failure: int = 2147483647 + 1; }", "overflow")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let failure: int = 2147483647 + 1; } catch (OverflowException) { value = new Plain(); }", "overflow")]
    [TestCase("try { let failure: int = 1 / 0; } catch (SystemException) { } catch (ZeroDivisionException) { value = new Plain(); } let failure: int = 2147483647 + 1;", "overflow")]
    [TestCase("try { let safe: int = 1; } catch { value = new Plain(); } let failure: int = 1 / 0;", "zero")]
    [TestCase("try { if (flag) { let failure: int = 1 / 0; } else { let failure: int = 2147483647 + 1; } } catch (ZeroDivisionException) { } let failure: int = 1 / 0;", "both")]
    [TestCase("try { try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let failure: int = 2147483647 + 1; } } catch (OverflowException) { } let failure: int = 1 / 0;", "zero")]
    [TestCase("while (true) { try { if (flag) { break; } let failure: int = 1 / 0; } catch (ZeroDivisionException) { } break; } let failure: int = 2147483647 + 1;", "overflow")]
    [TestCase("try { let failure: int = 2147483647 + 1; } catch (InvalidCastException) { value = new Plain(); } catch (SystemException) { } let failure: int = 1 / 0;", "zero")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { throw; } let failure: int = 2147483647 + 1;", "zero")]
    public void InterfaceFlow_NestedArithmeticHandlersKeepOnlyEscapingExceptions(string body, string expected)
    {
        var onZero = expected == "overflow" ? "value = new Plain();" : "";
        var onOverflow = expected == "zero" ? "value = new Plain();" : "";
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{onZero}} }
        catch (OverflowException) { {{onOverflow}} }
        catch (Exception) { value = new Plain(); }
        value.emit("nested-arithmetic-handler");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("nested-arithmetic-handler\nnested-arithmetic-handler\n"));
    }

    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let n: int = mayThrow(); } let failure: int = 2147483647 + 1;")]
    [TestCase("try { let n: int = mayThrow(); } catch (ZeroDivisionException) { } let failure: int = 2147483647 + 1;")]
    public void InterfaceFlow_UnknownNestedArithmeticHandlersRemainConservative(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 21; }
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        value.emit("must-retain-unknown-nested-handler");
    }
    return 21;
}
function main(args: string[]): int { return choose(false); }
"""));
    }

    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { throw; }", "zero")]
    [TestCase("try { let failure: int = 2147483647 + 1; } catch (OverflowException) { throw; }", "overflow")]
    [TestCase("try { if (flag) { let failure: int = 1 / 0; } else { let failure: int = 2147483647 + 1; } } catch (SystemException) { throw; }", "both")]
    [TestCase("try { if (flag) { let failure: int = 1 / 0; } else { let failure: int = 2147483647 + 1; } } catch (ZeroDivisionException) { let failure: int = 2147483647 + 1; } catch (SystemException) { throw; }", "overflow")]
    [TestCase("try { let failure: int = 1 / 0; } catch { throw; }", "zero")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { try { throw; } catch (InvalidCastException) { value = new Plain(); } catch (ZeroDivisionException) { throw; } }", "zero")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { try { let failure: int = 2147483647 + 1; } catch (OverflowException) { } try { throw; } catch (ZeroDivisionException) { throw; } catch (OverflowException) { value = new Plain(); } }", "zero")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { try { throw; } catch (SystemException) { let failure: int = 2147483647 + 1; } }", "overflow")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { while (true) { if (flag) { throw; } break; } throw; }", "zero")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { if (flag) { throw; } let failure: int = 2147483647 + 1; }", "both")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let failure: int = 2147483647 + 1; throw; }", "overflow")]
    public void InterfaceFlow_TypedRethrowPreservesItsActiveArithmeticFamily(string body, string expected)
    {
        var onZero = expected == "overflow" ? "value = new Plain();" : "";
        var onOverflow = expected == "zero" ? "value = new Plain();" : "";
        var result = Run($$"""
function choose(flag: bool): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { {{onZero}} }
        catch (OverflowException) { {{onOverflow}} }
        catch (Exception) { value = new Plain(); }
        value.emit("typed-rethrow");
    }
    return 21;
}
function main(args: string[]): int {
    let total: int = choose(false) + choose(true);
    tick(); return total;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("typed-rethrow\ntyped-rethrow\n"));
    }

    [Test]
    public void InterfaceFlow_ExactCatchProvidesRethrowFamilyAfterUnknownCall()
    {
        var result = Run("""
function fail(): int { return 2147483647 + 1; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { let n: int = fail(); }
        catch (OverflowException) {
            try { throw; }
            catch (ZeroDivisionException) { value = new Plain(); }
            catch (InvalidCastException) { value = new Plain(); }
            catch (OverflowException) { }
            value.emit("exact-catch-rethrow");
        }
    }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("exact-catch-rethrow\n"));
    }

    [TestCase("try { let n: int = mayThrow(); } catch { throw; }")]
    [TestCase("try { let n: int = mayThrow(); } catch (SystemException) { throw; }")]
    [TestCase("try { let failure: int = 1 / 0; } catch (ZeroDivisionException) { let n: int = mayThrow(); throw; }")]
    public void InterfaceFlow_UnknownRethrowPathsRemainConservative(string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 21; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        catch (InvalidCastException) { value = new Plain(); }
        catch (ZeroDivisionException) { }
        catch (OverflowException) { }
        value.emit("must-retain-unknown-rethrow");
    }
    return 0;
}
"""));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InterfaceFlow_UnknownCatchShadowsAndDoesNotLeakRethrowContext(bool nestUnderKnownCatch)
    {
        var unknownHandler = """
try { let n: int = mayThrow(); }
catch (SystemException) {
    try { throw; }
    catch (InvalidCastException) { value = new Plain(); }
    catch (OverflowException) { }
    value.emit("must-not-inherit-old-family");
}
""";
        var body = nestUnderKnownCatch
            ? "try { let failure: int = 2147483647 + 1; } catch (OverflowException) { " + unknownHandler + " }"
            : "try { let failure: int = 2147483647 + 1; } catch (OverflowException) { } " + unknownHandler;
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function mayThrow(): int { return 21; }
function main(args: string[]): int {
    let value: object = new C();
    if (value is I) { {{body}} }
    return 0;
}
"""));
    }

    private static (long ExitCode, string Output) Run(string body)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Declarations + body)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
