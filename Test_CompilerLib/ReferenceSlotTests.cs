using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ReferenceSlotTests
{
    [Test]
    public void ReferenceSlots_DocumentedElementUnboxAndIdempotentDelete()
    {
        const string source = """
function main(args: string[]): int {
    let values: Integer[] = [42];
    var alias = values[0];
    let value: int = unbox values[0];
    delete values[0];
    if (values[0] == invalid and alias.ToInt() == value) { return value; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceSlots_CaptureReceiverAndIndexOnceBeforeSuccessfulClear()
    {
        const string source = """
function receiver(values:Integer[]):Integer[] {print(1);return values;}
function index():int {print(2);return 0;}
function main(args:string[]):int {
    let values:Integer[]=[42];let n:int=unbox receiver(values)[index()];
    delete receiver(values)[index()];
    if(values[0]==invalid){return n;}return 0;
}
""";
        var collections=0;
        var result=Run(source,vm=>{vm.RequireGc();vm.FinishGc();collections++;});Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("1\n2\n1\n2\n"));Assert.That(collections,Is.EqualTo(4));
    }

    [Test]
    public void ReferenceSlots_NestedClassObjectAndNamedBoxElementsRestoreByAssignment()
    {
        const string source = """
class Box {public property Value:int {get{return 42;}}}
function main(args:string[]):int {
    let rows:Integer[][]=[[42]];var rowAlias=rows[0];delete rows[0];delete rows[0];
    if(rows[0]!=invalid or rowAlias[0].ToInt()!=42){return 0;}rows[0]=rowAlias;
    let mixed:object[]=[true,"text"];let flag:bool=unbox mixed[0];let text:string=unbox mixed[1];
    let objects:Box[]=[new Box()];var boxAlias=objects[0];delete objects[0];
    if(mixed[0]==invalid and mixed[1]==invalid and objects[0]==invalid and boxAlias.Value==42 and flag and text=="text"){return 42;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {let rows:int[][]=[[42]];delete rows[0];return rows[0][0];}"));
    }

    [Test]
    public void ReferenceSlots_FieldAndWithFieldUnboxingRetainIndependentAliases()
    {
        const string executable = """
class Store {
    field value:Integer;
    construct(value:Integer){this.value=value;}
    public property Invalid:bool {get{return this.value==invalid;}}
    public async method consume():void {let n:int=unbox this.value;print(n);delete this.value;with(this){delete .value;.value=42;let again:int=unbox .value;print(again);}}
}
function main(args:string[]):int {var alias=new Integer(42);var store=new Store(alias);store.consume();tick();if(store.Invalid and alias.ToInt()==42){return 42;}return 0;}
""";
        var result=Run(executable);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("42\n42\n"));
    }

    [Test]
    public void ReferenceSlots_FailedExtractionLeavesElementAndFieldUnchanged()
    {
        var array=CreateVm("function main(args:string[]):int {let values:object[]=[1.5];let n:int=unbox values[0];return 0;}");
        Assert.Throws<InvalidCastException>(()=>array.RunFromEntryPoint());
        var owner=array.CurrentFrame!.Locals[0];array.Push(owner);array.Push(AloeValue.FromInt(0));array.InvokeSyscall(EnumSyscall.ArrayGet);Assert.That(array.Pop().IsObject,Is.True);
        var field=CreateVm("class Store {field value:object;construct(value:object){this.value=value;let n:int=unbox this.value;}}function main(args:string[]):int {var store=new Store(1.5);return 0;}");
        Assert.Throws<InvalidCastException>(()=>field.RunFromEntryPoint());
        var store=field.Heap.ObjectTable.Values.Single(x=>x.TypeName=="Store");field.Push(AloeValue.FromObject(store.ObjectId));field.Push(AloeValue.FromInt(0));field.InvokeSyscall(EnumSyscall.ReferenceFieldGet);Assert.That(field.Pop().IsObject,Is.True);
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {let values:Integer[]=[42];delete values[0];let n:int=unbox values[0];return 0;}"));
    }

    [Test]
    public void ReferenceSlots_TickUsesVolatileInvalidationAndCommittedQueries()
    {
        const string source = """
class Store {
    field value:Integer;field values:Integer[];
    construct(){this.value=new Integer(41);this.values=[42];}
    public property FieldInvalid:bool {get{return this.value==invalid;}}
    public property ElementInvalid:bool {get{return this.values[0]==invalid;}}
    public async method consume(query:Store):void {
        let a:int=unbox this.value;let b:int=unbox this.values[0];print(a+b);
        print(this.value==invalid);print(query.FieldInvalid);print(this.values[0]==invalid);print(query.ElementInvalid);
    }
}
function main(args:string[]):int {var store=new Store();store.consume(store);tick();GC.require();GC.finish();if(store.FieldInvalid and store.ElementInvalid){return 42;}return 0;}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("83\ntrue\nfalse\ntrue\nfalse\n"));
        Assert.That(result.Vm.Heap.ObjectTable.Values.Any(x=>x.TypeName=="box:int"),Is.False);
    }

    [Test]
    public void ReferenceSlots_ReadonlyQueriesRejectDirectAndTransitiveHeapConsumption()
    {
        foreach(var operation in new[]{"delete this.value;return 0;","let n:int=unbox this.value;return n;","delete this.values[0];return 0;","let n:int=unbox this.values[0];return n;"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Store {field value:Integer;field values:Integer[];construct(){this.value=new Integer(42);this.values=[42];}public property Value:int {get{"+operation+"}}}function main(args:string[]):int {return new Store().Value;}"));
        Assert.Throws<VmException>(()=>Run("function consume(values:Integer[]):int {let n:int=unbox values[0];return n;}class Store {field values:Integer[];construct(){this.values=[42];}public property Value:int {get{return consume(this.values);}}}function main(args:string[]):int {return new Store().Value;}"));
    }

    [Test]
    public void ReferenceSlots_RejectPropertiesPrimitiveSlotsAndTemporaryReferences()
    {
        foreach(var body in new[]{"var values=[42];delete values[0];","var values=[true];let n:bool=unbox values[0];","delete new Integer(42);","let n:int=unbox new Integer(42);","var box=new Integer(42);delete box.ToInt();"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+body+"return 0;}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("class Store {public property Value:Integer {get{return new Integer(42);}}}function main(args:string[]):int {var store=new Store();delete store.Value;return 0;}"));
    }

    [Test]
    public void ReferenceSlots_RawClearAndReadsValidateKindBoundsAndStack()
    {
        var vm=CreateVm("function main(args:string[]):int{return 0;}");
        vm.Push(AloeValue.FromInt(42));vm.InvokeSyscall(EnumSyscall.PrimitiveBox);var box=vm.Pop();
        vm.Push(box);vm.Push(AloeValue.FromString("box:int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var array=vm.Pop();
        foreach(var id in new[]{EnumSyscall.ReferenceArrayInvalidate,EnumSyscall.ReferenceArrayInvalidate}) {vm.Push(array);vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(id);}
        vm.Push(array);vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ArrayGet);Assert.That(vm.Pop().IsNull,Is.True);
        var owner=vm.Heap.Allocate(ReadOnlySpan<byte>.Empty,fieldCount:1,typeName:"Store");vm.Heap.SetField(owner,0,box);
        vm.Push(AloeValue.FromObject(owner));vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ReferenceFieldInvalidate);
        vm.Push(AloeValue.FromObject(owner));vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ReferenceFieldGet);Assert.That(vm.Pop().IsNull,Is.True);
        vm.Push(array);vm.Push(AloeValue.FromInt(1));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ReferenceArrayInvalidate));
        vm.Push(box);vm.Push(AloeValue.FromInt(0));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ReferenceFieldInvalidate));
        vm.Push(AloeValue.Null);vm.Push(AloeValue.FromInt(0));Assert.Throws<InvalidReferenceException>(()=>vm.InvokeSyscall(EnumSyscall.ReferenceFieldGet));
        Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    private static AloeVm CreateVm(string source){var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;return new AloeVm(module,settings);}
    private static (long ExitCode,string Output,AloeVm Vm) Run(string source,Action<AloeVm>? onOutput=null){var vm=CreateVm(source);var output=new StringBuilder();vm.OutputWriter=line=>{output.AppendLine(line);onOutput?.Invoke(vm);};vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"),vm);}
}
