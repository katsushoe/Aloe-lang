using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ReferenceValidityTests
{
    [Test]
    public void ReferenceValidity_DocumentedUnboxInvalidatesOnlyItsVariable()
    {
        const string source = """
function main(args: string[]): int {
    let source: object = 42;
    var alias = source;
    let value: int = unbox source;
    delete source;
    if (source == invalid and invalid != alias and value == 42 and (alias as int) == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_SevenKindsConsumeMatchingBoxes()
    {
        const string source = """
function main(args:string[]):int {
    var i=new Integer(42);var b=new Byte(255);var c=new Char('字');var f=new Float(-0.0);
    var d=new Decimal(1.2300:d);var s=new String("字");var flag=new Boolean(true);
    let iv:int=unbox i;let bv:byte=unbox b;let cv:char=unbox c;let fv:float=unbox f;
    let dv:decimal=unbox d;let sv:string=unbox s;let flagv:bool=unbox flag;
    if (i==invalid and b==invalid and c==invalid and f==invalid and d==invalid and s==invalid and flag==invalid and iv==42 and bv==255 and cv=='字' and fv==-0.0 and dv==1.2300:d and sv=="字" and flagv) {return 42;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_DeleteIsIdempotentAndReferencesCanBeReassigned()
    {
        const string source = """
class Box {public property Value:int {get{return 42;}}}
function main(args:string[]):int {
    var box=new Box();var alias=box;delete box;delete box;
    var values=[42];var arrayAlias=values;delete values;delete values;
    if (box!=invalid or values!=invalid or alias.Value!=42 or arrayAlias[0]!=42) {return 0;}
    box=alias;values=arrayAlias;
    if (box!=invalid and values!=invalid and box==alias and values==arrayAlias) {return 42;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_UninitializedTypedReferencesStartInvalid()
    {
        const string source = """
class Box {public property Value:int {get{return 42;}}}
function main(args:string[]):int {
    var value:Integer;var box:Box;var array:int[];
    delete value;delete box;delete array;
    if (value!=invalid or box!=invalid or array!=invalid) {return 0;}
    value=new Integer(42);box=new Box();array=[42];
    if (value.ToInt()==42 and box.Value==42 and array[0]==42) {return 42;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {var value:object;let n:int=unbox value;return 0;}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {var value:int;return 0;}"));
    }

    [Test]
    public void ReferenceValidity_FailedExtractionKeepsLocalWhileSuccessClearsIt()
    {
        var wrong=CreateVm("function main(args:string[]):int {let value:object=1.5;let extracted:int=unbox value;return 0;}");
        Assert.Throws<InvalidCastException>(()=>wrong.RunFromEntryPoint());
        var original=wrong.CurrentFrame!.Locals[0];Assert.That(original.IsObject,Is.True);
        Assert.That(wrong.Heap.GetField(original.AsObjectId,0).AsFloat,Is.EqualTo(1.5));
        var repeated=CreateVm("function main(args:string[]):int {let value:object=42;let first:int=unbox value;let second:int=unbox value;return 0;}");
        Assert.Throws<InvalidReferenceException>(()=>repeated.RunFromEntryPoint());
        Assert.That(repeated.CurrentFrame!.Locals[0].IsNull,Is.True);Assert.That(repeated.CurrentFrame.Locals[1].AsInt,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_InvalidDereferenceFailsForBoxesClassesArraysAndCalls()
    {
        const string declarations="class Box {public property Value:int {get{return 42;}}public async method call():void {}}";
        foreach(var body in new[]{"var value=new Integer(42);delete value;return value.ToInt();","var value=new Box();delete value;return value.Value;","var value=[42];delete value;return value[0];","var value=[42];delete value;return value.Length;","var value=new Box();delete value;value.call();return 0;","var value=[42];delete value;foreach(item in value) {}return 0;"})
            Assert.Throws<InvalidReferenceException>(()=>Run(declarations+"function main(args:string[]):int {"+body+"}"),body);
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {let value:object=42;delete value;let result:int=unbox value;return 0;}"));
        foreach(var operation in new[]{"print(.value);",".value=1;"})
            Assert.Throws<InvalidReferenceException>(()=>Run("class Box {field value:int;construct(target:Box){this.value=42;with(target){"+operation+"}}}function main(args:string[]):int {var target:Box;var created=new Box(target);return 0;}"));
    }

    [Test]
    public void ReferenceValidity_ExpectedArgumentsReturnsArraysIndexesAndFallbackConsumeLocals()
    {
        const string source = """
function consume(value:object):int {return unbox value;}
function integer(value:int):int {return value;}
function empty():int[] {return [];}
function count(values:int[]):int {return values.Length;}
function main(args:string[]):int {
    let caller:object=42;var copied=consume(caller);
    let a:object=42;var argument=integer(unbox a);
    let b:object=42;var constructed=new Integer(unbox b);
    let c:object=42;let values:int[]=[unbox c];
    let index:object=0;let indexed:int=values[unbox index];
    let fallback:object=42;let wrong:object=1.5;var fallbackValue=wrong as int ifnot unbox fallback;
    let assignment:object=42;let n:int=0;n=unbox assignment;
    if (caller!=invalid and a==invalid and b==invalid and c==invalid and index==invalid and fallback==invalid and assignment==invalid and copied==42 and argument==42 and constructed.ToInt()==42 and indexed==42 and fallbackValue==42 and n==42 and count([])==0 and empty().Length==0) {return 42;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_GcCollectsDeletedOnlyRootAndRetainsAliases()
    {
        var deleted=Run("function main(args:string[]):int {var value=new Integer(42);delete value;GC.require();GC.finish();return 42;}");
        Assert.That(deleted.ExitCode,Is.EqualTo(42));Assert.That(deleted.Vm.Heap.ObjectTable.Values.Any(x=>x.TypeName=="box:int"),Is.False);
        var retained=Run("function main(args:string[]):int {var value=new Integer(42);let values:Integer[]=[value];delete value;GC.require();GC.finish();return values[0].ToInt();}");
        Assert.That(retained.ExitCode,Is.EqualTo(42));Assert.That(retained.Vm.Heap.ObjectTable.Values.Any(x=>x.TypeName=="box:int"),Is.True);
    }

    [Test]
    public void ReferenceValidity_ExplicitBooleanUnboxUsesConditionContext()
    {
        const string source = """
function main(args:string[]):int {
    var condition=new Boolean(true);var loop=new Boolean(true);var value=0;
    if (unbox condition) {value=40;}
    while (unbox loop) {value=value+2;break;}
    if (condition==invalid and loop==invalid) {return value;}return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_QueryLocalsCanBeConsumedWithoutHeapMutation()
    {
        const string source = """
class Box {public property Value:int {get{var local=new Integer(42);return unbox local;}}}
function main(args:string[]):int {var box=new Box();return box.Value;}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void ReferenceValidity_RejectsMissingContextNonlocalsPrimitivesAndInvalidAssignment()
    {
        foreach(var body in new[]{"let value:object=42;var n=unbox value;","let value:object=42;let n:object=unbox value;","var values=[42];delete values[0];","let value:int=42;delete value;","var value=new Integer(42);delete value.ToInt();","var value=new Integer(42);value=invalid;","var value=invalid;","var value=1;return value==invalid;","var value=new Integer(42);return value<invalid;","var value=new Integer(42);let n:int=unbox new Integer(42);"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+body+"return 0;}"),body);
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {let value:object=42;delete value;return 0;}"));
    }

    [Test]
    public void ReferenceValidity_RawUnboxAndLiveGuardValidateInputsWithoutDestroyingBoxes()
    {
        var vm=CreateVm("function main(args:string[]):int{return 0;}");
        vm.Push(AloeValue.FromInt(42));vm.InvokeSyscall(EnumSyscall.PrimitiveBox);var box=vm.Pop();
        vm.Push(box);vm.Push(AloeValue.FromString("int"));vm.InvokeSyscall(EnumSyscall.PrimitiveUnbox);Assert.That(vm.Pop().AsInt,Is.EqualTo(42));Assert.That(vm.Heap.ContainsObject(box.AsObjectId),Is.True);
        foreach(var value in new[]{AloeValue.Null,AloeValue.FromObject(long.MaxValue),AloeValue.FromInt(42)}) {
            vm.Push(value);vm.Push(AloeValue.FromString("int"));Assert.Throws<InvalidReferenceException>(()=>vm.InvokeSyscall(EnumSyscall.PrimitiveUnbox));
            vm.Push(value);Assert.Throws<InvalidReferenceException>(()=>vm.InvokeSyscall(EnumSyscall.ReferenceRequireLive));
        }
        vm.Push(box);vm.Push(AloeValue.FromString("float"));Assert.Throws<InvalidCastException>(()=>vm.InvokeSyscall(EnumSyscall.PrimitiveUnbox));
        vm.Push(box);vm.Push(AloeValue.FromString("unknown"));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.PrimitiveUnbox));
        vm.Push(box);vm.InvokeSyscall(EnumSyscall.ReferenceRequireLive);Assert.That(vm.Pop().AsObjectId,Is.EqualTo(box.AsObjectId));Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    private static AloeVm CreateVm(string source){var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;return new AloeVm(module,settings);}
    private static (long ExitCode,AloeVm Vm) Run(string source){var vm=CreateVm(source);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,vm);}
}
