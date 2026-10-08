using Aloe.CompilerLib;
using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.CommonLib.Constants;
using Aloe.RuntimeLib;
using NUnit.Framework;
using System.Linq;
using System.Text;


namespace Aloe.CompilerLib.Tests
{
    [TestFixture]
    public sealed class AloeCompilerTests
    {
        [Test]
        public void StructuredControlFlow_EmitsBlockLoopIfAndBranches()
        {
            const string source = """
function main(args: string[]): int {
    var i = 0;
    while (i < 3) {
        if (i == 1) {
            i = i + 1;
            continue;
        }
        i = i + 1;
    }
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var opcodes = module.Code.Select(i => i.Opcode).ToArray();

            Assert.That(opcodes, Does.Contain(EnumOpcode.Block));
            Assert.That(opcodes, Does.Contain(EnumOpcode.Loop));
            Assert.That(opcodes, Does.Contain(EnumOpcode.If));
            Assert.That(opcodes, Does.Contain(EnumOpcode.End));
            Assert.That(opcodes, Does.Contain(EnumOpcode.Br));
            Assert.That(opcodes, Does.Contain(EnumOpcode.BrIf));
        }


        [Test]
        public void Verifier_RejectsInvalidBranchDepth()
        {
            var module = new Module(
                Array.Empty<AloeValue>(),
                new[]
                {
                    new Instruction(EnumOpcode.Block),
                    new Instruction(EnumOpcode.Br, 1),
                    new Instruction(EnumOpcode.End),
                    new Instruction(EnumOpcode.Return),
                },
                new[] { new FunctionInfo("main", 0, 0, 0) },
                entryPointIndex: 0);

            Assert.That(() => new AloeVm(module), Throws.TypeOf<VmException>());
        }


        [Test]
        public void Verifier_RejectsUnmatchedElse()
        {
            var module = new Module(
                Array.Empty<AloeValue>(),
                new[]
                {
                    new Instruction(EnumOpcode.Else),
                    new Instruction(EnumOpcode.Return),
                },
                new[] { new FunctionInfo("main", 0, 0, 0) },
                entryPointIndex: 0);

            Assert.That(() => new AloeVm(module), Throws.TypeOf<VmException>());
        }


        [Test]
        public void Verifier_RejectsInvalidConstantIndex()
        {
            var module = new Module(
                Array.Empty<AloeValue>(),
                new[]
                {
                    new Instruction(EnumOpcode.PushConst, 0),
                    new Instruction(EnumOpcode.Return),
                },
                new[] { new FunctionInfo("main", 0, 0, 0) },
                entryPointIndex: 0);

            Assert.That(() => new AloeVm(module), Throws.TypeOf<VmException>());
        }


        [Test]
        public void MinimalMain_CompilesAndRuns()
        {
            const string source = """
function main(args: string[]): int {
    print("Hello, Aloe");
    print(1 + 2 * 3);
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("Hello, Aloe\n7\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void Locals_Assignment_AndWhile_CompileAndRun()
        {
            const string source = """
function main(args: string[]): int {
    var sum = 0;
    let i: int = 1;


    while (i < 11) {
        sum = sum + i;
        i = i + 1;
    }


    print(sum);
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("55\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void Comparisons_AndModulo_CompileAndRun()
        {
            const string source = """
function main(args: string[]): int {
    print(1 == 1);
    print(1 != 2);
    print(2 <= 2);
    print(3 > 2);
    print(3 >= 3);
    print("Aloe" == "Aloe");
    print(true != false);
    print(17 % 5);
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(
                output,
                Is.EqualTo(
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "2\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void IfElseIf_AndModulo_RunFizzBuzz()
        {
            const string source = """
function main(args: string[]): int {
    var i = 1;


    while (i <= 15) {
        if (i % 15 == 0) {
            print("FizzBuzz");
        } else if (i % 3 == 0) {
            print("Fizz");
        } else if (i % 5 == 0) {
            print("Buzz");
        } else {
            print(i);
        }


        i = i + 1;
    }


    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(
                output,
                Is.EqualTo(
                    "1\n" +
                    "2\n" +
                    "Fizz\n" +
                    "4\n" +
                    "Buzz\n" +
                    "Fizz\n" +
                    "7\n" +
                    "8\n" +
                    "Fizz\n" +
                    "Buzz\n" +
                    "11\n" +
                    "Fizz\n" +
                    "13\n" +
                    "14\n" +
                    "FizzBuzz\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void LogicalOperators_AreBoolAndShortCircuit()
        {
            const string source = """
function main(args: string[]): int {
    print(not false);
    print(true and true);
    print(false or true);
    print(false and (1 / 0 == 0));
    print(true or (1 / 0 == 0));
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(
                output,
                Is.EqualTo(
                    "true\n" +
                    "true\n" +
                    "true\n" +
                    "false\n" +
                    "true\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void BreakAndContinue_ControlNearestWhile()
        {
            const string source = """
function main(args: string[]): int {
    var i = 0;
    var sum = 0;


    while (i < 10) {
        i = i + 1;


        if (i == 3 or i == 5) {
            continue;
        }


        if (i >= 8 and not (i == 9)) {
            break;
        }


        sum = sum + i;
    }


    print(sum);
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("20\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void UserFunctions_ForwardCallsAndTypedReturns_CompileAndRun()
        {
            const string source = """
function main(args: string[]): int {
    print(add(20, 22));
    print(isPositive(add(-1, 2)));
    return 0;
}


function add(a: int, b: int): int {
    return a + b;
}


function isPositive(value: int): bool {
    return value > 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("42\ntrue\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void UserFunctions_CanRecurse()
        {
            const string source = """
function factorial(n: int): int {
    var result = 1;


    if (n > 1) {
        result = n * factorial(n + -1);
    }


    return result;
}


function main(args: string[]): int {
    print(factorial(5));
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("120\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void EarlyReturn_FromNestedIfAndWhile_CompilesAndRuns()
        {
            const string source = """
function firstMatch(limit: int): int {
    var i = 0;


    while (i < limit) {
        if (i == 3) {
            return i;
        }
        i = i + 1;
    }


    return -1;
}


function main(args: string[]): int {
    print(firstMatch(10));
    print(firstMatch(2));
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("3\n-1\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void IfElse_AllPathsReturn_DoesNotNeedTrailingReturn()
        {
            const string source = """
function sign(value: int): int {
    if (value < 0) {
        return -1;
    } else {
        return 1;
    }
}


function main(args: string[]): int {
    print(sign(-5));
    print(sign(5));
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("-1\n1\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void NonVoidFunction_MissingReturnOnOnePath_IsCompileError()
        {
            const string source = """
function maybe(value: int): int {
    if (value > 0) {
        return value;
    }
}


function main(args: string[]): int {
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void VoidFunction_ConditionalEarlyReturn_FallsThroughToImplicitReturn()
        {
            const string source = """
function maybePrint(value: int): void {
    if (value < 0) {
        return;
    }


    print(value);
}


function main(args: string[]): int {
    maybePrint(-1);
    maybePrint(7);
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("7\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void VoidFunction_CanBeCalledAsStatement()
        {
            const string source = """
function announce(value: string): void {
    print(value);
}


function main(args: string[]): int {
    announce("Aloe");
    return 0;
}
""";


            var (output, exitCode) = CompileAndRun(source);


            Assert.That(output, Is.EqualTo("Aloe\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void FunctionCall_ArgumentTypeMismatch_IsCompileError()
        {
            const string source = """
function addOne(value: int): int {
    return value + 1;
}


function main(args: string[]): int {
    print(addOne("bad"));
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void NonVoidFunction_CallAsStatement_IsCompileError()
        {
            const string source = """
function value(): int {
    return 1;
}


function main(args: string[]): int {
    value();
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [TestCase("break;")]
        [TestCase("continue;")]
        public void LoopControl_OutsideWhile_IsCompileError(string statement)
        {
            var source = $$"""
function main(args: string[]): int {
    {{statement}}
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void Let_RequiresMatchingType()
        {
            const string source = """
function main(args: string[]): int {
    let value: int = "bad";
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void BlockLocal_DoesNotEscape()
        {
            const string source = """
function main(args: string[]): int {
    var x = 1;


    if (x == 1) {
        var inside = 10;
    }


    print(inside);
    return 0;
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void Main_MustReturnInt()
        {
            const string source = """
function main(args: string[]): int {
    return "bad";
}
""";


            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }





        [Test]
        public void TickIntrinsic_CompilesAndRunsFromMain()
        {
            const string source = """
function main(args: string[]): int {
    tick();
    GC.require();
    GC.finish();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void TickIntrinsic_IsRejectedOutsideMain()
        {
            const string source = """
function helper(): void {
    tick();
}

function main(args: string[]): int {
    helper();
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void GcIntrinsics_RequireAndFinish_CompileAndRunFromMain()
        {
            const string source = """
function main(args: string[]): int {
    GC.require();
    GC.finish();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void GcDebugStaticProperty_CanBeWrittenAndRead()
        {
            const string source = """
function main(args: string[]): int {
    GC.debug = true;
    if (GC.debug) {
        print(1);
    }

    GC.debug = false;
    if (not GC.debug) {
        print(2);
    }

    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("1\n2\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void GcDebugStaticProperty_RejectsNonBoolSetterValue()
        {
            const string source = """
function main(args: string[]): int {
    GC.debug = 1;
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }



        [Test]
        public void MinimalClassAndNew_CompileAndRun()
        {
            const string source = """
class Node {
    construct() {
    }
}

function makeGarbage(): void {
    var node = new Node();
}

function main(args: string[]): int {
    makeGarbage();
    tick();
    GC.require();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void ClassConstructor_AssignsAndReadsField()
        {
            const string source = """
class Box {
    field value: int;

    construct(value: int) {
        this.value = value;
        print(this.value);
    }
}

function main(args: string[]): int {
    var box = new Box(42);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("42\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void ClassConstructor_RejectsWrongArgumentCount()
        {
            const string source = """
class Box {
    field value: int;
    construct(value: int) {
        this.value = value;
    }
}

function main(args: string[]): int {
    var box = new Box();
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void ClassWithFields_RequiresConstructor()
        {
            const string source = """
class Box {
    field value: int;
}

function main(args: string[]): int {
    var box = new Box();
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void ClassAllocation_StoresTypeMetadata()
        {
            const string source = """
class Box {
    construct() {
    }
}

function main(args: string[]): int {
    var box = new Box();
    return 0;
}
""";

            var module = new AloeCompiler().Compile(source);
            var vm = new AloeVm(module);
            vm.RunFromEntryPoint();

            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(1));
            Assert.That(vm.Heap.ObjectTable.Values.Single().TypeName, Is.EqualTo("Box"));
        }


        [Test]
        public void PublicPropertyGetter_ReadsInstanceState()
        {
            const string source = """
class Box {
    field value: int;

    construct(value: int) {
        this.value = value;
    }

    public property Value: int {
        get {
            return this.value;
        }
    }
}

function main(args: string[]): int {
    var box = new Box(42);
    print(box.Value);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("42\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PrivateInstanceMethod_CanBeCalledFromPropertyGetter()
        {
            const string source = """
class Box {
    field value: int;

    construct(value: int) {
        this.value = value;
    }

    private method doubled(value: int): int {
        return value * 2;
    }

    public property DoubleValue: int {
        get {
            return doubled(this.value);
        }
    }
}

function main(args: string[]): int {
    var box = new Box(21);
    print(box.DoubleValue);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("42\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void InstancePropertySetter_IsRejected()
        {
            const string source = """
class Box {
    field value: int;
    construct(value: int) {
        this.value = value;
    }
    public property Value: int {
        get { return this.value; }
        set { this.value = value; }
    }
}

function main(args: string[]): int {
    var box = new Box(1);
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void PublicAsyncMethod_IsDeferredUntilTick()
        {
            const string source = """
class Counter {
    field value: int;

    construct() {
        this.value = 0;
    }

    public async method add(amount: int): void {
        this.value = this.value + amount;
    }

    public property Value: int {
        get { return this.value; }
    }
}

function main(args: string[]): int {
    var counter = new Counter();
    counter.add(5);
    print(counter.Value);
    tick();
    print(counter.Value);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("0\n5\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PublicAsyncMethods_PreserveEnqueueOrderWithinTick()
        {
            const string source = """
class Counter {
    field value: int;

    construct() {
        this.value = 0;
    }

    public async method add(amount: int): void {
        this.value = this.value + amount;
    }

    public property Value: int {
        get { return this.value; }
    }
}

function main(args: string[]): int {
    var counter = new Counter();
    counter.add(2);
    counter.add(3);
    tick();
    print(counter.Value);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("5\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void AsyncCallEnqueuedDuringTick_IsProcessedInSameTick()
        {
            const string source = """
class Counter {
    field value: int;

    construct() {
        this.value = 0;
    }

    public async method add(amount: int): void {
        this.value = this.value + amount;
    }

    public property Value: int {
        get { return this.value; }
    }
}

class Relay {
    construct() {
    }

    public async method forward(counter: Counter): void {
        counter.add(4);
    }
}

function main(args: string[]): int {
    var counter = new Counter();
    var relay = new Relay();
    relay.forward(counter);
    tick();
    print(counter.Value);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("4\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PublicPropertyReadDuringTick_UsesCommittedStateUntilCommit()
        {
            const string source = """
class Counter {
    field value: int;

    construct() {
        this.value = 0;
    }

    public async method add(amount: int): void {
        this.value = this.value + amount;
    }

    public property Value: int {
        get { return this.value; }
    }
}

class Observer {
    construct() {
    }

    public async method inspect(counter: Counter): void {
        print(counter.Value);
    }
}

function main(args: string[]): int {
    var counter = new Counter();
    var observer = new Observer();
    counter.add(5);
    observer.inspect(counter);
    tick();
    print(counter.Value);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("0\n5\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PublicAsyncMethod_MustReturnVoid()
        {
            const string source = """
class Bad {
    construct() {
    }

    public async method value(): int {
        return 1;
    }
}

function main(args: string[]): int {
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void AsyncCallGraphCycle_IsRejected()
        {
            const string source = """
class Worker {
    construct() {
    }

    public async method first(target: Worker): void {
        target.second(target);
    }

    public async method second(target: Worker): void {
        target.first(target);
    }
}

function main(args: string[]): int {
    var worker = new Worker();
    worker.first(worker);
    tick();
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void GcIntrinsics_AreRejectedOutsideMain()
        {
            const string source = """
function helper(): void {
    GC.require();
}

function main(args: string[]): int {
    helper();
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
        }


        private static (string Output, long ExitCode) CompileAndRun(
            string source)
        {
            var module = new AloeCompiler().Compile(source);
            var output = new StringBuilder();


            var vm = new AloeVm(module)
            {
                OutputWriter = line => output.AppendLine(line)
            };


            vm.RunFromEntryPoint();


            Assert.That(vm.ValueStack.Count, Is.EqualTo(1));


            var exitCode = vm.Pop();
            Assert.That(exitCode.IsInt, Is.True);


            return (
                output.ToString().Replace("\r\n", "\n"),
                exitCode.AsInt);
        }
    }
}