using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class MapTests
{
    [Test]
    public void Maps_PrimitiveAndEnumKeysUseTypedValueEquality()
    {
        const string source = """
enum Color { Red, Blue }
function main(args:string[]):int {
    let ints:map<int/string> = [1:"first",2:"two",1:"last"];
    let bools:map<bool/int> = [true:42,false:0];
    let bytes:map<byte/int> = [255:42];
    let chars:map<char/int> = ['字':42];
    let floats:map<float/int> = [1.5:42];
    let decimals:map<decimal/int> = [1.25:d:42];
    let enums:map<Color/int> = [Color.Red:41,Color.Blue:42];
    ints.Set(3,"three");ints.Remove(2);
    if(ints.Count==2 and ints[1]=="last" and ints[3]=="three" and ints.ContainsKey(1) and bools[true]==42 and bytes[255]==42 and bytes.ContainsKey(255) and chars['字']==42 and floats[1.5]==42 and decimals[1.25:d]==42 and enums[Color.Blue]==42){return 42;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("enum A{One}enum B{One}function main(args:string[]):int{let m:map<A/int>=[B.One:1];return 0;}"));
    }

    [Test]
    public void Maps_DocumentedShorthandDuplicatesAliasesAndMutation()
    {
        const string source = """
function main(args: string[]): int {
    let values: map<int> = ["first": 10, "answer": 20, "answer": 21];
    var alias = values;
    alias.Set("answer", 42);
    alias.Set("temporary", 9);
    values.Remove("temporary");
    values.Remove("missing");
    if (values.Count == 2 and values.ContainsKey("answer") and not values.ContainsKey("Answer") and alias == values) {
        return values["answer"];
    }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Maps_EmptyContextInferenceFunctionReturnAndCast()
    {
        const string source = """
function identity(values: map<string/int>): map<int> { return values; }
function empty(): map<int> { return []; }
function fallback(): map<int> { print(99); return ["x": 0]; }
function main(args: string[]): int {
    let none: map<int> = [];
    var inferred = ["x": 42];
    let erased: object = inferred;
    var recovered = erased as map<string/int> ifnot fallback();
    none = [];
    GC.require(); GC.finish();
    if (empty().Count == 0 and identity([]).Count == 0 and none.Count == 0 and recovered == inferred) {
        return (identity(recovered))["x"];
    }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.Empty);
        Assert.Throws<InvalidCastException>(() => Run("function main(args:string[]):int {let x:object=[\"x\":true];var bad=x as map<int>;return 0;}"));
    }

    [Test]
    public void Maps_AllScalarKindsAndNominalEnums()
    {
        const string source = """
enum Color { Red, Blue }
bitfield enum Flags { First = 1, Second = 2 }
function main(args: string[]): int {
    let bytes: map<byte> = ["": 255];
    var chars = ["unicode": '字']; var floats = ["x": 1.5]; var decimals = ["x": 1.25:d];
    var strings = ["x": "Aloe"]; var flags = ["x": true];
    let colors: map<Color> = ["x": Color.Red]; colors.Set("x", Color.Blue);
    var masks = ["x": Flags.First | Flags.Second];
    bytes.Set("", 42);
    if (bytes[""] == 42 and chars["unicode"] == '字' and floats["x"] == 1.5 and decimals["x"] == 1.25:d and strings["x"] == "Aloe" and flags["x"] and colors["x"] == Color.Blue and masks["x"] == (Flags.First | Flags.Second)) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Maps_LiteralsAndReadsEvaluateOnceInSourceOrder()
    {
        const string source = """
function key(): string { print(1); return "x"; }
function value(): int { print(2); return 42; }
function make(): map<int> { print(3); return [key(): value(), key(): value()]; }
function main(args: string[]): int { return make()[key()]; }
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("3\n1\n2\n1\n2\n1\n"));
    }

    [Test]
    public void Maps_TickSnapshotsClassPropertiesAndAsyncArguments()
    {
        const string source = """
class Store {
    field values: map<int>; construct() { this.values = ["x": 1]; }
    public property Values: map<int> { get { return this.values; } }
    public property Current: int { get { return this.values["x"]; } }
    public property Count: int { get { return this.values.Count; } }
    public property HasNew: bool { get { return this.values.ContainsKey("new"); } }
    public async method update(values: map<int>, query: Store): void {
        values.Set("x", 42); values.Set("new", 9);
        print(query.Current); print(query.Count); print(query.HasNew);
        print(values["x"]); print(values.Count);
    }
}
function main(args: string[]): int {
    var store = new Store(); store.update(store.Values, store);
    GC.require(); GC.finish(); tick();
    if (store.Values.Count == 2 and store.Values.ContainsKey("new")) { return store.Values["x"]; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n1\nfalse\n42\n2\n"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("class C {field m:map<int>;construct(){this.m=[];}public property N:int {get{var m=this.m;m.Set(\"x\",2);return 0;}}}function main(args:string[]):int{return 0;}"));
        Assert.Throws<VmException>(() => Run("function change(m:map<int>):int{m.Set(\"x\",2);return 0;}class C {field m:map<int>;construct(){this.m=[];}public property N:int {get{return change(this.m);}}}function main(args:string[]):int{var c=new C();return c.N;}"));
    }

    [Test]
    public void Maps_RejectUnsupportedTypesApisAndMissingKeys()
    {
        var statements = new[] {
            "let m:map<object/int> = [];", "let m:map<int>[] = [];",
            "var m=[new C():42];", "var m=[\"x\":1,\"y\":true];", "let m:map<byte> = [\"x\":256];",
            "var m=[\"x\":1];m[\"x\"]=2;", "var m=[\"x\":1];m.Count=2;", "var m=[\"x\":1];m.Set(1,2);",
            "var m=[\"x\":1];m.Set(\"x\",true);", "let m:map<int> =[1];", "let m:int[]=[\"x\":1];",
            "var m=[\"x\":1];return m[0];"
        };
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args:string[]):int{" + statement + "return 0;}"), statement);
        Assert.Throws<VmException>(() => Run("function main(args:string[]):int{var m=[\"x\":1];return m[\"missing\"];}"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("enum A{One}enum B{One}function main(args:string[]):int{let m:map<A> = [\"x\":B.One];return 0;}"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args:string[]):int{let m:map<int> = [];return 0;}"));
    }

    [Test]
    public void Maps_ClassReferenceValuesRetainIdentityAndGcEdges()
    {
        const string source = """
class Item { field n:int; construct(n:int){this.n=n;} public property N:int{get{return this.n;}} }
function main(args:string[]):int {
    var first=new Item(20);var second=new Item(22);
    let values:map<Item> =["first":first,"second":second];
    var alias=values;GC.require();GC.finish();
    let read:Item=alias["first"];let secondRead:Item=alias["second"];
    if(read==first and alias==values and alias.Count==2){return read.N+secondRead.N;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));

        const string objectSource = """
class Item { public property N:int { get { return 42; } } }
function main(args:string[]):int {
    let values:map<object> =["item":new Item() as object];
    var item=values["item"] as Item;
    GC.require();GC.finish();return item.N;
}
""";
        Assert.That(Run(objectSource).ExitCode, Is.EqualTo(42));
        Assert.That(Run("class C{}function main(args:string[]):int{let m:map<C/int> = [];return m.Count;}" ).ExitCode,Is.EqualTo(0));
    }

    [Test]
    public void Maps_ForeachVisitsKeysInInsertionOrderAndSupportsEnumKeys()
    {
        const string source = """
enum Color { Red, Blue, Green }
function main(args:string[]):int {
    let values:map<int> =["a":1,"b":2,"a":3,"c":4];
    var sum=0;foreach(key in values){sum=sum+values[key];}
    let colors:map<Color/int> =[Color.Blue:20,Color.Red:22];
    foreach(color in colors){sum=sum+colors[color];}
    return sum;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(51));

        var vm = CreateVm();
        vm.Push(AloeValue.FromString("a")); vm.Push(AloeValue.FromInt(42));
        vm.Push(AloeValue.FromString("map:string[]|int[]")); vm.Push(AloeValue.FromInt(1));
        vm.InvokeSyscall(EnumSyscall.MapCreate); var map = vm.Pop();
        vm.Push(map); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.MapKeyAt);
        Assert.That(vm.Pop().AsString, Is.EqualTo("a"));
        vm.Push(map); vm.Push(AloeValue.FromInt(1)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.MapKeyAt));
    }

    [Test]
    public void Maps_ForeachVisitsKeyValuePairsInInsertionOrder()
    {
        const string source = """
function main(args:string[]):int {
    let values:map<int> = ["a":1,"b":2,"a":3,"c":4];
    foreach(key,value in values) { print(key); print(value); }
    return 42;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("a\n3\nb\n2\nc\n4\n"));

        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args:string[]):int{let values=[1,2];foreach(index,value in values){}return 0;}"));
    }

    [Test]
    public void Maps_ReferenceValuesPreserveCommittedSnapshotAcrossTick()
    {
        const string source = """
class Item { field n:int; construct(n:int){this.n=n;} public property N:int{get{return this.n;}} }
class Store {
    field values:map<Item>;
    construct(value:Item){this.values=["x":value];}
    public property Values:map<Item>{get{return this.values;}}
    public property OldN:int{get{let old:Item=this.values["x"];return old.N;}}
    public async method replace(values:map<Item>,query:Store,next:Item):void{
        values.Set("x",next);print(query.OldN);let current:Item=values["x"];print(current.N);
    }
}
function main(args:string[]):int{
    var store=new Store(new Item(5));var next=new Item(42);
    store.replace(store.Values,store,next);GC.require();GC.finish();tick();
    let result:Item=store.Values["x"];return result.N;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("5\n42\n"));
    }

    [Test]
    public void Maps_ClassReferenceKeysUseIdentityAndRemainGcRoots()
    {
        const string source = """
class Key { public property N:int{get{return 20;}} }
class ChildKey extends Key { }
function main(args:string[]):int {
    var first=new Key();var alias=first;var equalButDistinct=new Key();let child:Key=new ChildKey();
    let values:map<Key/int> = [first:1,alias:42,equalButDistinct:2,child:3];
    var mapped=values;GC.require();GC.finish();
    if(values.Count!=3 or values[first]!=42 or not values.ContainsKey(child)){return 0;}
    values.Remove(equalButDistinct);delete first;GC.require();GC.finish();
    var total=0;foreach(key in mapped){total=total+key.N;}
    if(mapped.Count==2 and mapped.ContainsKey(alias)){return total+mapped.Count;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int{let values:map<object/int> = [];return 0;}"));
    }

    [Test]
    public void Maps_RawSyscallsValidateDescriptorsKeysAndValueRanges()
    {
        var vm = CreateVm();
        vm.Push(AloeValue.FromString("x")); vm.Push(AloeValue.FromInt(42));
        vm.Push(AloeValue.FromString("map:string[]|int[]")); vm.Push(AloeValue.FromInt(1)); vm.InvokeSyscall(EnumSyscall.MapCreate);
        var map = vm.Pop();
        vm.Push(map); vm.Push(AloeValue.FromString("x")); vm.InvokeSyscall(EnumSyscall.MapGet);
        Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        vm.Push(map); vm.Push(AloeValue.FromInt(1)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.MapGet));
        vm.Push(map); vm.Push(AloeValue.FromString("x")); vm.Push(AloeValue.FromInt((long)int.MaxValue + 1));
        Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.MapSet));
        vm.Push(map); vm.Push(AloeValue.FromString("map:string[]|int[]")); vm.InvokeSyscall(EnumSyscall.ObjectCast); Assert.That(vm.Pop(), Is.EqualTo(map));
        vm.Push(AloeValue.FromString("map:object[]")); vm.Push(AloeValue.FromInt(0)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.MapCreate));
        var bad = vm.Heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: 1, typeName: "map:int[]");
        vm.Push(AloeValue.FromObject(bad)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.MapCount));
        Assert.That(vm.ValueStack.Count, Is.Zero);
    }

    private static AloeVm CreateVm(Module? module = null)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module ?? new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"), settings);
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var vm = CreateVm(AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source))));
        var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
