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
        public void FloatExpressions_SupportMixedArithmeticAndComparisons()
        {
            const string source = """
function halve(value: float): float {
    return value / 2.0;
}

function main(args: string[]): int {
    let start: float = 1.5;
    var mixed = 2 + start;
    print(halve(mixed));
    print(mixed * 2.0);
    print(mixed >= 3.5);
    print(mixed - 0.5);
    print(0.1 + 0.2);
    print(16777216.0 + 1.0);
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source);

            Assert.That(output, Is.EqualTo("1.75\n7\ntrue\n3\n0.3\n16777216\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void FloatModulo_SupportsFloatAndMixedNumericOperands()
        {
            const string source = """
function main(args: string[]): int {
    print(5.5 % 2.0);
    print(5 % 2.0);
    print(-5.5 % 2);
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source);

            Assert.That(output, Is.EqualTo("1.5\n1\n-1.5\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void FloatModulo_ByZero_ThrowsZeroDivisionException()
        {
            const string source = """
function main(args: string[]): int {
    print(5.5 % 0.0);
    return 0;
}
""";

            Assert.That(
                () => CompileAndRun(source),
                Throws.TypeOf<ZeroDivisionException>());
        }


        [Test]
        public void DecimalLiteral_PassesThroughTypedFunctionWithoutLosingPrecision()
        {
            const string source = """
function identity(value: decimal): decimal {
    return value;
}

function main(args: string[]): int {
    let amount: decimal = identity(10.12345678901234567890:d);
    print(amount);
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source);

            Assert.That(output, Is.EqualTo("10.12345678901234567890\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void DecimalArithmeticAndComparisons_RoundTripThroughAloeBcExactly()
        {
            const string source = """
function add(left: decimal, right: decimal): decimal {
    return left + right;
}

function main(args: string[]): int {
    let value: decimal = add(1.00000000000000000001:d, 2.00000000000000000002:d);
    print(value);
    print(value + 1);
    print(4 - value);
    print(value * 2);
    print(value / 3);
    print(value % 2);
    print(value > 3.00000000000000000002:d);
    print(value == 3.00000000000000000003:d);
    return 0;
}
""";

            var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
            var output = new StringBuilder();
            var settings = new AloeVmSettings();
            settings.Logging.FileEnabled = false;
            settings.Logging.ConsoleEnabled = false;
            var vm = new AloeVm(module, settings)
            {
                OutputWriter = line => output.AppendLine(line)
            };

            vm.RunFromEntryPoint();

            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo(
                "3.00000000000000000003\n" +
                "4.00000000000000000003\n" +
                "0.99999999999999999997\n" +
                "6.00000000000000000006\n" +
                "1.00000000000000000001\n" +
                "1.00000000000000000003\n" +
                "true\n" +
                "true\n"));
            Assert.That(vm.Pop().AsInt, Is.EqualTo(0));
        }


        [Test]
        public void DecimalDivision_ByZero_ThrowsZeroDivisionException()
        {
            const string source = """
function main(args: string[]): int {
    print(1.0:d / 0:d);
    return 0;
}
""";

            Assert.That(
                () => CompileAndRun(source),
                Throws.TypeOf<ZeroDivisionException>());
        }


        [Test]
        public void DecimalAddition_OverflowThrowsOverflowException()
        {
            const string source = """
function main(args: string[]): int {
    print(79228162514264337593543950335:d + 1:d);
    return 0;
}
""";

            Assert.That(
                () => CompileAndRun(source),
                Throws.TypeOf<OverflowException>());
        }


        [Test]
        public void DecimalAndFloatArithmetic_CannotBeMixedImplicitly()
        {
            const string source = """
function main(args: string[]): int {
    print(1.0:d + 1.0);
    return 0;
}
""";

            Assert.That(
                () => new AloeCompiler().Compile(source),
                Throws.TypeOf<AloeCompileException>());
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
        public void ByteValues_AreEncodedAndPromoteToInt()
        {
            const string source = """
function pass(value: byte): byte {
    return value;
}

function main(args: string[]): int {
    let low: byte = 0;
    let high: byte = pass(255);
    print(low == 0);
    print(high);
    print(high + 1);
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("true\n255\n256\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void ByteValues_RejectOutOfRangeLiteral()
        {
            const string source = """
function main(args: string[]): int {
    let bad: byte = 256;
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void CharValues_AreTypedComparedAndEncoded()
        {
            const string source = """
function echo(value: char): char {
    return value;
}

function main(args: string[]): int {
    let letter: char = echo('A');
    print(letter);
    print(letter == 'A');
    print('A' < 'B');
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("A\ntrue\ntrue\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void CharValues_RejectNumericConversionAndArithmetic()
        {
            const string numericAssignment = """
function main(args: string[]): int {
    let invalid: char = 65;
    return 0;
}
""";
            const string arithmetic = """
function main(args: string[]): int {
    print('A' + 'B');
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(numericAssignment), Throws.TypeOf<AloeCompileException>());
            Assert.That(() => new AloeCompiler().Compile(arithmetic), Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void EnumValues_AreTypedAndPassThroughAloeBc()
        {
            const string source = """
enum Color {
    Red,
    Green,
    Blue
}

function echo(value: Color): Color {
    return value;
}

function main(args: string[]): int {
    let chosen: Color = echo(Color.Green);
    print(chosen == Color.Green);
    print(chosen);
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("true\n1\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void EnumValues_RejectCrossTypeComparisonAndUnknownMembers()
        {
            const string crossType = """
enum Color { Red, Green }
function main(args: string[]): int {
    let value: Color = Color.Red;
    print(value == 0);
    return 0;
}
""";
            const string unknownMember = """
enum Color { Red, Green }
function main(args: string[]): int {
    let value: Color = Color.Blue;
    return 0;
}
""";
            const string crossEnumAssignment = """
enum Color { Red, Green }
enum Direction { North, South }
function main(args: string[]): int {
    let direction: Direction = Color.Red;
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(crossType), Throws.TypeOf<AloeCompileException>());
            Assert.That(() => new AloeCompiler().Compile(unknownMember), Throws.TypeOf<AloeCompileException>());
            Assert.That(() => new AloeCompiler().Compile(crossEnumAssignment), Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void EnumExplicitValues_MixRadicesAliasesAndImplicitNumberingThroughAloeBc()
        {
            const string source = """
enum Status { Zero, Start = 10, Next, Alias = 10, Negative = -0x2, AfterNegative, Binary = 0b101, Positive = + 7, Last, }
function echo(value: Status): Status { return value; }
function main(args: string[]): int {
    print(Status.Zero); print(Status.Start); print(Status.Next);
    print(Status.Alias == Status.Start);
    print(Status.Negative); print(Status.AfterNegative);
    print(Status.Binary); print(Status.Positive); print(echo(Status.Last));
    with (Status) { print(.Alias == Start); print(Next); }
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("0\n10\n11\ntrue\n-2\n-1\n5\n7\n8\ntrue\n11\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void EnumExplicitValues_AcceptInt32BoundariesAndResetAfterMaximum()
        {
            const string source = """
enum Boundary { Max = 2147483647, Min = -2147483648, Next, HexMax = 0x7fffffff, HexMin = - 0x80000000, Reset = 0, One }
enum FinalMaximum { Max = 2147483647 }
function main(args: string[]): int {
    print(Boundary.Max); print(Boundary.Min); print(Boundary.Next);
    print(Boundary.HexMax == Boundary.Max); print(Boundary.HexMin == Boundary.Min);
    print(Boundary.One); print(FinalMaximum.Max);
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("2147483647\n-2147483648\n-2147483647\ntrue\ntrue\n1\n2147483647\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void EnumExplicitValues_RejectOverflowExpressionsAndDuplicateNames()
        {
            string[] invalidMembers =
            [
                "A = 2147483648", "A = -2147483649", "A = 0x80000000", "A = -0x80000001",
                "A = 0xFFFFFFFFFFFFFFFF", "A = 18446744073709551616", "A = -18446744073709551616",
                "A = 2147483647, B", "A = 0x7fffffff, B, C = 0",
                "A = 1.0", "A = true", "A = 'A'", "A = \"1\"", "A = 1 + 2", "A = (1)",
                "A = 1, B = A", "A = - -1", "A = + -1", "A =", "A = 1, A = 2"
            ];
            foreach (var members in invalidMembers)
            {
                var source = "enum Value { " + members + " } function main(args: string[]): int { return 0; }";
                Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>(), source);
            }
        }

        [Test]
        public void EnumExplicitValues_PreserveTypeIdentityForEqualNumericValues()
        {
            const string declarations = "enum First { A = 7 } enum Second { A = 7 } ";
            string[] statements =
            [
                "let value: First = 7;", "let value: First = Second.A;",
                "print(First.A == Second.A);", "print(First.A == 7);"
            ];
            foreach (var statement in statements)
                Assert.That(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { " + statement + " return 0; }"), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void EnumTypeWith_ResolvesShortMembersAndNearestNestedScope()
        {
            const string source = """
enum Color { Red, Green }
enum Signal { Green, Blue }

function main(args: string[]): int {
    with (Color) {
        Red;
        let first: Color = Green;
        with (Signal) {
            let second: Signal = Green;
            print(second == Signal.Green);
        }
        print(first == Color.Green);
    }
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("true\ntrue\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void EnumTypeWith_ResolvesDotMemberAbbreviation()
        {
            const string source = """
enum Color { Red, Green }
function main(args: string[]): int {
    with (Color) {
        let chosen: Color = .Green;
        print(chosen == Color.Green);
    }
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("true\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void TypeWith_RejectsUnknownShortMembersAndNonTypeTargets()
        {
            const string unknownMember = """
enum Color { Red, Green }
function main(args: string[]): int {
    with (Color) {
        let value: Color = Missing;
    }
    return 0;
}
""";
            const string nonTypeTarget = """
class User {
}
function main(args: string[]): int {
    var user = 1;
    with (user) {
        let value: int = 1;
    }
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(unknownMember), Throws.TypeOf<AloeCompileException>());
            Assert.That(() => new AloeCompiler().Compile(nonTypeTarget), Throws.TypeOf<AloeCompileException>());
        }


        [Test]
        public void ClassTypeWith_DefersStaticAsyncCallsUntilTickAndRoundTrips()
        {
            const string source = """
class Logger {
    public static async method emit(value: int): void {
        print(value);
    }
}
function main(args: string[]): int {
    with (Logger) {
        .emit(11);
        .emit(12);
        print("queued");
    }
    tick();
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("queued\n11\n12\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void ClassTypeWith_UsesNearestMemberAndRestoresOuterContext()
        {
            const string source = """
class First {
    public static async method emit(value: int): void {
        print(100 + value);
    }
    public static async method outerOnly(): void {
        print("outer");
    }
}
class Second {
    public static async method emit(value: int): void {
        print(200 + value);
    }
}
function main(args: string[]): int {
    with (First) {
        .emit(1);
        tick();
        with (Second) {
            .emit(2);
            tick();
            .outerOnly();
            tick();
        }
        .emit(3);
        tick();
    }
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source);

            Assert.That(output, Is.EqualTo("101\n202\nouter\n103\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void ClassTypeWith_CanUseOuterEnumMembersAsArguments()
        {
            const string source = """
enum Color { Red, Green }
class Printer {
    public static async method emit(value: Color): void {
        print(value == Color.Green);
    }
}
function main(args: string[]): int {
    with (Color) {
        with (Printer) {
            .emit(.Green);
            .emit(Red);
        }
    }
    tick();
    return 0;
}
""";

            var (output, exitCode) = CompileAndRun(source, roundTripAloeBc: true);

            Assert.That(output, Is.EqualTo("true\nfalse\n"));
            Assert.That(exitCode, Is.EqualTo(0));
        }


        [Test]
        public void ClassTypeWith_RejectsInstanceMembersInvalidCallsAndAsyncCycles()
        {
            string[] invalidPrograms =
            [
                """
class Item {
    public async method emit(): void { }
}
function main(args: string[]): int {
    with (Item) { .emit(); }
    return 0;
}
""",
                """
class Item { field value: int; }
function main(args: string[]): int {
    with (Item) { let result: int = .value; }
    return 0;
}
""",
                """
class Sink {
    public static async method emit(value: int): void { }
}
function main(args: string[]): int {
    with (Sink) { .emit("bad"); }
    return 0;
}
""",
                """
class Sink {
    public static async method emit(value: int): void { }
}
function main(args: string[]): int {
    with (Sink) { .missing(); }
    return 0;
}
""",
                """
class Sink {
    public static async method emit(value: int): void { }
}
function main(args: string[]): int {
    with (Sink) { }
    .emit(1);
    return 0;
}
""",
                """
class Sink {
    public static async method emit(value: int): void { }
}
function main(args: string[]): int {
    var Sink = 1;
    with (Sink) { .emit(1); }
    return 0;
}
""",
                """
class Repeater {
    public static async method again(): void {
        with (Repeater) { .again(); }
    }
}
function main(args: string[]): int { return 0; }
"""
            ];

            foreach (var source in invalidPrograms)
                Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>(), source);
        }


        [Test]
        public void InstanceWith_EvaluatesTargetOnceAndQueuesCallsThroughAloeBc()
        {
            const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
    public async method emit(value: int): void { print(value); }
}
function make(): Box { print("make"); return new Box(42); }
function main(args: string[]): int {
    with (make()) {
        GC.require();
        GC.finish();
        print(.Value);
        .emit(.Value);
        print("queued");
    }
    tick();
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("make\n42\nqueued\n42\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void InstanceWith_RetainsOriginalTargetAndRestoresNestedContexts()
        {
            const string source = """
enum Color { Red, Green }
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
}
function main(args: string[]): int {
    var chosen = new Box(1);
    var second = new Box(2);
    with (chosen) {
        chosen = second;
        print(.Value);
        with (second) {
            print(.Value);
            with (Color) { print(.Value); print(.Green == Color.Green); }
        }
        print(.Value);
    }
    return 0;
}
""";
            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("1\n2\n2\ntrue\n1\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void InstanceWith_SelfFieldsSupportTypedReadsAndWrites()
        {
            const string source = """
class Box {
    field value: byte;
    construct(value: byte) { with (this) { .value = value; } }
    public property Value: byte { get { with (this) { return .value; } } }
    public async method change(): void {
        with (this) { .value = 255; print(.value); }
    }
}
function main(args: string[]): int {
    var box = new Box(1);
    with (box) { print(.Value); .change(); }
    tick();
    print(box.Value);
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("1\n255\n255\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void InstanceWith_PreservesInheritedGetterAndVirtualDispatch()
        {
            const string source = """
class Base {
    public property Value: int { get { return 7; } }
    public virtual async method emit(): void { print("base"); }
}
class Derived extends Base {
    public override async method emit(): void { print("derived"); }
}
function main(args: string[]): int {
    let box: Base = new Derived();
    with (box) { print(.Value); .emit(); }
    tick();
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("7\nderived\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void InstanceWith_RejectsInvalidTargetsMembersArgumentsAndCycles()
        {
            string[] invalidPrograms =
            [
                "function main(args: string[]): int { with (1) { } return 0; }",
                "function main(args: string[]): int { with (this) { } return 0; }",
                "class Box { public property Value: int { get { return 1; } } } function main(args: string[]): int { with (new Box()) { .Value = 2; } return 0; }",
                "class Box { field value: int; construct() { this.value = 1; } } function main(args: string[]): int { with (new Box()) { print(.value); } return 0; }",
                "class Box { public async method emit(value: int): void { } } function main(args: string[]): int { with (new Box()) { .emit(\"bad\"); } return 0; }",
                "class Box { } function main(args: string[]): int { with (new Box()) { print(.missing); } return 0; }",
                "class Box { public async method again(): void { with (this) { .again(); } } } function main(args: string[]): int { return 0; }",
                "class Box { field value: byte; construct() { with (this) { .value = 256; } } } function main(args: string[]): int { return 0; }",
                "class Base { public virtual async method ping(other: Base): void { with (other) { .pong(other); } } public virtual async method pong(other: Base): void { } } class Derived extends Base { public override async method pong(other: Base): void { with (other) { .ping(other); } } } function main(args: string[]): int { return 0; }",
                "class Box { private method hidden(): void { } } function main(args: string[]): int { with (new Box()) { .hidden(); } return 0; }",
                "class Box { field value: int; construct() { this.value = 1; } } function main(args: string[]): int { with (new Box()) { .value = 2; } return 0; }"
            ];
            foreach (var source in invalidPrograms)
                Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>(), source);
        }

        [Test]
        public void MultipleWith_EvaluatesTargetsOnceLeftToRightAndRetainsGcRoots()
        {
            const string source = """
class Left {
    field value: int;
    construct(value: int) { this.value = value; }
    public property First: int { get { return this.value; } }
}
class Right {
    public property Second: int { get { return 9; } }
}
function makeLeft(a: int, b: int): Left { print("left"); return new Left(a + b); }
function makeRight(): Right { print("right"); return new Right(); }
function main(args: string[]): int {
    with (makeLeft(1, 2), makeRight()) {
        GC.require(); GC.finish();
        print(.First); print(.Second); print(.First);
    }
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("left\nright\n3\n9\n3\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void MultipleWith_MixesInstancesClassTypesAndEnumTypes()
        {
            const string source = """
enum Color { Red, Green }
class Box {
    field value: int;
    construct() { this.value = 1; }
    public property Value: int { get { return this.value; } }
    public async method change(value: int): void { this.value = value; }
}
class Logger {
    public static async method emit(color: Color): void { print(color == Color.Green); }
}
function main(args: string[]): int {
    var box = new Box();
    with (box, Logger, Color) {
        print(.Value);
        .emit(.Green); tick();
        .change(8); tick(); print(.Value);
        print(Red == Color.Red);
    }
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("1\ntrue\n8\ntrue\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void MultipleWith_PrefersInnerBlockAndRestoresOuterTargets()
        {
            const string source = """
class First {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
}
class Second { public property Other: int { get { return 9; } } }
function main(args: string[]): int {
    var outer = new First(1);
    var inner = new First(2);
    var second = new Second();
    with (outer, second) {
        outer = inner;
        print(.Value);
        with (inner, new Second()) { print(.Value); print(.Other); }
        print(.Value); print(.Other);
    }
    return 0;
}
""";
            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("1\n2\n9\n1\n9\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void MultipleWith_AllowsSelfFieldWritesAndLocalEnumShadowing()
        {
            const string source = """
enum Color { Red, Green }
enum OtherColor { Red, Blue }
class Box {
    field value: byte;
    construct() { with (this, Color) { .value = 255; print(.Green == Color.Green); } }
    public property Value: byte { get { return this.value; } }
}
function main(args: string[]): int {
    with (new Box(), Color, OtherColor) {
        var Red = 7;
        print(Red); print(.Value); print(.Blue == OtherColor.Blue);
    }
    return 0;
}
""";
            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("true\n7\n255\ntrue\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void MultipleWith_RejectsAmbiguousMembersWithinOneBlock()
        {
            (string Declarations, string Targets, string Statement)[] cases =
            [
                ("enum A { Shared } enum B { Shared }", "A, B", "print(Shared == A.Shared);"),
                ("enum A { Shared } enum B { Shared }", "A, B", "print(.Shared == A.Shared);"),
                ("enum A { Shared }", "A, A", ".Shared;"),
                ("class A { public property Value: int { get { return 1; } } } class B { public property Value: int { get { return 2; } } }", "new A(), new B()", "print(.Value);"),
                ("class A { public async method emit(): void { } } class B { public async method emit(): void { } }", "new A(), new B()", ".emit();"),
                ("class A { public static async method emit(): void { } } class B { public static async method emit(): void { } }", "A, B", ".emit();"),
                ("class A { public async method emit(): void { } } class B { public static async method emit(): void { } }", "new A(), B", ".emit();"),
                ("enum A { Shared } class B { public property Shared: int { get { return 1; } } }", "A, new B()", "print(.Shared);"),
                ("class A { field value: int; construct() { this.value = 1; } public async method change(other: A): void { with (this, other) { .value = 2; } } }", "new A()", "")
            ];
            foreach (var item in cases)
            {
                var source = item.Declarations + " function main(args: string[]): int { with (" + item.Targets + ") { " + item.Statement + " } return 0; }";
                var exception = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
                Assert.That(exception!.Message, Does.Contain("Ambiguous with member"), source);
            }
        }

        [Test]
        public void MultipleWith_RejectsInvalidLaterTargetsAndPreservesAsyncCycleChecks()
        {
            string[] invalidPrograms =
            [
                "class A { } function main(args: string[]): int { with (new A(), 1) { } return 0; }",
                "class A { } function main(args: string[]): int { with (new A(),) { } return 0; }",
                "class A { } function main(args: string[]): int { var A = 1; with (new A(), A) { } return 0; }",
                "enum Color { Red } class A { public async method again(): void { with (this, Color) { .again(); } } } function main(args: string[]): int { return 0; }"
            ];
            foreach (var source in invalidPrograms)
                Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>(), source);
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
        public void DerivedClass_InheritsBaseFieldAndPropertyAndCanBeUsedAsBaseType()
        {
            const string source = """
class Base {
    field value: int;

    public property current: int {
        get { return this.value; }
    }
}

class Derived extends Base {
    construct(initial: int) {
        this.value = initial;
    }
}

function main(args: string[]): int {
    let item: Base = new Derived(42);
    print(item.current);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("42\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void SealedClass_CannotBeExtended()
        {
            const string source = """
sealed class Base {
}

class Derived extends Base {
}

function main(args: string[]): int {
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void DerivedClass_CallsParameterlessBaseConstructor()
        {
            const string source = """
class Base {
    field value: int;

    construct() {
        this.value = 7;
    }

    public property current: int {
        get { return this.value; }
    }
}

class Derived extends Base {
}

function main(args: string[]): int {
    let item: Base = new Derived();
    print(item.current);
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("7\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void DerivedClass_ResolvesInheritedAsyncInstanceMethod()
        {
            const string source = """
class Base {
    public async method announce(): void {
        print("base");
    }
}

class Derived extends Base {
}

function main(args: string[]): int {
    let item: Base = new Derived();
    item.announce();
    tick();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("base\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void DerivedClass_OverrideUsesRuntimeDispatchThroughBaseReference()
        {
            const string source = """
class Base {
    public virtual async method announce(): void {
        print("base");
    }
}

class Derived extends Base {
    public override async method announce(): void {
        print("derived");
    }
}

class Leaf extends Derived {
}

function main(args: string[]): int {
    let item: Base = new Leaf();
    item.announce();
    tick();
    return 0;
}
""";

            var result = CompileAndRun(source, roundTripAloeBc: true);
            Assert.That(result.Output, Is.EqualTo("derived\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }

        [Test]
        public void Override_MustMatchInheritedVirtualMethodSignature()
        {
            const string source = """
class Base {
    public virtual async method announce(value: int): void {
    }
}

class Derived extends Base {
    public override async method announce(value: string): void {
    }
}

function main(args: string[]): int {
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void Override_RequiresInheritedVirtualMethod()
        {
            const string source = """
class Base {
    public async method announce(): void {
    }
}

class Derived extends Base {
    public override async method announce(): void {
    }
}

function main(args: string[]): int {
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        }

        [Test]
        public void AsyncCallGraph_IncludesVirtualOverrideTargets()
        {
            const string source = """
class Base {
    public virtual async method ping(other: Base): void {
        other.pong(other);
    }

    public virtual async method pong(other: Base): void {
    }
}

class Derived extends Base {
    public override async method pong(other: Base): void {
        other.ping(other);
    }
}

function main(args: string[]): int {
    return 0;
}
""";

            Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
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
        public void PublicStaticAsyncMethod_IsDeferredUntilTick()
        {
            const string source = """
class Commands {
    public static async method emit(value: int): void {
        print(value);
    }
}

function main(args: string[]): int {
    Commands.emit(7);
    print(1);
    tick();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("1\n7\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PublicStaticAsyncMethod_EnqueuedDuringTickRunsBeforeReturn()
        {
            const string source = """
class Commands {
    public static async method first(): void {
        print(1);
        Commands.second();
    }

    public static async method second(): void {
        print(2);
    }
}

function main(args: string[]): int {
    Commands.first();
    tick();
    return 0;
}
""";

            var result = CompileAndRun(source);
            Assert.That(result.Output, Is.EqualTo("1\n2\n"));
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }


        [Test]
        public void PublicStaticAsyncMethod_MustReturnVoid()
        {
            const string source = """
class Commands {
    public static async method invalid(): int {
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
            string source,
            bool roundTripAloeBc = false)
        {
            var module = new AloeCompiler().Compile(source);
            if (roundTripAloeBc)
                module = AloeBcCodec.Read(AloeBcCodec.Write(module));
            var output = new StringBuilder();
            var settings = new AloeVmSettings();
            settings.Logging.FileEnabled = false;
            settings.Logging.ConsoleEnabled = false;


            var vm = new AloeVm(module, settings)
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
