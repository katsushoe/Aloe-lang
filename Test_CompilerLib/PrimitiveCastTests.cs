using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class PrimitiveCastTests
{
    [Test]
    public void PrimitiveCasts_DocumentedIdentityAndLazyFailureExample()
    {
        const string source = """
function fallback(): int { print(99); return 42; }
function main(args: string[]): int {
    let original: int = 42;
    var same = original as int ifnot fallback();
    var replacement = 1.5 as int ifnot fallback();
    if (same == 42 and replacement == 42) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo("99\n"));
    }

    [Test]
    public void PrimitiveCasts_AllSevenKindsPreserveValuesAndChaining()
    {
        const string source = """
function identity(value: decimal): decimal { return value as decimal; }
function main(args: string[]): int {
    let b: byte = 255; let c: char = '字'; let flag: boolean = true;
    var f = -0.0; var d = identity(10.12345678901234567890:d);
    var s = "Aloe字";
    if ((42 as int as int) == 42 and (b as byte) == b and (c as char) == c and
        (flag as boolean) and (f as float) == f and (d as decimal) == d and (s as string) == s) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void PrimitiveCasts_DifferentKindsFailWithoutConversionOrParsing()
    {
        foreach (var expression in new[] { "1 as float", "1.0 as int", "1:d as float", "1.0 as decimal", "\"42\" as int", "true as int", "'A' as int", "1 as byte", "new Box() as int", "(1 as float ifnot throw)" })
            Assert.Throws<InvalidCastException>(() => Run("class Box {} function main(args: string[]): int { var value = " + expression + "; return 0; }"), expression);
    }

    [Test]
    public void PrimitiveCasts_FallbackKindsByteContextAndNestedEffects()
    {
        const string source = """
function source(): float { print(1); return 1.5; }
function fallback(): int { print(2); return 42; }
function main(args: string[]): int {
    var n = source() as int ifnot fallback();
    var b = 1 as byte ifnot 255;
    var c = true as char ifnot '字';
    var f = 1 as float ifnot 1.5;
    var d = 1.5 as decimal ifnot 0.125:d;
    var s = 1 as string ifnot "text";
    var flag = 1 as boolean ifnot true;
    var nested = 1.5 as int ifnot (false as int ifnot 42);
    if (n == 42 and b == 255 and c == '字' and f == 1.5 and d == 0.125:d and s == "text" and flag and nested == 42) { return 42; }
    return 0;
}
""";
        var result = Run(source); Assert.That(result.ExitCode, Is.EqualTo(42)); Assert.That(result.Output, Is.EqualTo("1\n2\n"));
    }

    [Test]
    public void PrimitiveCasts_RejectFallbackConversionsAndNominalErasure()
    {
        const string declarations = "struct Point {field x:int;} enum Color {Red} class Box {}";
        foreach (var expression in new[] { "1 as float ifnot 42", "true as int ifnot false", "1 as byte ifnot 256", "Color.Red as int", "new Point(1) as int", "42 as Color", "42 as Point" })
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { var value = " + expression + "; return 0; }"), expression);
    }

    [Test]
    public void PrimitiveCasts_RawKindsRangesDescriptorsAndStackContract()
    {
        var vm = CreateVm(new AloeCompiler().Compile("function main(args:string[]):int{return 0;}"));
        (string Name, AloeValue Value)[] values = [("int", AloeValue.FromInt(int.MinValue)), ("byte", AloeValue.FromByte(255)), ("char", AloeValue.FromChar('\ud800')), ("float", AloeValue.FromFloat(-0.0)), ("decimal", AloeValue.FromDecimal(decimal.MaxValue)), ("string", AloeValue.FromString("字")), ("bool", AloeValue.FromBool(true))];
        foreach (var (name, value) in values)
        {
            Cast(vm,value,name,EnumSyscall.PrimitiveCast); var result=vm.Pop(); Assert.That(result.Kind,Is.EqualTo(value.Kind)); Assert.That(result.ToString(),Is.EqualTo(value.ToString()));
            if (name=="float") Assert.That(BitConverter.DoubleToInt64Bits(result.AsFloat),Is.EqualTo(BitConverter.DoubleToInt64Bits(value.AsFloat)));
            Cast(vm,value,name,EnumSyscall.PrimitiveTryCast); Assert.That(vm.Pop().AsBool,Is.True); Assert.That(vm.Pop().Kind,Is.EqualTo(value.Kind));
        }
        foreach (var value in new[] {AloeValue.Null,AloeValue.FromInt((long)int.MaxValue+1),AloeValue.FromBool(true),AloeValue.FromObject(long.MaxValue)})
        {
            Cast(vm,value,"int",EnumSyscall.PrimitiveTryCast); Assert.That(vm.Pop().AsBool,Is.False); Assert.That(vm.Pop().IsNull,Is.True);
            Assert.Throws<InvalidCastException>(()=>Cast(vm,value,"int",EnumSyscall.PrimitiveCast));
        }
        foreach (var value in new[] {AloeValue.FromFloat(double.NaN),AloeValue.FromFloat(double.PositiveInfinity),AloeValue.FromFloat(double.NegativeInfinity)})
        {
            Cast(vm,value,"float",EnumSyscall.PrimitiveTryCast); Assert.That(vm.Pop().AsBool,Is.False); Assert.That(vm.Pop().IsNull,Is.True);
            Assert.Throws<InvalidCastException>(()=>Cast(vm,value,"float",EnumSyscall.PrimitiveCast));
        }
        foreach (var name in new[] {"","integer","boolean","object","enum:Color"})
            Assert.Throws<VmException>(()=>Cast(vm,AloeValue.FromInt(1),name,EnumSyscall.PrimitiveTryCast));
        vm.Push(AloeValue.FromInt(1));vm.Push(AloeValue.FromInt(1));Assert.Throws<VmException>(()=>vm.InvokeSyscall(EnumSyscall.PrimitiveCast));
        Assert.That(vm.ValueStack.Count,Is.Zero);
    }

    [Test]
    public void PrimitiveCasts_GetterQueriesPreserveCommittedValues()
    {
        const string source = """
class Counter {
    field value:int;
    construct(){this.value=1;}
    public property Value:int {get{return this.value as int;}}
    public async method update(target:Counter):void {this.value=42;print(target.Value);print(this.value as int);}
}
function main(args:string[]):int {var counter=new Counter();counter.update(counter);tick();return counter.Value;}
""";
        var result=Run(source);Assert.That(result.ExitCode,Is.EqualTo(42));Assert.That(result.Output,Is.EqualTo("1\n42\n"));
    }

    private static void Cast(AloeVm vm,AloeValue value,string name,EnumSyscall id) {vm.Push(value);vm.Push(AloeValue.FromString(name));vm.InvokeSyscall(id);}
    private static AloeVm CreateVm(Module module) {var settings=new AloeVmSettings();settings.Logging.FileEnabled=false;settings.Logging.ConsoleEnabled=false;return new AloeVm(module,settings);}
    private static (long ExitCode,string Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));var vm=CreateVm(module);var output=new StringBuilder();vm.OutputWriter=line=>output.AppendLine(line);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));return(vm.Pop().AsInt,output.ToString().Replace("\r\n","\n"));
    }
}
