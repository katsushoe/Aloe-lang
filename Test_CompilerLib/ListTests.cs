using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ListTests
{
    [Test]
    public void Sets_IntegerLiteralMutationAliasesCountAndForeach()
    {
        const string source = """
function main(args: string[]): int {
    let values: int{} = {10, 10, 32};
    var alias = values;
    alias.Add(1);
    alias.Add(1);
    values.Remove(10);
    var total = 0;
    foreach (value in values) { total = total + value; }
    if (values.Count == 2 and values.Contains(32) and not values.Contains(10) and alias == values) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(33));
    }

    [Test]
    public void Sets_EmptyContextInferenceAndFunctionRoundTrip()
    {
        const string source = """
function identity(values: int{}): int{} { return values; }
function main(args: string[]): int {
    let empty: int{} = {};
    var inferred = {40, 2, 40};
    let returned: int{} = identity(empty);
    if (empty.Count == 0 and returned == empty and inferred.Count == 2 and inferred.Contains(2)) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Sets_ClearEmptiesTheSharedHandleAndAllowsReuse()
    {
        const string source = """
function main(args:string[]):int {
    let values:int{}={1,2,2};var alias=values;
    values.Clear();
    if(alias.Count!=0 or values!=alias){return 0;}
    alias.Add(40);alias.Add(2);alias.Clear();alias.Clear();
    values.Add(42);
    if(values.Count==1 and alias.Contains(42)){return 42;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Sets_ClearPreservesCommittedTickSnapshotAndQueryGuard()
    {
        const string source = """
class Store {
    field values:int{};construct(){this.values={1,2};}
    public property Snapshot:int {get{return this.values.Count;}}
    public async method clear(query:Store):void {var values=this.values;values.Clear();print(query.Snapshot);print(this.values.Count);}
}
function main(args:string[]):int {var store=new Store();store.clear(store);tick();return store.Snapshot;}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(0));Assert.That(result.Output,Is.EqualTo("2\n0\n"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class C {field values:int{};construct(){this.values={1};}public property N:int {get{this.values.Clear();return 0;}}}function main(args:string[]):int{return 0;}"));
    }

    [Test]
    public void Sets_AlgebraMutatesAliasesAndPreservesInsertionOrder()
    {
        const string source = """
function main(args:string[]):int {
    let union:int{}={1,2,3};var unionAlias=union;var right={3,4};union.UnionWith(right);
    let intersection:int{}={4,3,2,1};intersection.IntersectWith(right);
    let difference:int{}={4,3,2,1};difference.ExceptWith(right);
    var unionOrder=0;foreach(value in unionAlias){unionOrder=unionOrder*10+value;}
    var intersectionOrder=0;foreach(value in intersection){intersectionOrder=intersectionOrder*10+value;}
    var differenceOrder=0;foreach(value in difference){differenceOrder=differenceOrder*10+value;}
    if(union==unionAlias and unionOrder==1234 and intersectionOrder==43 and differenceOrder==21 and union.Count==4){return 42;}
    return 0;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42),result.Output);
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int{let a:int{}={1};let b:string{}={\"x\"};a.UnionWith(b);return 0;}"));
    }

    [Test]
    public void Sets_AlgebraPreservesCommittedTickSnapshotAndHandlesSelfOperand()
    {
        const string source = """
class Store {
    field values:int{};field other:int{};construct(){this.values={1};this.other={2};}
    public property Snapshot:int {get{return this.values.Count;}}
    public async method merge(query:Store):void {var values=this.values;values.UnionWith(this.other);print(query.Snapshot);print(this.values.Count);}
}
function main(args:string[]):int {
    var store=new Store();store.merge(store);tick();
    let values:int{}={4,5};values.UnionWith(values);values.IntersectWith(values);values.ExceptWith(values);
    if(values.Count==0 and store.Snapshot==2){return 42;}return 0;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("1\n2\n"));
    }

    [Test]
    public void Lists_DocumentedLiteralAliasesIndexesAndForeach()
    {
        const string source = """
function main(args: string[]): int {
    let values: int() = (20, 21);
    var alias = values;
    alias[1] = 22;
    var total = 0;
    foreach (value in values) { total = total + value; }
    if (values.Length == 2 and alias == values) { return total; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void Lists_EmptySingletonGroupingAndTypedFunctionContexts()
    {
        const string source = """
function identity(values:int()):int(){return values;}
function empty():int(){return ();}
function main(args:string[]):int {
    let none:int()=();var single=(42,);let assigned:int()=(1,2);assigned=();
    if(none.Length==0 and assigned.Length==0 and empty().Length==0 and identity(()).Length==0 and single.Length==1){return identity(single)[0]+(1+1) - 2;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void Lists_AllPrimitiveKindsEnumAndStructSnapshots()
    {
        const string source = """
enum Color {Red,Blue}
struct Point {field x:int;}
function main(args:string[]):int {
    let bytes:byte()=(255,);var chars=('字',);var floats=(1.5,);var decimals=(1.25:d,);var text=("Aloe",);var flags=(true,false);
    let colors:Color()=(Color.Red,Color.Blue);var points=(Point {x:41},);var original=points[0];points[0].x=42;
    if(bytes[0]==255 and chars[0]=='字' and floats[0]==1.5 and decimals[0]==1.25:d and text[0]=="Aloe" and flags[0] and colors[1]==Color.Blue and original.x==41){return points[0].x;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void Lists_ClassObjectNamedBoxReferencesGcAndConsumption()
    {
        const string source = """
class Item {field n:int;construct(n:int){this.n=n;}public property N:int {get{return this.n;}}}
function main(args:string[]):int {
    let items:Item()=(new Item(42),);let boxes:Integer()=(40,2);let erased:object()=(true,"Aloe");
    var alias=boxes[0];GC.require();GC.finish();let first:int=unbox boxes[0];delete boxes[0];boxes[0]=first;
    if(items[0].N==42 and alias==40 and boxes[0]==40 and erased[0]==true and erased[1]=="Aloe"){return boxes[0]+boxes[1];}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void Lists_ExpressionReceiversCapturedOnceAndForeachSurvivesReassignment()
    {
        const string source = """
function values():int(){print(1);return (20,22);}
function index():int{print(2);return 0;}
function main(args:string[]):int {
    values()[index()]=42;var list=(20,22);var total=0;
    foreach(value in list){list=(0,);total=total+value;}
    return total;
}
""";
        var r=Run(source);Assert.That(r.ExitCode,Is.EqualTo(42));Assert.That(r.Output,Is.EqualTo("1\n2\n"));
    }

    [Test]
    public void Lists_TickCommittedQueriesAndTransitiveWriteGuard()
    {
        const string source = """
class Store {
    field values:int();construct(){this.values=(1,);}
    public property Values:int(){get{return this.values;}}
    public property Current:int {get{return this.values[0];}}
    public async method update(query:Store):void {this.values[0]=42;print(query.Current);print(this.values[0]);}
}
function main(args:string[]):int {var store=new Store();store.update(store);tick();return store.Values[0];}
""";
        var r=Run(source);Assert.That(r.ExitCode,Is.EqualTo(42));Assert.That(r.Output,Is.EqualTo("1\n42\n"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class C {field xs:int();construct(){this.xs=(1,);}public property N:int {get{this.xs[0]=2;return 0;}}}function main(args:string[]):int{return 0;}"));
        Assert.Throws<VmException>(()=>Run("function change(xs:int()):int{xs[0]=2;return 0;}class C {field xs:int();construct(){this.xs=(1,);}public property N:int {get{return change(this.xs);}}}function main(args:string[]):int {var c=new C();return c.N;}"));
    }

    [Test]
    public void Lists_ExplicitReferenceCastsAreExactAndArrayTypesRemainDistinct()
    {
        const string source = """
function fallback():int(){print(99);return (42,);}
function main(args:string[]):int {
    var list=(42,);let erased:object=list;var recovered=erased as int() ifnot fallback();
    if(recovered==list){return recovered[0];}return 0;
}
""";
        var r=Run(source);Assert.That(r.ExitCode,Is.EqualTo(42));Assert.That(r.Output,Is.Empty);
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let erased:object=[42];var list=erased as int();return 0;}"));
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let erased:object=(42,);var array=erased as int[];return 0;}"));
    }

    [Test]
    public void Lists_RejectBadKindsNestedTypesUnsupportedApisAndInvalidAccess()
    {
        foreach(var statement in new[]{"var empty=();","let xs:int()=[1,2];","let xs:int[]=(1,2);","var xs=(1,true);","var xs=(1,2);xs.Length=3;","let xs:void()=();"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+statement+"return 0;}"),statement);
        Assert.Throws<VmException>(()=>Run("function main(args:string[]):int {var xs=(1,);return xs[1];}"));
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {var xs=(1,);delete xs;return xs[0];}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {var xs=(42,);return xs[0];}"));
    }

    [Test]
    public void Lists_AddAndRemoveAtResizeSharedAliasesAndPreserveTicks()
    {
        const string source = """
class Item { field value:int; construct(value:int){this.value=value;} public property Value:int {get{return this.value;}} }
class Mutator { field values:Item(); construct(values:Item()){this.values=values;} public property Count:int {get{return this.values.Length;}} public async method change(values:Item(), view:Mutator):void { values.Add(new Item(42)); print(view.Count); values.RemoveAt(0); } }
function main(args:string[]):int {
    var values=(new Item(20),);var alias=values;var worker=new Mutator(values);worker.change(values,worker);tick();GC.require();GC.finish();
    if(alias==values and values.Length==1){return alias[0].Value;}return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n"));
    }

    [Test]
    public void Lists_AddValidatesElementTypeAndRemoveAtValidatesBounds()
    {
        Assert.That(Run("function main(args:string[]):int {let xs:int()=();xs.Add(42);return xs[0];}").ExitCode, Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args:string[]):int {var xs=(1,);xs.Add(true);return 0;}"));
        Assert.Throws<VmException>(() => Run("function main(args:string[]):int {var xs=(1,);xs.RemoveAt(1);return 0;}"));
        Assert.Throws<VmException>(() => Run("function main(args:string[]):int {let xs:int()=();xs.RemoveAt(0);return 0;}"));
    }

    [Test]
    public void Lists_RawDescriptorsValidateLeafTypesAndSeparateIdentity()
    {
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"),settings);
        vm.Push(AloeValue.FromInt(42));vm.Push(AloeValue.FromString("list:int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var list=vm.Pop();
        Assert.That(vm.Heap.GetEntry(list.AsObjectId).TypeName,Is.EqualTo("list:int[]"));
        vm.Push(list);vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ArrayGet);Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
        vm.Push(list);vm.Push(AloeValue.FromString("array:int[]"));vm.InvokeSyscall(EnumSyscall.ObjectTryCast);Assert.That(vm.Pop().AsBool,Is.False);Assert.That(vm.Pop().IsNull,Is.True);
        vm.Push(AloeValue.FromBool(true));vm.Push(AloeValue.FromString("list:int[]"));vm.Push(AloeValue.FromInt(1));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ArrayCreate));
        Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    private static (long ExitCode,string Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(module,settings);var output=new StringBuilder();vm.OutputWriter=line=>output.AppendLine(line);
        vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));
    }
}
