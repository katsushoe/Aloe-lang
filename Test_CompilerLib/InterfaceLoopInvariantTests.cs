using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceLoopInvariantTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
interface J extends I { method extra(): void; }
class C implements J {
    public async method emit(message: string): void { print(message); }
    public async method extra(): void {}
}
class Plain {}
class Other implements I { public async method emit(message: string): void { print(message); } }
class D implements I {
    construct(flag: bool) {}
    public async method emit(message: string): void { print(message); }
}
""";

        [Test]
    public void CatchWriteAnalysisIgnoresWritesAfterGuaranteedInvalidReferenceCast()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        try {
            var failed = new Plain() as C ifnot throw;
            value = new Plain();
        } catch (InvalidCastException) { value.emit("cast-edge"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "cast-edge" }));
    }

    [Test]
    public void LoopCatchWriteAnalysisIgnoresWritesAfterGuaranteedInvalidReferenceCast()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try {
                var failed = new Plain() as C ifnot throw;
                value = new Plain();
            } catch (InvalidCastException) { value.emit("loop-cast-edge"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "loop-cast-edge" }));
    }

    [Test]
    public void CatchWriteAnalysisKeepsWritesAfterCastInUnknownConditionalPath()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, flag: bool): void {
    if (value is I) {
        try {
            if (flag) { var failed = new Plain() as C ifnot throw; }
            value = new Plain();
        } catch (InvalidCastException) { value.emit("must-not-compile"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [Test]
    public void CatchWriteAnalysisKeepsWritesAfterCastThatCanSucceed()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        try {
            var tested = new Other() as I ifnot throw;
            value = new Plain();
        } catch (InvalidCastException) { value.emit("must-not-compile"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [Test]
    public void CatchWriteAnalysisIgnoresWritesAfterGuaranteedPrimitiveCastFailure()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        try {
            var failed = new Plain() as int ifnot throw;
            value = new Plain();
        } catch (InvalidCastException) { value.emit("primitive-cast-edge"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "primitive-cast-edge" }));
    }

    [TestCase("if (true)", "else { value = new C(); }")]
    [TestCase("while (true)", "else { value = new C(); }")]
    [TestCase("if (true)", "else if (false) { value = new C(); } else {}")]
    [TestCase("while (true)", "else if (false) { value = new C(); } else {}")]
    public void CatchWriteAnalysisIncludesConstantTrueTryBranch(string enclosing, string tail)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        {{enclosing}} {
            try {
                if (true) { value = new Plain(); } {{tail}}
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (true)")]
    [TestCase("while (true)")]
    public void OuterCatchIncludesWritesAfterNestedConstantTrueCastHandler(string enclosing)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        {{enclosing}} {
            try {
                try { if (true) { var failed = new Plain() as C ifnot throw; } }
                catch (InvalidCastException) {}
                value = new Plain();
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (true)", "if (flag) {} else if (true) { var failed = new Plain() as C ifnot throw; }")]
    [TestCase("while (true)", "if (flag) {} else if (true) { var failed = new Plain() as C ifnot throw; }")]
    [TestCase("if (true)", "if (flag) {} else { if (true) { var failed = new Plain() as C ifnot throw; } }")]
    [TestCase("while (true)", "if (flag) {} else { if (true) { var failed = new Plain() as C ifnot throw; } }")]
    [TestCase("if (true)", "foreach (item in items) { if (true) { var failed = new Plain() as C ifnot throw; } }")]
    [TestCase("while (true)", "foreach (item in items) { if (true) { var failed = new Plain() as C ifnot throw; } }")]
    public void OptionalExceptionalBranchDoesNotHideFollowingTryWrite(string enclosing, string branch)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, flag: bool, items: int[]): void {
    if (value is I) {
        {{enclosing}} {
            try {
                {{branch}}
                value = new Plain();
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("if (true)", "if", "unbox value and false")]
    [TestCase("while (true)", "if", "unbox value and false")]
    [TestCase("if (true)", "while", "unbox value and false")]
    [TestCase("while (true)", "while", "unbox value and false")]
    [TestCase("if (true)", "if", "false or (unbox value and false)")]
    [TestCase("while (true)", "if", "false or (unbox value and false)")]
    [TestCase("if (true)", "while", "false or (unbox value and false)")]
    [TestCase("while (true)", "while", "false or (unbox value and false)")]
    public void ConstantFalseBodyDoesNotHideEvaluatedConditionConsumer(string enclosing, string branch, string condition)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        {{enclosing}} {
            try {
                {{branch}} ({{condition}}) { value = new Plain(); }
                let zero: int = 1 / 0;
            } catch { value.emit("must-not-compile"); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OuterGuaranteedExceptionIsNotHiddenBySeparateNestedHandler(bool insideLoop, bool handlerFirst)
    {
        const string handler = "try { let safe: int = 1; } catch {}";
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{(insideLoop ? "while (iteration < 1)" : "if (true)")}} {
            try {
                {{(handlerFirst ? handler : "")}}
                let zero: int = 1 / 0;
                {{(handlerFirst ? "" : handler)}}
                value = new Plain();
            } catch (ZeroDivisionException) { value.emit("outer-exception"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "outer-exception" }));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void CatchLoopEntryUsesPreTryInterfaceAcrossSiblingWrites(string loop)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("before-try");
            try {
                value = new Other();
                value = new C();
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("catch-sibling"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-try"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "catch-sibling"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)", "new C()")]
    [TestCase("foreach (item in [0, 1])", "new C()")]
    [TestCase("while (iteration < 2)", "new Other()")]
    [TestCase("foreach (item in [0, 1])", "new Other()")]
    public void CatchLoopEntryJoinsPreTryClassAndSiblingWritesThroughCommonInterface(string loop, string initial)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("catch-common-entry");
            value = new Plain();
            value = {{initial}};
            try {
                value = new Other();
                value = new C();
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "catch-common-entry"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void CatchLoopCommonJoinWidensChildGuardToParentInterface(string loop)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is J) {
        var iteration = 0;
        {{loop}} {
            value.emit("parent-interface-entry");
            value = new Plain();
            value = new C();
            try { value = new Other(); let zero: int = 1 / 0; } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "parent-interface-entry"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void CatchLoopCommonJoinDoesNotKeepChildOnlyMember(string loop)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is J) {
        var iteration = 0;
        {{loop}} {
            value.extra();
            value = new Plain();
            value = new C();
            try { value = new Other(); let zero: int = 1 / 0; } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
    }

    [TestCase("while (iteration < 2)", "Exception", "(InvalidCastException)")]
    [TestCase("foreach (item in [0, 1])", "Exception", "(InvalidCastException)")]
    [TestCase("while (iteration < 2)", "SystemException", "(ZeroDivisionException)")]
    [TestCase("foreach (item in [0, 1])", "SystemException", "(ZeroDivisionException)")]
    [TestCase("while (iteration < 2)", "ZeroDivisionException", "(ZeroDivisionException)")]
    [TestCase("foreach (item in [0, 1])", "ZeroDivisionException", "(ZeroDivisionException)")]
    [TestCase("while (iteration < 2)", "Exception", "")]
    [TestCase("foreach (item in [0, 1])", "Exception", "")]
    public void EarlierCatchTypeSubsumesLaterCatchWrites(string loop, string first, string second)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("before-shadowed-catch");
            try { let zero: int = 1 / 0; }
            catch ({{first}}) {}
            catch {{second}} { value = new Plain(); }
            value.emit("after-shadowed-catch");
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-shadowed-catch"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "after-shadowed-catch"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)", "ZeroDivisionException", "SystemException")]
    [TestCase("foreach (item in [0, 1])", "ZeroDivisionException", "SystemException")]
    [TestCase("while (iteration < 2)", "VmException", "SystemException")]
    [TestCase("foreach (item in [0, 1])", "VmException", "SystemException")]
    [TestCase("while (iteration < 2)", "InvalidCastException", "ZeroDivisionException")]
    [TestCase("foreach (item in [0, 1])", "InvalidCastException", "ZeroDivisionException")]
    [TestCase("while (iteration < 2)", "SystemException", "Exception")]
    [TestCase("foreach (item in [0, 1])", "SystemException", "Exception")]
    public void EarlierCatchDoesNotHideBroaderOrSiblingHandlerWrites(string loop, string first, string second)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("must-not-compile");
            try { let zero: int = 1 / 0; }
            catch ({{first}}) {}
            catch ({{second}}) { value = new Plain(); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
    }

    [TestCase("if (true)")]
    [TestCase("while (iteration < 1)")]
    public void OuterCatchWriteScanSkipsTypeShadowedNestedHandlers(string enclosing)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{enclosing}} {
            try {
                value = new C();
                try { let inner: int = 1 / 0; }
                catch (Exception) {}
                catch (ZeroDivisionException) { value = new Plain(); }
                let outer: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("shadowed-nested-handler"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "shadowed-nested-handler" }));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void EmptyTryDoesNotInventCatchWriteEdges(string loop)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("empty-try-entry");
            try {} catch { value = new Plain(); }
            value.emit("empty-try-exit");
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "empty-try-entry"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "empty-try-exit"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)", "return;", 1)]
    [TestCase("foreach (item in [0, 1])", "return;", 1)]
    [TestCase("while (iteration < 2)", "break;", 1)]
    [TestCase("foreach (item in [0, 1])", "break;", 1)]
    [TestCase("while (iteration < 2)", "continue;", 2)]
    [TestCase("foreach (item in [0, 1])", "continue;", 2)]
    public void BareTryTransferDoesNotInventCatchWriteEdges(string loop, string transfer, int expectedCount)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("bare-try-entry");
            iteration = iteration + 1;
            try { {{transfer}} } catch { value = new Plain(); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "bare-try-entry"), Is.EqualTo(expectedCount));
    }

    [TestCase("if (true)")]
    [TestCase("while (iteration < 1)")]
    public void OuterTryWriteScanSkipsNestedUnreachableCatch(string enclosing)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{enclosing}} {
            try {
                value = new C();
                try {} catch { value = new Plain(); }
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("nested-empty-try"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-empty-try" }));
    }

    [TestCase("while (iteration < 2)", "fail();")]
    [TestCase("foreach (item in [0, 1])", "fail();")]
    [TestCase("while (iteration < 2)", "let zero: int = 1 / 0;")]
    [TestCase("foreach (item in [0, 1])", "let zero: int = 1 / 0;")]
    public void PotentiallyThrowingTryKeepsCatchWriteEdges(string loop, string body)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function fail(): void { let zero: int = 1 / 0; }
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("must-not-compile");
            try { {{body}} } catch { value = new Plain(); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
    }

    [TestCase("while (iteration < 2)", "new Plain()")]
    [TestCase("foreach (item in [0, 1])", "new Plain()")]
    [TestCase("while (iteration < 2)", "unknown()")]
    [TestCase("foreach (item in [0, 1])", "unknown()")]
    public void CatchLoopCommonJoinRejectsNonconformingOrUnknownWrite(string loop, string replacement)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function unknown(): object { return new C(); }
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("must-not-compile");
            value = new Plain();
            value = new C();
            try { value = {{replacement}}; let zero: int = 1 / 0; } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void CatchLoopInvariantIgnoresBlockShadowWrites(string loop)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("before-shadow");
            try {
                if (true) { let value: object = new Plain(); value = new Plain(); }
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("after-shadow"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-shadow"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "after-shadow"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)", "if (true) { let value: object = new Plain(); value = new Plain(); }")]
    [TestCase("foreach (item in [0, 1])", "if (true) { let value: object = new Plain(); value = new Plain(); }")]
    [TestCase("while (iteration < 2)", "foreach (value in [new Plain()]) { value = new Plain(); }")]
    [TestCase("foreach (item in [0, 1])", "foreach (value in [new Plain()]) { value = new Plain(); }")]
    public void CatchLoopFixedPointKeepsRestoredOuterLocalAcrossShadowWrites(string loop, string shadow)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("restored-entry");
            value = new Plain();
            value = new C();
            try {
                {{shadow}}
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "restored-entry"), Is.EqualTo(2));
    }

    [TestCase("value = new Plain();")]
    [TestCase("delete value;")]
    public void CatchWriteScanIgnoresForeachBindingChanges(string body)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        try {
            value = new C();
            foreach (value in [new Plain()]) { {{body}} }
            let zero: int = 1 / 0;
        } catch (ZeroDivisionException) { value.emit("foreach-shadow"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "foreach-shadow" }));
    }

    [TestCase("while (iteration < 2)", "new Throwing()")]
    [TestCase("foreach (item in [0, 1])", "new Throwing()")]
    [TestCase("while (iteration < 2)", "fail()")]
    [TestCase("foreach (item in [0, 1])", "fail()")]
    public void CatchFixedPointKeepsPreTryStateWhenInitializerThrowsBeforeAssignment(string loop, string initializer)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
class Throwing implements I {
    construct() { let zero: int = 1 / 0; }
    public async method emit(message: string): void { print(message); }
}
function fail(): I { let zero: int = 1 / 0; return new C(); }
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("must-not-compile");
            value = new Plain();
            try { value = {{initializer}}; } catch (ZeroDivisionException) {}
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void CatchFixedPointPreservesConformingPreTryStateWhenInitializerThrows(string loop)
    {
        var result = Run($$"""
function fail(): I { let zero: int = 1 / 0; return new C(); }
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("pre-try-entry");
            try { value = fail(); }
            catch (ZeroDivisionException) { value.emit("pre-try-catch"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "pre-try-entry"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "pre-try-catch"), Is.EqualTo(2));
    }

    [TestCase("this.value = new Plain();")]
    [TestCase("delete this.value;")]
    public void CatchLoopFixedPointDistinguishesReferenceFieldFromSameNamedParameter(string write)
    {
        var result = Run($$"""
class Holder {
    field value: object;
    construct(value: object) {
        this.value = new Plain();
        if (value is I) {
            var iteration = 0;
            while (iteration < 2) {
                value.emit("field-distinct");
                value = new Plain();
                value = new C();
                try { {{write}} let zero: int = 1 / 0; } catch (ZeroDivisionException) {}
                iteration = iteration + 1;
            }
        }
    }
}
function main(args: string[]): int { var holder = new Holder(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "field-distinct"), Is.EqualTo(2));
    }

    [TestCase("if (true)", "if (true) { let value: object = new Plain(); } value = new Plain();")]
    [TestCase("while (true)", "if (true) { let value: object = new Plain(); } value = new Plain();")]
    [TestCase("if (true)", "foreach (value in [new Plain()]) { value = new Plain(); } value = new Plain();")]
    [TestCase("while (true)", "foreach (value in [new Plain()]) { value = new Plain(); } value = new Plain();")]
    [TestCase("if (true)", "if (true) { let replacement: object = new Plain(); value = replacement; }")]
    [TestCase("while (true)", "if (true) { let replacement: object = new Plain(); value = replacement; }")]
    public void CatchShadowScopeDoesNotHideOuterWritesOrBorrowOuterSourceType(string enclosing, string body)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, replacement: I): void {
    if (value is I) {
        {{enclosing}} {
            try {
                {{body}}
                let zero: int = 1 / 0;
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (iteration < 2)", "return;")]
    [TestCase("while (iteration < 2)", "break;")]
    [TestCase("while (iteration < 2)", "continue;")]
    [TestCase("foreach (item in [0, 1])", "return;")]
    [TestCase("foreach (item in [0, 1])", "break;")]
    [TestCase("foreach (item in [0, 1])", "continue;")]
    public void CatchLoopInvariantIgnoresWritesAfterMandatoryBareTransfer(string loop, string transfer)
    {
        var result = Run($$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("before-transfer");
            iteration = iteration + 1;
            try {
                if (flag) { let zero: int = 1 / 0; }
                {{transfer}}
                value = new Plain();
            } catch (ZeroDivisionException) { value.emit("transfer-catch"); }
        }
    }
}
function main(args: string[]): int { route(new C(), true); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-transfer"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "transfer-catch"), Is.EqualTo(2));
    }

    [Test]
    public void OuterCatchWriteAnalysisIncludesWritesAfterNestedCatchHandlesCastFailure()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        try {
            try { var failed = new Plain() as C ifnot throw; }
            catch (InvalidCastException) {}
            value = new Plain();
            let zero: int = 1 / 0;
        } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

[TestCase("while (iteration < 2)", "new C()")]
    [TestCase("while (iteration < 2)", "value")]
    [TestCase("foreach (item in [0, 1])", "new C()")]
    [TestCase("foreach (item in [0, 1])", "value")]
    public void NominallyConformingWritesPreserveLoopEntryInvariant(string loop, string replacement)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("invariant");
            value = {{replacement}};
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "invariant", "invariant" }));
    }

    [Test]
    public void MultipleTypedCatchClausesInsideLoopPreserveGuardedInterfaceCall()
    {
        var result = Run("""
function route(value: object): void {
    var iteration = 0;
    while (iteration < 1) {
        try { let failed: int = 1 / 0; }
        catch (InvalidCastException) { return; }
        catch (ZeroDivisionException) {
            if (value is I) { value.emit("multi-catch-loop"); }
        }
        iteration = iteration + 1;
    }
}
function main(args: string[]): int { route(new C()); tick(); return 42; }
""");

        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo(new[] { "multi-catch-loop" }));
    }

    [Test]
    public void CatchWithoutTrackedLocalWritesPreservesLoopInterfaceFacts()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 2) {
            try { let failed: int = 1 / 0; }
            catch (ZeroDivisionException) { value.emit("catch-loop-fact"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "catch-loop-fact", "catch-loop-fact" }));
    }

    [TestCase("1 / 0")]
    [TestCase("-1 % 0")]
    [TestCase("(1) / 0")]
    [TestCase("1 / (0)")]
    [TestCase("((-1)) % ((0))")]
    [TestCase("(1 + 1) / 0")]
    [TestCase("1 / (2 - 2)")]
    [TestCase("(3 * 2) % (5 - 5)")]
    public void CatchIgnoresWritesAfterGuaranteedDivisionFailure(string expression)
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            try {
                let failed: int = EXPR;
                value = new Plain();
            } catch {
                value.emit("catch-preserved");
            }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""".Replace("EXPR", expression));

        Assert.That(result.Output, Is.EqualTo(new[] { "catch-preserved" }));
    }

    [TestCase("1 / 0")]
    [TestCase("(1) / (0)")]
    [TestCase("-1 % (0)")]
    [TestCase("(1 / 0)")]
    [TestCase("((-1) % (0))")]
    [TestCase("(1 + 1) / 0")]
    [TestCase("1 / (2 - 2)")]
    [TestCase("(3 * 2) % (5 - 5)")]
    public void CatchIgnoresWritesAfterGuaranteedZeroDivisionAssignment(string expression)
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var failed = 1;
        try {
            failed = EXPR;
            value = new Plain();
        } catch {
            value.emit("catch-preserved-after-assignment");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""".Replace("EXPR", expression));

        Assert.That(result.Output, Is.EqualTo(new[] { "catch-preserved-after-assignment" }));
    }

    [Test]
    public void CatchFlowDoesNotAssumeZeroDivisionAfterEarlierInt32Overflow()
    {
        Assert.Throws<AloeCompileException>(() => Run("""
function route(value: object): void {
    if (value is I) {
        try {
            let failed: int = (2147483647 + 1) / 0;
            value = new Plain();
        } catch (ZeroDivisionException) {
            value.emit("must-not-compile");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
"""));
    }

    [Test]
    public void CatchWriteInvalidatesOnlyTheWrittenLoopInterfaceLocal()
    {
        var result = Run("""
function route(value: object, peer: object): void {
    if ((value is I) and (peer is I)) {
        var iteration = 0;
        while (iteration < 2) {
            try { peer = new Plain(); let failed: int = 1 / 0; }
            catch (ZeroDivisionException) { value.emit("unmodified-catch-fact"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C(), new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "unmodified-catch-fact", "unmodified-catch-fact" }));
    }

    [Test]
    public void CatchPreservesInterfaceFactWhenEveryTryWriteConforms()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try { value = new C(); value = produce(); let failed: int = 1 / 0; }
            catch (ZeroDivisionException) { value.emit("catch-compatible-write"); }
            iteration = iteration + 1;
        }
    }
}
function produce(): I { return new C(); }
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "catch-compatible-write" }));
    }

    [Test]
    public void NonLoopCatchPreservesInterfaceFactForCompatibleTryWrites()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        try { value = new C(); let failed: int = 1 / 0; }
        catch (ZeroDivisionException) { value.emit("non-loop-catch-compatible-write"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "non-loop-catch-compatible-write" }));
    }

    [Test]
    public void CatchWriteAnalysisIgnoresShadowedLocals()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        try {
            value = new C();
            if (true) {
                let value: object = new Plain();
                value = new Plain();
            }
            let failed: int = 1 / 0;
        } catch (ZeroDivisionException) { value.emit("outer-interface-fact"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "outer-interface-fact" }));
    }

    [TestCase("if (false)")]
    [TestCase("while (false)")]
    [TestCase("if (false and unbox value)")]
    public void CatchWriteAnalysisIgnoresWritesInConstantUnreachableBranches(string branch)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        try {
            value = new C();
            {{branch}} { value = new Plain(); }
            let failed: int = 1 / 0;
        } catch (ZeroDivisionException) { value.emit("unreachable-write-ignored"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "unreachable-write-ignored" }));
    }

    [Test]
    public void CatchWriteAnalysisIgnoresConstantTrueElseBranch()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        try {
            if (true or unbox value) { value = new C(); } else { value = new Plain(); }
            let failed: int = 1 / 0;
        } catch (ZeroDivisionException) { value.emit("constant-true-else-ignored"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "constant-true-else-ignored" }));
    }

    [Test]
    public void CatchWriteAnalysisIgnoresWritesAfterGuaranteedDivisionInConstantTrueBlock()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var failed = 1;
        try {
            if (true) {
                failed = 1 / 0;
                value = new Plain();
            }
        } catch (ZeroDivisionException) { value.emit("division-block-write-ignored"); }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "division-block-write-ignored" }));
    }

    [Test]
    public void CatchWriteAnalysisKeepsWritesInReachableConditionalDivisionPath()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, flag: bool): void {
    if (value is I) {
        try {
            if (flag) {
                if (true) { let failed: int = 1 / 0; }
            }
            value = new Plain();
        } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void CatchWriteAnalysisRetainsWritesBeforeGuaranteedDivisionInConstantTrueBlock()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        try {
            if (true) {
                value = new Plain();
                let failed: int = 1 / 0;
            }
        } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void LoopCatchAnalysisIgnoresWritesAfterGuaranteedDivisionInConstantTrueBlock()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try {
                if (true) {
                    let failed: int = 1 / 0;
                    value = new Plain();
                }
            } catch (ZeroDivisionException) { value.emit("loop-division-block-write-ignored"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");

        Assert.That(result.Output, Is.EqualTo(new[] { "loop-division-block-write-ignored" }));
    }

    [Test]
    public void LoopCatchAnalysisRetainsWritesBeforeGuaranteedDivisionInConstantTrueBlock()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try {
                if (true) {
                    value = new Plain();
                    let failed: int = 1 / 0;
                }
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void LoopCatchAnalysisKeepsWritesAfterDivisionOnUnknownConditionalPath()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, flag: bool): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try {
                if (flag) {
                    if (true) { let failed: int = 1 / 0; }
                }
                value = new Plain();
            } catch (ZeroDivisionException) { value.emit("must-not-compile"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }


    [Test]
    public void CatchRejectsInterfaceCallWhenTryWritesIncompatibleType()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 1) {
            try { value = new Plain(); let failed: int = 1 / 0; }
            catch (ZeroDivisionException) { value.emit("must-not-compile"); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void MultipleTypedCatchClausesAllContributeToLoopBackEdgeJoin()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 2) {
            value.emit("must-not-retain-narrowing");
            try { let failed: int = 1 / 0; return; }
            catch (InvalidCastException) { return; }
            catch (ZeroDivisionException) {
                value = new Plain();
                iteration = iteration + 1;
                continue;
            }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));

        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void LaterTypedCatchBreakParticipatesInLoopExitJoin()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        while (true) {
            try { let failed: int = 1 / 0; return; }
            catch (InvalidCastException) { return; }
            catch (ZeroDivisionException) {
                value = new Plain();
                break;
            }
        }
        value.emit("must-not-retain-narrowing");
    }
}
function main(args: string[]): int { return 0; }
"""));

        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (flag)", "new C()")]
    [TestCase("while (flag)", "value")]
    [TestCase("foreach (item in [0, 1])", "new C()")]
    [TestCase("foreach (item in [0, 1])", "value")]
    public void InvariantEntryDoesNotRetainFactAfterDirectWrite(string loop, string replacement)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        {{loop}} { value = {{replacement}}; value.emit("must-not-narrow"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (iteration < 2)", true)]
    [TestCase("while (iteration < 2)", false)]
    [TestCase("foreach (item in [0, 1])", true)]
    [TestCase("foreach (item in [0, 1])", false)]
    public void LoopBackEdgeJoinsIncomingAndSiblingImplementationsToCommonInterface(string loop, bool chooseChild)
    {
        var result = Run($$"""
function route(value: object, chooseChild: bool): void {
    if (value is J) {
        var iteration = 0;
        {{loop}} {
            value.emit("loop-join");
            if (chooseChild) { value = new C(); } else { value = new Other(); }
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { route(new C(), {{chooseChild.ToString().ToLowerInvariant()}}); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "loop-join"), Is.EqualTo(2));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("foreach (item in [0, 1])")]
    public void LoopBackEdgeJoinDoesNotRetainChildOnlyMembers(string loop)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, chooseChild: bool): void {
    if (value is J) {
        var iteration = 0;
        {{loop}} {
            if (chooseChild) { value = new C(); } else { value = new Other(); }
            value.extra();
            iteration = iteration + 1;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object")
            .Or.Contain("'I.extra' is not a public async instance method"));
    }

    [TestCase("value = new Plain();")]
    [TestCase("value = new C(); value = new Plain();")]
    [TestCase("value = unknown();")]
    [TestCase("value = new C() as Plain;")]
    [TestCase("let flag: bool = unbox value;")]
    public void UnprovenOrConsumedBackEdgesStillInvalidateEntry(string tail)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function unknown(): object { return new Plain(); }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("must-not-narrow"); {{tail}} }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("new C()")]
    [TestCase("new Other()")]
    [TestCase("value")]
    public void ContinueWithConformingWritePreservesEntryInvariant(string replacement)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("continue-invariant");
            if (item == 0) { value = {{replacement}}; continue; }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "continue-invariant", "continue-invariant" }));
    }

    [Test]
    public void ParentOnlyReplacementDoesNotPreserveChildInvariant()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is J) {
        foreach (item in [0, 1]) { value.extra(); value = new Other(); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'I.extra' is not a public async instance method"));
    }

    [TestCase("(value)")]
    [TestCase("((new C()))")]
    [TestCase("peer")]
    [TestCase("contract")]
    [TestCase("produce()")]
    [TestCase("new D(true)")]
    [TestCase("new D(true or unbox value)")]
    [TestCase("value as I")]
    [TestCase("new C() as J")]
    [TestCase("produce() as C")]
    [TestCase("((peer as I))")]
    public void KnownNominalResultsPreserveEntryInvariant(string replacement)
    {
        var result = Run($$"""
function produce(): I { return new C(); }
function route(value: object, peer: C, contract: I): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("known-result"); value = {{replacement}}; }
    }
}
function main(args: string[]): int { route(new C(), new C(), new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "known-result", "known-result" }));
    }

    [TestCase("new D(unbox value)")]
    [TestCase("produce(unbox value)")]
    [TestCase("new D(unbox value) as I")]
    public void KnownResultDoesNotHideArgumentConsumptionAtCatchContinue(string replacement)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function produce(flag: bool): I { return new C(); }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            try { value = {{replacement}}; } catch { continue; }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ShadowedSourceDoesNotBorrowOuterDeclaredContract()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, peer: C, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            if (flag) { let peer: object = new Plain(); value = peer; }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void AnotherLocalsTemporaryFactDoesNotEstablishInvariant()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, peer: object): void {
    if ((value is I) and (peer is I)) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            value = peer;
            peer = new Plain();
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (gate)", "true or probe()")]
    [TestCase("while (gate)", "false and probe()")]
    [TestCase("foreach (item in [0, 1])", "true or probe()")]
    [TestCase("foreach (item in [0, 1])", "false and probe()")]
    public void ShortCircuitRangesDoNotCrossStatementBoundary(string loop, string expression)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function probe(): bool { return false; }
function route(value: object, gate: bool): void {
    if (value is I) {
        {{loop}} {
            value.emit("must-not-narrow");
            let ignored: bool = {{expression}};
            let consumed: bool = unbox value;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ThrowingCheckedAssignmentKeepsUnchangedValueOnCatchContinue()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("cast-fault-invariant");
            try { value = new Plain() as I; } catch { continue; }
        }
        value.emit("cast-fault-invariant");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "cast-fault-invariant", "cast-fault-invariant", "cast-fault-invariant" }));
    }

    [TestCase("while (iteration < 2)", "value = new Plain(); value = new C();", "")]
    [TestCase("foreach (item in [0, 1])", "value = new Plain(); value = new C();", "")]
    [TestCase("while (iteration < 2)", "value = new Plain(); value = new C();", "continue;")]
    [TestCase("foreach (item in [0, 1])", "value = new Plain(); value = new C();", "continue;")]
    [TestCase("foreach (item in [0, 1])", "value = unknown(); value = new C();", "")]
    [TestCase("foreach (item in [0, 1])", "value = new Plain(); value = produce();", "")]
    public void StraightLineBackEdgeCanReestablishIncomingInterface(string loop, string writes, string terminal)
    {
        var result = Run($$"""
function unknown(): object { return new Plain(); }
function produce(): I { return new C(); }
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("restored-back-edge");
            {{writes}}
            iteration = iteration + 1;
            {{terminal}}
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "restored-back-edge", "restored-back-edge" }));
    }

    [TestCase("value = new Plain(); continue; value = new C();")]
    [TestCase("value = new Plain(); value = value;")]
    [TestCase("value = new Plain(); value.emit(\"invalid-mid-body\"); value = new C();")]
    [TestCase("value = new Plain(); if (flag) { value = new C(); }")]
    [TestCase("try { value = new Plain(); value = new C(); } catch { continue; }")]
    [TestCase("value = new Plain(); if (flag) { value = new C(); } else { value = new Plain(); }")]
    [TestCase("if (flag) { value = new Plain(); continue; } value = new C();")]
    [TestCase("value = new Plain(); if (flag) { let value: object = new C(); } else { value = new C(); }")]
    [TestCase("value = new Plain(); if (flag) { value = value; } else { value = new C(); }")]
    [TestCase("delete value; continue; value = new C();")]
    [TestCase("value = new C(); delete value;")]
    [TestCase("delete value; value.emit(\"invalid-after-delete\"); value = new C();")]
    public void StraightLineProofDoesNotHideUnsafeIntermediateOrBypassPaths(string body)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("entry"); {{body}} }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ReestablishedBackEdgePreservesNormalExhaustionFact()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) { value = new Plain(); value = new C(); }
        value.emit("exhaustion-invariant");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "exhaustion-invariant" }));
    }

    [TestCase("if (item == 0) { value = new C(); } else { value = produce(); }")]
    [TestCase("if (item == 0) { value = new C(); continue; } value = produce();")]
    [TestCase("if (item == 0) { value = new C(); } else if (item == 1) { value = produce(); } else { value = new Other(); }")]
    [TestCase("if (true) { value = new C(); } else { value = new Plain(); }")]
    [TestCase("if (item == 0) { let ignored: bool = true or unbox value; value = new C(); } else { value = produce(); }")]
    [TestCase("if (item == 0) { if (item < 2) { value = new C(); } else { value = produce(); } } else { value = new Other(); }")]
    public void ConditionalBackEdgesCanReestablishIncomingInterface(string restoration)
    {
        var result = Run($$"""
function produce(): I { return new C(); }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("conditional-back-edge");
            value = new Plain();
            {{restoration}}
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "conditional-back-edge", "conditional-back-edge" }));
    }

    [TestCase("if (value is I) { continue; } else { return; }")]
    [TestCase("if (not (value is I)) { return; }")]
    [TestCase("if (not (value is I)) { break; }")]
    [TestCase("if ((value is J) or (value is I)) { continue; } return;")]
    public void InterfaceGuardsCanReestablishAllContinuingBackEdges(string guard)
    {
        var result = Run($$"""
function unknown(): object { return new C(); }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("guarded-back-edge");
            value = unknown();
            {{guard}}
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "guarded-back-edge", "guarded-back-edge" }));
    }

    [TestCase("if ((value is I) or flag) { continue; } return;")]
    [TestCase("if (not (value is I) and flag) { return; }")]
    [TestCase("if (peer is I) { continue; } return;")]
    [TestCase("if (flag) { let value: object = new C(); if (value is I) { continue; } } return;")]
    [TestCase("if ((value is I) and unbox value) { continue; } return;")]
    public void IncompleteShadowedOrConsumedGuardsDoNotEstablishInvariant(string guard)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function unknown(): object { return new Plain(); }
function route(value: object, peer: object, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            value = unknown();
            {{guard}}
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void DeletedValueRecreatedBeforeBackEdgePreservesEntryAndExhaustion()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("recreated-back-edge");
            delete value;
            value = new C();
        }
        value.emit("recreated-back-edge");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "recreated-back-edge", "recreated-back-edge", "recreated-back-edge" }));
    }

    [Test]
    public void ConsumedReturningBranchDoesNotInvalidateRestoredContinuingBranch()
    {
        var result = Run("""
function route(value: object, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("returning-consumer");
            if (flag) { delete value; return; }
            value = new Plain(); value = new C();
        }
    }
}
function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "returning-consumer", "returning-consumer", "returning-consumer" }));
    }

    [Test]
    public void ParentGuardDoesNotReestablishChildInvariant()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function unknown(): object { return new Other(); }
function route(value: object): void {
    if (value is J) {
        foreach (item in [0, 1]) {
            value.extra();
            value = unknown();
            if (value is I) { continue; } else { return; }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'I.extra' is not a public async instance method"));
    }

    [TestCase("while (iteration < 2)")]
    [TestCase("while (true)")]
    [TestCase("foreach (item in [0, 1])")]
    public void RestoredBackEdgesDoNotHideIncompatibleBreakExits(string loop)
    {
        string Source(string exitRead) => Declarations + $$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        var iteration = 0;
        {{loop}} {
            value.emit("safe-entry");
            value = new Plain();
            if (flag) { break; }
            value = new C();
            iteration = iteration + 1;
        }
        {{exitRead}}
    }
}
function main(args: string[]): int { return 0; }
""";
        Assert.DoesNotThrow(() => new AloeCompiler().Compile(Source("")));
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Source("value.emit(\"unsafe-exit\");")));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (flag)")]
    [TestCase("foreach (item in [0, 1])")]
    public void RestoredBackEdgeDoesNotSuppressOuterCatchWriteTracking(string loop)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function produce(): I { let number: int = 1 / 0; return new C(); }
function route(value: object, flag: bool): void {
    if (value is I) {
        try {
            {{loop}} { value.emit("safe-entry"); value = new Plain(); value = produce(); }
        } catch { value.emit("unsafe-catch"); }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void FaultBeforeRestorationKeepsActualIncompatibleValueInOuterCatch()
    {
        var result = Run("""
function produce(): I { let number: int = 1 / 0; return new C(); }
function route(value: object): void {
    if (value is I) {
        try {
            foreach (item in [0, 1]) { value.emit("entry-before-fault"); value = new Plain(); value = produce(); }
        } catch {
            if (value is I) { value.emit("must-not-run"); } else { print("plain-in-catch"); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EquivalentTo(new[] { "entry-before-fault", "plain-in-catch" }));
    }

    [TestCase("with (new C()) { value = new Plain(); value = new C(); }")]
    [TestCase("with (new C()) { value = new Plain(); if (item == 0) { value = new C(); continue; } value = new Other(); }")]
    [TestCase("with (C) { with (new D(true)) { value = new Plain(); value = new C(); } }")]
    [TestCase("with (new D(true or unbox value), new C()) { value = new Plain(); value = new C(); }")]
    public void WithBodyCanReestablishLoopBackEdge(string body)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("with-back-edge"); {{body}} }
        value.emit("with-back-edge");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-back-edge", "with-back-edge", "with-back-edge" }));
    }

    [TestCase("with (new C()) { value = new Plain(); continue; } value = new C();")]
    [TestCase("with (new C()) { let value: object = new C(); } value = value;")]
    public void WithTransferDoesNotHideUnsafeBackEdge(string body)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            value = new Plain();
            {{body}}
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("new D(unbox value)")]
    [TestCase("new D(true or unbox value), new D(unbox value)")]
    public void ReachableWithTargetConsumerInvalidatesRepeatedEntry(string targets)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("must-not-narrow"); with ({{targets}}) {} }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void WithCapturedTargetIsNotRetargetedByLocalRestoration()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("entry");
            with (value as C) {
                value = new Plain();
                .emit("captured");
                value = new C();
            }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "entry"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "captured"), Is.EqualTo(2));
    }

    [Test]
    public void WithScopedShadowDoesNotEscapeIntoRestoringAssignment()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("with-scope");
            value = new Plain();
            with (new C()) { let value: object = new Plain(); }
            value = new C();
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "with-scope", "with-scope" }));
    }

    [TestCase("")]
    [TestCase("continue;")]
    public void WithRestorationAlsoPreservesWhileEntry(string terminal)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        var iteration = 0;
        while (iteration < 2) {
            value.emit("while-with");
            with (new C()) { value = new Plain(); value = new C(); iteration = iteration + 1; {{terminal}} }
        }
        value.emit("while-with");
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "while-with", "while-with", "while-with" }));
    }

    [TestCase("with (new C()) { value = new Plain(); if (flag) { break; } value = new C(); }", "value.emit(\"unsafe-exit\");")]
    [TestCase("with (new D(unbox value)) { value.emit(\"unsafe-after-header\"); value = new C(); }", "")]
    public void WithInvariantDoesNotRestoreUnsafeExitOrIntermediateRead(string body, string exitRead)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) { value.emit("safe-entry"); {{body}} }
        {{exitRead}}
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("foreach (item in [0, 1])", "while (inner < 1) { value = new Plain(); inner = inner + 1; }")]
    [TestCase("while (outer < 2)", "foreach (inner in [0, 1]) { value = new Plain(); }")]
    public void NestedLoopCanRestoreOuterBackEdgeBeforeContinuation(string outerLoop, string nestedLoop)
    {
        var iterationInit = outerLoop.StartsWith("while", StringComparison.Ordinal) ? "var outer = 0; var inner = 0;" : "var inner = 0;";
        var iterationAdvance = outerLoop.StartsWith("while", StringComparison.Ordinal) ? "outer = outer + 1;" : string.Empty;
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        {{iterationInit}}
        {{outerLoop}} {
            value.emit("nested-loop-entry");
            {{nestedLoop}}
            value = new C();
            {{iterationAdvance}}
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "nested-loop-entry"), Is.EqualTo(2));
    }

    [TestCase("while (inner < 1) { value = new Plain(); inner = inner + 1; }")]
    [TestCase("foreach (inner in [0, 1]) { value = new Plain(); }")]
    public void NestedLoopWithoutRestorationInvalidatesOuterBackEdge(string nestedLoop)
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + $$"""
function route(value: object): void {
    if (value is I) {
        var inner = 0;
        foreach (outer in [0, 1]) {
            value.emit("must-not-narrow");
            {{nestedLoop}}
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [TestCase("while (inner < 1) { value = new Plain(); value = new C(); inner = inner + 1; }")]
    [TestCase("foreach (inner in [0, 1]) { value = new Plain(); value = new C(); }")]
    public void NestedLoopRestorationPreservesOuterFactAtItsExit(string nestedLoop)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            var inner = 0;
            value.emit("outer-entry");
            {{nestedLoop}}
            value.emit("after-inner-loop");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "outer-entry"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "after-inner-loop"), Is.EqualTo(2));
    }

    [Test]
    public void NestedForeachBindingShadowsCandidateOnlyInItsBody()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            foreach (value in [0, 1]) { value = 1; }
            value.emit("outer-binding");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "outer-binding", "outer-binding" }));
    }

    [Test]
    public void NestedBreakWithoutRestorationInvalidatesOuterBackEdge()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, gate: bool): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            value.emit("must-not-narrow");
            while (gate) { value = new Plain(); break; }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedWhileFalseConditionCanEstablishOuterBackEdge()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            value.emit("before-condition-loop");
            value = new Plain();
            while (not (value is I)) { value = new C(); }
            value.emit("after-condition-loop");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-condition-loop"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "after-condition-loop"), Is.EqualTo(2));
    }

    [Test]
    public void NestedWhileFalseProofAppliesToBackEdgeAfterUnknownWrite()
    {
        var result = Run("""
function make(): object { return new C(); }
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            value = new Plain();
            while (not (value is I)) {
                value = make();
                continue;
                value = new Plain();
            }
            value.emit("false-proof-after-unknown-write");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "false-proof-after-unknown-write"), Is.EqualTo(2));
    }

    [Test]
    public void NestedWhileFalseOrProofJoinsCommonInterface()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            value = new Plain();
            while (not ((value is J) or (value is I))) { value = new Other(); }
            value.emit("nested-false-or-common-interface");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-false-or-common-interface" }));
    }

    [Test]
    public void NestedWhileFalseSiblingOrProofJoinsSharedParent()
    {
        var result = Run("""
interface K extends I { method extraK(): void; }
class E implements K {
    public async method emit(message: string): void { print(message); }
    public async method extraK(): void {}
}
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            value = new Plain();
            while (not ((value is J) or (value is K))) { value = new E(); }
            value.emit("nested-false-sibling-shared-parent");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-false-sibling-shared-parent" }));
    }

    [Test]
    public void NestedWhileFalseProofAppliesAfterContinue()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            value = new Plain();
            while (not (value is I)) {
                value = new C();
                continue;
            }
            value.emit("false-proof-after-continue");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "false-proof-after-continue"), Is.EqualTo(2));
    }

    [Test]
    public void NestedWhileFalseProofDoesNotHideUnrestoredBreak()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, leaveEarly: bool): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            value = new Plain();
            while (not (value is I)) {
                if (leaveEarly) { break; }
                value = new C();
            }
            value.emit("must-not-narrow-after-break");
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedWhileFalseProofJoinsRestoredBreak()
    {
        var result = Run("""
function route(value: object, leaveEarly: bool): void {
    if (value is I) {
        foreach (outer in [0]) {
            value = new Plain();
            while (not (value is I)) {
                if (leaveEarly) {
                    value = new C();
                    if (value is I) { break; }
                }
                value = new C();
            }
            value.emit("false-proof-restored-break");
        }
    }
}
function main(args: string[]): int { route(new C(), true); route(new C(), false); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "false-proof-restored-break"), Is.EqualTo(2));
    }

    [Test]
    public void NestedWhileTrueConditionNarrowsItsBody()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            value = new C();
            while (value is I) {
                value.emit("nested-true-condition");
                value = new Plain();
            }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-true-condition" }));
    }

    [Test]
    public void NestedWhileTrueOrProofJoinsCommonInterface()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            value = new Other();
            while ((value is J) or (value is I)) {
                value.emit("nested-or-common-interface");
                value = new Plain();
            }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-or-common-interface" }));
    }

    [Test]
    public void NestedWhileSiblingOrProofJoinsSharedParent()
    {
        var result = Run("""
interface K extends I { method extraK(): void; }
class E implements K {
    public async method emit(message: string): void { print(message); }
    public async method extraK(): void {}
}
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            value = new E();
            while ((value is J) or (value is K)) {
                value.emit("nested-sibling-or-shared-parent");
                value = new Plain();
            }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "nested-sibling-or-shared-parent" }));
    }

    [Test]
    public void NestedWhileOrWithoutCommonInterfaceDoesNotChooseAnArm()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
interface K { method onlyK(): void; }
interface L { method onlyL(): void; }
class EK implements K { public async method onlyK(): void {} }
class EL implements L { public async method onlyL(): void {} }
function route(value: object): void {
    foreach (item in [0]) {
        while ((value is K) or (value is L)) {
            value.onlyK();
            break;
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedWhileFalseOrWithoutCommonInterfaceDoesNotChooseAnArm()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
interface K { method onlyK(): void; }
interface L { method onlyL(): void; }
class EK implements K { public async method onlyK(): void {} }
class EL implements L { public async method onlyL(): void {} }
function route(value: object): void {
    foreach (item in [0]) {
        while (not ((value is K) or (value is L))) { value = new EK(); }
        value.onlyK();
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedWhileTrueProofIsInvalidatedByBodyWrite()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            while (value is I) {
                value = new Plain();
                value.emit("must-not-narrow-after-write");
            }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedWhileConsumingConditionDoesNotRestoreItsProof()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        foreach (item in [0]) {
            while ((value is I) and unbox value) {
                value.emit("must-not-narrow");
            }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void NestedConstantFalseWhileDoesNotContributeABackEdge()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("before-constant-false-loop");
            while (false) { value = new Plain(); }
            value.emit("after-constant-false-loop");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "before-constant-false-loop"), Is.EqualTo(2));
        Assert.That(result.Output.Count(line => line == "after-constant-false-loop"), Is.EqualTo(2));
    }

    [TestCase("value = new Plain(); break;")]
    [TestCase("value = new Plain(); continue;")]
    public void NestedConstantFalseWhileExcludesUnreachableTransfersAndWrites(string body)
    {
        var result = Run($$"""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            while (false) { {{body}} }
            value.emit("after-unreachable-loop");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "after-unreachable-loop"), Is.EqualTo(2));
    }

    [Test]
    public void NestedConstantFalseWhileSkipsShortCircuitedConsumer()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            while (false and unbox value) { value = new Plain(); }
            value.emit("after-short-circuited-consumer");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "after-short-circuited-consumer"), Is.EqualTo(2));
    }

    [Test]
    public void NestedConstantTrueWhileSkipsShortCircuitedConsumer()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            while (true or unbox value) { break; }
            value.emit("after-true-short-circuited-consumer");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "after-true-short-circuited-consumer"), Is.EqualTo(2));
    }

    [Test]
    public void TryAndCatchCanBothRestoreOuterBackEdge()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("handler-back-edge");
            try { value = new Plain(); value = new C(); }
            catch { value = new C(); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "handler-back-edge", "handler-back-edge" }));
    }

    [Test]
    public void CatchPreservesOuterInvariantWhenTryDoesNotMutateIt()
    {
        var result = Run("""
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            try { let ignored: int = 1 / 0; }
            catch { print("caught-preserved"); }
            value.emit("handler-preserved-invariant");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[]
        {
            "caught-preserved", "caught-preserved",
            "handler-preserved-invariant", "handler-preserved-invariant"
        }));
    }

    [Test]
    public void TerminalCatchWriteDoesNotInvalidateContinuingTryPath()
    {
        var result = Run("""
function route(value: object, fail: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            try { if (fail) { let ignored: int = 1 / 0; } }
            catch { value = new Plain(); return; }
            value.emit("continuing-try-path");
        }
    }
}
function main(args: string[]): int { route(new C(), false); route(new C(), true); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "continuing-try-path"), Is.EqualTo(2));
    }

    [Test]
    public void CatchEntryMustAlsoRestoreOuterBackEdge()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            try { value = new Plain(); value = new C(); }
            catch { }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void CatchGuardAndRestorationCoverEachBackEdge()
    {
        var result = Run("""
        function fail(): I { let number: int = 1 / 0; return new C(); }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("catch-edge");
            try { value = new Plain(); value = fail(); }
            catch (Exception) { if (value is I) { continue; } value = new C(); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "catch-edge", "catch-edge" }));
    }

    [Test]
    public void NestedRethrowAndOuterCatchRestorationPreserveBackEdge()
    {
        var result = Run("""
function fail(): int { return 1 / 0; }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("before-nested-rethrow");
            try {
                try { value = new Plain(); let ignored: int = fail(); value = new C(); }
                catch (ZeroDivisionException) { throw; }
            }
            catch (Exception) { value = new C(); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "before-nested-rethrow", "before-nested-rethrow" }));
    }

    [Test]
    public void NestedRethrowCatchRestorationDoesNotHideUnrestoredNormalPath()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function fail(): int { return 1 / 0; }
function route(value: object): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            try {
                try { value = new Plain(); let ignored: int = fail(); }
                catch { throw; }
            }
            catch { value = new C(); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void CatchContinueBeforeRestorationInvalidatesOuterBackEdge()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, flag: bool): void {
    if (value is I) {
        foreach (item in [0, 1]) {
            value.emit("must-not-narrow");
            try { value = new Plain(); value = new C(); }
            catch { if (flag) { continue; } value = new C(); }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }

    [Test]
    public void ContinueAndFallthroughBackEdgesJoinAtCommonInterface()
    {
        var result = Run("""
function route(value: object): void {
    if (value is J) {
        foreach (item in [0, 1]) {
            value.emit("continued-join");
            if (item == 0) { value = new Other(); continue; }
            value = new C();
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "continued-join", "continued-join" }));
    }
    [Test]
    public void BreakExitDoesNotRestoreFactsDiscardedByDirectAssignment()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object, enter: bool): void {
    if (value is J) {
        while (enter) { value = new Other(); break; }
        value.emit("must-not-narrow");
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [Test]
    public void NestedForeachBackEdgesJoinAtEachLoopHead()
    {
        var result = Run("""
function route(value: object): void {
    if (value is J) {
        foreach (outer in [0, 1]) {
            foreach (inner in [0, 1]) {
                value.emit("nested-join");
                if (inner == 0) { value = new Other(); continue; }
                value = new C();
            }
            value.emit("outer-join");
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output.Count(line => line == "nested-join"), Is.EqualTo(4));
        Assert.That(result.Output.Count(line => line == "outer-join"), Is.EqualTo(2));
    }
    [Test]
    public void NestedLoopUnknownContinueInvalidatesOuterEntry()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            foreach (inner in [0, 1]) {
                value.emit("must-not-narrow");
                if (inner == 0) { value = new Plain(); continue; }
            }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [Test]
    public void NestedLoopUnknownBreakInvalidatesOuterEntry()
    {
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Declarations + """
function route(value: object): void {
    if (value is I) {
        foreach (outer in [0, 1]) {
            foreach (inner in [0, 1]) {
                value.emit("must-not-narrow");
                value = new Plain();
                break;
            }
        }
    }
}
function main(args: string[]): int { return 0; }
"""));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [Test]
    public void ReturningBranchDoesNotWidenLoopBackEdge()
    {
        var result = Run("""
function route(value: object): void {
    if (value is J) {
        foreach (item in [0, 1]) {
            value.emit("terminal-join");
            if (item == 0) { value = new Plain(); return; }
            value = new Other();
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "terminal-join" }));
    }
    [Test]
    public void LoopFixedPointJoinsSiblingInterfacesThroughSharedParent()
    {
        var source = """
interface I { method emit(message: string): void; }
interface J extends I { method extra(): void; }
interface K extends I {}
interface L extends I {}
class CK implements K { public async method emit(message: string): void { print(message); } }
class CL implements L { public async method emit(message: string): void { print(message); } }
function route(value: object): void {
    if (value is K) {
        foreach (item in [0, 1]) {
            value.emit("shared-parent");
            if (item == 0) { value = new CK(); } else { value = new CL(); }
        }
    }
}
function main(args: string[]): int { route(new CK()); tick(); return 0; }
""";
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        vm.Pop();
        Assert.That(output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { "shared-parent", "shared-parent" }));
    }
    [Test]
    public void LoopFixedPointDoesNotChooseBetweenAmbiguousSharedParents()
    {
        var source = """
interface IA { method alpha(): void; }
interface IB { method beta(): void; }
interface K extends IA, IB {}
interface L extends IA, IB {}
class CK implements K {
    public async method alpha(): void {}
    public async method beta(): void {}
}
class CL implements L {
    public async method alpha(): void {}
    public async method beta(): void {}
}
function route(value: object): void {
    if (value is K) {
        foreach (item in [0, 1]) {
            value.alpha();
            if (item == 0) { value = new CK(); } else { value = new CL(); }
        }
    }
}
function main(args: string[]): int { return 0; }
""";
        var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(exception!.Message, Does.Contain("'value' is object"));
    }
    [TestCase(true)]
    [TestCase(false)]
    public void MapForeachLoopFixedPointJoinsSiblingImplementations(bool chooseChild)
    {
        var result = Run($$"""
function route(value: object, chooseChild: bool): void {
    if (value is J) {
        let items: map<I/I> = [new C(): new C(), new Other(): new Other()];
        foreach (key, item in items) {
            value.emit("map-loop-join");
            if (chooseChild) { value = new C(); } else { value = new Other(); }
        }
    }
}
function main(args: string[]): int { route(new C(), {{chooseChild.ToString().ToLowerInvariant()}}); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "map-loop-join", "map-loop-join" }));
    }
    [Test]
    public void PipeForeachLoopFixedPointJoinsSiblingImplementations()
    {
        var result = Run("""
function route(value: object): void {
    if (value is J) {
        let items: pipe<int> = pipe<int>.create();
        items.write(0); items.write(1); items.close();
        foreach (item in items) {
            value.emit("pipe-loop-join");
            if (item == 0) { value = new C(); } else { value = new Other(); }
        }
    }
}
function main(args: string[]): int { route(new C()); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "pipe-loop-join", "pipe-loop-join" }));
    }
    [TestCase(true)]
    [TestCase(false)]
    public void SetForeachLoopFixedPointJoinsSiblingImplementations(bool chooseChild)
    {
        var result = Run($$"""
function route(value: object, chooseChild: bool): void {
    if (value is J) {
        let items: I{} = {new C(), new Other()};
        foreach (item in items) {
            value.emit("set-loop-join");
            if (chooseChild) { value = new C(); } else { value = new Other(); }
        }
    }
}
function main(args: string[]): int { route(new C(), {{chooseChild.ToString().ToLowerInvariant()}}); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { "set-loop-join", "set-loop-join" }));
    }
    [TestCase("[new C(), new Other()]", "array")]
    [TestCase("(new C(), new Other())", "list")]
    public void ReferenceCollectionForeachLoopFixedPointJoinsSiblings(string literal, string kind)
    {
        var suffix = kind == "array" ? "[]" : "()";
        var result = Run($$"""
function route(value: object, chooseChild: bool): void {
    if (value is J) {
        let items: I{{suffix}} = {{literal}};
        foreach (item in items) {
            value.emit("{{kind}}-loop-join");
            if (chooseChild) { value = new C(); } else { value = new Other(); }
        }
    }
}
function main(args: string[]): int { route(new C(), true); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo(new[] { $"{kind}-loop-join", $"{kind}-loop-join" }));
    }
    private static (long ExitCode, string[] Output) Run(string body)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Declarations + body)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
