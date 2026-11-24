using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskEntryIsolationTests
{
    [Test]
    public void DeletedEntryDoesNotSuppressTheNextTasksCatch()
    {
        var source="task First { method run(): void { delete this; } } task Second { method run(): void { try { var bad=1/0; } catch(ZeroDivisionException) { print(7); } this.stop(); } } function main(args: string[]): int { var first=new First(); var second=new Second(); first.start(); second.start(); tick(); return 0; }";
        var vm=new AloeVm(new AloeCompiler().Compile(source));var output=new List<string>();vm.OutputWriter=output.Add;
        vm.RunFromEntryPoint();Assert.That(vm.Pop().AsInt,Is.Zero);
        Assert.That(output,Is.EqualTo(new[]{"7"}));Assert.That(vm.LastTaskFailure,Is.Null);Assert.That(vm.TaskPool.LiveCount,Is.EqualTo(1));
    }

    [Test]
    public void CleanupFailureDoesNotLeakThroughRuntimeReset()
    {
        var source="task Worker { method run(): void { try { delete this; } finally { var bad=1/0; } } } function main(args: string[]): int { var worker=new Worker(); worker.start(); try { tick(); } catch(ZeroDivisionException) { print(7); } return 0; }";
        var vm=new AloeVm(new AloeCompiler().Compile(source));var output=new List<string>();vm.OutputWriter=output.Add;
        for(var iteration=0;iteration<3;iteration++) {
            vm.RunFromEntryPoint();Assert.That(vm.Pop().AsInt,Is.Zero);
            Assert.That(vm.LastTaskFailure!.ExceptionType,Is.EqualTo("ZeroDivisionException"));Assert.That(vm.TaskPool.LiveCount,Is.Zero);
        }
        Assert.That(output,Is.EqualTo(new[]{"7","7","7"}));
    }
}
