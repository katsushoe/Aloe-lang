using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ExceptionSubsetTests
{
    [Test]
    public void CatchAll_CatchesRuntimeExceptionRaisedInCurrentFunction()
    {
        const string source = """
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch { return 42; }
    return 0;
}
""";

        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void CatchAll_UnwindsSynchronousCalleeAndRestoresEvaluationStack()
    {
        const string source = """
function fail(): int { return 1 / 0; }
function main(args: string[]): int {
    try { let value: int = fail(); }
    catch { return 42; }
    return 0;
}
""";

        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void Rethrow_PropagatesToOuterHandler()
    {
        const string source = """
function main(args: string[]): int {
    try {
        try { let value: int = 1 / 0; }
        catch { throw; }
    }
    catch { return 42; }
    return 0;
}
""";

        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void Rethrow_CanCrossMultipleNestedCatchHandlers()
    {
        const string source = """
function main(args: string[]): int {
    try {
        try {
            try { let value: int = 1 / 0; }
            catch { throw; }
        }
        catch { throw; }
    }
    catch { return 73; }
    return 0;
}
""";

        Assert.That(Run(source), Is.EqualTo(73));
    }

    [Test]
    public void Rethrow_PreservesCurrentContextUntilDispatchAndExceptionIdentity()
    {
        const string source = "function main(args: string[]): int { return 0; }";
        var module = new AloeCompiler().Compile(source);
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var function = module.Functions[0];
        var frame = new CallFrame(module, function, function.EntryIp, new AloeValue[function.LocalCount]);
        var original = new ZeroDivisionException();
        frame.CaughtExceptions.Push(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original));

        Assert.That(Assert.Throws<ZeroDivisionException>(() => vm.Rethrow(frame)), Is.SameAs(original));
        Assert.That(frame.CaughtExceptions.Count, Is.EqualTo(1));
        vm.EndCatch(frame);
        Assert.That(frame.CaughtExceptions, Is.Empty);
    }

    [Test]
    public void CatchAll_LeavesNormalTryPathAndSkipsCatch()
    {
        const string source = """
function main(args: string[]): int {
    try { print(7); }
    catch { return 0; }
    return 42;
}
""";

        Assert.That(Run(source), Is.EqualTo(42));
    }

    [TestCase("ZeroDivisionException")]
    [TestCase("SystemException")]
    [TestCase("Exception")]
    public void TypedCatch_MatchesExactAndBaseExceptionTypes(string typeName)
    {
        var source = $$"""
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch ({{typeName}}) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void TypedCatch_UnmatchedExceptionEscapesUnchanged()
    {
        const string source = """
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch (InvalidCastException) { return 0; }
    return 1;
}
""";
        Assert.Throws<ZeroDivisionException>(() => Run(source));
    }

    [Test]
    public void TypedCatch_MismatchContinuesToOuterHandler()
    {
        const string source = """
function main(args: string[]): int {
    try {
        try { let value: int = 1 / 0; }
        catch (InvalidCastException) { return 1; }
    }
    catch (ZeroDivisionException) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void TypedCatch_OrderedMultipleClausesSelectFirstAssignableType()
    {
        const string source = """
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch (InvalidCastException) { return 1; }
    catch (ZeroDivisionException) { return 42; }
    catch { return 2; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void TypedCatch_FirstAssignableBaseTypeWins()
    {
        const string source = """
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch (Exception) { return 41; }
    catch (ZeroDivisionException) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(41));
    }

    [Test]
    public void TypedMultipleCatchClauses_PreserveInterfaceFlowInHandlerBodies()
    {
        const string source = """
interface I { method emit(): void; }
class C implements I { public async method emit(): void {} }
function main(args: string[]): int {
    let value: object = new C();
    try { let failed: int = 1 / 0; }
    catch (InvalidCastException) { return 1; }
    catch (ZeroDivisionException) {
        if (value is I) { value.emit(); return 42; }
    }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void TypedCatch_ExceptionFromCatchBodySkipsSiblingClauses()
    {
        const string source = """
function main(args: string[]): int {
    try {
        try { let value: int = 1 / 0; }
        catch (ZeroDivisionException) { let again: int = 1 / 0; }
        catch { return 1; }
    }
    catch (ZeroDivisionException) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo(42));
    }

    [Test]
    public void Verifier_RejectsInvalidMultiCatchTypeDescriptor()
    {
        const string source = """
function main(args: string[]): int {
    try { let value: int = 1 / 0; }
    catch (ZeroDivisionException) { return 42; }
    catch (Exception) { return 41; }
    return 0;
}
""";
        var compiled = new AloeCompiler().Compile(source);
        var code = compiled.Code.ToArray();
        var descriptorIp = Array.FindIndex(code, instruction => instruction.Opcode == EnumOpcode.TryCatch);
        Assert.That(descriptorIp, Is.GreaterThanOrEqualTo(0));
        code[descriptorIp] = new Instruction(EnumOpcode.TryCatch, code[descriptorIp].Operand0,
            compiled.Constants.Count + 1);
        var malformed = new Module(compiled.Constants, code, compiled.Functions, compiled.EntryPointIndex);
        Assert.Throws<VmException>(() => new AloeVm(malformed));
    }

    [Test]
    public void UnsupportedThrowAndTypedCatch_AreCompileErrors()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { throw; return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch { throw 1; } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch (e) { } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch (UnknownException) { } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch (e: Exception) { } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch { } catch (Exception) { } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { try { print(1); } catch { } finally { print(2); } return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "class C { public async method run(): void { try { print(1); } catch { } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(
            "function main(args: string[]): int { try { print(1); } catch { } return 0; }"));
    }

    [Test]
    public void UnhandledRuntimeException_StillEscapesVm()
    {
        const string source = "function main(args: string[]): int { return 1 / 0; }";
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);

        Assert.Throws<ZeroDivisionException>(() => vm.RunFromEntryPoint());
    }

    [Test]
    public void Break_LeavingTryDoesNotLeaveStaleHandler()
    {
        const string source = """
function fail(): int { return 1 / 0; }
function main(args: string[]): int {
    while (true) {
        try { break; }
        catch { return 7; }
    }
    return fail();
}
""";
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);

        Assert.Throws<ZeroDivisionException>(() => vm.RunFromEntryPoint());
    }

    private static long Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return vm.Pop().AsInt;
    }
}
