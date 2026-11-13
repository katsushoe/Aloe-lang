using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class CatchBindingTests
{
    [TestCase("Exception")]
    [TestCase("ZeroDivisionException")]
    public void BindingContainsOriginalTypeAndMessage(string caughtType) => Safe($$"""
function main(args: string[]): int {
 try { var failure=1/0; } catch (error: {{caughtType}}) {
  if(error.TypeName == "ZeroDivisionException" and error.Message != "") { return 42; }
 }
 return 0;
}
""");

    [Test]
    public void SnapshotMayBeReturnedAndCopied() => Safe("""
function capture(): ExceptionInfo { try { var n=1/0; } catch (e: Exception) { return e; } return ExceptionInfo { Message: "", TypeName: "" }; }
function main(args: string[]): int { var original=capture(); var copy=original; copy.TypeName="edited";
 if(original.TypeName == "ZeroDivisionException" and copy.TypeName == "edited") { return 42; } return 0; }
""");

    [Test]
    public void SnapshotEditsAndNestedCatchPreserveOriginalRethrow() => Safe("""
function main(args: string[]): int {
 try {
  try { var n=1/0; } catch (e: Exception) {
   e.TypeName="edited"; e.Message="edited";
   try { var x=2147483647+1; } catch (inner: OverflowException) { inner.Message="inner"; }
   throw;
  }
 } catch (outer: ZeroDivisionException) { if(outer.TypeName == "ZeroDivisionException" and outer.Message != "edited") { return 42; } }
 return 0;
}
""");

    [Test]
    public void HandlerBindingShadowsOuterLocalAndRestoresItOnExit() => Safe("""
function main(args: string[]): int { var e=42; try { var n=1/0; } catch (e: Exception) { e.Message="changed"; } return e; }
""");

    [Test]
    public void EachSiblingHandlerMayUseSameName() => Safe("""
function main(args: string[]): int { try { var n=1/0; }
 catch (e: OverflowException) { return 0; }
 catch (e: ZeroDivisionException) { if(e.TypeName == "ZeroDivisionException") { return 42; } }
 return 0; }
""");

    [Test]
    public void FinallyRunsAfterBindingReturnValueIsCaptured() => Safe("""
function capture(): ExceptionInfo { try { var n=1/0; } catch(e: Exception) { return e; } finally { var done=1; }
 return ExceptionInfo { Message: "", TypeName: "" }; }
function main(args: string[]): int { var e=capture(); if(e.TypeName == "ZeroDivisionException") { return 42; } return 0; }
""");

    [Test]
    public void BreakAndContinueCleanUpCaughtContext() => Safe("""
function main(args: string[]): int { var n=0; while(n < 3) { n=n+1;
 try { var bad=1/0; } catch(e: Exception) { if(n == 1) { continue; } break; } }
 try { var bad=2147483647+1; } catch(e: OverflowException) { if(n == 2 and e.TypeName == "OverflowException") { return 42; } } return 0; }
""");

    [Test]
    public void ConstructorMethodGetterSetterAndStaticMethodSupportBindings() => Safe("""
class Box { field n: int;
 construct() { try { var bad=1/0; } catch(e: Exception) { this.n=40; } }
 private method read(): int { try { var bad=1/0; } catch(e: Exception) { return this.n; } return 0; }
 public property Value: int { get { try { var bad=1/0; } catch(e: Exception) { return read(); } return 0; }
 set { try { var bad=1/0; } catch(e: Exception) { this.n=value; } } }
 public static method extra(): int { try { var bad=1/0; } catch(e: Exception) { return 2; } return 0; } }
function main(args: string[]): int { var b=new Box(); b.Value=b.Value+Box.extra(); return b.Value; }
""");

    [TestCase("print(e.Message);")]
    [TestCase("var copy=e;")]
    public void BindingDoesNotEscapeScope(string use) => Reject("function main(args: string[]): int { try { var n=1/0; } catch(e: Exception) { } "+use+" return 0; }");

    [TestCase("catch(e)")]
    [TestCase("catch(e: UnknownException)")]
    [TestCase("catch(this: Exception)")]
    [TestCase("catch(e: ExceptionInfo)")]
    public void InvalidBindingHeadersAreRejected(string header) => Reject("function main(args: string[]): int { try { var n=1/0; } "+header+" { } return 0; }");

    [TestCase("struct ExceptionInfo { field x: int; }")]
    [TestCase("class ExceptionInfo { }")]
    [TestCase("interface ExceptionInfo { }")]
    [TestCase("enum ExceptionInfo { Value }")]
    public void BuiltinTypeNameIsReserved(string declaration) => Reject(declaration+" function main(args: string[]): int { return 0; }");

    [Test]
    public void DuplicateBindingLocalIsRejected() => Reject("function main(args: string[]): int { try { var n=1/0; } catch(e: Exception) { var e=1; } return 0; }");

    [Test]
    public void OrdinaryConstructionDoesNotRequireCaughtContext() => Safe("""
function main(args: string[]): int { var e=ExceptionInfo { Message: "manual", TypeName: "snapshot" };
 if(e.Message == "manual" and e.TypeName == "snapshot") { return 42; } return 0; }
""");

    [TestCase("while(n < 2)")]
    [TestCase("foreach(item in [1,2])")]
    public void LoopHandlerShadowKeepsOuterScalarValue(string loop) => Safe($$"""
function main(args: string[]): int { var e=42; var n=0; {{loop}} { n=n+1;
 try { var bad=1/0; } catch(e: Exception) { if(e.TypeName != "ZeroDivisionException") { return 0; } }
 } return e; }
""");

    [Test]
    public void HandlerWriteCannotPreserveInvalidInterfaceNarrowing() => Reject("""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function main(args: string[]): int { object target=new C(); if(target is I) {
 var n=0; while(n < 2) { n=n+1; try { var bad=1/0; } catch(e: Exception) { target=new Plain(); } }
 target.emit(); } return 0; }
""");

    [Test]
    public void DirectWasmRejectsExceptionInfoButKeepsScalarPrograms()
    {
        Assert.DoesNotThrow(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { return 42; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { var e=ExceptionInfo { Message: \"x\", TypeName: \"y\" }; return 0; }"));
    }

    [Test]
    public void SnapshotSyscallOutsideCatchIsRejected()
    {
        var vm=CreateVm("function main(args: string[]): int { return 0; }");
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.CaughtExceptionInfo));
    }

    private static void Reject(string source) => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    private static AloeVm CreateVm(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        return new AloeVm(module,settings);
    }
    private static void Safe(string source)
    {
        var vm=CreateVm(source); vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count,Is.EqualTo(1)); Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
