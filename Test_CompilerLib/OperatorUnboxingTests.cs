using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class OperatorUnboxingTests
{
    [Test]
    public void OperatorUnboxing_DocumentedArithmeticConditionsAndIdentity()
    {
        const string source = """
function main(args: string[]): int {
    var number = new Integer(40);
    let increment: object = 2;
    var enabled = new Boolean(true);
    var alias = number;
    if (enabled and number == alias and number != invalid) {
        var total = number + increment;
        if (total == 42 and number == 40) { return total; }
    }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void OperatorUnboxing_NumericKindsPromotionAndExactOperandKinds()
    {
        const string source = """
function main(args:string[]):int {
    var i=new Integer(41);var b=new Byte(1);var f=new Float(1.5);var d=new Decimal(1.25:d);
    var a=i+b;var ratio=new Integer(7)/new Integer(2);var mod=new Integer(7)%2;
    if(a==42 and ratio==3 and mod==1 and f+i==42.5 and d+b==2.25:d and i*2==82 and 42-i==1 and i>40 and 42>=i){return a;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let n:object=1.5;return n+1;}"));
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {let n:object=42;var b=new Byte(1);var sum=n+b;return 0;}"));
    }

    [Test]
    public void OperatorUnboxing_OpaqueOperandsUsePeerOrExplicitNumericResultContext()
    {
        const string source = """
function main(args:string[]):int {
    let left:object=20;let right:object=22;let total:int=left+right;
    var forward=left+22;var backward=20+right;
    if(total==42 and forward==42 and backward==42 and left<21 and 21>left){return total;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {let a:object=1;let b:object=2;var sum=a+b;return 0;}"));
    }

    [Test]
    public void OperatorUnboxing_BooleanConditionsNotAndShortCircuitStayNonconsuming()
    {
        const string source = """
function skipped():object {print(99);return 42;}
function main(args:string[]):int {
    let yes:object=true;let no:object=false;var box=new Boolean(true);var total=0;
    if(yes or skipped()){total=40;}
    if(no and skipped()){return 0;}
    while(box){total=total+2;break;}
    if(not no and box and box==true and box!=invalid){return total;}
    return 0;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.Empty);
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {if(new Integer(1)){return 1;}return 0;}"));
    }

    [Test]
    public void OperatorUnboxing_IntegerMasksShiftsAndComplementKeepInt32Rules()
    {
        const string source = """
function main(args:string[]):int {
    let mask:object=15;let count:object=2;var n=new Integer(10);
    if((n|mask)==15 and (mask&n)==10 and (n^mask)==5 and ~n==-11 and (n<<count)==40 and (new Integer(-8)>>new Integer(1))==-4){return (n<<count)+2;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {var b=new Byte(1);return b|1;}"));
        Assert.Throws<VmException>(()=>Run("function main(args:string[]):int {var n=new Integer(1);var count=new Integer(32);return n<<count;}"));
    }

    [Test]
    public void OperatorUnboxing_ReferenceEqualityStaysIdentityAndScalarStringCharComparisonsExtract()
    {
        const string source = """
function main(args:string[]):int {
    var a=new Integer(42);var b=new Integer(42);var alias=a;
    var text=new String("Aloe");var letter=new Char('字');
    let opaque:object=42;
    if(a!=b and a==alias and a!=opaque and a==42 and 42==a and text=="Aloe" and "Aloe"==text and letter=='字' and letter>='字' and letter!=invalid){return 42;}
    return 0;
}
""";
        Assert.That(Run(source).ExitCode,Is.EqualTo(42));
    }

    [Test]
    public void OperatorUnboxing_EvaluatesEachOperandOnceAndLeftFailureSkipsRight()
    {
        const string source = """
function left():Integer{print(1);return 40;}
function right():Integer{print(2);return 2;}
function main(args:string[]):int {return left()+right();}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("1\n2\n"));
        var output=new StringBuilder();
        Assert.Throws<InvalidCastException>(()=>Run("function side():int{print(99);return 1;}function main(args:string[]):int {let bad:object=1.5;return bad+side();}",line=>output.AppendLine(line)));
        Assert.That(output.Length,Is.Zero);
        Assert.Throws<InvalidCastException>(()=>Run("function main(args:string[]):int {var n=new Integer(42);delete n;return n+0;}"));
    }

    [Test]
    public void OperatorUnboxing_ArrayRootsGettersAndTickRetainCommittedViews()
    {
        const string source = """
class Store {
    field value:Integer;
    construct(){this.value=new Integer(1);}
    public property Value:Integer {get{return this.value;}}
    public property Doubled:int {get{return this.value+this.value;}}
    public async method update(query:Store):void {
        this.value=new Integer(21);print(query.Doubled);print(this.value+this.value);
    }
}
function main(args:string[]):int {
    let values:Integer[]=[20,22];GC.require();GC.finish();
    var total=values[0]+values[1];var store=new Store();store.update(store);tick();
    if(total==42 and store.Value==21){return store.Doubled;}return 0;
}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("2\n42\n"));
    }

    [Test]
    public void OperatorUnboxing_RejectsUnsupportedKindsAndPreservesRuntimeArithmeticFailures()
    {
        foreach(var statement in new[]{"var value=new String(\"1\")+1;","var value=new Char('a')+1;","var value=new Boolean(true)+1;","var value=new Float(1.5)+new Decimal(1:d);","var value=new Integer(1)==true;"})
            Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("function main(args:string[]):int {"+statement+"return 0;}"),statement);
        Assert.Throws<OverflowException>(()=>Run("function main(args:string[]):int {var n=new Integer(2147483647);return n+1;}"));
        Assert.Throws<ZeroDivisionException>(()=>Run("function main(args:string[]):int {var n=new Integer(42);return n/new Integer(0);}"));
        Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm("function main(args:string[]):int {var n=new Integer(42);return n+0;}"));
    }

    private static (long ExitCode,string Output) Run(string source,Action<string>? onOutput=null)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;
        var vm=new AloeVm(module,settings);var output=new StringBuilder();vm.OutputWriter=line=>{output.AppendLine(line);onOutput?.Invoke(line);};
        vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));
        return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));
    }
}
