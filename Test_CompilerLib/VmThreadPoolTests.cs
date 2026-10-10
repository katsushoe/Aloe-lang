using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class VmThreadPoolTests
    {
        [Test]
        public void TickRunsDistinctObjectsOnSeparateWorkersAndWaitsForBoth()
        {
            const string source = """
class Worker {
    construct() {
    }

    public async method write(value: int): void {
        print(value);
    }
}

function main(args: string[]): int {
    var first = new Worker();
    var second = new Worker();
    first.write(1);
    second.write(2);
    tick();
    print(3);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module) { MaxVmThreads = 2 };
            var gate = new object();
            var output = new List<string>();
            var outputThreads = new Dictionary<string, int>();
            vm.OutputWriter = line =>
            {
                // Keep the first worker inside the serialized host callback long enough
                // for the second worker to become runnable on another pool thread.
                Thread.Sleep(100);
                lock (gate)
                {
                    output.Add(line);
                    outputThreads[line] = Environment.CurrentManagedThreadId;
                }
            };

            vm.RunFromEntryPoint();

            Assert.That(output.Take(2), Is.EquivalentTo(new[] { "1", "2" }));
            Assert.That(output.Last(), Is.EqualTo("3"));
            Assert.That(outputThreads["1"], Is.Not.EqualTo(outputThreads["2"]));
        }

        [Test]
        public void WorkerFailureSkipsVolatileCommitAndFaultsTick()
        {
            const string source = """
class Counter {
    field value: int;
    construct() {
        this.value = 0;
    }

    public async method update(): void {
        print(1);
        this.value = 1;
    }
}

class Failer {
    field value: int;
    construct() {
    }

    public async method fail(): void {
        print(this.value);
    }
}

function main(args: string[]): int {
    var counter = new Counter();
    var failer = new Failer();
    counter.update();
    failer.fail();
    tick();
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module) { MaxVmThreads = 2 };
            var gate = new object();
            var output = new List<string>();
            vm.OutputWriter = line =>
            {
                Thread.Sleep(100);
                lock (gate) output.Add(line);
            };

            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<InvalidOperationException>());

            var counter = vm.Heap.ObjectTable.Values.Single(x => x.TypeName == "Counter");
            Assert.That(vm.Heap.GetCommittedField(counter.ObjectId, 0).AsInt, Is.EqualTo(0));
            Assert.That(output, Does.Contain("1"), "Tick must join the sibling worker before propagating a failure.");
            Assert.That(() => vm.Tick(), Throws.TypeOf<InvalidOperationException>());
        }
    }
}
