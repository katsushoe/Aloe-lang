using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskSourceTests
{
    [Test]
    public void ConstructorFieldsAndRunExecuteFromSourceAndCodecRoundTrip()
    {
        var output = Run("task Worker { field value: int; construct(value: int) { this.value=value; print(this.value); } method run(): void { this.value=this.value+1; print(this.value); } } function main(args: string[]): int { var worker=new Worker(40); worker.start(); tick(); tick(); worker.stop(); tick(); delete worker; return 0; }");
        Assert.That(output, Is.EqualTo(new[] { "40", "41", "42" }));
    }

    [Test]
    public void StoppedTaskReceivesNotificationsAndSelfAliasCallsSynchronously()
    {
        var output = Run("task Worker { method send(value: int): void { print(value); } method run(): void { var alias=this; alias.send(7); print(8); this.stop(); } } function main(args: string[]): int { var worker=new Worker(); worker.send(3); print(1); tick(); worker.start(); tick(); tick(); return 0; }");
        Assert.That(output, Is.EqualTo(new[] { "1", "3", "7", "8" }));
    }

    [Test]
    public void ConstructorReceivesAndTaskRetainsOrdinaryObject()
    {
        var output = Run("class Target { public async method emit(): void { print(9); } } task Worker { field target: Target; construct(target: Target) { this.target=target; } method run(): void { var target=this.target; target.emit(); this.stop(); } } function main(args: string[]): int { var target=new Target(); var worker=new Worker(target); worker.start(); tick(); return 0; }");
        Assert.That(output, Is.EqualTo(new[] { "9" }));
    }

    [Test]
    public void SynchronousSelfFailureIsCaughtAndFinallyRuns()
    {
        var output = Run("task Worker { method fail(): void { var bad=1/0; } method run(): void { try { this.fail(); } catch(error: Exception) { print(error.TypeName); } finally { print(7); } this.stop(); } } function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); return 0; }");
        Assert.That(output, Is.EqualTo(new[] { "ZeroDivisionException", "7" }));
    }

    [Test]
    public void ConstructorFailureCanBeCaughtAtNewAndReleasesInstance()
    {
        var module = new AloeCompiler().Compile("task Worker { construct() { try { var bad=1/0; } finally { print(7); } } method run(): void { } } function main(args: string[]): int { try { var worker=new Worker(); } catch(ZeroDivisionException) { print(8); } return 0; }");
        var vm = new AloeVm(module); var output = new List<string>(); vm.OutputWriter=output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero); Assert.That(vm.TaskPool.LiveCount, Is.Zero); Assert.That(output, Is.EqualTo(new[] { "7", "8" }));
    }

    [TestCase("this.run();")]
    [TestCase("delete this;")]
    [TestCase("try { var other=new Worker(); other.send(1); } catch(Exception) { }")]
    public void UnsupportedAndUnsafeTaskOperationsAreRejected(string body)
    {
        var source="task Worker { method send(value: int): void { } method run(): void { "+body+" } } function main(args: string[]): int { var worker=new Worker(); return 0; }";
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
    }

    [TestCase("function accept(value: Worker): void { }")]
    [TestCase("class Holder { field handle: Worker; }")]
    [TestCase("function produce(): Worker { var worker=new Worker(); return worker; }")]
    public void TaskReferencesCannotCrossDeclaredNonlocalBoundaries(string declaration)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task Worker { method run(): void { } } "+declaration+" function main(args: string[]): int { return 0; }"));

    [Test]
    public void TaskCannotBePassedAsOrdinaryObject()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function accept(value: object): void { } task Worker { method run(): void { } } function main(args: string[]): int { var worker=new Worker(); accept(worker); return 0; }"));

    [Test]
    public void DifferentTaskTypesCannotBeAssignedToSameTypedLocal()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task First { method run(): void { } } task Second { method run(): void { } } function main(args: string[]): int { let handle: First=new Second(); return 0; }"));

    [Test]
    public void ObjectAsyncCallInsideTaskProtectedRegionIsRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Target { public async method emit(): void { } } task Worker { construct(target: Target) { try { target.emit(); } catch(Exception) { } } method run(): void { } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void DeletedAliasCannotStartReplacementTask()
        => Assert.Throws<InvalidReferenceException>(()=>Run("task Worker { method run(): void { } } function main(args: string[]): int { var worker=new Worker(); var alias=worker; delete worker; var replacement=new Worker(); alias.start(); return 0; }"));

    [Test]
    public void DirectWasmContinuesToRejectTaskPrograms()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("task Worker { method run(): void { } } function main(args: string[]): int { return 0; }"));

    private static List<string> Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source))); var vm = new AloeVm(module);
        var output = new List<string>(); vm.OutputWriter=output.Add; vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero); return output;
    }
}
