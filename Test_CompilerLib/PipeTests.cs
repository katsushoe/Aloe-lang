using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using NUnit.Framework;

namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class PipeTests
    {
        [Test]
        public void BufferedPipe_ForeachConsumesValues()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.write(10);
    source.write(20);
    source.close();

    var sum = 0;
    foreach (value in source) {
        sum = sum + value;
    }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();

            Assert.That(vm.Pop().AsInt, Is.EqualTo(30));
        }

        [Test]
        public void PipeWrite_TypeMismatch_IsCompileError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.write("bad");
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void PipeWrite_AfterClose_IsVmError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    source.close();
    source.write(1);
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>());
        }

        [Test]
        public void Pipeline_FilterRunsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) {
            output.write(value * 2);
        }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;
    source.write(3);
    source.write(4);
    source.close();
    var sum = 0;
    foreach (value in result) {
        sum = sum + value;
    }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(14));
        }

        [Test]
        public void Pipeline_FilterChainPropagatesClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | filter(plusOne) | result;
    source.write(5);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Pipeline_FilterDrainsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;

    var sum = 0;
    source.write(3);
    source.write(4);
    source.close();
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(14));
        }

        [Test]
        public void Pipeline_FilterChainDrainsAfterSourceClose()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | filter(plusOne) | result;

    source.write(5);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Pipeline_FilterPreservesLocalsAcrossBufferedWrites()
        {
            const string source = """
filter runningTotal {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        var total = 0;
        foreach (value in input) {
            total = total + value;
            output.write(total);
        }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(runningTotal) | result;

    var sum = 0;
    source.write(2);
    source.write(3);
    source.close();
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(7));
        }


        [Test]
        public void BoundedPipe_FilterWriterResumesAfterConsumerRead()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}

function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(doubleValues) | result;

    source.write(1);
    source.write(2);

    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void BoundedPipe_MainWriterSuspendsAndResumes()
        {
            const string source = """
filter doubleValues {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value * 2); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create();
    source | filter(doubleValues) | result;
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(10));
        }

        [Test]
        public void BoundedPipe_MainWriterMaintainsValueAcrossMultipleResumes()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.write(1);
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void Pipeline_MainReaderWaitsUntilClose_ReportsDeadlock()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    foreach (value in source) { }
    source.close();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("deadlock"));
        }

        [Test]
        public void Pipeline_FilterWaitingAfterMainReturns_ReportsDeadlock()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.write(1);
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("deadlock"));
        }

        [Test]
        public void Scheduler_RunAfterCompletion_DoesNotLoseReturnValue()
        {
            const string source = """
function main(args: string[]): int {
    return 42;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            vm.Run();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void Pipeline_EmptySourceClose_PropagatesToOutput()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source | filter(forward) | result;
    source.close();
    var sum = 42;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }

        [Test]
        public void Pipeline_TwoBoundedStages_ResumesBlockedFilterAndMain()
        {
            const string source = """
filter forward {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let middle: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(forward) | middle;
    middle | filter(forward) | result;
    source.write(1);
    source.write(2);
    source.write(3);
    source.close();
    var sum = 0;
    foreach (value in result) { sum = sum + value; }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
        }

        [Test]
        public void Scheduler_DeadlockIncludesBlockedReaderCounts()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    foreach (value in source) { }
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>()
                .With.Message.Contains("readers=1"));
        }

        [Test]
        public void Scheduler_RunWithoutFrames_IsNoOp()
        {
            const string source = """
function main(args: string[]): int { return 9; }
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.DoesNotThrow(() => vm.Run());
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(9));
        }

        [Test]
        public void PipeCreate_NonIntCapacity_IsCompileError()
        {
            const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create("bad");
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void Scheduler_BlockingPipeWriteInsideTick_IsVmError()
        {
            // tick() runs instance calls synchronously; they cannot be suspended,
            // so a write to a full pipe must fail instead of retrying forever.
            const string source = """
class Writer {
    construct() { }
    public async method push(target: pipe<int>, value: int): void {
        target.write(value);
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create(1);
    var w = new Writer();
    w.push(p, 1);
    w.push(p, 2);
    tick();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(),
                Throws.TypeOf<VmException>().With.Message.Contains("synchronous instance call"));
        }

        [Test]
        public void Scheduler_BlockingPipeReadInsideTick_IsVmError()
        {
            const string source = """
class Reader {
    field total: int;
    construct() { this.total = 0; }
    public async method drain(source: pipe<int>): void {
        foreach (value in source) {
            this.total = this.total + value;
        }
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create();
    var r = new Reader();
    p.write(1);
    r.drain(p);
    tick();
    return 0;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            Assert.That(() => vm.RunFromEntryPoint(),
                Throws.TypeOf<VmException>().With.Message.Contains("synchronous instance call"));
        }

        [Test]
        public void Scheduler_NonBlockingPipeOpsInsideTick_AreAllowed()
        {
            const string source = """
class Writer {
    construct() { }
    public async method push(target: pipe<int>, value: int): void {
        target.write(value);
    }
}
function main(args: string[]): int {
    let p: pipe<int> = pipe<int>.create(2);
    var w = new Writer();
    w.push(p, 5);
    w.push(p, 6);
    tick();
    p.close();
    var sum = 0;
    foreach (value in p) {
        sum = sum + value;
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(11));
        }

        [Test]
        public void Scheduler_SuspendedFilterLocalSurvivesMainGc()
        {
            // The filter is suspended on an empty input while main runs tick/GC.
            // Its frame-local Node must remain a GC root and still be readable afterwards.
            const string source = """
class Node {
    field value: int;
    construct() { this.value = 7; }
    public property Value: int { get { return this.value; } }
}
filter keep {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        var node = new Node();
        foreach (value in input) { output.write(value); }
        output.write(node.Value);
        output.close();
    }
}
function main(args: string[]): int {
    let s: pipe<int> = pipe<int>.create();
    let r: pipe<int> = pipe<int>.create();
    s | filter(keep) | r;
    s.write(1);
    var sum = 0;
    var first = true;
    foreach (value in r) {
        sum = sum + value;
        if (first) {
            first = false;
            tick();
            GC.require();
            GC.finish();
            s.close();
        }
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(8));
        }

        [Test]
        public void BoundedPipe_LargeStreamThroughFilterChain_DoesNotLoseValues()
        {
            // Exceeds InstructionQuantum many times and forces repeated reader/writer handoffs.
            const string source = """
filter plusOne {
    in: pipe<int>;
    out: pipe<int>;
    bound(input, output) {
        foreach (value in input) { output.write(value + 1); }
        output.close();
    }
}
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(2);
    let result: pipe<int> = pipe<int>.create();
    source | filter(plusOne) | filter(plusOne) | result;
    var i = 0;
    while (i < 2000) {
        source.write(i);
        i = i + 1;
    }
    source.close();
    var sum = 0;
    foreach (value in result) {
        sum = sum + value;
    }
    return sum;
}
""";
            var vm = new AloeVm(new AloeCompiler().Compile(source));
            vm.RunFromEntryPoint();
            // sum(i + 2) for i = 0..1999
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2003000));
        }
    }
}
