using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class BoxingTests
{
    [Test]
    public void Boxing_DocumentedObjectArrayAndNonconsumingUnboxing()
    {
        const string source = """
function identity(value: object): object { return value; }
function main(args: string[]): int {
    let boxed: object = 42;
    var alias = identity(boxed);
    let values: object[] = [boxed, "Aloe", true];
    let first: int = values[0];
    var second = alias as int;
    if (boxed == alias and first == 42 and second == 42 and (values[1] as string) == "Aloe" and (values[2] as bool)) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Boxing_AllKindsImplicitBoundariesAndExplicitCastPreserveValues()
    {
        const string source = """
function boxed(value: object): object { return value; }
function integer(value: int): int { return value; }
function make(): object { return 42; }
function main(args: string[]): int {
    let b: byte = 255; let c: char = '字'; let flag: boolean = true;
    let values: object[] = [42, b, c, -0.0, 10.12345678901234567890:d, "字", flag];
    let n: int = values[0]; let bb: byte = values[1]; let cc: char = values[2];
    let f: float = values[3]; let d: decimal = values[4]; let s: string = values[5]; let ok: bool = values[6];
    var explicitBox = 42 as object; var roundTrip = explicitBox as int;
    let returned: int = make();
    if (n == 42 and bb == b and cc == c and f == -0.0 and d == 10.12345678901234567890:d and s == "字" and ok and integer(boxed(42)) == 42 and roundTrip == 42 and returned == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Boxing_FreshBoxesHaveDistinctIdentityWhileAliasesShare()
    {
        const string source = """
function main(args: string[]): int {
    let first: object = 42; let second: object = 42; var alias = first;
    var value = first as int; var again = first as int;
    if (first != second and first == alias and value == again) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Boxing_ArrayContextsWritesNestedForeachAndPrimitiveIndexes()
    {
        const string source = """
function main(args: string[]): int {
    let rows: object[][] = [[1, "text"], [true]]; rows[0][0] = 40;
    let index: object = 0; let values: int[] = [rows[index][0]]; values[0] = 42 as object;
    var total = 0;
    foreach (row in rows) { foreach (value in row) { total = total + (value as int ifnot 0); } }
    if (total == 40 and values[0] == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Boxing_UnboxingFailuresAreExactAndFallbackIsLazy()
    {
        const string source = """
function fallback(): int { print(99); return 42; }
function main(args: string[]): int {
    let integer: object = 42; let wrong: object = 1.5;
    var ok = integer as int ifnot fallback();
    var replaced = wrong as int ifnot fallback();
    var fromBox = wrong as int ifnot integer;
    if (ok == 42 and replaced == 42 and fromBox == 42 and integer == integer) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo("99\n"));
        foreach (var statement in new[] { "let value:float = 42 as object;", "let value:int = \"42\" as object;", "var value = (42 as object) as byte;", "var value = (1.5 as object) as decimal;", "let value:int = new Ordinary();" })
            Assert.Throws<InvalidCastException>(() => Run("class Ordinary {} function main(args:string[]):int { " + statement + " return 0; }"), statement);
    }

    [Test]
    public void Boxing_ClassFieldsAsyncArgumentsAndTickQueriesRetainBoxes()
    {
        const string source = """
class Store {
    field value: object;
    construct(value: object) { this.value = value; }
    public property Value: object { get { return this.value; } }
    public property Fresh: object { get { return 42; } }
    public async method update(value: object, query: Store): void {
        this.value = value; print(query.Value as int); print(this.value as int);
        print(query.Fresh as int);
    }
}
function main(args:string[]):int {
    var store = new Store(1); store.update(42, store);
    GC.require(); GC.finish(); tick();
    return store.Value;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo("1\n42\n42\n"));
    }

    [Test]
    public void Boxing_HeapEdgesRetainOnlyArrayReachableBoxesAcrossGc()
    {
        const string source = """
function main(args:string[]):int {
    let values:object[] = [42, "字"]; GC.require(); GC.finish();
    let n:int = values[0]; let s:string = values[1];
    if (n == 42 and s == "字") { return 42; } return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Vm.Heap.ObjectTable.Values.Count(x => x.TypeName?.StartsWith("box:", StringComparison.Ordinal) == true), Is.EqualTo(2));
    }

    [Test]
    public void Boxing_RawSyscallValidatesDomainAndPreservesPayloadBits()
    {
        var vm = CreateVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"));
        AloeValue[] values = [AloeValue.FromInt(int.MinValue), AloeValue.FromByte(255), AloeValue.FromChar('\ud800'), AloeValue.FromFloat(-0.0), AloeValue.FromDecimal(1.2300m), AloeValue.FromString("字"), AloeValue.FromBool(true)];
        foreach (var value in values)
        {
            vm.Push(value); vm.InvokeSyscall(EnumSyscall.PrimitiveBox); var box = vm.Pop();
            var entry = vm.Heap.GetEntry(box.AsObjectId); var stored = vm.Heap.GetCommittedField(box.AsObjectId, 0);
            Assert.That(entry.FieldCount, Is.EqualTo(1)); Assert.That(stored.Kind, Is.EqualTo(value.Kind));
            var name = entry.TypeName![4..]; vm.Push(box); vm.Push(AloeValue.FromString(name)); vm.InvokeSyscall(EnumSyscall.PrimitiveCast); var result = vm.Pop();
            Assert.That(result.Kind, Is.EqualTo(value.Kind)); Assert.That(result.ToString(), Is.EqualTo(value.ToString()));
            if (value.IsFloat) Assert.That(BitConverter.DoubleToInt64Bits(result.AsFloat), Is.EqualTo(BitConverter.DoubleToInt64Bits(value.AsFloat)));
            if (value.IsDecimal) Assert.That(decimal.GetBits(result.AsDecimal), Is.EqualTo(decimal.GetBits(value.AsDecimal)));
            Assert.That(vm.Heap.ContainsObject(box.AsObjectId), Is.True);
        }
        foreach (var value in new[] { AloeValue.Null, AloeValue.FromObject(long.MaxValue), AloeValue.FromInt((long)int.MaxValue+1), AloeValue.FromFloat(double.NaN), AloeValue.FromFloat(double.PositiveInfinity), AloeValue.FromStruct(new AloeStructValue("Empty", [])) })
        {
            var count = vm.Heap.ObjectTable.Count; vm.Push(value); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.PrimitiveBox)); Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(count));
        }
        Assert.That(vm.ValueStack.Count, Is.Zero);
    }

    [Test]
    public void Boxing_RawUnboxingRejectsDeadUninitializedAndCorruptBoxes()
    {
        var vm = CreateVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"));
        var wrongKind = vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount:1, typeName:"box:int"); vm.Heap.SetField(wrongKind,0,AloeValue.FromBool(true));
        var overflow = vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount:1, typeName:"box:int"); vm.Heap.SetField(overflow,0,AloeValue.FromInt((long)int.MaxValue+1));
        long[] handles = [long.MaxValue, wrongKind, overflow, vm.Heap.Allocate(ReadOnlySpan<byte>.Empty,fieldCount:1,typeName:"box:int"), vm.Heap.Allocate(ReadOnlySpan<byte>.Empty,fieldCount:2,typeName:"box:int"), vm.Heap.Allocate(ReadOnlySpan<byte>.Empty,fieldCount:1,typeName:"Integer")];
        foreach (var handle in handles)
        {
            vm.Push(AloeValue.FromObject(handle)); vm.Push(AloeValue.FromString("int")); vm.InvokeSyscall(EnumSyscall.PrimitiveTryCast);
            Assert.That(vm.Pop().AsBool, Is.False); Assert.That(vm.Pop().IsNull, Is.True);
            vm.Push(AloeValue.FromObject(handle)); vm.Push(AloeValue.FromString("int")); Assert.Throws<InvalidCastException>(() => vm.InvokeSyscall(EnumSyscall.PrimitiveCast));
        }
        Assert.That(vm.ValueStack.Count, Is.Zero);
    }

    [Test]
    public void Boxing_RejectsNominalTypesOpaqueBoxApiAndWasm()
    {
        const string declarations = "enum Color {Red} struct Point {field x:int;construct(x:int){this.x=x;}} class Box {}";
        foreach (var statement in new[] { "let value:object = Color.Red;", "let value:object = new Point(1);", "let value:object[] = [Color.Red];", "let value:Box = 42;", "let value:object = 42; var extracted = value.ToInt();" })
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args:string[]):int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args:string[]):int {let value:object = 42; return 0;}"));
    }

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }
    private static (long ExitCode, string Output, AloeVm Vm) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"), vm);
    }
}
