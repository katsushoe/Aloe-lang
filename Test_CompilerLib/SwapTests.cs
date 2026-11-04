using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class SwapTests
{
    [TestCase("int","-2147483648","2147483647")]
    [TestCase("bool","false","true")]
    [TestCase("byte","0","255")]
    [TestCase("char","'A'","'字'")]
    [TestCase("float","1.5","-2.5")]
    [TestCase("decimal","1.0000000000000000000000000001:d","-2.5:d")]
    [TestCase("string","\"Aloe字\"","\"\"")]
    public void ScalarsExchangeExactValues(string type,string first,string second)
        => AssertResult($$"""
function main(args: string[]): int {
 let a: {{type}}={{first}}; let b: {{type}}={{second}};
 swap(a,b);
 if(a != {{second}} or b != {{first}}) { return 9; }
 return 42;
}
""");

    [TestCase("byte","0","255")]
    [TestCase("string","\"left\"","\"right\"")]
    public void ParametersExchangeWithoutChangingCaller(string type,string first,string second)
        => AssertResult($$"""
function check(a: {{type}},b: {{type}}): bool { swap(a,b); return a == {{second}} and b == {{first}}; }
function main(args: string[]): int {
 let a: {{type}}={{first}}; let b: {{type}}={{second}};
 if(check(a,b) and a == {{first}} and b == {{second}}) { return 42; } return 0;
}
""");

    [TestCase("int","42")]
    [TestCase("float","-0.0")]
    public void SameSlotPreservesValue(string type,string literal)
        => AssertResult($$"""
function main(args: string[]): int { let a: {{type}}={{literal}}; swap(a,a); if(a == {{literal}}) { return 42; } return 0; }
""");

    [Test]
    public void LexicalShadowsResolveTheInnerSlots()
        => AssertResult("""
function main(args: string[]): int {
 var a=1; var b=2;
 if(true) { var a=10; var b=20; swap(a,b); if(a != 20 or b != 10) { return 0; } }
 if(a == 1 and b == 2) { return 42; } return 0;
}
""");

    [Test]
    public void FloatSignBitsSurviveExchange()
    {
        var compiled=new AloeCompiler().Compile("""
function exchanged(): float { let a: float=1.5; let b: float=-0.0; swap(a,b); return a; }
function main(args: string[]): int { return 0; }
""");
        var index=compiled.Functions.ToList().FindIndex(f=>f.Name=="exchanged");
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new Module(compiled.Constants,compiled.Code,compiled.Functions,index)));
        var vm=QuietVm(module);vm.RunFromEntryPoint();
        Assert.That(BitConverter.SingleToInt32Bits((float)vm.Pop().AsFloat),Is.EqualTo(int.MinValue));
    }

    [Test]
    public void TryAndLoopTransfersPreserveBothWrites()
        => AssertResult("""
function main(args: string[]): int {
 var a=1; var b=2; var count=0;
 while(count < 3) { try { swap(a,b); count=count+1; if(count == 1) { continue; } if(count == 3) { break; } } catch { return 9; } }
 if(a == 2 and b == 1 and count == 3) { return 42; } return 0;
}
""");

    [TestCase("swap(a,missing);")]
    [TestCase("swap(a);")]
    [TestCase("swap(a,b,b);")]
    [TestCase("swap(a,b)")]
    [TestCase("swap((a),b);")]
    [TestCase("swap(a+1,b);")]
    [TestCase("swap(items[0],a);")]
    [TestCase("swap(box.value,a);")]
    [TestCase("swap(make(),a);")]
    [TestCase("swap(1,a);")]
    [TestCase("swap(a,flag);")]
    public void MalformedAndNonlocalTargetsAreRejected(string statement)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile($$"""
class Box { field value: int; }
function make(): int { return 7; }
function main(args: string[]): int {
 var a=1; var b=2; var flag=true; var items=[7]; var box=new Box();
 {{statement}} return 0;
}
"""));

    [TestCase("let a: object=1; let b: object=2;")]
    [TestCase("var a=new Box(); var b=new Box();")]
    [TestCase("let a: int[]=[]; let b: int[]=[];")]
    [TestCase("let a: int()=(); let b: int()=();")]
    [TestCase("let a: int{}={}; let b: int{}={};")]
    [TestCase("let a: map<int> = []; let b: map<int> = [];")]
    [TestCase("let a: Tone=Tone.One; let b: Tone=Tone.Two;")]
    [TestCase("var a=Point { x: 1 }; var b=Point { x: 2 };")]
    public void UnsupportedDeclaredTypesAreRejected(string declarations)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile($$"""
class Box { }
struct Point { field x: int; }
enum Tone { One, Two }
function main(args: string[]): int { {{declarations}} swap(a,b); return 0; }
"""));
        Assert.That(error!.Message,Does.Contain("swap requires two local scalar variables"));
    }

    private static void AssertResult(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm=QuietVm(module);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
    private static AloeVm QuietVm(Module module)
    {
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        return new AloeVm(module,settings);
    }
}
