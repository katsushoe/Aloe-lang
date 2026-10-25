using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ReferenceCastTests
{
    [Test]
    public void ReferenceCasts_DocumentedClassArrayAndLazyFallbackKeepIdentity()
    {
        const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
}
class Other { }
function main(args: string[]): int {
    var box = new Box(42); let values: object[] = [box, [1, 2], new Other()];
    var typed = values[0] as Box;
    var row = values[1] as int[]; row[0] = 42;
    var fallback = values[2] as Box ifnot box;
    if (typed == box and fallback == box and typed.Value == 42 and (values[1] as int[])[0] == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceCasts_BaseDerivedChainingGenericObjectAndPostfixGetters()
    {
        const string source = """
class Base { public property Value: int { get { return 42; } } }
class Derived extends Base { }
function main(args: string[]): int {
    var derived = new Derived(); let parent: Base = derived;
    let erased: object = parent as object;
    var back = erased as Base as Derived;
    with (back) { print(.Value); }
    GC.require(); GC.finish();
    if (back == derived and (erased as Base).Value == 42) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("42\n"));
    }

    [Test]
    public void ReferenceCasts_FallbackIsLazyAndOperandsRunOnce()
    {
        const string source = """
class Box { }
class Other { }
function operand(value: object, number: int): object { print(number); return value; }
function fallback(value: Box, number: int): Box { print(number); return value; }
function main(args: string[]): int {
    var box = new Box(); var other = new Other();
    var good = operand(box, 1) as Box ifnot fallback(box, 99);
    var bad = operand(other, 2) as Box ifnot fallback(box, 3);
    var chained = operand(other, 4) as Box ifnot (operand(other, 5) as Box ifnot fallback(box, 6));
    if (good == box and bad == box and chained == box) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n3\n4\n5\n6\n"));
    }

    [Test]
    public void ReferenceCasts_ArraysNestedNominalAndTypedEmptyFallbacksPreserveReferences()
    {
        const string source = """
enum Color { Red, Blue }
struct Point { field x: int; }
class Box { }
function identity(value: object): object { return value; }
function main(args: string[]): int {
    var colors = [Color.Red]; var points = [[Point { x: 1 }]]; var boxes = [new Box()];
    var strings = ["Aloe"]; let mixed: object[] = [colors, points, boxes, strings];
    (mixed[0] as Color[])[0] = Color.Blue;
    (mixed[1] as Point[][])[0][0].x = 42;
    var empty = identity(colors) as int[] ifnot [];
    var generic = mixed as object as object[];
    GC.require(); GC.finish();
    if (generic == mixed and empty.Length == 0 and colors[0] == Color.Blue and points[0][0].x == 42 and
        (mixed[2] as Box[])[0] == boxes[0] and (mixed[3] as string[])[0] == "Aloe") { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceCasts_GettersAndTickCastsUseExistingCommittedReferences()
    {
        const string source = """
class Box {
    field value: int;
    construct(value: int) { this.value = value; }
    public property Value: int { get { return this.value; } }
}
class Store {
    field values: object[];
    construct(values: object[]) { this.values = values; }
    public property First: Box { get { return this.values[0] as Box; } }
    public async method update(next: Box, query: Store): void {
        this.values[0] = next; var old = query.First; print(old.Value); print((this.values[0] as Box).Value);
    }
}
function main(args: string[]): int {
    var first = new Box(1); var next = new Box(42); let values: object[] = [first]; var store = new Store(values);
    store.update(next, store); GC.require(); GC.finish(); tick();
    if (store.First == next and (values[0] as Box).Value == 42) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void ReferenceCasts_RuntimeFailuresUseInvalidCastAndArraysRemainInvariant()
    {
        const string declarations = "class Base {} class Derived extends Base {} class Other {}";
        string[] statements =
        [
            "var value = new Other(); var result = value as Base;",
            "var value = new Other(); var result = value as Base ifnot throw;",
            "var values = [new Derived()]; var result = values as Base[];",
            "var values = [1]; var result = values as bool[];",
            "var values = [[1]]; var result = values as int[];"
        ];
        foreach (var statement in statements)
            Assert.Throws<InvalidCastException>(() => Run(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        var result = Run(declarations + " function main(args: string[]): int { var original = [new Derived()]; var fallback = [new Base()]; var value = original as Base[] ifnot fallback; if (value == fallback) { return 42; } return 0; }");
        Assert.That(result.ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ReferenceCasts_RejectValueConversionsUnknownTargetsAndWrongFallbackTypes()
    {
        const string declarations = "class Box {} class Other {} struct Point { field x: int; } enum Color { Red }";
        string[] statements =
        [
            "var value = new Box() as Point;", "var value = new Box() as Color;",
            "var value = new Box() as Missing;", "var value = new Box() as pipe<int>;",
            "var value = new Other() as Box ifnot 42;", "var value = new Other() as Box ifnot new Other();",
            "var value = new Box() as int[] ifnot [true];"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("class Box {} function main(args: string[]): int { var value = new Box() as object; return 0; }"));
    }

    [Test]
    public void ReferenceCasts_RawSyscallsValidateDescriptorsLiveKindsAndTryStackShape()
    {
        var vm = CreateVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"));
        var derived = AloeValue.FromObject(vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "Derived"));
        Cast(vm, derived, "class:Base|Derived", EnumSyscall.ObjectCast);
        Assert.That(vm.Pop().AsObjectId, Is.EqualTo(derived.AsObjectId));
        Cast(vm, derived, "object", EnumSyscall.ObjectTryCast);
        Assert.That(vm.Pop().AsBool, Is.True); Assert.That(vm.Pop().AsObjectId, Is.EqualTo(derived.AsObjectId));
        foreach (var value in new[] { AloeValue.Null, AloeValue.FromInt(1), AloeValue.FromObject(long.MaxValue), derived })
        {
            Cast(vm, value, "class:Other", EnumSyscall.ObjectTryCast);
            Assert.That(vm.Pop().AsBool, Is.False); Assert.That(vm.Pop().IsNull, Is.True);
        }
        foreach (var name in new[] { "class:", "class:Base|Base", "array:bad[]", "number:int" })
            Assert.Throws<VmException>(() => Cast(vm, derived, name, EnumSyscall.ObjectTryCast), name);
        Assert.Throws<InvalidCastException>(() => Cast(vm, AloeValue.FromObject(long.MaxValue), "object", EnumSyscall.ObjectCast));
        var arrayId = vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, typeName: "int[]");
        Cast(vm, AloeValue.FromObject(arrayId), "array:int[]", EnumSyscall.ObjectCast);
        Assert.That(vm.Pop().AsObjectId, Is.EqualTo(arrayId)); Assert.That(vm.ValueStack.Count, Is.EqualTo(0));
    }

    [Test]
    public void ReferenceCasts_FallbackReferencesRemainRootsAcrossHostGc()
    {
        const string source = """
class Box { public property Value: int { get { return 42; } } }
class Other { }
function fallback(value: Box): Box { print(1); return value; }
function main(args: string[]): int {
    var box = new Box(); var other = new Other();
    var result = other as Box ifnot fallback(box);
    if (result == box and result.Value == 42) { return 42; }
    return 0;
}
""";
        var collections = 0;
        var result = Run(source, (vm, _) => { vm.RequireGc(); vm.FinishGc(); collections++; });
        Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(collections, Is.EqualTo(1));
    }

    private static void Cast(AloeVm vm, AloeValue value, string name, EnumSyscall syscall)
    {
        vm.Push(value); vm.Push(AloeValue.FromString(name)); vm.InvokeSyscall(syscall);
    }
    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }
    private static (long ExitCode, string Output) Run(string source, Action<AloeVm, string>? onOutput = null)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder();
        vm.OutputWriter = line => { output.AppendLine(line); onOutput?.Invoke(vm, line); };
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
