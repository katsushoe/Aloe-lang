using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class BoxWithTests
{
    [Test]
    public void BoxWith_DocumentedCapturedTargetRetainsOriginalReference()
    {
        const string source = """
function main(args: string[]): int {
    var number = new Integer(42);
    with (number) {
        number = new Integer(7);
        return .ToInt();
    }
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void BoxWith_AllSevenTypesAndMixedTargets()
    {
        const string source = """
function main(args:string[]):int {
    with (new Integer(42),new Byte(255),new Char('字'),new Float(-0.0),new Decimal(1.2300:d),new String("Aloe字"),new Boolean(true)) {
        if (.ToInt()==42 and .ToByte()==255 and .ToChar()=='字' and .ToFloat()==-0.0 and .ToDecimal()==1.2300:d and .ToString()=="Aloe字" and .ToBool()) {return 42;}
    }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void BoxWith_ReceiverEvaluatesOnceLeftToRightAndRemainsRooted()
    {
        const string source = """
function number():Integer {print(1);return 42;}
function text():String {print(2);return "value";}
function main(args:string[]):int {
    with (number(),text()) {
        GC.require(); GC.finish();
        print(.ToInt()); print(.ToInt()); print(.ToString());
        return .ToInt();
    }
}
""";
        var result=Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n2\n42\n42\nvalue\n"));
    }

    [Test]
    public void BoxWith_NestedResolutionRestoresOuterAndSupportsArrayCastTargets()
    {
        const string source = """
enum E {Yes=1}
struct Point { field x:int; }
function main(args:string[]):int {
    let values:Integer[]=[40]; let erased:object=2; var p=Point {x:1}; var total=0;
    with (values[0],E,p) {
        if (.Yes != E.Yes) {return 0;}
        total=.ToInt()+.x;
        with (erased as Integer) {total=total+.ToInt();}
        total=total-.ToInt()+39;
    }
    return total;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void BoxWith_DeletedOriginalKeepsCapturedAliasAndInvalidTargetFailsOnRead()
    {
        Assert.That(Run("function main(args:string[]):int {var n=new Integer(42);with(n){delete n;GC.require();GC.finish();return .ToInt();}}").ExitCode,Is.EqualTo(42));
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {var n:Integer;with(n){return .ToInt();}}"));
        Assert.Throws<InvalidReferenceException>(()=>Run("function main(args:string[]):int {var n=new Integer(42);delete n;with(n){return .ToInt();}}"));
    }

    [Test]
    public void BoxWith_TickAndGetterUseExistingCommittedReferenceRules()
    {
        const string source = """
class Holder {
    field value:Integer;
    construct(){this.value=new Integer(1);}
    public property Value:int {get {with(this.value){return .ToInt();}}}
    public async method update():void {
        this.value=new Integer(42);
        with(this.value){print(.ToInt());}
    }
}
function main(args:string[]):int {
    var holder=new Holder();holder.update();print(holder.Value);tick();return holder.Value;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));
        Assert.That(result.Output,Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void BoxWith_InvalidMembersAmbiguityOpaqueAndWasmTargetsAreRejected()
    {
        foreach(var statement in new[]{"with(new Integer(1),new Integer(2)){return .ToInt();}","with(new Integer(42)){return .ToInt(1);}","with(new Integer(42)){return .ToInt;}","with(new Integer(42)){return .ToFloat();}","with(new Integer(42)){.ToInt=1;}","with(new Integer(42)){.ToInt();}","with(new Integer(42)){return .Value;}","with(Integer){return 0;}","let n:object=42;with(n){return 0;}","with(42){return 0;}","return .ToInt();"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+statement+"return 0;}"),statement);
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {with(new Integer(42)){return .ToInt();}}"));
    }

    private static (long ExitCode,string Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(module,settings);var output=new StringBuilder();vm.OutputWriter=line=>output.AppendLine(line);
        vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));
        return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));
    }
}
