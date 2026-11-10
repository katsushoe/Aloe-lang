using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class StaticScalarMethodTests
{
    [TestCase("int", "42")]
    [TestCase("bool", "true")]
    [TestCase("byte", "255")]
    [TestCase("char", "'字'")]
    [TestCase("float", "-1.5")]
    [TestCase("decimal", "1.0000000000000000000000000001:d")]
    [TestCase("string", "\"Aloe字\"")]
    public void ScalarArgumentsAndResults(string type,string literal)
        => AssertResult($$"""
class Utility { public static method identity(value: {{type}}): {{type}} { return value; } }
function main(args: string[]): int { if(Utility.identity({{literal}}) == {{literal}}) { return 42; } return 0; }
""");

    [Test]
    public void VoidCallExecutesImmediatelyWithoutTick()
    {
        var result=Run("""
class Utility { public static method emit(value: string): void { print(value); } }
function main(args: string[]): int { print("before"); Utility.emit("now"); print("after"); return 42; }
""");
        Assert.That(result.Exit,Is.EqualTo(42)); Assert.That(result.Output,Is.EqualTo(new[]{"before","now","after"}));
    }

    [Test]
    public void RecursionAndCrossClassCalls()
        => AssertResult("""
class Factorial { public static method calculate(n: int): int { if(n == 0) { return 1; } return n*Factorial.calculate(n - 1); } }
class Answer { public static method calculate(): int { return Factorial.calculate(4)+18; } }
function main(args: string[]): int { return Answer.calculate(); }
""");

    [Test]
    public void ArgumentsEvaluateLeftToRightExactlyOnce()
    {
        var result=Run("""
function next(value: int): int { print(value); return value; }
class Utility { public static method combine(a: int,b: int): int { return a*10+b; } }
function main(args: string[]): int { return Utility.combine(next(4),next(2)); }
""");
        Assert.That(result.Exit,Is.EqualTo(42)); Assert.That(result.Output,Is.EqualTo(new[]{"4","2"}));
    }

    [Test]
    public void QualifiedCallCannotUseSameNameGlobalFunction()
        => AssertResult("""
function answer(): int { return 9; }
class Utility { public static method answer(): int { return 42; } }
function main(args: string[]): int { return Utility.answer(); }
""");

    [Test]
    public void InstanceConstructorGetterAndPrivateMethodMayCallStatic()
        => AssertResult("""
class Utility { public static method identity(value: int): int { return value; } }
class Box { field value: int; construct() { this.value=Utility.identity(42); }
 private method read(): int { return Utility.identity(this.value); }
 public property Value: int { get { return this.read(); } } }
function main(args: string[]): int { var box=new Box(); return box.Value; }
""");

    [Test]
    public void AsyncBodyCallsSynchronousStaticWithoutEnqueueingIt()
    {
        var result=Run("""
class Utility { public static method answer(): int { print("sync"); return 42; }
 public static async method emit(): void { print(Utility.answer()); } }
function main(args: string[]): int { Utility.emit(); print("queued"); tick(); return 42; }
""");
        Assert.That(result.Exit,Is.EqualTo(42)); Assert.That(result.Output,Is.EqualTo(new[]{"queued","sync","42"}));
    }

    [Test]
    public void FinallyCapturesValueAndRunsBeforeCallerResumes()
    {
        var result=Run("""
class Utility { public static method answer(value: int): int { try { return value; } finally { value=9; print("finally"); } } }
function main(args: string[]): int { var answer=Utility.answer(42); print("caller"); return answer; }
""");
        Assert.That(result.Exit,Is.EqualTo(42)); Assert.That(result.Output,Is.EqualTo(new[]{"finally","caller"}));
    }

    [Test]
    public void StaticCalleeFailureReachesCallerHandlerAfterFinally()
    {
        var result=Run("""
class Utility { public static method fail(): int { try { return 1/0; } finally { print("finally"); } } }
function main(args: string[]): int { try { return Utility.fail(); } catch(ZeroDivisionException) { print("catch"); return 42; } }
""");
        Assert.That(result.Exit,Is.EqualTo(42)); Assert.That(result.Output,Is.EqualTo(new[]{"finally","catch"}));
    }

    [Test]
    public void FinallyFailureOverridesStaticReturn()
        => AssertResult("""
class Utility { public static method answer(): int { try { return 9; } finally { var failure=1/0; } } }
function main(args: string[]): int { try { return Utility.answer(); } catch(ZeroDivisionException) { return 42; } }
""");

    [Test]
    public void FloatNegativeZeroBitsSurviveStaticCall()
    {
        var value=RunValue("""
class Utility { public static method identity(value: float): float { return value; } }
function probe(): float { return Utility.identity(-0.0); }
function main(args: string[]): int { return 0; }
""");
        Assert.That(BitConverter.DoubleToInt64Bits(value.AsFloat),Is.EqualTo(unchecked((long)0x8000000000000000)));
    }

    [Test]
    public void DecimalScaleSurvivesStaticCall()
    {
        var value=RunValue("""
class Utility { public static method identity(value: decimal): decimal { return value; } }
function probe(): decimal { return Utility.identity(1.2300:d); }
function main(args: string[]): int { return 0; }
""");
        Assert.That(decimal.GetBits(value.AsDecimal),Is.EqualTo(decimal.GetBits(1.2300m)));
    }

    [TestCase("private static method answer(): int { return 42; }")]
    [TestCase("protected static method answer(): int { return 42; }")]
    [TestCase("public static virtual method answer(): int { return 42; }")]
    [TestCase("public static override method answer(): int { return 42; }")]
    [TestCase("public static method answer(value: object): int { return 42; }")]
    [TestCase("public static method answer(value: int[]): int { return 42; }")]
    [TestCase("public static method answer(): object { return 42; }")]
    [TestCase("public static method answer(): int[] { return [42]; }")]
    [TestCase("public static method answer(value: E): int { return 42; }")]
    [TestCase("public static method answer(value: Point): int { return 42; }")]
    [TestCase("public static method answer(): int { return this.value; }")]
    [TestCase("public static method answer(): int { return this.privateAnswer(); }")]
    [TestCase("public static method answer(): int { return privateAnswer(); }")]
    [TestCase("public static method answer(): int { this.value=42; return 42; }")]
    public void UnsupportedDeclarationsOrInstanceAccessAreRejected(string method)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("enum E { One } struct Point { field x: int; } class Utility { field value: int; private method privateAnswer(): int { return this.value; } " + method + " } function main(args: string[]): int { return 0; }"));

    [TestCase("return Utility.answer();")]
    [TestCase("return Utility.answer(1,2);")]
    [TestCase("return Utility.answer(true);")]
    [TestCase("Utility.answer(42); return 0;")]
    [TestCase("return Utility.missing(42);")]
    [TestCase("let Utility: int=1; return Utility.answer(42);")]
    [TestCase("let Utility: int=1; Utility.emit(); return 0;")]
    [TestCase("return Utility.emit();")]
    [TestCase("with Utility { return .answer(42); }")]
    public void InvalidCallsAreRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Utility { public static method answer(value: int): int { return value; } public static method emit(): void { } } function main(args: string[]): int { " + body + " }"));

    [Test]
    public void StaticLookupDoesNotInherit()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Base { public static method answer(): int { return 42; } } class Derived extends Base { } function main(args: string[]): int { return Derived.answer(); }"));

    [Test]
    public void DuplicateStaticAsyncAndSyncNamesAreRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Utility { public static method answer(): int { return 42; } public static async method answer(): void { } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void DirectWasmRemainsUnsupported()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("class Utility { public static method answer(): int { return 42; } } function main(args: string[]): int { return Utility.answer(); }"));

    private static void AssertResult(string source) => Assert.That(Run(source).Exit,Is.EqualTo(42));
    private static AloeValue RunValue(string source)
    {
        var compiled=new AloeCompiler().Compile(source);var entry=compiled.Functions.ToList().FindIndex(f=>f.Name=="probe");
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new Module(compiled.Constants,compiled.Code,compiled.Functions,entry)));
        var vm=new AloeVm(module,Settings());vm.RunFromEntryPoint();return vm.Pop();
    }
    private static AloeVmSettings Settings()
    {
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;return settings;
    }
    private static (long Exit,List<string> Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm=new AloeVm(module,Settings());var output=new List<string>();vm.OutputWriter=output.Add;vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return (vm.Pop().AsInt,output);
    }
}
