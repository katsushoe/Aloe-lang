using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ObjectEqualityTests
{
    [Test]
    public void ObjectEquality_DocumentedClassAndArrayAliasesUseIdentity()
    {
        const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
}
function main(args: string[]): int {
    var box = new Box(42); var alias = box; var other = new Box(42);
    var values = [1, 2]; var shared = values; var sameContents = [1, 2];
    GC.require(); GC.finish();
    if (box == alias and box != other and values == shared and values != sameContents) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ObjectEquality_BaseGenericObjectsNestedArraysAndCyclesKeepSameIds()
    {
        const string source = """
class Base { }
class Derived extends Base { }
class Other { }
function identity(value: object): object { return value; }
function main(args: string[]): int {
    var derived = new Derived(); let base: Base = derived; var other = new Other();
    var row = [1]; var matrix = [row]; var old = matrix[0];
    let objects: object[] = [derived, matrix]; objects[1] = objects;
    matrix[0] = [1]; GC.require(); GC.finish();
    if (derived == base and identity(derived) == derived and objects[0] == derived and
        objects[1] == objects and derived != other and old == row and matrix[0] != row) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ObjectEquality_OperandsRunOnceLeftToRightAndLogicalOperatorsShortCircuit()
    {
        const string source = """
class Box { }
function operand(value: Box, number: int): Box { print(number); return value; }
function main(args: string[]): int {
    var box = new Box(); var other = new Box();
    if (operand(box, 1) == operand(box, 2) and operand(box, 3) != operand(other, 4)) {
        if (box == box or operand(other, 99) == box) {
            if (box != box and operand(other, 98) == box) { return 0; }
            return 42;
        }
    }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n3\n4\n"));
    }

    [Test]
    public void ObjectEquality_GetterComparisonsObserveCommittedArrayReferencesDuringTick()
    {
        const string source = """
class Box { }
class Store {
    field values: object[];
    construct(values: object[]) { this.values = values; }
    public property Same: bool { get { return this.values[0] == this.values[1]; } }
    public async method update(other: Box, query: Store): void {
        this.values[0] = other;
        if (query.Same) { print(1); } else { print(0); }
        if (this.values[0] == this.values[1]) { print(1); } else { print(0); }
    }
}
function main(args: string[]): int {
    var box = new Box(); var other = new Box(); let values: object[] = [box, box];
    var store = new Store(values); store.update(other, store); GC.require(); GC.finish(); tick();
    if (not store.Same and values[0] == other and values[1] == box) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n0\n"));
    }

    [Test]
    public void ObjectEquality_RejectsOpaqueOrderingStructEqualityWasmAndChecksScalarExtraction()
    {
        string[] statements =
        [
            "var box = new Box(); var result = box < box;",
            "var value = Point { x: 1 }; var result = value == value;"
        ];
        const string declarations = "class Box {} struct Point { field x: int; }";
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        foreach (var statement in new[] { "var box = new Box(); var result = box == 1;", "var values = [1]; var result = values == true;", "var values = [1]; var result = values != \"one\";" })
            Assert.Throws<InvalidCastException>(() => Run(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("class Box {} function main(args: string[]): int { var box = new Box(); if (box == box) { return 42; } return 0; }"));
    }

    [Test]
    public void ObjectEquality_RawBytecodeComparesIdPayloadsWithoutDereferencingHandles()
    {
        var first = AloeValue.FromObject(123);
        Assert.That(EqualRaw(first, first, EnumOpcode.CmpEq), Is.True);
        Assert.That(EqualRaw(first, AloeValue.FromObject(124), EnumOpcode.CmpEq), Is.False);
        Assert.That(EqualRaw(first, first, EnumOpcode.CmpNe), Is.False);
        Assert.That(EqualRaw(first, AloeValue.FromObject(124), EnumOpcode.CmpNe), Is.True);
        Assert.That(EqualRaw(first, AloeValue.Null, EnumOpcode.CmpEq), Is.False);
        Assert.That(EqualRaw(AloeValue.Null, AloeValue.Null, EnumOpcode.CmpEq), Is.True);
        Assert.That(EqualRaw(first, AloeValue.FromInt(123), EnumOpcode.CmpEq), Is.False);
        var point = AloeValue.FromStruct(new AloeStructValue("Point", new[] { AloeValue.FromInt(1) }));
        Assert.Throws<VmException>(() => EqualRaw(point, point, EnumOpcode.CmpEq));
    }

    [Test]
    public void ObjectEquality_IdentitySurvivesActualGcBankEvacuation()
    {
        var constants = new List<AloeValue> { AloeValue.Null, AloeValue.Null };
        var vm = CreateVm(EqualityModule(constants, EnumOpcode.CmpEq));
        var size = vm.Heap.BankSlotCapacity;
        vm.Heap.Allocate(new byte[size * 3 / 8 * AloeHeap.HeapSlotSize]);
        var live = vm.Heap.Allocate(new byte[size * 3 / 8 * AloeHeap.HeapSlotSize]);
        vm.Heap.Allocate(new byte[size / 4 * AloeHeap.HeapSlotSize]);
        vm.Heap.SetExternalReferenceCount(live, 1);
        for (var index = 0; index < 4; index++)
        {
            var destination = vm.Heap.Allocate(new byte[size / 8 * AloeHeap.HeapSlotSize]);
            vm.Heap.SetExternalReferenceCount(destination, 1);
        }
        var before = vm.Heap.GetEntry(live).BankId;
        constants[0] = AloeValue.FromObject(live); constants[1] = AloeValue.FromObject(live);
        vm.RequireGc(); vm.FinishGc();
        Assert.That(vm.Heap.GetEntry(live).BankId, Is.Not.EqualTo(before));
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsBool, Is.True);
    }

    private static Module EqualityModule(IReadOnlyList<AloeValue> values, EnumOpcode opcode)
        => new(values, new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1), new Instruction(opcode), new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0) }, 0);
    private static bool EqualRaw(AloeValue first, AloeValue second, EnumOpcode opcode)
    {
        var vm = CreateVm(EqualityModule(new[] { first, second }, opcode));
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); return vm.Pop().AsBool;
    }
    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }
    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
