using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceCollectionTests
{
    [Test]
    public void InterfaceCollections_ArraysListsFieldsAndForeachPreserveDispatchThroughGc()
    {
        const string source = """
interface I { method emit(): void; }
interface IStore { method drain(extra: I[]): void; }
class Base implements I { public virtual async method emit(): void { print("base"); } }
class Derived extends Base { public override async method emit(): void { print("derived"); } }
class Store implements IStore {
    field values: I[];
    construct(values: I[]) { this.values = values; }
    public async method drain(extra: I[]): void {
        foreach (value in this.values) { value.emit(); }
        foreach (value in extra) { value.emit(); }
    }
}
function identity(values: I[]): I[] { return values; }
function main(args: string[]): int {
    let item: I = new Derived();
    let values: I[] = [item, new Base()];
    let rows: I[][] = [values, []];
    let list: I() = (item, new Base());
    list.Add(item); list.RemoveAt(1);
    values[1] = item;
    foreach (value in identity(rows[0])) { value.emit(); }
    foreach (value in list) { value.emit(); }
    let store: IStore = new Store(values); store.drain(values);
    GC.require(); GC.finish(); tick();
    return values.Length + list.Length + rows.Length;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(6));
        Assert.That(result.Output, Is.EqualTo(string.Concat(Enumerable.Repeat("derived\n", 8))));
    }

    [Test]
    public void InterfaceCollections_SetAndMapUseReferenceIdentityAndTypedIteration()
    {
        const string source = """
interface I { method emit(message: string): void; }
class Item implements I { public async method emit(message: string): void { print(message); } }
function main(args: string[]): int {
    let a: I = new Item(); let b: I = new Item();
    let values: I{} = {a, a}; values.Add(b);
    let other: I{} = {a}; values.ExceptWith(other); values.UnionWith(other);
    print(values.Contains(a));
    foreach (value in values) { value.emit("set"); }
    let pairs: map<I/I> = [a: b]; pairs.Set(b, a);
    foreach (key, value in pairs) { key.emit("key"); value.emit("value"); }
    pairs.Remove(a); print(pairs.ContainsKey(a));
    GC.require(); GC.finish(); tick();
    return values.Count * 10 + pairs.Count;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(21));
        Assert.That(result.Output, Does.StartWith("true\nfalse\n"));
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2),
            Is.EquivalentTo(new[] { "set", "set", "key", "value", "key", "value" }));
    }

    [Test]
    public void InterfaceCollections_CastsRetainNominalTypeEvenWithIdenticalImplementers()
    {
        const string source = """
interface I { method emit(): void; }
interface J { method emit(): void; }
interface IUnused { method emit(): void; }
class C implements I, J { public async method emit(): void { } }
function main(args: string[]): int {
    let values: I[] = [new C()]; let reference: object = values;
    print(reference is I[]); print(reference is J[]);
    let alias: I[] = reference as I[];
    let fallback: J[] = reference as J[] ifnot [];
    let empty: IUnused[] = [];
    let emptyList: IUnused() = (); let emptySet: IUnused{} = {};
    let emptyMap: map<IUnused/IUnused> = [];
    return alias.Length + fallback.Length + empty.Length + emptyList.Length + emptySet.Count + emptyMap.Count;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Is.EqualTo("true\nfalse\n"));
    }

    [Test]
    public void InterfaceCollections_RuntimeRejectsNonconformingElementWithoutReplacingSlot()
    {
        var result = Run("interface I { method emit(): void; } class C implements I { public async method emit(): void { } } function main(args: string[]): int { let values: I[] = [new C()]; return 0; }");
        var vm = result.Vm;
        var values = vm.Heap.ObjectTable.Values.Single(entry => entry.TypeName == "class:I|C[]");
        var original = vm.Heap.GetField(values.ObjectId, 0).AsObjectId;
        var wrong = vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Wrong");
        vm.Push(AloeValue.FromObject(values.ObjectId)); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromObject(wrong));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArraySet));
        Assert.That(vm.Heap.GetField(values.ObjectId, 0).AsObjectId, Is.EqualTo(original));
    }

    [TestCase("let values: I[] = [new D()];")]
    [TestCase("let values: I() = (); values.Add(new D());")]
    [TestCase("let values: I{} = {}; values.Add(new D());")]
    [TestCase("let values: map<I/I> = []; values.Set(new D(), new C());")]
    [TestCase("let values: map<I/I> = []; values.Set(new C(), new D());")]
    [TestCase("let values: C[] = [new C()]; let aliases: I[] = values;")]
    [TestCase("let values: I[] = []; let aliases: J[] = values;")]
    public void InterfaceCollections_RejectNonconformingValuesAndCovariantAliases(string body)
    {
        var source = "interface I { method emit(): void; } interface J { method emit(): void; } class C implements I, J { public async method emit(): void { } } class D { } function main(args: string[]): int { " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    private static (long ExitCode, string Output, AloeVm Vm) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"), vm);
    }
}
