using System;
using System.Collections.Generic;
using System.Linq;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests;

[TestFixture]
public sealed class TickQuiescenceTests
{
    [TestCase(1)]
    [TestCase(4)]
    public void Tick_ChildInstanceCalls_DrainsBeforeCommit(int workers)
    {
        const string source = """
class Worker {
    field value: int;
    construct() { this.value = 0; }
    public property Value: int { get { return this.value; } }
    public async method write(value: int): void { this.value = value; print(value); }
    public async method forward(other: Worker): void {
        other.write(11); other.write(12); other.write(13);
    }
}
function main(args: string[]): int {
    var first = new Worker(); var second = new Worker();
    first.forward(second); tick(); print(second.Value); tick(); return 0;
}
""";
        var output = Run(source, workers);
        Assert.That(output, Is.EqualTo(new[] { "11", "12", "13", "13" }));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void Tick_StaticWorkerEnqueuesInstanceAndStaticCalls_DrainsBoth(int workers)
    {
        const string source = """
class Worker { public async method emit(): void { print("instance"); } }
class Logger {
    public static async method emit(value: int): void { print(value); }
    public static async method forward(worker: Worker): void {
        worker.emit(); with (Logger) { .emit(21); .emit(22); }
    }
}
function main(args: string[]): int {
    var worker = new Worker(); with (Logger) { .forward(worker); }
    tick(); print("done"); return 0;
}
""";
        var output = Run(source, workers);
        Assert.That(output.Take(3), Is.EquivalentTo(new[] { "instance", "21", "22" }));
        Assert.That(output.Where(x => x == "21" || x == "22"), Is.EqualTo(new[] { "21", "22" }));
        Assert.That(output.Last(), Is.EqualTo("done"));
    }

    [Test]
    public void Tick_WorkerFails_DiscardsQueuedCallsAndSkipsCommit()
    {
        const string source = """
class Worker {
    field value: int;
    construct() { this.value = 0; }
    public async method fail(): void { this.value = 9; print("fail"); }
    public async method later(): void { print("later"); }
}
function main(args: string[]): int {
    var worker = new Worker(); worker.fail(); worker.later(); tick(); return 0;
}
""";
        var vm = new AloeVm(new AloeCompiler().Compile(source)) { MaxVmThreads = 1 };
        var output = new List<string>();
        vm.OutputWriter = line => { output.Add(line); throw new InvalidOperationException("host failure"); };
        Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("host failure"));
        Assert.That(output, Is.EqualTo(new[] { "fail" }));
        var worker = vm.Heap.ObjectTable.Values.Single(x => x.TypeName == "Worker");
        Assert.That(vm.Heap.GetCommittedField(worker.ObjectId, 0).AsInt, Is.EqualTo(0));
        Assert.That(() => vm.Tick(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("host failure"));
    }

    [Test]
    public void Tick_WorkerAttemptsNestedTick_RejectsAndFaultsVm()
    {
        const string source = """
class Worker { public async method run(): void { print("nested"); } }
function main(args: string[]): int { var worker = new Worker(); worker.run(); tick(); return 0; }
""";
        var vm = new AloeVm(new AloeCompiler().Compile(source));
        vm.OutputWriter = _ => vm.Tick();
        Assert.That(() => vm.RunFromEntryPoint(), Throws.Exception.With.Message.Contains("Nested or concurrent tick()"));
        Assert.That(() => vm.Tick(), Throws.Exception.With.Message.Contains("Nested or concurrent tick()"));
    }

    private static List<string> Run(string source, int workers)
    {
        var vm = new AloeVm(new AloeCompiler().Compile(source)) { MaxVmThreads = workers };
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        return output;
    }
}
