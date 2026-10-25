using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class NamedBoxTests
{
    [Test]
    public void NamedBoxes_DocumentedConstructorTypedReferenceAndExtraction()
    {
        const string source = """
function identity(value: Integer): Integer { return value; }
function main(args: string[]): int {
    let number: Integer = 42;
    let opaque: object = identity(number);
    var recovered = opaque as Integer;
    var explicitBox = new Integer(42);
    if (recovered == number and explicitBox != number and recovered.ToInt() == 42) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NamedBoxes_AllSevenConstructorsAndExactToMethods()
    {
        const string source = """
function main(args:string[]):int {
    let i:Integer = new Integer(-2147483648); let b:Byte = new Byte(255); let c:Char = new Char('字');
    let f:Float = new Float(-0.0); let d:Decimal = new Decimal(1.2300:d);
    let s:String = new String("Aloe字"); let flag:Boolean = new Boolean(true);
    if (i.ToInt() == -2147483648 and b.ToByte() == 255 and c.ToChar() == '字' and f.ToFloat() == -0.0 and d.ToDecimal() == 1.2300:d and s.ToString() == "Aloe字" and flag.ToBool()) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NamedBoxes_ImplicitBoxingTypedBoundariesAndReferenceIdentity()
    {
        const string source = """
function fromValue(value:Integer):Integer {return value;}
function make():Integer {return 42;}
function main(args:string[]):int {
    let b:byte = 42; let byteBox:Byte = b;
    let character:Char = '字'; let floating:Float = 1.5; let dec:Decimal = 0.125:d;
    let text:String = "text"; let flag:Boolean = true;
    var number = fromValue(42); var alias = number; number = 42;
    if (number != alias and make().ToInt() == 42 and alias.ToInt() == 42 and byteBox.ToByte() == 42 and character.ToChar() == '字' and floating.ToFloat() == 1.5 and dec.ToDecimal() == 0.125:d and text.ToString() == "text" and flag.ToBool()) {return 42;} return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void NamedBoxes_ExplicitCastsFromPrimitiveAndOpaqueReferencesUseLazyFallback()
    {
        const string source = """
function fallback():Integer {print(99);return new Integer(42);}
function main(args:string[]):int {
    let erased:object = 42; var original = erased as Integer;
    var same = erased as Integer ifnot fallback();
    var other = 1.5 as Integer ifnot fallback();
    var explicitBox = 42 as Integer;
    if (original == same and other.ToInt() == 42 and explicitBox.ToInt() == 42 and (erased as Integer).ToInt() == 42) {return 42;} return 0;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("99\n"));
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {var value = 1.5 as Integer;return 0;}"));
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let erased:object = 1.5;var value = erased as Integer;return 0;}"));
    }

    [Test]
    public void NamedBoxes_TypedArraysNestedArraysForeachAndGc()
    {
        const string source = """
function main(args:string[]):int {
    let values:Integer[] = [40, new Integer(1)]; values[0] = 41;
    var alias = values; let rows:Integer[][] = [values, []]; rows[1] = [1];
    let strings:String[] = ["字"]; let bytes:Byte[] = [new Byte(255)];
    let chars:Char[] = ['字']; let floats:Float[] = [1.5]; let decimals:Decimal[] = [0.125:d]; let flags:Boolean[] = [true];
    let erased:object = rows; var recovered = erased as Integer[][];
    GC.require(); GC.finish(); var total = 0;
    foreach (value in recovered[0]) {total = total + value.ToInt();}
    if (total == 42 and alias == rows[0] and strings[0].ToString() == "字" and bytes[0].ToByte() == 255 and chars[0].ToChar() == '字' and floats[0].ToFloat() == 1.5 and decimals[0].ToDecimal() == 0.125:d and flags[0].ToBool()) {return 42;} return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void NamedBoxes_ClassFieldsGettersQueuedArgumentsAndTickQueries()
    {
        const string source = """
class Store {
    field value:Integer;
    construct(value:Integer){this.value=value;}
    public property Value:Integer {get{return this.value;}}
    public property Fresh:Integer {get{return new Integer(42);}}
    public async method update(value:Integer,query:Store):void {
        this.value=value;print(query.Value.ToInt());print(this.value.ToInt());print(query.Fresh.ToInt());
    }
}
function main(args:string[]):int {var store=new Store(1);store.update(42,store);GC.require();GC.finish();tick();return store.Value.ToInt();}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("1\n42\n42\n"));
    }

    [Test]
    public void NamedBoxes_RejectReservedNamesWrongKindsCountsMembersAndArrayVariance()
    {
        foreach (var declaration in new[] {"class Integer {}","enum Boolean {Yes}","struct String {}","class Derived extends Integer {}"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(declaration+" function main(args:string[]):int{return 0;}"),declaration);
        foreach (var statement in new[] {"var value=new Integer();","var value=new Integer(1,2);","var value=new Float(1);","var value=new Integer(true);","var value=new Byte(256);","let value:Byte=255;","let value:Integer=1.5;","let erased:object=42;let value:Integer=erased;","var value=new Integer(42).Value;","var value=new Integer(42).ToFloat();","var value=new Integer(42).ToInt(1);","let values:Integer[]=[1.5];","let values:object[]=[];let named:Integer[]=values;","let values:Integer[]=[];let other:Float[]=values;","let values:Integer[]=[];let objects:object[]=values;"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+statement+"return 0;}"),statement);
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {var value=new Integer(42);return value.ToInt();}"));
    }

    [Test]
    public void NamedBoxes_RawBoxReferenceCastsAndArraysCheckShapePayloadAndStack()
    {
        var vm=CreateVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"));
        vm.Push(AloeValue.FromInt(42));vm.InvokeSyscall(EnumSyscall.PrimitiveBox);var box=vm.Pop();
        vm.Push(box);vm.Push(AloeValue.FromString("box:int"));vm.InvokeSyscall(EnumSyscall.ObjectCast);Assert.That(vm.Pop().AsObjectId,Is.EqualTo(box.AsObjectId));
        vm.Push(box);vm.Push(AloeValue.FromString("box:int[]"));vm.Push(AloeValue.FromInt(1));vm.InvokeSyscall(EnumSyscall.ArrayCreate);var array=vm.Pop();
        vm.Push(array);vm.Push(AloeValue.FromInt(0));vm.InvokeSyscall(EnumSyscall.ArrayGet);Assert.That(vm.Pop().AsObjectId,Is.EqualTo(box.AsObjectId));
        var bad=vm.Heap.Allocate(ReadOnlySpan<byte>.Empty,fieldCount:1,typeName:"box:int");vm.Heap.SetField(bad,0,AloeValue.FromBool(true));
        foreach(var value in new[]{AloeValue.FromObject(bad),AloeValue.FromObject(long.MaxValue),AloeValue.FromInt(42)}) {
            vm.Push(value);vm.Push(AloeValue.FromString("box:int"));vm.InvokeSyscall(EnumSyscall.ObjectTryCast);Assert.That(vm.Pop().AsBool,Is.False);Assert.That(vm.Pop().IsNull,Is.True);
        }
        vm.Push(box);vm.Push(AloeValue.FromString("box:missing"));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ObjectCast));
        vm.Heap.SetField(box.AsObjectId,0,AloeValue.FromBool(true));
        vm.Push(array);vm.Push(AloeValue.FromInt(0));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.ArrayGet));
        Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    private static AloeVm CreateVm(Module module){var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;return new AloeVm(module,settings);}
    private static (long ExitCode,string Output) Run(string source){var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));var vm=CreateVm(module);var output=new StringBuilder();vm.OutputWriter=line=>output.AppendLine(line);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));}
}
