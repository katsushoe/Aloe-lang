using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ReferenceArrayTests
{
    [Test]
    public void ReferenceArrays_DocumentedClassElementsAliasesGettersAndAsyncCalls()
    {
        const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
    public async method update(value: int): void { this.value = value; }
}
function identity(values: Box[]): Box[] { return values; }
function main(args: string[]): int {
    var boxes = [new Box(1), new Box(22)]; var alias = boxes;
    with (identity(boxes)[0]) { .update(20); }
    GC.require(); GC.finish(); tick();
    var total = 0; foreach (box in alias) { total = total + box.Value; }
    if (boxes[0].Value == 20) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceArrays_BaseElementsAllowDerivedInstancesAndVirtualDispatch()
    {
        const string source = """
class Base {
    public property Value: int { get { return 20; } }
    public async virtual method emit(): void { print(1); }
}
class Derived extends Base { public async override method emit(): void { print(2); } }
function main(args: string[]): int {
    let values: Base[] = []; values = [new Base(), new Derived()];
    let derived: Base = new Derived(); values[0] = derived;
    var count = 0;
    foreach (value in values) { value.emit(); count = count + 1; }
    GC.require(); GC.finish(); tick();
    if (count == 2 and values[0].Value == 20 and values[1].Value == 20) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("2\n2\n"));
    }

    [Test]
    public void ReferenceArrays_ObjectElementsShareMixedLiveHeapReferencesAndCycles()
    {
        const string source = """
class Box { }
class Other { }
function identity(value: object): object { return value; }
function main(args: string[]): int {
    var box = new Box(); var other = new Other(); var row = [1];
    let values: object[] = [box, other, row]; var before = values[0];
    values[0] = values; values[1] = box;
    GC.require(); GC.finish();
    var count = 0; foreach (value in values) { count = count + 1; }
    if (count == 3) {
        var inferred = [identity(other)];
        if (inferred.Length == 1) { return 42; }
    }
    return 0;
}
""";
        Assert.That(Run(source, vm =>
        {
            var values = vm.Heap.ObjectTable.Values.Single(entry => entry.TypeName == "object[]" && entry.FieldCount == 3);
            Assert.That(vm.Heap.GetField(values.ObjectId, 0).AsObjectId, Is.EqualTo(values.ObjectId));
            Assert.That(vm.Heap.GetEntry(vm.Heap.GetField(values.ObjectId, 1).AsObjectId).TypeName, Is.EqualTo("Box"));
            Assert.That(vm.Heap.GetEntry(vm.Heap.GetField(values.ObjectId, 2).AsObjectId).TypeName, Is.EqualTo("int[]"));
        }).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceArrays_NestedClassObjectAndGetterResultChainsSurviveGc()
    {
        const string source = """
struct Point { field x: int; }
class Box {
    field values: int[];
    construct() { this.values = [42]; }
    public property Values: int[] { get { return this.values; } }
    public property Point: Point { get { return Point { x: 42 }; } }
}
function make(): Box[][] { return [[new Box()]]; }
function main(args: string[]): int {
    let empty: Box[][] = [[], [new Box()]]; empty[0] = [new Box()];
    var values = make(); let objects: object[][] = [[], [values]];
    objects[0] = [values[0][0]]; GC.require(); GC.finish();
    if (values[0][0].Values[0] == 42 and values[0][0].Point.x == 42 and objects[0].Length == 1) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceArrays_ClassFieldsAsyncArgumentsAndTickKeepCommittedElements()
    {
        const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
}
class Store {
    field values: Box[];
    construct(values: Box[]) { this.values = values; }
    public property First: int { get { return this.values[0].Value; } }
    public async method update(values: Box[], query: Store, replacement: Box): void {
        values[0] = replacement; print(query.First); print(values[0].Value);
    }
}
function main(args: string[]): int {
    var values = [new Box(1)]; var old = values[0]; var store = new Store(values);
    store.update(values, store, new Box(42)); GC.require(); GC.finish(); tick();
    if (store.First == 42 and values[0].Value == 42 and old.Value == 1) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void ReferenceArrays_RejectUnrelatedClassesCovarianceScalarsAndQueryWrites()
    {
        const string declarations = "class Base { public property Value: int { get { return 1; } } } class Derived extends Base {} class Other {}";
        string[] statements =
        [
            "let values: Base[] = [new Other()];", "let values: Base[] = [1];",
            "var values = [new Base()]; values[0] = new Other();",
            "var values = [new Derived()]; let bases: Base[] = values;",
            "let values: Base[] = [new Derived()]; let derived: Derived[] = values;",
            "let values: object[] = [1];", "let values: object[] = [\"a\"];",
            "let values: object[] = [true];", "let values: Base[][] = [[new Other()]];",
            "let values: pipe<int>[] = [];",
            "var values = [new Base()]; values[0].Value = 42;",
            "let values: object[] = [new Base()]; let value: Base = values[0];",
            "foreach (value in [new Base()]) { let other: Other = value; }"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations + " function main(args: string[]): int { var values = [new Base()]; return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " class Store { field values: Base[]; construct(values: Base[]) { this.values = values; } public property Value: int { get { this.values[0] = new Base(); return 0; } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<VmException>(() => Run(declarations + " function mutate(values: Base[]): int { values[0] = new Base(); return 0; } class Store { field values: Base[]; construct(values: Base[]) { this.values = values; } public property Value: int { get { return mutate(this.values); } } } function main(args: string[]): int { var store = new Store([new Base()]); return store.Value; }"));
    }

    [Test]
    public void ReferenceArrays_RuntimeGuardsAllowlistKindsDeadHandlesAndWrongNestedNames()
    {
        var vm = CreateVm();
        var derived = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Derived"));
        var other = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Other"));
        foreach (var name in new[] { "class:[]", "class:Base|[]", "class:Base|Base[]", "class:1Base[]", "class:Base.Other[]" })
        {
            vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(0));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        }
        foreach (var value in new[] { other, AloeValue.Null, AloeValue.FromInt(1), AloeValue.FromObject(long.MaxValue) })
        {
            vm.Push(value); vm.Push(AloeValue.FromString("class:Base|Derived[]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        }
        var values = CreateArray(vm, "class:Base|Derived[]", derived);
        vm.Push(values); vm.Push(AloeValue.FromInt(0)); vm.Push(other);
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArraySet));
        vm.Push(values); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.ArrayGet);
        Assert.That(vm.Pop().AsObjectId, Is.EqualTo(derived.AsObjectId));
        var objects = CreateArray(vm, "object[]", derived, values);
        Assert.That(vm.Heap.GetEntry(objects.AsObjectId).OutgoingReferences.Count, Is.EqualTo(2));
        vm.Push(objects); vm.Push(AloeValue.FromString("class:Base|Derived[][]")); vm.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        vm.Heap.SetField(values.AsObjectId, 0, other);
        vm.Push(values); vm.Push(AloeValue.FromInt(0));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayGet));
    }

    [Test]
    public void ReferenceArrays_DuplicateEdgesAndUnrootedCyclesAreCollected()
    {
        var vm = CreateVm();
        var box = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Box"));
        var outer = CreateArray(vm, "class:Box[]", box, box);
        vm.Heap.SetExternalReferenceCount(outer.AsObjectId, 1);
        vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(box.AsObjectId), Is.True);
        Assert.That(vm.Heap.GetEntry(outer.AsObjectId).OutgoingReferences[box.AsObjectId], Is.EqualTo(2));
        vm.Heap.SetExternalReferenceCount(outer.AsObjectId, 0); vm.RequireGc(); vm.FinishGc();
        // A subsequent graph pass removes the child group after the parent group is swept.
        vm.RequireGc(); vm.FinishGc();
        Assert.That(vm.Heap.ContainsObject(box.AsObjectId), Is.False);
        var item = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Box"));
        var cycle = CreateArray(vm, "object[]", item); vm.Heap.SetExternalReferenceCount(cycle.AsObjectId, 1);
        vm.Push(cycle); vm.Push(AloeValue.FromInt(0)); vm.Push(cycle); vm.InvokeSyscall(EnumSyscall.ArraySet);
        vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(cycle.AsObjectId), Is.True);
        Assert.That(vm.Heap.ContainsObject(item.AsObjectId), Is.False);
        vm.Heap.SetExternalReferenceCount(cycle.AsObjectId, 0); vm.RequireGc(); vm.FinishGc();
        Assert.That(vm.Heap.ContainsObject(cycle.AsObjectId), Is.False);
    }

    private static AloeValue CreateArray(AloeVm vm, string name, params AloeValue[] values)
    {
        foreach (var value in values) vm.Push(value);
        vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(values.Length)); vm.InvokeSyscall(EnumSyscall.ArrayCreate);
        return vm.Pop();
    }
    private static AloeVm CreateVm(Module? module = null)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module ?? new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }
    private static (long ExitCode, string Output) Run(string source, Action<AloeVm>? inspect = null)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); inspect?.Invoke(vm);
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
