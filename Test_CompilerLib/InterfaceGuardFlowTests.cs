using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceGuardFlowTests
{
    [TestCase("return;")]
    [TestCase("throw;")]
    public void TypeShadowedCatchBreakDoesNotCreateLoopExit(string firstTransfer)
    {
        var result = Run($$"""
function route(value: object): void {
    if (not (value is I)) { return; }
    while (true) {
        try { let divisor: int = 1; let quotient: int = 1 / divisor; break; }
        catch (Exception) { {{firstTransfer}} }
        catch (ZeroDivisionException) { value = new Plain(); break; }
    }
    value.emit("after-shadowed-break");
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-shadowed-break" }));
    }

    [TestCase("while (flag)", "return;")]
    [TestCase("while (flag)", "break;")]
    [TestCase("foreach (item in [0, 1])", "return;")]
    [TestCase("foreach (item in [0, 1])", "break;")]
    public void TerminalConditionalBodyStillIgnoresShortCircuitedConditionConsumer(string loop, string terminal)
    {
        var result = Run($$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        {{loop}} {
            value.emit("dead-condition-consumer");
            if ((false and unbox value) or flag) { {{terminal}} }
        }
    }
}
function main(args: string[]): int { route(new C(), true); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "dead-condition-consumer" }));
    }

    [TestCase("while (flag)", "return;")]
    [TestCase("while (flag)", "break;")]
    [TestCase("foreach (item in [0, 1])", "return;")]
    [TestCase("foreach (item in [0, 1])", "break;")]
    public void TerminalConditionalBodyDoesNotHideExecutedConditionConsumer(string loop, string terminal)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        {{loop}} {
            value.emit("must-not-narrow");
            if (unbox value) { {{terminal}} }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    private const string Declarations = """
interface I { method emit(message: string): void; }
interface J extends I { method extra(): void; }
class C implements J {
    public async method emit(message: string): void { print(message); }
    public async method extra(): void { print("extra"); }
}
class Other implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
""";

    [TestCase("(value is I) and (true or unbox value)")]
    [TestCase("(value is I) and not (false and unbox value)")]
    public void UnreachableConditionConsumerDoesNotDiscardInterfaceProof(string condition)
    {
        var result = Run($"function route(value: object): void {{ if ({condition}) {{ value.emit(\"unreachable-consumer-proof\"); }} }} function main(args: string[]): int {{ route(new C()); route(new Plain()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-consumer-proof" }));
    }

    [TestCase("true or unbox value")]
    [TestCase("false and unbox value")]
    public void UnreachableExpressionConsumerPreservesIncomingInterfaceFact(string expression)
    {
        var result = Run($"function route(value: object): void {{ if (value is I) {{ let flag: bool = {expression}; value.emit(\"unreachable-consumer-state\"); }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-consumer-state" }));
    }

    [Test]
    public void UnreachableConditionConsumerPreservesWhileBodyProof()
    {
        var result = Run("function route(value: object): void { while ((value is I) and (true or unbox value)) { value.emit(\"while-unreachable-consumer\"); break; } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "while-unreachable-consumer" }));
    }

    [TestCase("(value is I) and unbox value")]
    [TestCase("(value is I) and (flag or unbox value)")]
    public void ReachableConditionConsumerStillDiscardsInterfaceProof(string condition)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if ({condition}) {{ value.emit(\"must-not-narrow\"); }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void UnreachableConsumerDoesNotEnterEnclosingCatchWriteSet()
    {
        var result = Run("function route(value: object): void { if (value is I) { try { let flag: bool = true or unbox value; } catch { value.emit(\"unreachable-catch\"); } value.emit(\"after-short-circuit-try\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-short-circuit-try" }));
    }

    [TestCase("(unbox value or true) or unbox value")]
    [TestCase("(unbox value and false) and unbox value")]
    public void UnreachableLaterConsumerDoesNotRestoreEarlierConsumedFact(string expression)
    {
        var source = Declarations + $"function route(value: object): void {{ if (value is I) {{ let flag: bool = {expression}; value.emit(\"must-not-narrow\"); }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void UnreachableConsumerAllowsFreshWhileProofAfterBackEdge()
    {
        var result = Run("function route(value: object): void { while ((value is I) and (true or unbox value)) { value.emit(\"short-circuit-recheck\"); value = new Plain(); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-recheck" }));
    }

    [Test]
    public void UnreachableConsumerPreservesWhileFalseExitProof()
    {
        var result = Run("function route(value: object): void { while (not (value is I) and not (false and unbox value)) { return; } value.emit(\"short-circuit-false-exit\"); } function main(args: string[]): int { route(new C()); route(new Plain()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-false-exit" }));
    }

    [TestCase("ready and (true or unbox value)")]
    [TestCase("ready and not (false and unbox value)")]
    public void UnreachableHeaderConsumerDoesNotWidenLoopEntryFact(string condition)
    {
        var result = Run($"function route(value: object): void {{ if (value is I) {{ var ready = true; while ({condition}) {{ value.emit(\"unreachable-header-consumer\"); ready = false; }} }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-header-consumer" }));
    }

    [TestCase("if (true or unbox value) { value.emit(\"unreachable-body-condition\"); }")]
    [TestCase("while (inner and (true or unbox value)) { break; } value.emit(\"unreachable-body-condition\");")]
    public void UnreachableNestedConditionConsumerDoesNotWidenOuterLoopFact(string body)
    {
        var result = Run($"function route(value: object, inner: bool): void {{ if (value is I) {{ var ready = true; while (ready) {{ {body} ready = false; }} }} }} function main(args: string[]): int {{ route(new C(), true); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-body-condition" }));
    }

    [Test]
    public void PossibleHeaderConsumerStillWidensLoopEntryFact()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { var ready = true; while (ready and (flag or unbox value)) { value.emit(\"must-not-narrow\"); ready = false; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ConstantFalseWhileStillInvalidatesReachableConditionConsumer()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { while (unbox value and false) { } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (unbox value and false) { }")]
    [TestCase("while (unbox value and false) { }")]
    public void ConstantFalseNestedConditionStillWidensReachableConsumer(string statement)
    {
        var source = Declarations + $"function route(value: object): void {{ if (value is I) {{ var ready = true; while (ready) {{ value.emit(\"must-not-narrow\"); {statement} ready = false; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("unbox value and false", 1)]
    [TestCase("false and unbox value", 0)]
    public void ConstantFalseWhileRetainsRuntimeConsumerEvaluationOrder(string condition, int expected)
    {
        var result = Run($"function main(args: string[]): int {{ let value: object = true; while ({condition}) {{ }} if (value == invalid) {{ return 1; }} return 0; }}");
        Assert.That(result.ExitCode, Is.EqualTo(expected));
    }

    [TestCase("let ignored: bool = true or unbox value;")]
    [TestCase("let ignored: bool = false and unbox value;")]
    [TestCase("marker = true or unbox value;")]
    [TestCase("marker = false and unbox value;")]
    [TestCase("if (true) { let value: bool = true or unbox value; }")]
    public void UnreachableAssignmentConsumerDoesNotWidenLoopEntryFact(string statement)
    {
        var result = Run($"function route(value: object): void {{ if (value is I) {{ var ready = true; var marker = false; while (ready) {{ {statement} value.emit(\"unreachable-assignment-consumer\"); ready = false; }} }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-assignment-consumer" }));
    }

    [TestCase("let ignored: bool = flag or unbox value;")]
    [TestCase("marker = flag or unbox value;")]
    [TestCase("let ignored: bool = unbox value and false;")]
    public void PossibleAssignmentConsumerStillWidensLoopEntryFact(string statement)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if (value is I) {{ var ready = true; var marker = false; while (ready) {{ value.emit(\"must-not-narrow\"); {statement} ready = false; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (ready and accept(true or unbox value, false)) { value.emit(\"short-circuit-call-argument\"); ready = false; }")]
    [TestCase("while (ready) { let marker: bool = accept(true or unbox value, false); value.emit(\"short-circuit-call-argument\"); ready = false; }")]
    public void UnreachableCallArgumentConsumerDoesNotWidenLoopEntryFact(string loop)
    {
        var result = Run($"function accept(first: bool, second: bool): bool {{ return first; }} function route(value: object): void {{ if (value is I) {{ var ready = true; {loop} }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-call-argument" }));
    }

    [Test]
    public void ShortCircuitInFirstArgumentDoesNotHideSecondArgumentConsumer()
    {
        var source = Declarations + "function accept(first: bool, second: bool): bool { return first; } function route(value: object): void { if (value is I) { var ready = true; while (ready) { value.emit(\"must-not-narrow\"); let marker: bool = accept(true or unbox value, unbox value); ready = false; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void UnreachableArrayElementConsumerDoesNotWidenLoopEntryFact()
    {
        var result = Run("function route(value: object): void { if (value is I) { var ready = true; while (ready) { let items: bool[] = [true or unbox value, false]; value.emit(\"short-circuit-array-element\"); ready = false; } } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-array-element" }));
    }

    [Test]
    public void ShortCircuitInFirstElementDoesNotHideSecondElementConsumer()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { var ready = true; while (ready) { value.emit(\"must-not-narrow\"); let items: bool[] = [true or unbox value, unbox value]; ready = false; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ShortCircuitInFirstArgumentStillEvaluatesSecondArgumentAtRuntime()
    {
        var result = Run("function accept(first: bool, second: bool): bool { return first; } function main(args: string[]): int { let value: object = true; let result: bool = accept(true or unbox value, unbox value); if (value == invalid and result) { return 1; } return 0; }");
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [Test]
    public void UnreachableCallStatementConsumerDoesNotWidenLoopEntryFact()
    {
        var result = Run("function accept(first: bool, second: bool): void { } function route(value: object): void { if (value is I) { var ready = true; while (ready) { accept(true or unbox value, false); value.emit(\"short-circuit-call-statement\"); ready = false; } } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-call-statement" }));
    }

    [Test]
    public void CallStatementStillWidensForIndependentSecondArgumentConsumer()
    {
        var source = Declarations + "function accept(first: bool, second: bool): void { } function route(value: object): void { if (value is I) { var ready = true; while (ready) { value.emit(\"must-not-narrow\"); accept(true or unbox value, unbox value); ready = false; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("(value is I) or false")]
    [TestCase("false or (value is I)")]
    [TestCase("(value is I) or (1 > 2)")]
    [TestCase("(false and flag) or (value is I)")]
    public void ConstantFalseAlternativeDoesNotDiscardTrueInterfaceFact(string condition)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if ({condition}) {{ value.emit(\"constant-alternative\"); }} }} function main(args: string[]): int {{ route(new C(), true); route(new Plain(), false); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-alternative" }));
    }

    [TestCase("not (value is I) and true")]
    [TestCase("true and not (value is I)")]
    [TestCase("not (value is I) and (1 < 2)")]
    [TestCase("(true or flag) and not (value is I)")]
    public void ConstantTrueConjunctDoesNotDiscardFalseInterfaceFact(string condition)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if ({condition}) {{ return; }} value.emit(\"constant-false-proof\"); }} function main(args: string[]): int {{ route(new C(), true); route(new Plain(), false); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-false-proof" }));
    }

    [TestCase("(value is I) or true")]
    [TestCase("(value is I) or flag")]
    [TestCase("(value is I) or (true and flag)")]
    public void PossibleTrueAlternativeWithoutInterfaceFactStillRejectsMemberCall(string condition)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if ({condition}) {{ value.emit(\"must-not-narrow\"); }} }} function main(args: string[]): int {{ route(new Plain(), true); return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ConstantFalseAlternativePreservesWhileBodyInterfaceFact()
    {
        var result = Run("function route(value: object): void { while ((value is I) or false) { value.emit(\"constant-while-body\"); break; } } function main(args: string[]): int { route(new C()); route(new Plain()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-while-body" }));
    }

    [Test]
    public void ConstantTrueConjunctPreservesWhileFalseExitInterfaceFact()
    {
        var result = Run("function route(value: object): void { while (not (value is I) and true) { return; } value.emit(\"constant-while-exit\"); } function main(args: string[]): int { route(new C()); route(new Plain()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-while-exit" }));
    }

    [Test]
    public void ConstantFalseAlternativePreservesParentInterfaceJoin()
    {
        var result = Run("function route(value: object): void { if (((value is J) or false) or (value is I)) { value.emit(\"constant-parent-join\"); } } function main(args: string[]): int { route(new C()); route(new Other()); route(new Plain()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-parent-join", "constant-parent-join" }));
    }

    [TestCase("true")]
    [TestCase("((true))")]
    [TestCase("not false")]
    [TestCase("not (false)")]
    [TestCase("((not false))")]
    [TestCase("true and (not false)")]
    [TestCase("false or true")]
    [TestCase("(false or true) and true")]
    [TestCase("true or flag")]
    [TestCase("not flag or true")]
    [TestCase("1 < 2")]
    [TestCase("3 == 3")]
    [TestCase("true == true")]
    [TestCase("false != true")]
    [TestCase("(true) == true")]
    [TestCase("-1 < 0")]
    [TestCase("(-1) < 0")]
    [TestCase("-1 < (0)")]
    public void ConstantTrueWhileDoesNotRequireAReachableReturn(string condition)
    {
        var source = $"function loop(flag: bool): int {{ while ({condition}) {{ }} }} function main(args: string[]): int {{ return loop(false); }}";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [TestCase("false")]
    [TestCase("((false))")]
    [TestCase("not true")]
    [TestCase("true and false")]
    [TestCase("false or (not true)")]
    [TestCase("(true or false) and (not true)")]
    [TestCase("flag and false")]
    [TestCase("not flag and false")]
    [TestCase("4 < 2")]
    [TestCase("1 != 1")]
    [TestCase("true == false")]
    [TestCase("false != false")]
    [TestCase("(true) == (false)")]
    [TestCase("-1 > 0")]
    [TestCase("(-1) > 0")]
    public void ConstantFalseWhileStillRequiresAReachableReturn(string condition)
    {
        var source = $"function loop(flag: bool): int {{ while ({condition}) {{ }} }} function main(args: string[]): int {{ return loop(false); }}";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("false")]
    [TestCase("flag and false")]
    [TestCase("not flag and false")]
    [TestCase("1 > 2")]
    public void ConstantFalseWhilePreservesPreLoopInterfaceNarrowing(string condition)
    {
        var source = Declarations + """
function route(value: object, flag: bool): void {
    if (value is I) {
        while (CONDITION) { value = new Plain(); }
        value.emit("after-no-loop");
    }
}
function main(args: string[]): int {
    route(new C(), true); route(new Other(), false); route(new Plain(), true);
    tick(); return 0;
}
""".Replace("CONDITION", condition);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { "after-no-loop", "after-no-loop" }));
    }

    [Test]
    public void UnreachableWritesAfterUnconditionalContinueDoNotInvalidateLoopEntryFacts()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (flag) { continue; value = new Plain(); } value.emit(\"after-continue\"); } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-continue" }));
    }

    [TestCase("break;", 2)]
    [TestCase("return;", 1)]
    public void UnreachableWritesAfterLoopExitDoNotInvalidateLoopEntryFacts(string exit, int expectedOutputCount)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (value is I) {{ while (flag) {{ {exit} value = new Plain(); }} value.emit(\"after-exit\"); }} }} function main(args: string[]): int {{ route(new C(), true); route(new C(), false); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(Enumerable.Repeat("after-exit", expectedOutputCount)));
    }

    [TestCase("if (false) { value = new Plain(); } break; value = new Plain();")]
    [TestCase("if (true) { break; } value = new Plain();")]
    public void ConstantIfLoopTerminatorKeepsFollowingWritesUnreachable(string body)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (value is I) {{ while (flag) {{ {body} }} value.emit(\"after-constant-if\"); }} }} function main(args: string[]): int {{ route(new C(), true); route(new C(), false); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-constant-if", "after-constant-if" }));
    }

    [Test]
    public void ConstantIfBranchKeepsWritesAfterItsTerminatorUnreachable()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (flag) { if (true) { print(\"before-break\"); break; } value = new Plain(); } value.emit(\"after-break\"); } } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-break", "after-break", "after-break" }));
    }

    [Test]
    public void NestedConstantFalseIfDoesNotInvalidateLoopEntryFacts()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { if (false) { value = new Plain(); } } break; } value.emit(\"after-nested-false\"); } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-nested-false" }));
    }

    [TestCase("continue", false)]
    [TestCase("break", true)]
    public void NestedConditionalLoopTerminatorSkipsDeadWritesInItsBlock(string terminator, bool flag)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (value is I) {{ while (true) {{ if (flag) {{ {terminator}; value = new Plain(); }} break; }} value.emit(\"after-nested-terminal\"); }} }} function main(args: string[]): int {{ route(new C(), {flag.ToString().ToLowerInvariant()}); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-nested-terminal" }));
    }

    [Test]
    public void NestedConstantTrueLoopTerminalSkipsRemainderOfConditionalBlock()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { if (true) { break; } value = new Plain(); } break; } value.emit(\"after-nested-constant-terminal\"); } } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-nested-constant-terminal", "after-nested-constant-terminal" }));
    }

    [Test]
    public void NestedConstantTrueLoopTerminalPreservesReachableSiblingWrites()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { if (true) { break; } value = new Plain(); } else { value = new Plain(); } break; } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ConstantFalseElseTerminalSkipsFollowingLoopWrites()
    {
        var result = Run("function route(value: object): void { if (value is I) { while (true) { if (false) { value = new Plain(); } else { break; value = new Plain(); } value = new Plain(); } value.emit(\"after-false-else-break\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-false-else-break" }));
    }

    [Test]
    public void ConstantFalseElseTerminalKeepsWritesBeforeTerminalReachable()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { while (true) { if (false) { value = new Plain(); } else { value = new Plain(); break; } value = new Plain(); } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void BothCompoundLoopBranchesBreakAndKeepNarrowing()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { continue; } else { break; } value = new Plain(); } value.emit(\"after-compound-breaks\"); } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-compound-breaks" }));
    }

    [Test]
    public void CompoundLoopTerminalBranchesKeepReachableWritesBeforeTerminal()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { value = new Plain(); break; } else { continue; } value = new Plain(); } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ElseIfTerminalChainDoesNotCountUnreachableLoopTailWrites()
    {
        var result = Run("""
function route(value: object, first: bool, second: bool): void {
    if (not (value is I)) { return; }
    while (true) {
        if (first) { break; }
        else if (second) { value = new Plain(); return; }
        else { break; }
        value = new Plain();
    }
    value.emit("else-if-terminal-chain");
}
function main(args: string[]): int {
    route(new C(), true, false); route(new C(), false, false); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "else-if-terminal-chain", "else-if-terminal-chain" }));
    }

    [Test]
    public void DeepElseIfTerminalChainKeepsOnlyReachableBreakFacts()
    {
        var result = Run("""
function route(value: object, first: bool, second: bool, third: bool): void {
    if (not (value is I)) { return; }
    while (true) {
        if (first) { break; }
        else if (second) { value = new Plain(); return; }
        else if (third) { break; }
        else { value = new Plain(); return; }
        value = new Plain();
    }
    value.emit("deep-else-if-break-join");
}
function main(args: string[]): int {
    route(new C(), true, false, false); route(new C(), false, false, true); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "deep-else-if-break-join", "deep-else-if-break-join" }));
    }

    [Test]
    public void DeepElseIfTerminalChainIncludesWritesOnReachableBreakEdges()
    {
        var source = Declarations + "function route(value: object, first: bool, second: bool, third: bool): void { if (not (value is I)) { return; } while (true) { if (first) { break; } else if (second) { value = new Plain(); return; } else if (third) { value = new Plain(); break; } else { value = new Plain(); return; } value = new Plain(); } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), false, false, true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void NestedTerminalThenElseIfBreakWriteInvalidatesOnlyReachableExit()
    {
        var source = Declarations + "function route(value: object, first: bool, second: bool, third: bool): void { if (not (value is I)) { return; } while (true) { if (first) { if (second) { value = new Plain(); return; } else { break; } value = new Plain(); } else if (third) { value = new Plain(); break; } else { value = new Plain(); return; } value = new Plain(); } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), false, false, true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ParentBreakAfterPartiallyTerminalNestedIfSkipsOuterTail()
    {
        var result = Run("function route(value: object, first: bool, second: bool): void { if (not (value is I)) { return; } while (true) { if (first) { if (second) { value = new Plain(); return; } break; } else { break; } value = new Plain(); } value.emit(\"parent-break-after-if\"); } function main(args: string[]): int { route(new C(), false, false); route(new C(), true, false); route(new C(), true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "parent-break-after-if", "parent-break-after-if" }));
    }

    [Test]
    public void PartiallyContinuingElseIfChainKeepsReachableTailWrite()
    {
        var source = Declarations + "function route(value: object, first: bool, second: bool): void { if (not (value is I)) { return; } while (true) { if (first) { } else if (second) { break; } else { break; } value = new Plain(); break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), true, false); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ConstantTrueElseIfSkipsItsUnreachableFinalElseWrite()
    {
        var result = Run("function route(value: object, first: bool): void { if (not (value is I)) { return; } while (true) { if (first) { break; } else if (true) { break; } else { value = new Plain(); break; } value = new Plain(); } value.emit(\"constant-else-if\"); } function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-else-if", "constant-else-if" }));
    }

    [Test]
    public void ConstantTrueElseIfKeepsWritesOnItsReachableBreakArm()
    {
        var source = Declarations + "function route(value: object, first: bool): void { if (not (value is I)) { return; } while (true) { if (first) { break; } else if (true) { value = new Plain(); break; } else { value = new Plain(); break; } value = new Plain(); } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), false); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void NonTerminalConstantTrueElseIfSkipsDeadElseWithoutHidingOuterBreak()
    {
        var result = Run("function route(value: object, first: bool): void { if (not (value is I)) { return; } while (true) { if (first) { break; } else if (true) { } else { value = new Plain(); } break; } value.emit(\"true-else-fallthrough\"); } function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "true-else-fallthrough", "true-else-fallthrough" }));
    }


    [Test]
    public void NestedTerminalReturnWritesDoNotPolluteSiblingBreakExit()
    {
        var result = Run("function route(value: object, first: bool, second: bool): void { if (not (value is I)) { return; } while (true) { if (first) { if (second) { value = new Plain(); return; } else { break; } value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"nested-return-break\"); } function main(args: string[]): int { route(new C(), false, false); route(new C(), true, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-return-break", "nested-return-break" }));
    }

    [Test]
    public void ThreeNestedTerminalIfMarkersPreserveOnlyBreakEdgeFacts()
    {
        var result = Run("function route(value: object, first: bool, second: bool, third: bool): void { if (not (value is I)) { return; } while (true) { if (first) { if (second) { if (third) { value = new Plain(); return; } else { break; } value = new Plain(); } else { break; } value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"three-level-terminal-join\"); } function main(args: string[]): int { route(new C(), false, false, false); route(new C(), true, false, false); route(new C(), true, true, false); route(new C(), true, true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(Enumerable.Repeat("three-level-terminal-join", 3)));
    }

    [Test]
    public void ThreeNestedTerminalIfMarkersKeepDeepReachableBreakWrite()
    {
        var source = Declarations + "function route(value: object, first: bool, second: bool, third: bool): void { if (not (value is I)) { return; } while (true) { if (first) { if (second) { if (third) { break; } else { value = new Plain(); break; } value = new Plain(); } else { break; } value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), true, true, false); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void NestedTerminalMarkersPreserveOuterFactAcrossShadowedLocal()
    {
        var result = Run("function route(value: object, first: bool, second: bool): void { if (not (value is I)) { return; } while (true) { if (first) { let value: object = new Plain(); if (second) { return; } else { break; } value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"outer-shadow-preserved\"); } function main(args: string[]): int { route(new C(), false, false); route(new C(), true, false); route(new C(), true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "outer-shadow-preserved", "outer-shadow-preserved" }));
    }

    [Test]
    public void InnerLoopBreakAndContinueSkipDeadWritesWithoutChangingOuterExitFacts()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { while (true) { if (flag) { break; } else { continue; } value = new Plain(); } break; value = new Plain(); } value.emit(\"inner-loop-terminal\"); } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "inner-loop-terminal" }));
    }

    [Test]
    public void InnerLoopReachableWriteInvalidatesOuterBreakExitFacts()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { while (true) { value = new Plain(); break; } break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }


    [TestCase("false")]
    [TestCase("not true")]
    [TestCase("true and false")]
    [TestCase("((false))")]
    public void NestedConstantFalseWhileDoesNotInvalidateOuterBreakFact(string condition)
    {
        var result = Run($"function route(value: object): void {{ if (not (value is I)) {{ return; }} while (true) {{ while ({condition}) {{ value = new Plain(); }} break; }} value.emit(\"nested-false-loop\"); }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-false-loop" }));
    }

    [Test]
    public void NestedLoopThenBreakDoesNotExposeOuterConditionalTailWrites()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { if (flag) { while (true) { break; value = new Plain(); } break; value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"nested-loop-branch\"); } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-loop-branch", "nested-loop-branch" }));
    }

    [Test]
    public void NestedForeachBreakDoesNotHideOuterLoopTerminal()
    {
        var result = Run("function route(value: object, items: object[]): void { if (not (value is I)) { return; } while (true) { if (items.Length > 0) { foreach (item in items) { break; value = new Plain(); } break; value = new Plain(); } else { break; } value = new Plain(); } value.emit(\"nested-foreach-terminal\"); } function main(args: string[]): int { route(new C(), [new C()]); route(new C(), []); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-foreach-terminal", "nested-foreach-terminal" }));
    }

    [Test]
    public void NestedForeachReachableBreakWriteInvalidatesOuterExitFact()
    {
        var source = Declarations + "function route(value: object, items: object[]): void { if (not (value is I)) { return; } while (true) { foreach (item in items) { value = new Plain(); break; } break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), [new C()]); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void NestedTryCatchBreakEdgesPreserveOuterLoopFacts()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { while (true) { try { let number: int = 1 / 0; break; } catch { break; } value = new Plain(); } break; value = new Plain(); } value.emit(\"nested-try-break\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-try-break" }));
    }

    [Test]
    public void NestedTryCatchReachableBreakWriteInvalidatesOuterLoopFacts()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { while (true) { try { let number: int = 1 / 0; break; } catch { value = new Plain(); break; } } break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ConditionalTryBreakDoesNotHideReachableFollowingWrite()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { try { if (flag) { let number: int = 1 / 0; break; } } catch { break; } value = new Plain(); break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), false); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void NestedTryCatchTerminalEdgesSkipOuterTryAndLoopTails()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { try { let number: int = 1 / 0; break; } catch { break; } value = new Plain(); } catch { break; } value = new Plain(); break; } value.emit(\"nested-try-terminal\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-try-terminal" }));
    }

    [Test]
    public void NestedTryCatchOuterCatchWriteInvalidatesLoopExitFacts()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { try { try { let number: int = 1 / 0; break; } catch { break; } } catch { value = new Plain(); break; } break; } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void TerminalTryCatchInsideConditionalBreakJoinSkipsLoopTail()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { if (flag) { try { let number: int = 1 / 0; break; } catch { return; } } else { break; } value = new Plain(); } value.emit(\"conditional-try-break\"); } function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "conditional-try-break" }));
    }

    [Test]
    public void TryBreakAndCatchRethrowKeepOnlyBreakExitFacts()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { throw; } value = new Plain(); } value.emit(\"break-or-rethrow\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-or-rethrow" }));
    }

    [Test]
    public void CatchWriteBeforeRethrowDoesNotPolluteSiblingLoopBreak()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { value = new Plain(); throw; } } value.emit(\"catch-rethrow-break-join\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-rethrow-break-join" }));
    }

    [Test]
    public void NestedNonLoopExitCatchBranchesDoNotPolluteLoopBreak()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (flag) { value = new Plain(); throw; } else { return; } } } value.emit(\"nested-catch-rethrow-break-join\"); } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-catch-rethrow-break-join", "nested-catch-rethrow-break-join" }));
    }

    [Test]
    public void CatchThrowSkipsUnreachableBreakWhenClassifyingLoopExit()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { value = new Plain(); throw; break; } } value.emit(\"catch-throw-dead-break\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-throw-dead-break" }));
    }

    [Test]
    public void TerminalCatchIfChainSkipsUnreachableLoopTransferAfterChain()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (flag) { value = new Plain(); throw; } else { return; } break; } } value.emit(\"catch-terminal-chain-dead-break\"); } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-terminal-chain-dead-break", "catch-terminal-chain-dead-break" }));
    }

    [Test]
    public void TerminalCatchArmsIgnoreBreakTokensAfterReturnOrThrow()
    {
        var result = Run("function route(value: object, flag: bool): int { if (not (value is I)) { return 0; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (flag) { value = new Plain(); throw; break; } else { return (1 + 0); break; } break; } } value.emit(\"catch-arm-dead-break\"); return 2; } function main(args: string[]): int { let first: int = route(new C(), true); let second: int = route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-arm-dead-break", "catch-arm-dead-break" }));
    }

    [Test]
    public void NestedTerminalCatchChainsIgnoreUnreachableTransfersAfterInnerChain()
    {
        var result = Run("function route(value: object, outer: bool, inner: bool): int { if (not (value is I)) { return 0; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (outer) { if (inner) { return 1; break; } else { throw; break; } value = new Plain(); break; } else { return 2; break; } break; } } value.emit(\"nested-terminal-catch\"); return 3; } function main(args: string[]): int { let first: int = route(new C(), true, true); let second: int = route(new C(), false, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-terminal-catch", "nested-terminal-catch" }));
    }

    [Test]
    public void ConstantTrueThrowCatchIgnoresUnreachableElseBreakAndWrite()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (true) { value = new Plain(); throw; } else { value = new Plain(); break; } } } value.emit(\"constant-true-throw-catch\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-true-throw-catch" }));
    }

    [Test]
    public void ConstantTrueReturnCatchIgnoresUnreachableElseBreakAndWrite()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (true) { value = new Plain(); return; } else { value = new Plain(); break; } } } value.emit(\"constant-true-return-catch\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-true-return-catch" }));
    }

    [Test]
    public void ConstantFalseReturnCatchIgnoresUnreachableThenBreakAndWrite()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (false) { value = new Plain(); break; } else { value = new Plain(); return; } } } value.emit(\"constant-false-return-catch\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-false-return-catch" }));
    }

    [Test]
    public void ConstantTrueCatchBreakRemainsAReachableLoopExit()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (true) { value = new Plain(); break; } else { return; } } } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ConstantFalseCatchElseBreakRemainsAReachableLoopExit()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (false) { return; } else { value = new Plain(); break; } } } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if (false) { value = new Plain(); }", "false-then")]
    [TestCase("if (true) { } else { value = new Plain(); }", "true-else")]
    public void UnselectedConstantIfWriteKeepsEnclosingNarrowing(string branch, string caseName)
    {
        var result = Run($"function route(value: object): void {{ if (value is I) {{ {branch} value.emit(\"constant-unselected-{caseName}\"); }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { $"constant-unselected-{caseName}" }));
    }

    [TestCase("if (value is I) { if (true) { } else if (true) { value = new Plain(); } value.emit(\"if-else-if\"); }")]
    [TestCase("if (not (value is I)) { return 0; } if (true) { if (not (value is I)) { return 0; } } else { if (not (value is I)) { return 0; } delete value; } value.emit(\"unselected-delete\");")]
    [TestCase("if (true) { if (not (value is J)) { return 0; } } else { if (not (value is I)) { return 0; } } value.extra();")]
    public void UnselectedConstantIfElseIfArmsDoNotAffectContinuation(string body)
    {
        var source = Declarations + "function main(args: string[]): int { let value: object = new C(); " + body + " return 0; }";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void ConstantTerminalBranchKeepsItsUnselectedFallthroughDead()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } try { if (true) { return; } else { } value = new Plain(); } catch { value.emit(\"terminal-constant-branch\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(Array.Empty<string>()));
    }

    [TestCase("true", true)]
    [TestCase("false", false)]
    public void ConstantIfReturnControlsFunctionFallthrough(string condition, bool shouldCompile)
    {
        var source = $"function value(): int {{ if ({condition}) {{ return 1; }} else {{ }} }} function main(args: string[]): int {{ return value(); }}";
        if (shouldCompile)
            Assert.That(Run(source).ExitCode, Is.EqualTo(1));
        else
            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void TypeWithDeadWriteAfterReturnDoesNotInvalidateCatchFacts()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } try { with (new C()) { return; value = new Plain(); } } catch { value.emit(\"with-terminal-write\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(Array.Empty<string>()));
    }

    [Test]
    public void FunctionDeadTailDoesNotStartTryWriteTracking()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } return; try { value = new Plain(); } catch { value.emit(\"function-terminal-write\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(Array.Empty<string>()));
    }

    [Test]
    public void StructConstructorDeadTailDoesNotStartTryWriteTracking()
    {
        var result = Run("struct R { field code: int; construct() { this.code = 1; let value: object = new C(); if (not (value is I)) { return; } return; try { value = new Plain(); } catch { value.emit(\"constructor-terminal-write\"); } } } function main(args: string[]): int { var result = new R(); return result.code; }");
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [TestCase("foreach (item in [1]) { break; value = new Plain(); }")]
    [TestCase("foreach (item in [1]) { continue; value = new Plain(); }")]
    [TestCase("foreach (item in [1]) { return; value = new Plain(); }")]
    [TestCase("foreach (item in [1]) { if (true) { break; } else { continue; } value = new Plain(); }")]
    [TestCase("foreach (item in [1]) { if (flag) { break; } else { continue; } value = new Plain(); }")]
    [TestCase("foreach (key in [\"x\": 1]) { break; value = new Plain(); }")]
    [TestCase("foreach (key in [\"x\": 1]) { continue; value = new Plain(); }")]
    [TestCase("foreach (key in [\"x\": 1]) { return; value = new Plain(); }")]
    [TestCase("foreach (key in [\"x\": 1]) { if (true) { break; } else { continue; } value = new Plain(); }")]
    [TestCase("foreach (key in [\"x\": 1]) { if (flag) { break; } else { continue; } value = new Plain(); }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { break; value = new Plain(); }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { continue; value = new Plain(); }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { return; value = new Plain(); }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { if (true) { break; } else { continue; } value = new Plain(); }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { if (flag) { break; } else { continue; } value = new Plain(); }")]
    public void ForeachDeadWriteAfterTerminalDoesNotInvalidateCatchFacts(string loop)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if (not (value is I)) {{ return; }} try {{ {loop} return; }} catch {{ value.emit(\"foreach-terminal-write\"); }} }} function main(args: string[]): int {{ route(new C(), true); return 0; }}";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [TestCase("foreach (item in [1]) { value = new Plain(); break; }")]
    [TestCase("foreach (key in [\"x\": 1]) { value = new Plain(); break; }")]
    [TestCase("let items: pipe<int> = pipe<int>.create(); foreach (item in items) { value = new Plain(); break; }")]
    public void ForeachReachableWriteBeforeBreakInvalidatesCatchFacts(string loop)
    {
        var source = Declarations + $"function route(value: object): void {{ if (not (value is I)) {{ return; }} try {{ {loop} return; }} catch {{ value.emit(\"reachable-foreach-write\"); }} }} function main(args: string[]): int {{ route(new C()); return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("try { value = new Plain(); return; } catch { value.emit(\"reachable-function-write\"); }")]
    [TestCase("try { with (new C()) { value = new Plain(); return; } } catch { value.emit(\"reachable-with-write\"); }")]
    public void ReachableManualSequenceWriteStillInvalidatesCatchFacts(string body)
    {
        var source = Declarations + $"function route(value: object): void {{ if (not (value is I)) {{ return; }} {body} }} function main(args: string[]): int {{ route(new C()); return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ReachableStructConstructorWriteStillInvalidatesCatchFacts()
    {
        const string source = "struct R { field code: int; construct() { this.code = 1; let value: object = new C(); if (not (value is I)) { return; } try { value = new Plain(); return; } catch { value.emit(\"reachable-constructor-write\"); } } } function main(args: string[]): int { var value = new R(); return value.code; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ConstantFalseCatchThrowIgnoresUnreachableThenBreakAndWrite()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (false) { value = new Plain(); break; } else { value = new Plain(); throw; } } } value.emit(\"constant-false-throw-catch\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-false-throw-catch" }));
    }

    [Test]
    public void ConstantFalseLoopArmWriteDoesNotPolluteContinueExitFact()
    {
        var result = Run("function route(value: object): void { if (value is I) { var ready = true; while (ready) { if (false) { value = new Plain(); break; } ready = false; continue; } value.emit(\"constant-false-continue-exit\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-false-continue-exit" }));
    }

    [Test]
    public void ContinueConditionRecheckIgnoresThrowOnlyCatchWrite()
    {
        var result = Run("function route(value: object): void { if (value is I) { var ready = true; while (ready) { try { ready = false; continue; } catch { value = new Plain(); throw; } } value.emit(\"continue-catch-recheck\"); } } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "continue-catch-recheck" }));
    }

    [Test]
    public void ContinueWithReachableCatchBreakWriteInvalidatesExitFact()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { while (true) { try { let number: int = 1 / 0; continue; } catch { value = new Plain(); break; } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void WithBlockImmediateBreakPreservesFirstIterationInterfaceFact()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { let target: C = new C(); while (gate) { with (target) { if (false) { continue; } else { value.emit(\"with-loop-break\"); break; } } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-loop-break" }));
    }

    [Test]
    public void WithBlockReachableContinueInvalidatesFirstIterationInterfaceFact()
    {
        var source = Declarations + "function route(value: object, gate: bool): void { if (value is I) { let target: C = new C(); while (gate) { with (target) { if (gate) { value = new Plain(); continue; } else { break; } } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void CatchContinueWriteIsRefinedByNextIterationGuardBeforeBreak()
    {
        var result = Run("function route(value: object): void { var iteration = 0; while (true) { if (iteration == 0) { iteration = iteration + 1; try { let number: int = 1 / 0; } catch { value = new C(); continue; } } if (value is I) { break; } return; } value.emit(\"catch-continue-reguard\"); } function main(args: string[]): int { route(new Plain()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-continue-reguard" }));
    }

    [Test]
    public void CatchContinueSkipsDeadBreakAndWriteBeforeIndependentBreakJoin()
    {
        var result = Run("function route(value: object): void { if (not (value is I)) { return; } while (true) { if (value is I) { try { let number: int = 1 / 0; break; } catch { value = new Plain(); continue; value = new Plain(); break; } } else { return; } } value.emit(\"catch-continue-dead-tail\"); } function main(args: string[]): int { route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(Array.Empty<string>()));
    }

    [TestCase("true or flag")]
    public void ShortCircuitConstantCatchArmsIgnoreUnreachableTransfers(string condition)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (not (value is I)) {{ return; }} while (true) {{ try {{ break; }} catch {{ if ({condition}) {{ value = new Plain(); throw; }} else {{ value = new Plain(); break; }} }} }} value.emit(\"short-circuit-catch\"); }} function main(args: string[]): int {{ route(new C(), false); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "short-circuit-catch" }));
    }

    [Test]
    public void FalseAndShortCircuitCatchIgnoresUnreachableThenBreak()
    {
        var result = Run("function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (false and flag) { value = new Plain(); break; } else if (false) { value = new Plain(); break; } else { value = new Plain(); throw; } } } value.emit(\"false-and-short-circuit-catch\"); } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "false-and-short-circuit-catch" }));
    }

    [Test]
    public void NonconstantShortCircuitCatchBreakRemainsReachable()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (true and flag) { value = new Plain(); break; } else { throw; } } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void TrueOrShortCircuitLoopBreakIgnoresUnreachableElseWrite()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (true) { if (true or flag) { break; } else { value = new Plain(); break; } } value.emit(\"true-or-loop-break\"); } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "true-or-loop-break" }));
    }

    [Test]
    public void FalseAndShortCircuitLoopBreakRetainsReachableElseWrite()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (true) { if (false and flag) { break; } else { value = new Plain(); break; } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void CatchWithReachableBreakIsNotClassifiedAsNonLoopExit()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (not (value is I)) { return; } while (true) { try { let divisor: int = 1; let quotient: int = 1 / divisor; break; } catch { if (flag) { value = new Plain(); break; } else { throw; } } } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void TryWriteBeforeBreakRemainsReachableBesideCatchRethrow()
    {
        var source = Declarations + "function route(value: object): void { if (not (value is I)) { return; } while (true) { try { value = new Plain(); break; } catch { throw; } } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C()); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ReturnBranchWritesDoNotInvalidateLoopBreakBranch()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { value = new Plain(); return; } else { break; } value = new Plain(); } value.emit(\"after-return-or-break\"); } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "after-return-or-break" }));
    }

    [Test]
    public void ValueReturnMakesFollowingLoopBreakAndWriteUnreachable()
    {
        var result = Run("function route(value: object): int { if (value is I) { while (true) { return 1; value = new Plain(); break; } value.emit(\"after-return-value\"); } return 0; } function main(args: string[]): int { let result: int = route(new C()); tick(); return 0; }");
        Assert.That(result.Output, Is.Empty);
    }

    [Test]
    public void LoopBreakBranchWritesRemainReachableBesideReturnBranch()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (true) { if (flag) { value = new Plain(); return; } else { value = new Plain(); break; } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { route(new C(), true); return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if (false) { value = new Plain(); } else { value = new Plain(); } break;")]
    [TestCase("if (true) { if (false) { value = new Plain(); } else { value = new Plain(); } break; }")]
    public void ConstantFalseIfLoopScanStillIncludesReachableElseWrites(string body)
    {
        var source = Declarations + $"function main(args: string[]): int {{ let value: object = new C(); if (value is I) {{ while (true) {{ {body} }} value.emit(\"must-not-narrow\"); }} return 0; }}";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("true")]
    [TestCase("((true))")]
    [TestCase("not false")]
    [TestCase("true and (not false)")]
    [TestCase("true or flag")]
    [TestCase("1 <= 2")]
    [TestCase("false == false")]
    [TestCase("(false) != true")]
    [TestCase("-2 <= -1")]
    [TestCase("(-2) <= (-1)")]
    public void ConstantTrueIfReturningBranchSatisfiesFunctionReturn(string condition)
    {
        var source = $"function route(flag: bool): int {{ if ({condition}) {{ return 1; }} }} function main(args: string[]): int {{ return route(false); }}";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [TestCase("false")]
    [TestCase("((false))")]
    [TestCase("not true")]
    [TestCase("true and false")]
    [TestCase("flag and false")]
    [TestCase("2 > 3")]
    [TestCase("true != true")]
    [TestCase("true != (true)")]
    [TestCase("-1 >= 0")]
    [TestCase("0 < (-1)")]
    public void ConstantFalseIfDoesNotHideMissingFunctionReturn(string condition)
    {
        var source = $"function route(flag: bool): int {{ if ({condition}) {{ return 1; }} }} function main(args: string[]): int {{ return route(false); }}";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("not (value is I)")]
    [TestCase("not (value is I or value is J)")]
    [TestCase("not (not (not (value is I)))")]
    [TestCase("not (value is I) or false")]
    public void ReturningGuardNarrowsTheSurvivingContinuation(string condition)
    {
        var result = Run("""
function route(value: object): void {
    if (CONDITION) { return; }
    value.emit("guard");
}
function main(args: string[]): int {
    route(new C()); route(new Other()); route(new Plain()); GC.require(); GC.finish(); tick(); return 0;
}
""".Replace("CONDITION", condition));
        Assert.That(result.Output, Is.EqualTo(new[] { "guard", "guard" }));
    }

    [Test]
    public void ReturningWritesDoNotChangeTheImplicitSurvivingPath()
    {
        var result = Run("""
function route(value: object): void {
    if (not (value is I)) { value = new Plain(); return; }
    value.emit("before"); value = new Other();
    if (not (value is I)) { return; }
    value.emit("fresh");
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "before", "fresh" }));
    }

    [Test]
    public void GuardRefinementRetainsIncomingParentFactsAtAnEnclosingJoin()
    {
        var result = Run("""
function route(value: object, refine: bool): void {
    if (not (value is I)) { return; }
    if (refine) { if (not (value is J)) { return; } value.extra(); }
    value.emit("parent");
}
function main(args: string[]): int { route(new C(), true); route(new Other(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "extra", "parent", "parent" }));
    }

    [Test]
    public void BreakAndContinueGuardsNarrowOnlyTheCurrentIteration()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    while (count < 2) {
        if (not (value is I)) { break; }
        value.emit("loop"); value = new Plain(); count = count + 1;
    }
    let items: object[] = [new C(), new Plain(), new Other()];
    foreach (item in items) { if (not (item is I)) { continue; } item.emit("item"); }
    GC.require(); GC.finish(); tick(); return count;
}
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "loop", "item", "item" }));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [Test]
    public void RethrowingGuardUsesTheFalseConditionInsideCatch()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C();
    try { let number: int = 1 / 0; }
    catch { if (not (value is I)) { throw; } value.emit("catch"); }
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch" }));
    }

    [Test]
    public void ContinuingTryAndCatchBranchesJoinTheirCommonInterface()
    {
        var result = Run("""
function route(value: object, raise: bool): void {
    try {
        if (raise) { let number: int = 1 / 0; }
        if (not (value is J)) { return; }
    }
    catch { if (not (value is I)) { throw; } }
    value.emit("try-catch-join");
}
function main(args: string[]): int {
    route(new C(), true); route(new C(), false); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "try-catch-join", "try-catch-join" }));
    }

    [Test]
    public void TryAndCatchBreakEdgesJoinTheirGuardedInterfaceFacts()
    {
        var result = Run("""
function route(value: object, raise: bool): void {
    while (true) {
        try {
            if (raise) { let number: int = 1 / 0; }
            if (not (value is I)) { return; }
            break;
        }
        catch {
            if (not (value is I)) { return; }
            break;
        }
    }

    value.emit("try-catch-break-join");
}
function main(args: string[]): int {
    route(new C(), true); route(new C(), false); route(new Plain(), false);
    tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "try-catch-break-join", "try-catch-break-join" }));
    }

    [Test]
    public void LoopBreakEdgesJoinTheirCommonInterfaceGuard()
    {
        var result = Run("function route(value: object, flag: bool): void { while (true) { if (flag) { if (not (value is J)) { return; } break; } else { if (not (value is I)) { return; } break; } } value.emit(\"break-common-interface\"); } function main(args: string[]): int { route(new C(), true); route(new Other(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-common-interface", "break-common-interface" }));
    }

    [Test]
    public void ZeroIterationAndImmediateBreakPathsJoinTheirCommonInterfaceGuard()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (flag) { value = new C(); if (not (value is I)) { return; } break; } value.emit(\"zero-or-break-common-interface\"); } } function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "zero-or-break-common-interface", "zero-or-break-common-interface" }));
    }

    [Test]
    public void LoopBackEdgeStillInvalidatesPreLoopInterfaceNarrowing()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (flag) { value = new Plain(); continue; } value.emit(\"must-not-narrow-after-backedge\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void StraightLineImmediateExitLoopKeepsFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { while (flag) { value.emit(\"first-iteration\"); value = new Plain(); break; } } } function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "first-iteration" }));
    }

    [Test]
    public void StraightLineImmediateExitLoopDoesNotNarrowAcrossIncompatibleBreakState()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { while (flag) { value.emit(\"first-iteration\"); value = new Plain(); break; } value.emit(\"unsafe-after-break\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ImmediateExitLoopKeepsConditionConsumptionOnZeroIterationEdge()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { while (unbox value) { break; } value.emit(\"consumed-condition\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ImmediateExitConditionConsumptionDoesNotDiscardOtherPreLoopFacts()
    {
        var source = Declarations + "function route(value: object, other: object): void { if ((value is I) and (other is I)) { while (unbox value) { other = new C(); if (not (other is I)) { return; } break; } other.emit(\"other-still-I\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.Nothing);
    }

    [Test]
    public void ShortCircuitedConditionConsumerDoesNotDiscardOtherPreLoopFacts()
    {
        var source = Declarations + "function route(value: object, other: object, gate: bool): void { if ((value is I) and (other is I)) { while (gate and (true or unbox value)) { other = new C(); if (not (other is I)) { return; } break; } other.emit(\"other-still-I\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.Nothing);
    }

    [Test]
    public void UnreachableShortCircuitedConditionConsumerDoesNotDiscardPreLoopFacts()
    {
        var source = Declarations + "function route(value: object, other: object, gate: bool): void { if ((value is I) and (other is I)) { while (gate and (false and unbox value)) { other = new C(); if (not (other is I)) { return; } break; } other.emit(\"other-still-I\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.Nothing);
    }

    [Test]
    public void LoopConditionConsumptionRemainsVisibleToEnclosingCatchJoin()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { try { while (unbox value) { break; } } catch { return; } value.emit(\"condition-write-catch-join\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void AllTerminalConditionalBranchesKeepFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool, flag: bool): void { if (value is I) { while (gate) { value.emit(\"before-branch-exit\"); if (flag) { value = new Plain(); break; } else { return; } } } } function main(args: string[]): int { route(new C(), true, true); route(new C(), true, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-branch-exit", "before-branch-exit" }));
    }

    [Test]
    public void UnreachableContinueDoesNotDiscardFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { if (false) { continue; } value.emit(\"unreachable-continue\"); value = new Plain(); break; } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-continue" }));
    }

    [Test]
    public void ContinueInUnselectedElseDoesNotDiscardFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { if (true) { value.emit(\"unselected-else-continue\"); value = new Plain(); break; } else { continue; } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "unselected-else-continue" }));
    }

    [Test]
    public void UnselectedElseContinueAfterNonterminalBranchIsIgnored()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { if (true) { value.emit(\"unselected-else-fallthrough\"); } else { continue; } value = new Plain(); break; } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "unselected-else-fallthrough" }));
    }

    [Test]
    public void UnreachableContinueInTryKeepsFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { try { if (false) { continue; } value.emit(\"try-unreachable-continue\"); value = new Plain(); break; } catch { return; } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "try-unreachable-continue" }));
    }

    [TestCase("while (inner) { value = new Plain(); break; }")]
    [TestCase("foreach (item in [1]) { value = new Plain(); continue; }")]
    public void NestedLoopBeforeOuterBreakPreservesEarlierFirstIterationFact(string nestedLoop)
    {
        var result = Run($"function route(value: object, gate: bool, inner: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"before-nested-loop\"); {nestedLoop} break; }} }} }} function main(args: string[]): int {{ route(new C(), true, true); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-nested-loop" }));
    }

    [TestCase("while (true) { value = new Plain(); }")]
    [TestCase("while (true) { if (false) { break; } value = new Plain(); }")]
    [TestCase("while (true) { foreach (item in [1]) { break; } value = new Plain(); }")]
    [TestCase("while (true) { value = new Plain(); continue; break; }")]
    public void NonfallthroughNestedLoopPreservesFirstOuterIterationFact(string nestedLoop)
    {
        var source = Declarations + $"function route(value: object, gate: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"before-nonfallthrough-loop\"); {nestedLoop} continue; }} }} }} function main(args: string[]): int {{ return 0; }}";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [TestCase("while (true) { value = new Plain(); break; }")]
    [TestCase("while (true) { value = new Plain(); if (flag) { break; } }")]
    [TestCase("while (true) { try { value = new Plain(); let code: int = 1 / 0; } catch { break; } }")]
    public void NestedLoopWithReachableBreakDoesNotHideOuterBackEdge(string nestedLoop)
    {
        var source = Declarations + $"function route(value: object, gate: bool, flag: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"must-not-narrow\"); {nestedLoop} continue; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (flag) { while (true) { value = new Plain(); } continue; } value = new Plain(); break;")]
    [TestCase("with (new C()) { while (true) { value = new Plain(); } continue; }")]
    [TestCase("try { while (true) { value = new Plain(); } } catch { return; }")]
    public void NestedNonfallthroughPathsHideDeadOuterContinue(string statement)
    {
        var source = Declarations + $"function route(value: object, gate: bool, flag: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"before-nonfallthrough-path\"); {statement} continue; }} }} }} function main(args: string[]): int {{ return 0; }}";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
    }

    [TestCase("try { while (true) { value = new Plain(); let code: int = 1 / 0; } } catch { continue; }")]
    [TestCase("if (flag) { while (true) { value = new Plain(); } } else { value = new Plain(); continue; }")]
    public void NestedNonfallthroughSiblingDoesNotHideReachableOuterContinue(string statement)
    {
        var source = Declarations + $"function route(value: object, gate: bool, flag: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"must-not-narrow\"); {statement} continue; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (flag) { value = new Plain(); let code: int = 1 / 0; return; }", "step = step + 1; continue;")]
    [TestCase("try { let code: int = 1 / 0; return; } catch { value = new Plain(); throw; }", "step = step + 1; continue;")]
    [TestCase("try { value = new Plain(); let code: int = 1 / 0; return; } catch { throw; }", "step = step + 1; continue;")]
    [TestCase("if (flag) { value = new Plain(); let code: int = 1 / 0; return; }", "")]
    [TestCase("try { let code: int = 1 / 0; return; } catch { value = new Plain(); throw; }", "")]
    [TestCase("try { value = new Plain(); let code: int = 1 / 0; return; } catch { throw; }", "")]
    public void ReturningOrRethrowingWriteStillReachesEnclosingCatchBackEdge(string statement, string catchBody)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if (value is I) {{ var step = 0; while (step < 2) {{ value.emit(\"must-not-narrow\"); try {{ {statement} }} catch {{ {catchBody} }} step = step + 1; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("try { if (flag) { value = new Plain(); return; } } catch { return; }")]
    [TestCase("try { if (flag) { value = new Plain(); return; } } catch { throw; }")]
    [TestCase("try { if (flag) { try { value = new Plain(); return; } catch { return; } } } catch { return; }")]
    public void ReturningWriteWithNonRejoiningHandlersPreservesLoopFact(string statement)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (value is I) {{ var step = 0; while (step < 2) {{ value.emit(\"non-rejoining-handler\"); {statement} step = step + 1; }} }} }} function main(args: string[]): int {{ route(new C(), false); route(new C(), true); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "non-rejoining-handler", "non-rejoining-handler", "non-rejoining-handler" }));
    }

    [Test]
    public void NonfallthroughConditionalWriteDoesNotWidenSurvivingLoopPath()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { var ready = true; while (ready) { if (flag) { while (true) { } value = new Plain(); } value.emit(\"surviving-loop-path\"); ready = false; } } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "surviving-loop-path" }));
    }

    [Test]
    public void RethrowingCatchBranchWriteDoesNotWidenSurvivingLoopPath()
    {
        var result = Run("function route(value: object, flag: bool): void { if (value is I) { var ready = true; while (ready) { try { let code: int = 1 / 0; } catch { if (flag) { value = new Plain(); throw; } } value.emit(\"surviving-catch-path\"); ready = false; } } } function main(args: string[]): int { route(new C(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "surviving-catch-path" }));
    }

    [Test]
    public void EscapableNestedConditionalWriteStillWidensLoopPath()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { var ready = true; while (ready) { if (flag) { while (true) { break; } value = new Plain(); } value.emit(\"must-not-narrow\"); ready = false; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (flag) { } else { value = new Plain(); return; }")]
    [TestCase("if (flag) { } else { while (true) { } value = new Plain(); }")]
    [TestCase("try { let code: int = 1 / 0; } catch { if (flag) { } else { value = new Plain(); throw; } }")]
    public void NonRejoiningElseWritePreservesSurvivingLoopPath(string statement)
    {
        var result = Run($"function route(value: object, flag: bool): void {{ if (value is I) {{ var ready = true; while (ready) {{ {statement} value.emit(\"surviving-else-path\"); ready = false; }} }} }} function main(args: string[]): int {{ route(new C(), true); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "surviving-else-path" }));
    }

    [TestCase("if (flag) { } else { value = new Plain(); continue; }")]
    [TestCase("try { if (flag) { } else { value = new Plain(); let code: int = 1 / 0; return; } } catch { continue; }")]
    public void RejoiningElseWriteStillInvalidatesLoopEntryFact(string statement)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if (value is I) {{ var ready = true; while (ready) {{ value.emit(\"must-not-narrow\"); {statement} ready = false; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (step == 1) { value = new Plain(); break; } step = step + 1;")]
    [TestCase("if (step == 0) { step = step + 1; continue; } else { value = new Plain(); break; }")]
    public void BreakOnlyConditionalWritesPreserveWhileIterationFact(string body)
    {
        var result = Run($"function route(value: object): void {{ if (value is I) {{ var step = 0; while (step < 2) {{ value.emit(\"break-only-write\"); {body} }} }} }} function main(args: string[]): int {{ route(new C()); tick(); return 0; }}");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-only-write", "break-only-write" }));
    }

    [Test]
    public void BreakOnlyWriteStillInvalidatesWhileExitFact()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { var step = 0; while (step < 2) { if (step == 1) { value = new Plain(); break; } step = step + 1; } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void BreakPathWriteWithCatchContinueStillInvalidatesWhileEntry()
    {
        var source = Declarations + "function route(value: object): void { if (value is I) { var step = 0; while (step < 3) { value.emit(\"must-not-narrow\"); try { if (step == 1) { value = new Plain(); let code: int = 1 / 0; break; } } catch { step = step + 1; continue; } step = step + 1; } } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (true) { if (flag) { value = new Plain(); break; } else { break; } }")]
    [TestCase("foreach (item in [1]) { if (flag) { value = new Plain(); break; } }")]
    public void InnerBreakWriteStillInvalidatesOuterWhileEntry(string nestedLoop)
    {
        var source = Declarations + $"function route(value: object, flag: bool): void {{ if (value is I) {{ var step = 0; while (step < 2) {{ value.emit(\"must-not-narrow\"); {nestedLoop} step = step + 1; }} }} }} function main(args: string[]): int {{ return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void BreakOnlyWriteStillInvalidatesForeachExitFact()
    {
        var source = Declarations + "function route(value: object, flag: bool): void { if (value is I) { foreach (item in [1]) { if (flag) { value = new Plain(); break; } } value.emit(\"must-not-narrow\"); } } function main(args: string[]): int { return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (inner) { value = new Plain(); break; }")]
    [TestCase("foreach (item in [1]) { value = new Plain(); continue; }")]
    public void NestedLoopWriteStillInvalidatesLaterFirstIterationRead(string nestedLoop)
    {
        var source = Declarations + $"function route(value: object, gate: bool, inner: bool): void {{ if (value is I) {{ while (gate) {{ {nestedLoop} value.emit(\"must-not-narrow\"); break; }} }} }} function main(args: string[]): int {{ route(new C(), true, true); return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (inner) { value = new Plain(); break; }", "continue;")]
    [TestCase("foreach (item in [1]) { value = new Plain(); continue; }", "continue;")]
    [TestCase("while (inner) { value = new Plain(); break; }", "")]
    [TestCase("foreach (item in [1]) { value = new Plain(); continue; }", "")]
    public void NestedLoopBeforeOuterBackEdgeStillInvalidatesLoopCarriedFact(string nestedLoop, string backEdge)
    {
        var source = Declarations + $"function route(value: object, gate: bool, inner: bool): void {{ if (value is I) {{ while (gate) {{ value.emit(\"must-not-narrow\"); {nestedLoop} {backEdge} }} }} }} function main(args: string[]): int {{ route(new C(), true, true); return 0; }}";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedLoopContinueDoesNotBecomeOuterTryLoopBackEdge()
    {
        var result = Run("function route(value: object, gate: bool, inner: bool): void { if (value is I) { while (gate) { try { while (inner) { continue; } value.emit(\"inner-continue\"); value = new Plain(); break; } catch { return; } } } } function main(args: string[]): int { route(new C(), true, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "inner-continue" }));
    }

    [Test]
    public void NestedLoopContinueInsideWithDoesNotBecomeOuterLoopBackEdge()
    {
        var result = Run("function route(value: object, gate: bool, inner: bool): void { if (value is I) { let target: C = new C(); while (gate) { with (target) { while (inner) { continue; } value.emit(\"with-inner-continue\"); value = new Plain(); break; } } } } function main(args: string[]): int { route(new C(), true, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-inner-continue" }));
    }

    [Test]
    public void NonterminalWithBlockFallsThroughToOuterBreak()
    {
        var result = Run("function route(value: object, gate: bool, stop: bool): void { if (value is I) { let target: C = new C(); while (gate) { with (target) { if (stop) { break; } } value.emit(\"with-outer-break\"); break; } } } function main(args: string[]): int { route(new C(), true, false); route(new C(), true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-outer-break" }));
    }

    [Test]
    public void TerminalTryCatchInsideWithPreservesFirstIterationInterfaceFact()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { let target: C = new C(); while (gate) { with (target) { try { value.emit(\"with-try-break\"); value = new Plain(); break; } catch { return; } } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-try-break" }));
    }

    [Test]
    public void WithBreakWriteJoinsWithZeroIterationFalseExit()
    {
        var source = Declarations + "function route(value: object, gate: bool): void { let target: C = new C(); while (value is I) { with (target) { value = new Plain(); break; } } value.emit(\"must-not-narrow\"); } function main(args: string[]): int { route(new C(), true); return 0; }";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ForeachContinueDoesNotBecomeOuterTryLoopBackEdge()
    {
        var result = Run("function route(value: object, gate: bool, items: int[]): void { if (value is I) { while (gate) { try { foreach (item in items) { continue; } value.emit(\"foreach-continue\"); value = new Plain(); break; } catch { return; } } } } function main(args: string[]): int { route(new C(), true, [1]); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "foreach-continue" }));
    }

    [Test]
    public void ContinueAfterBreakInTryDoesNotDiscardFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { try { value.emit(\"try-break-before-dead-continue\"); value = new Plain(); break; continue; } catch { return; continue; } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "try-break-before-dead-continue" }));
    }

    [Test]
    public void ContinueAfterTerminalNestedTryCatchIsUnreachable()
    {
        var result = Run("function route(value: object, gate: bool, flag: bool): void { if (value is I) { while (gate) { if (flag) { try { value.emit(\"nested-terminal-try\"); value = new Plain(); break; } catch { return; } continue; } value.emit(\"outer-fallthrough\"); value = new Plain(); break; } } } function main(args: string[]): int { route(new C(), true, true); route(new C(), true, false); tick(); return 0; }");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "nested-terminal-try", "outer-fallthrough" }));
    }

    [Test]
    public void ContinueAfterConditionalReturnDoesNotDiscardFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool, flag: bool): void { if (value is I) { while (gate) { value.emit(\"conditional-dead-continue\"); if (flag) { return; continue; } else { value = new Plain(); break; } } } } function main(args: string[]): int { route(new C(), true, false); route(new C(), true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "conditional-dead-continue", "conditional-dead-continue" }));
    }

    [Test]
    public void CatchFallthroughContinueStillInvalidatesLoopCarriedNarrowing()
    {
        var source = Declarations + "function route(value: object, gate: bool): void { if (value is I) { while (gate) { try { value = new Plain(); break; } catch { } continue; } value.emit(\"unsafe-after-catch-continue\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ReachableConditionalContinueStillInvalidatesLoopCarriedNarrowing()
    {
        var source = Declarations + "function route(value: object, gate: bool, flag: bool): void { if (value is I) { while (gate) { if (flag) { value = new Plain(); continue; } value = new C(); break; } value.emit(\"unsafe-after-continue\"); } } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ReturningBranchDoesNotDiscardFirstIterationFactOnFallthroughPath()
    {
        var result = Run("function route(value: object, gate: bool, bail: bool): void { if (value is I) { while (gate) { if (bail) { value = new Plain(); return; } value.emit(\"fallthrough-still-I\"); break; } } } function main(args: string[]): int { route(new C(), true, false); route(new C(), true, true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "fallthrough-still-I" }));
    }

    [Test]
    public void AllTerminalTryCatchBranchesKeepFirstIterationNarrowing()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { value.emit(\"before-try-exit\"); try { value = new Plain(); break; } catch { return; } } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-try-exit" }));
    }

    [Test]
    public void ReturningTryPathKeepsFirstIterationFactBeforeCatchFallthrough()
    {
        var result = Run("function route(value: object, gate: bool): void { if (value is I) { while (gate) { value.emit(\"before-try-fallthrough\"); try { value = new Plain(); return; } catch { } break; } } } function main(args: string[]): int { route(new C(), true); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-try-fallthrough" }));
    }

    [Test]
    public void LoopBreakEdgesJoinIncomparableInterfacesAtTheirCommonParent()
    {
        var result = Run("interface K extends I { method extra(): void; } class D implements K { public async method emit(message: string): void { print(message); } public async method extra(): void { } } function route(value: object, flag: bool): void { while (true) { if (flag) { if (not (value is J)) { return; } break; } else { if (not (value is K)) { return; } break; } } value.emit(\"break-incomparable-common-parent\"); } function main(args: string[]): int { route(new C(), true); route(new D(), false); tick(); return 0; }");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-incomparable-common-parent", "break-incomparable-common-parent" }));
    }

    [Test]
    public void LoopBreakEdgesDoNotChooseArbitraryParentForAmbiguousJoin()
    {
        var source = Declarations + "interface A { method alpha(): void; } interface B { method beta(): void; } interface X extends A, B { } interface Y extends A, B { } function route(value: object, flag: bool): void { while (true) { if (flag) { if (not (value is X)) { return; } break; } else { if (not (value is Y)) { return; } break; } } value.alpha(); } function main(args: string[]): int { return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void SoleContinuingTryPathRetainsItsInterfaceNarrowing()
    {
        var result = Run("""
function route(value: object, fail: bool): void {
    try {
        if (fail) { let number: int = 1 / 0; }
        if (not (value is J)) { return; }
    }
    catch { return; }
    value.extra();
}
function main(args: string[]): int {
    route(new C(), false); route(new C(), true); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "extra" }));
    }

    [Test]
    public void UnreachableTryWriteDoesNotInvalidateCatchInterfaceNarrowing()
    {
        var result = Run("""
function route(value: object, flag: bool): void {
    if (not (value is I)) { return; }
    try { let number: int = 1 / 0; return; value = new Plain(); }
    catch { }
    value.emit("catch-continuation");
}
function main(args: string[]): int { route(new C(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-continuation" }));
    }

    [TestCase("if (false) { value = new Plain(); }")]
    [TestCase("if (true) { } else { value = new Plain(); }")]
    public void ConstantUnreachableTryWriteDoesNotInvalidateCatchInterfaceNarrowing(string branch)
    {
        var result = Run("""
function route(value: object): void {
    if (not (value is I)) { return; }
    try { BRANCH let number: int = 1 / 0; }
    catch { value.emit("constant-unreachable-catch"); }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""".Replace("BRANCH", branch));
        Assert.That(result.Output, Is.EqualTo(new[] { "constant-unreachable-catch" }));
    }

    [Test]
    public void GuardRefinementDoesNotChangeTheDeclaredAssignmentContract()
    {
        var result = Run("""
function route(value: I): void {
    if (not (value is J)) { return; }
    value.extra(); value = new Other(); value.emit("parent");
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "extra", "parent" }));
    }

    [TestCase("if (value is I) { } else { return; }")]
    [TestCase("if (not (value is I)) { return; } else { }")]
    [TestCase("if (not (value is I)) { value = new Plain(); return; } else { }")]
    public void ExplicitElseGuardExportsTheSurvivingBranchFacts(string guard)
    {
        var result = Run("""
function route(value: object): void { GUARD value.emit("explicit"); }
function main(args: string[]): int {
    route(new C()); route(new Other()); route(new Plain()); GC.require(); GC.finish(); tick(); return 0;
}
""".Replace("GUARD", guard));
        Assert.That(result.Output, Is.EqualTo(new[] { "explicit", "explicit" }));
    }

    [Test]
    public void ElseIfExitChainRetainsTheOnlyContinuingSubtype()
    {
        var result = Run("""
function route(value: object): void {
    if (not (value is I)) { return; }
    else if (not (value is J)) { return; }
    else { value.extra(); }
    value.extra(); value.emit("continued");
}
function main(args: string[]): int { route(new C()); route(new Other()); route(new Plain()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "extra", "extra", "continued" }));
    }

    [Test]
    public void SurvivingBranchUsesItsFinalFactAfterReassignmentAndFreshGuard()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        value = new Other(); if (not (value is I)) { return; }
    } else { return; }
    value.emit("fresh");
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "fresh" }));
    }

    [Test]
    public void ExplicitBreakContinueAndRethrowGuardsRespectOuterBoundaries()
    {
        var result = Run("""
function main(args: string[]): int {
    let value: object = new C(); var count = 0;
    while (count < 2) {
        if (value is I) { } else { break; }
        value.emit("loop"); value = new Plain(); count = count + 1;
    }
    let items: object[] = [new C(), new Plain(), new Other()];
    foreach (item in items) { if (item is I) { } else { continue; } item.emit("item"); }
    value = new C();
    try { let number: int = 1 / 0; }
    catch { if (value is I) { } else { throw; } value.emit("catch"); }
    GC.require(); GC.finish(); tick(); return count;
}
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "loop", "item", "item", "catch" }));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [Test]
    public void UnconditionalWhileExportsFactsFromItsOnlyBreakEdge()
    {
        var result = Run("""
function route(value: object): void {
    while (true) { if (value is I) { break; } return; }
    value.emit("break-edge");
}
function main(args: string[]): int { route(new C()); route(new Other()); route(new Plain()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-edge", "break-edge" }));
    }

    [Test]
    public void UnconditionalWhileJoinsMultipleBreakEdges()
    {
        var result = Run("""
function route(value: object, flag: bool): void {
    while (true) {
        if (flag) { if (value is J) { break; } }
        else { if (value is I) { break; } }
        return;
    }
    value.emit("break-join");
}
function main(args: string[]): int { route(new C(), true); route(new Other(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "break-join", "break-join" }));
    }

    [Test]
    public void WhileFalseConditionExportsItsGuaranteedInterfaceFact()
    {
        var result = Run("""
function route(value: object): void {
    while (not (value is I)) { return; }
    value.emit("condition-exit");
}
function main(args: string[]): int { route(new C()); route(new Other()); route(new Plain()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "condition-exit", "condition-exit" }));
    }

    [Test]
    public void WhileFalseOrExitDoesNotChooseAnUnrelatedInterface()
    {
        const string source = """
interface K { method first(): void; }
interface L { method second(): void; }
class EK implements K { public async method first(): void { } }
class EL implements L { public async method second(): void { } }
function route(value: object): void {
    while (not ((value is K) or (value is L))) { return; }
    value.first();
}
function main(args: string[]): int { return 0; }
""";

        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ContinueRechecksTheWhileConditionBeforeExportingExitFacts()
    {
        var result = Run("""
function route(value: object): void {
    while (not (value is I)) { value = new C(); continue; }
    value.emit("rechecked");
}
function main(args: string[]): int { route(new Plain()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "rechecked" }));
    }

    [Test]
    public void ContinueRechecksAlternativeInterfaceTestsAndExportsTheirCommonParent()
    {
        var result = Run("""
interface K extends I { method another(): void; }
class D implements K {
    public async method emit(message: string): void { print(message); }
    public async method another(): void { }
}
function route(value: object): void {
    while (not (value is J or value is K)) { value = new D(); continue; }
    value.emit("continued-common-parent");
}
function main(args: string[]): int {
    route(new Plain()); route(new C()); route(new D()); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "continued-common-parent", "continued-common-parent", "continued-common-parent" }));
    }

    [Test]
    public void ContinueEdgesJoinDifferentWritesBeforeRecheckingLoopCondition()
    {
        var result = Run("""
interface K extends I { method extra(): void; }
class D implements K {
    public async method emit(message: string): void { print(message); }
    public async method extra(): void { print("extra"); }
}
function route(value: object, flag: bool): void {
    while (not (value is J or value is K)) {
        if (flag) { value = new C(); continue; }
        else { value = new D(); continue; }
    }
    value.emit("continue-join");
}
function main(args: string[]): int {
    route(new Plain(), true); route(new Plain(), false);
    route(new C(), false); route(new D(), true); tick(); return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "continue-join", "continue-join", "continue-join", "continue-join" }));
    }

    [Test]
    public void ContinueCarriedWriteIsRefinedByTheNextIterationGuardBeforeBreak()
    {
        var result = Run("""
function route(value: object): void {
    var iteration = 0;
    while (true) {
        if (iteration == 0) {
            value = new C();
            iteration = iteration + 1;
            continue;
        }
        if (value is I) { break; }
        return;
    }
    value.emit("continue-break-edge");
}
function main(args: string[]): int { route(new Plain()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "continue-break-edge" }));
    }

    [TestCase("if (flag) { if (not (value is I)) { return; } } else { if (not (value is I)) { return; } }")]
    [TestCase("if (flag) { if (not (value is J)) { return; } } else { if (not (value is I)) { return; } }")]
    [TestCase("if (flag) { if (not (value is I)) { return; } } else if (flag) { return; } else { if (not (value is I)) { return; } }")]
    [TestCase("if (flag) { value = new C(); if (not (value is I)) { return; } } else { value = new Other(); if (not (value is I)) { return; } }")]
    public void BothContinuingBranchesExportTheirCommonInterface(string guard)
    {
        var result = Run("""
function route(value: object, flag: bool): void { GUARD value.emit("joined"); }
function main(args: string[]): int {
    route(new C(), true); route(new Other(), false); GC.require(); GC.finish(); tick(); return 0;
}
""".Replace("GUARD", guard));
        Assert.That(result.Output, Is.EqualTo(new[] { "joined", "joined" }));
    }

    [Test]
    public void SiblingBranchInterfacesJoinThroughTheirDeclaredParent()
    {
        var result = Run("""
interface K extends I { method another(): void; }
class D implements K {
    public async method emit(message: string): void { print(message); }
    public async method another(): void { }
}
function route(value: object, flag: bool): void {
    if (flag) { if (not (value is J)) { return; } }
    else { if (not (value is K)) { return; } }
    value.emit("parent");
}
function main(args: string[]): int { route(new C(), true); route(new D(), false); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "parent", "parent" }));
    }

    [TestCase("X", "Y")]
    [TestCase("I", "A")]
    public void AmbiguousOrUnrelatedBranchInterfacesDoNotExportFacts(string left, string right)
    {
        var source = Declarations + """
interface A { method alpha(): void; }
interface B { method beta(): void; }
interface X extends A, B { }
interface Y extends A, B { }
function route(value: object, flag: bool): void {
    if (flag) { if (not (value is LEFT)) { return; } }
    else { if (not (value is RIGHT)) { return; } }
    value.alpha();
}
function main(args: string[]): int { return 0; }
""".Replace("LEFT", left).Replace("RIGHT", right);
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("if (not (value is I)) { } value.emit(\"fallthrough\");")]
    [TestCase("if (value is I) { return 0; } value.emit(\"negative-only\");")]
    [TestCase("if (not (value is I)) { return 0; } value = new Plain(); value.emit(\"changed\");")]
    [TestCase("if (not (value is I)) { return 0; } delete value; value.emit(\"deleted\");")]
    [TestCase("if (not (value is I)) { return 0; } let number: int = unbox value; value.emit(\"consumed\");")]
    [TestCase("if (true) { if (not (value is I)) { return 0; } } value.emit(\"scope\");")]
    [TestCase("while (false) { if (not (value is I)) { break; } } value.emit(\"zero-loop\");")]
    [TestCase("if (value is I) { value = new Plain(); } else { return 0; } value.emit(\"changed-survivor\");")]
    [TestCase("if (not (value is I)) { return 0; } else { delete value; } value.emit(\"deleted-survivor\");")]
    [TestCase("if (value is I) { let number: int = unbox value; } else { return 0; } value.emit(\"consumed-survivor\");")]
    [TestCase("while (true) { if (value is I) { value = new Plain(); break; } return; } value.emit(\"changed-break\");")]
    [TestCase("while (value is I) { value = new Plain(); continue; } value.emit(\"condition-write-exit\");")]
    [TestCase("var first = true; var second = true; if (not (value is I)) { return 0; } while (true) { if (first) { break; } else if (second) { value = new Plain(); continue; } else { return 0; } } value.emit(\"continue-write-before-later-break\");")]
    [TestCase("var flag = true; if (not (value is I)) { return 0; } while (true) { try { if (flag) { value = new Plain(); break; } break; } catch { if (not (value is I)) { return 0; } break; } } value.emit(\"try-break-write\");")]
    [TestCase("while (true) { return; if (value is I) { break; } } value.emit(\"unreachable-break\");")]
    [TestCase("while (not (value is I)) { if (true) { break; } return; } value.emit(\"break-condition-join\");")]
    [TestCase("if (value is I) { } else { } value.emit(\"both-continue\");")]
    [TestCase("if (true) { if (value is I) { } else { return 0; } } value.emit(\"outer-scope\");")]
    [TestCase("while (false) { if (value is I) { } else { break; } } value.emit(\"loop-scope\");")]
    [TestCase("if (not (value is I) and false) { return 0; } value.emit(\"missing-proof\");")]
    [TestCase("if (not (value is I) or unbox value) { return 0; } value.emit(\"condition-consumed\");")]
    [TestCase("let parent: I = new C(); if (not (parent is J)) { return 0; } parent = new Other(); parent.extra();")]
    [TestCase("if (true) { if (not (value is I)) { return 0; } value = new Plain(); } else { if (not (value is I)) { return 0; } } value.emit(\"written-join\");")]
    [TestCase("if (true) { if (not (value is I)) { return 0; } let number: int = unbox value; } else { if (not (value is I)) { return 0; } } value.emit(\"consumed-join\");")]
    [TestCase("if (true) { let value: I = new C(); } else { if (not (value is I)) { return 0; } } value.emit(\"shadow-join\");")]
    public void UnprovenOrInvalidatedContinuationFactsRemainErrors(string body)
    {
        var source = Declarations + "function main(args: string[]): int { let value: object = new C(); " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void ConditionalTryCatchBreakDoesNotHideLoopCarriedWrite()
    {
        var source = Declarations + """
function route(value: object, shouldBreak: bool): void {
    if (not (value is I)) { return; }
    var iteration = 0;
    while (iteration < 2) {
        iteration = iteration + 1;
        try { if (shouldBreak) { break; } }
        catch { if (shouldBreak) { break; } }
        value = new Plain();
    }
    value.emit("must-not-narrow");
}
function main(args: string[]): int { route(new C(), false); return 0; }
""";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
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
