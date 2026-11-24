using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskDeletionTests
{
    private const string Main = "function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); print(8); return 0; }";

    [TestCase("delete this;")]
    [TestCase("var self=this; delete self;")]
    [TestCase("let self: Worker=this; delete self;")]
    public void SelfDeletionBypassesUserCatchAndEndsBodyAfterFinally(string deletion)
    {
        var (vm, output) = Run("try { " + deletion + " print(99); } catch(Exception) { print(98); } finally { print(7); } print(97);");
        Assert.That(output, Is.EqualTo(new[] { "7", "8" }));
        Assert.That(vm.TaskPool.LiveCount, Is.Zero); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void SelfCallChainUnwindsEveryEntryAndCleanupOnce()
    {
        var members = "method inner(): void { try { delete this; print(99); } finally { print(1); } } method middle(): void { try { this.inner(); print(98); } catch(Exception) { print(97); } finally { print(2); } }";
        var (_, output) = Run("try { this.middle(); print(96); } catch(Exception) { print(95); } finally { print(3); }", members);
        Assert.That(output, Is.EqualTo(new[] { "1", "2", "3", "8" }));
    }

    [Test]
    public void StoppedNotificationDeletionRemovesLaterNotificationsAndCapturedRun()
    {
        var members = "method kill(): void { try { delete this; } finally { print(7); } } method send(): void { print(99); }";
        var main = "function main(args: string[]): int { var worker=new Worker(); worker.kill(); worker.send(); tick(); print(8); return 0; }";
        var (vm, output) = Run("print(98);", members, main: main);
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [Test]
    public void NotificationDeletionSkipsRunCapturedForRunningTask()
    {
        var members = "method kill(): void { delete this; }";
        var main = "function main(args: string[]): int { var worker=new Worker(); worker.start(); worker.kill(); tick(); print(8); return 0; }";
        var (_, output) = Run("print(99);", members, main: main);
        Assert.That(output, Is.EqualTo(new[] { "8" }));
    }

    [TestCase("this.start();")]
    [TestCase("this.stop();")]
    [TestCase("this.send();")]
    public void DeletedReceiverFailsButCleanupCanHandleItsOwnError(string operation)
    {
        var (vm, output) = Run("try { delete this; } finally { try { " + operation + " } catch(InvalidReferenceException) { print(7); } print(9); }", "method send(): void { print(99); }");
        Assert.That(output, Is.EqualTo(new[] { "7", "9", "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void RepeatedSelfDeletionInsideCleanupIsNoOp()
    {
        var (_, output) = Run("try { delete this; } finally { delete this; print(7); }");
        Assert.That(output, Is.EqualTo(new[] { "7", "8" }));
    }

    [Test]
    public void HandledCatchExceptionDoesNotBecomeDeletionFailure()
    {
        var (vm, output) = Run("try { var bad=1/0; } catch(Exception) { delete this; print(99); } finally { print(7); }");
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void DeletionInsideFaultCleanupPreservesItsUncaughtFailure()
    {
        var (vm, output) = Run("try { try { var bad=1/0; } finally { delete this; print(99); } } finally { print(7); }", main: CatchMain("ZeroDivisionException"));
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.LastTaskFailure!.ExceptionType, Is.EqualTo("ZeroDivisionException"));
    }

    [Test]
    public void CleanupFailureBypassesOuterTaskCatchAndStillRunsOuterFinally()
    {
        var (vm, output) = Run("try { try { delete this; } finally { this.start(); } } catch(InvalidReferenceException) { print(99); } finally { print(7); } print(98);", main: CatchMain("InvalidReferenceException"));
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.LastTaskFailure!.ExceptionType, Is.EqualTo("InvalidReferenceException"));
    }

    [Test]
    public void OuterCleanupExceptionTakesPriority()
    {
        var (vm, output) = Run("try { try { delete this; } finally { this.start(); } } finally { print(7); var bad=1/0; }", main: CatchMain("ZeroDivisionException"));
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.LastTaskFailure!.ExceptionType, Is.EqualTo("ZeroDivisionException"));
    }

    [Test]
    public void NestedCaughtErrorDoesNotReplacePriorFaultCleanupFailure()
    {
        var (vm, output) = Run("try { var bad=1/0; } finally { var other=new Worker(); delete other; try { other.start(); } catch(InvalidReferenceException) { delete this; } }", main: CatchMain("ZeroDivisionException"));
        Assert.That(output, Is.EqualTo(new[] { "8" })); Assert.That(vm.LastTaskFailure!.ExceptionType, Is.EqualTo("ZeroDivisionException"));
    }

    [Test]
    public void SynchronousHelperCalledByCleanupCanHandleItsOwnException()
    {
        var prefix = "function helper(): void { try { var bad=1/0; } catch(ZeroDivisionException) { print(7); } finally { print(9); } }";
        var (vm, output) = Run("try { delete this; } finally { helper(); }", prefix: prefix);
        Assert.That(output, Is.EqualTo(new[] { "7", "9", "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void EscapingHelperFailureDoesNotResumeDeletedTask()
    {
        var prefix = "function helper(): void { try { var bad=1/0; } finally { print(1); } }";
        var (vm, output) = Run("try { try { delete this; } finally { helper(); } } catch(Exception) { print(99); } finally { print(7); }", prefix: prefix, main: CatchMain("ZeroDivisionException"));
        Assert.That(output, Is.EqualTo(new[] { "1", "7", "8" })); Assert.That(vm.LastTaskFailure!.ExceptionType, Is.EqualTo("ZeroDivisionException"));
    }

    [Test]
    public void ActiveSlotIsUnavailableInCleanupThenLifoReusableAfterEntry()
    {
        var main = "function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); var after=new Worker(); print(8); return 0; }";
        var (vm, output) = Run("print(7); try { delete this; } finally { var during=new Worker(); during.start(); }", main: main);
        Assert.That(output, Is.EqualTo(new[] { "7", "8" }));
        Assert.That(vm.TaskPool.LocationOf(2).SlotIndex, Is.EqualTo(1)); Assert.That(vm.TaskPool.LocationOf(3).SlotIndex, Is.Zero);
        Assert.That(vm.TaskPool.IsLive(1), Is.False); Assert.That(vm.TaskPool.LiveCount, Is.EqualTo(2));
    }

    [Test]
    public void DeletingDifferentTaskDoesNotUnwindCurrentEntry()
    {
        var (_, output) = Run("var other=new Worker(); delete other; delete other; print(7); this.stop();");
        Assert.That(output, Is.EqualTo(new[] { "7", "8" }));
    }

    [Test]
    public void NewDeletionSyscallRequiresTaskMetadata()
    {
        var module = new Module(Array.Empty<AloeValue>(), new[] { new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.TaskDeleteAndUnwind), new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0) }, 0);
        Assert.That(Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(module))!.Message, Does.Contain("Task module metadata"));
    }

    [Test]
    public void BytecodeRoundTripPreservesNewDeletionOpcode()
    {
        var module = new AloeCompiler().Compile("task Worker { method run(): void { delete this; } } " + Main);
        Assert.That(module.Code.Any(i => i.Opcode == EnumOpcode.Syscall && i.Operand0 == (int)EnumSyscall.TaskDeleteAndUnwind), Is.True);
        var (vm, output) = Execute(AloeBcCodec.Read(AloeBcCodec.Write(module)));
        Assert.That(output, Is.EqualTo(new[] { "8" })); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [TestCase("delete this;")]
    [TestCase("var self=this; delete self;")]
    [TestCase("this.kill();")]
    public void ConstructorSelfDeletionReturnsInvalidTaskAfterCleanup(string deletion)
    {
        var source = "task Worker { construct() { try { " + deletion + " print(99); } finally { print(7); } } method kill(): void { try { delete this; } finally { print(1); } } method run(): void { print(98); } } function main(args: string[]): int { var worker=new Worker(); try { worker.start(); } catch(InvalidReferenceException) { print(8); } return 0; }";
        var (vm, output) = Execute(new AloeCompiler().Compile(source));
        Assert.That(output, Is.EqualTo(deletion == "this.kill();" ? new[] { "1", "7", "8" } : new[] { "7", "8" }));
        Assert.That(vm.TaskPool.LiveCount, Is.Zero); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void ConstructorCleanupFailureMakesNewFail()
    {
        var source = "task Worker { construct() { try { delete this; } finally { print(7); var bad=1/0; } } method run(): void { } } function main(args: string[]): int { try { var worker=new Worker(); print(99); } catch(ZeroDivisionException) { print(8); } return 0; }";
        var (vm, output) = Execute(new AloeCompiler().Compile(source));
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [Test]
    public void DeletedChildConstructionDoesNotUnwindParentTask()
    {
        var source = "task Child { construct() { delete this; } method run(): void { print(99); } } task Worker { method run(): void { var child=new Child(); print(7); this.stop(); } } " + Main;
        var (vm, output) = Execute(new AloeCompiler().Compile(source));
        Assert.That(output, Is.EqualTo(new[] { "7", "8" })); Assert.That(vm.TaskPool.LiveCount, Is.EqualTo(1));
    }

    [Test]
    public void DeletingOneTaskDoesNotAbortOtherCapturedTasks()
    {
        var source = "task First { method run(): void { try { delete this; } finally { print(1); } } } task Worker { method run(): void { print(7); this.stop(); } } function main(args: string[]): int { var first=new First(); var worker=new Worker(); first.start(); worker.start(); tick(); print(8); return 0; }";
        var (vm, output) = Execute(new AloeCompiler().Compile(source));
        Assert.That(output, Is.EqualTo(new[] { "1", "7", "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void CopiedObjectRemainsUsableWhileDeletedTaskFieldIsInvalid()
    {
        var source = "class Target { field value: int; construct() { this.value=42; } public property Value: int { get { return this.value; } } } task Worker { field target: Target; construct(target: Target) { this.target=target; } method run(): void { var held=this.target; try { delete this; } finally { print(held.Value); try { var bad=this.target; } catch(InvalidReferenceException) { print(7); } } } } function main(args: string[]): int { var target=new Target(); var worker=new Worker(target); worker.start(); tick(); print(8); return 0; }";
        var (vm, output) = Execute(new AloeCompiler().Compile(source));
        Assert.That(output, Is.EqualTo(new[] { "42", "7", "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void DeletionInsideReturnCleanupCancelsReturnWithoutInventingFailure()
    {
        var (vm, output) = Run("try { return; } finally { delete this; print(99); }");
        Assert.That(output, Is.EqualTo(new[] { "8" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    private static string CatchMain(string type) => "function main(args: string[]): int { var worker=new Worker(); worker.start(); try { tick(); } catch(" + type + ") { print(8); } return 0; }";
    private static (AloeVm Vm, List<string> Output) Run(string body, string members = "", string prefix = "", string main = Main)
        => Execute(new AloeCompiler().Compile(prefix + " task Worker { " + members + " method run(): void { " + body + " } } " + main));
    private static (AloeVm Vm, List<string> Output) Execute(Module module)
    {
        var vm = new AloeVm(module); var output = new List<string>(); vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero); return (vm, output);
    }
}
