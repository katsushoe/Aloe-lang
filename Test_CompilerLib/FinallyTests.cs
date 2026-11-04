using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class FinallyTests
{
    [TestCase("try { print(\"T\"); } finally { print(\"F\"); }", "T,F")]
    [TestCase("try { print(\"T\"); } catch { print(\"C\"); } finally { print(\"F\"); }", "T,F")]
    [TestCase("try { var bad=1/0; } catch { print(\"C\"); } finally { print(\"F\"); }", "C,F")]
    [TestCase("try { try { var bad=1/0; } finally { print(\"F\"); } } catch(ZeroDivisionException) { print(\"O\"); }", "F,O")]
    [TestCase("try { try { var bad=1/0; } catch(OverflowException) { print(\"wrong\"); } finally { print(\"F\"); } } catch(ZeroDivisionException) { print(\"O\"); }", "F,O")]
    [TestCase("try { while(true) { break; } print(\"T\"); } finally { print(\"F\"); }", "T,F")]
    public void NormalAndExceptionalOrder(string body,string output)
    {
        var result=Run("function main(args: string[]): int { " + body + " return 42; }");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(output.Split(',')));
    }

    [TestCase("int", "-2147483648", "2147483647")]
    [TestCase("bool", "true", "false")]
    [TestCase("byte", "255", "0")]
    [TestCase("char", "'字'", "'A'")]
    [TestCase("float", "-1.5", "2.5")]
    [TestCase("decimal", "1.2300:d", "2:d")]
    [TestCase("string", "\"original字\"", "\"changed\"")]
    public void ReturnCapturesValueBeforeFinally(string type,string value,string changed)
    {
        var result=Run($$"""
function choose(value: {{type}}): {{type}} { try { return value; } finally { value={{changed}}; print("F"); } }
function main(args: string[]): int { let result: {{type}}=choose({{value}}); if(result == {{value}}) { return 42; } return 0; }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"F"}));
    }

    [Test]
    public void NestedReturnRunsInnerThenOuterExactlyOnce()
    {
        var result=Run("""
function choose(): int { try { try { return 42; } finally { print("inner"); } } finally { print("outer"); } }
function main(args: string[]): int { return choose(); }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"inner","outer"}));
    }

    [Test]
    public void BreakAndContinueRunFinallyBeforeTheirDestinations()
    {
        var result=Run("""
function main(args: string[]): int {
 var i=0; var sum=0;
 while(i < 3) { i=i+1; try { if(i == 1) { continue; } if(i == 3) { break; } sum=sum+i; } finally { sum=sum+10; } }
 return sum;
}
""");
        Assert.That(result.Exit, Is.EqualTo(32));
    }

    [Test]
    public void ContinueFromCatchCleansCatchContextBeforeFinally()
    {
        var result=Run("""
function main(args: string[]): int {
 var i=0; while(i < 3) { try { var bad=1/0; } catch { i=i+1; continue; } finally { print(i); } }
 return 42;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"1","2","3"}));
    }

    [Test]
    public void FinallyExceptionOverridesPendingReturn()
    {
        var result=Run("""
function choose(): int { try { return 42; } finally { print("F"); var bad=1/0; } }
function main(args: string[]): int { try { return choose(); } catch(ZeroDivisionException) { return 7; } }
""");
        Assert.That(result.Exit, Is.EqualTo(7)); Assert.That(result.Output, Is.EqualTo(new[]{"F"}));
    }

    [Test]
    public void FinallyExceptionOverridesOriginalException()
    {
        var result=Run("""
function choose(): int { try { return 1/0; } finally { print("F"); var bad=2147483647+1; } }
function main(args: string[]): int {
 try { return choose(); } catch(ZeroDivisionException) { return 9; } catch(OverflowException) { return 42; }
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"F"}));
    }

    [Test]
    public void FinallyUsesOuterLexicalBinding()
    {
        var result=Run("""
function choose(): int { const value: int=1; try { var value=2; return value; } finally { print(value); } }
function main(args: string[]): int { if(choose() == 2) { return 42; } return 0; }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"1"}));
    }

    [Test]
    public void VoidReturnRunsFinally()
    {
        var result=Run("""
function choose(): void { try { print("T"); return; } finally { print("F"); } }
function main(args: string[]): int { choose(); return 42; }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"T","F"}));
    }

    [Test]
    public void PendingTransferKindResetsOnEveryLoopEntry()
    {
        var result=Run("""
function main(args: string[]): int {
 var i=0; var after=0;
 while(i < 3) { i=i+1; try { if(i == 1) { continue; } if(i == 3) { break; } } finally { print(i); } after=after+1; }
 if(after == 1) { return 42; } return 0;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"1","2","3"}));
    }

    [Test]
    public void NestedFinallyGuardsPreserveLoopDestinations()
    {
        var result=Run("""
function main(args: string[]): int {
 var i=0; while(i < 2) { i=i+1; try { try { if(i == 1) { continue; } break; } finally { print("inner"); } } finally { print("outer"); } }
 return 42;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"inner","outer","inner","outer"}));
    }

    [Test]
    public void FinallyOwnLoopTransfersStayInsideItsBody()
    {
        var result=Run("""
function main(args: string[]): int { try { print("T"); } finally {
 var i=0; while(i < 3) { i=i+1; if(i == 1) { continue; } break; } print(i);
} return 42; }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"T","2"}));
    }

    [Test]
    public void SynchronousCalleeFaultRunsFinallyBeforeOuterHandler()
    {
        var result=Run("""
function fail(): int { return 1/0; }
function choose(): int { try { return fail(); } finally { print("F"); } }
function main(args: string[]): int { try { return 1+choose(); } catch(ZeroDivisionException) { print("caught"); return 42; } }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"F","caught"}));
    }

    [Test]
    public void CatchReturnRunsFinallyOnce()
    {
        var result=Run("""
function choose(): int { try { return 1/0; } catch(ZeroDivisionException) { print("C"); return 42; } finally { print("F"); } }
function main(args: string[]): int { return choose(); }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"C","F"}));
    }

    [Test]
    public void RethrowFromCatchRunsFinallyBeforeMatchingOuterCatch()
    {
        var result=Run("""
function main(args: string[]): int {
 try { try { var bad=1/0; } catch(ZeroDivisionException) { print("C"); throw; } finally { print("F"); } }
 catch(ZeroDivisionException) { return 42; }
 return 0;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"C","F"}));
    }

    [Test]
    public void FinallyNestedCatchRethrowOverridesOriginalFault()
    {
        var result=Run("""
function main(args: string[]): int {
 try { try { var bad=1/0; } finally { try { var overflow=2147483647+1; } catch(OverflowException) { throw; } } }
 catch(ZeroDivisionException) { return 9; } catch(OverflowException) { return 42; }
 return 0;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void FaultInFinallyCanBeHandledByAnEnclosingSourceCatch()
    {
        var result=Run("""
function choose(): int {
 try { try { return 9; } finally { print("inner"); var bad=1/0; } }
 catch(ZeroDivisionException) { return 42; }
 finally { print("outer"); }
}
function main(args: string[]): int { return choose(); }
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"inner","outer"}));
    }

    [Test]
    public void FinallyFaultOverridesPendingBreak()
    {
        var result=Run("""
function main(args: string[]): int {
 try { while(true) { try { break; } finally { var bad=1/0; } } }
 catch(ZeroDivisionException) { return 42; }
 return 0;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void FaultPathUsesOuterLexicalBindingAndPrivateFinallyScope()
    {
        var result=Run("""
function main(args: string[]): int {
 const value: int=1;
 try { try { var value=2; var bad=1/0; } finally { var copy=value; copy=copy+1; print(value); print(copy); } }
 catch(ZeroDivisionException) { print(value); }
 return 42;
}
""");
        Assert.That(result.Exit, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo(new[]{"1","2","1"}));
    }

    [Test]
    public void FinallyCapturePreservesArrayHandleButNotADeepSnapshot()
    {
        var result=Run("""
function choose(value: int[]): int[] { try { return value; } finally { value[0]=9; value=[7]; } }
function main(args: string[]): int { var input=[1]; var result=choose(input); if(result == input and result[0] == 9) { return 42; } return 0; }
""");
        Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void FinallyCapturePreservesImmutableStructValue()
    {
        var result=Run("""
struct Point { field x: int; }
function choose(value: Point): Point { try { return value; } finally { value.x=9; } }
function main(args: string[]): int { var result=choose(Point { x: 42 }); return result.x; }
""");
        Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void FinallyReturnPreservesFloatNegativeZeroBits()
    {
        var value=RunValue("function choose(): float { var value=-0.0; try { return value; } finally { value=1.5; } } function main(args: string[]): int { return 0; }", "choose");
        Assert.That(BitConverter.SingleToInt32Bits((float)value.AsFloat), Is.EqualTo(int.MinValue));
    }

    [Test]
    public void FinallyReturnPreservesDecimalScaleBits()
    {
        var value=RunValue("function choose(): decimal { var value=1.2300:d; try { return value; } finally { value=2:d; } } function main(args: string[]): int { return 0; }", "choose");
        Assert.That(decimal.GetBits(value.AsDecimal), Is.EqualTo(decimal.GetBits(1.2300m)));
    }

    [Test]
    public void ClassConstructorFinallyCanInitializeOwnReadonlyField()
    {
        var result=Run("""
class Box { readonly field value: int;
 construct() { try { this.value=1; return; } finally { this.value=42; } }
 public property Value: int { get { return this.value; } } }
function main(args: string[]): int { var box=new Box(); return box.Value; }
""");
        Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void OrdinarySynchronousStaticMethodRemainsUnsupported()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("""
class Box { public static method choose(): int { try { return 42; } finally { print("F"); } } }
function main(args: string[]): int { return Box.choose(); }
"""));

    [TestCase("try { print(1); } finally { return 0; }")]
    [TestCase("while(true) { try { break; } finally { break; } }")]
    [TestCase("while(true) { try { break; } finally { continue; } }")]
    [TestCase("try { print(1); } finally { throw; }")]
    [TestCase("try { var bad=1/0; } catch { try { print(1); } finally { throw; } }")]
    [TestCase("try { print(1); } finally { const value: int=1; value=2; }")]
    [TestCase("try { print(1); } finally")]
    [TestCase("finally { print(1); }")]
    [TestCase("try { print(1); } finally { } catch { }")]
    public void UnsupportedOrMalformedFinallyIsRejected(string body)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args: string[]): int { " + body + " return 0; }"));

    [TestCase("class Box { public async method run(): void { try { print(1); } finally { print(2); } } }")]
    [TestCase("struct Point { field x: int; construct() { try { this.x=1; } finally { this.x=2; } } }")]
    [TestCase("struct Point { field x: int; method read(): int { try { return this.x; } finally { print(1); } } }")]
    public void UnsupportedExecutionContextsAreRejected(string declarations)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(declarations + " function main(args: string[]): int { return 0; }"));

    [Test]
    public void DirectWasmFinallyRemainsRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm(
            "function main(args: string[]): int { try { return 1; } finally { print(2); } }"));

    [TestCase("try { print(1); } finally { value=new Plain(); }")]
    [TestCase("while(true) { try { break; } finally { value=new Plain(); } }")]
    [TestCase("var i=0; while(i < 2) { value.emit(); try { i=i+1; continue; } finally { value=new Plain(); } }")]
    public void FinallyWritesCannotPreserveStaleInterfaceFacts(string body)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function main(args: string[]): int { let value: object=new C(); if(value is I) {
""" + body + " value.emit(); } return 0; }"));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static AloeValue RunValue(string source,string function)
    {
        var compiled=new AloeCompiler().Compile(source); var index=compiled.Functions.ToList().FindIndex(f=>f.Name==function);
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new Module(compiled.Constants,compiled.Code,compiled.Functions,index)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint(); return vm.Pop();
    }

    private static (long Exit,List<string> Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); var output=new List<string>(); vm.OutputWriter=output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); return (vm.Pop().AsInt,output);
    }
}
