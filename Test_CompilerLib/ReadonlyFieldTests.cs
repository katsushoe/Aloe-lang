using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ReadonlyFieldTests
{
    public static IEnumerable<TestCaseData> Scalars()
    {
        yield return new("int","-2147483648","2147483647");
        yield return new("bool","false","true");
        yield return new("byte","0","255");
        yield return new("char","'A'","'字'");
        yield return new("float","1.5","-2.5");
        yield return new("decimal","1.0:d","1.0000000000000000000000000001:d");
        yield return new("string","\"first\"","\"Aloe字\"");
    }

    [TestCaseSource(nameof(Scalars))]
    public void DeclaringConstructorMayAssignMoreThanOnce(string type,string first,string second)
        => AssertResult($$"""
class Box {
 readonly field value: {{type}};
 construct(value: {{type}}) { this.value={{first}}; this.value=value; }
 public property Value: {{type}} { get { return this.value; } }
}
function main(args: string[]): int { var box=new Box({{second}}); if(box.Value == {{second}}) { return 42; } return 0; }
""");

    [TestCaseSource(nameof(Scalars))]
    public void PrivateMethodCannotWriteReadonlyField(string type,string first,string second)
        => AssertReadonlyRejected($$"""
class Box { readonly field value: {{type}}; construct() { this.value={{first}}; }
 private method change(): void { this.value={{second}}; } }
function main(args: string[]): int { return 0; }
""");

    [TestCaseSource(nameof(Scalars))]
    public void AsyncMethodCannotWriteReadonlyField(string type,string first,string second)
        => AssertReadonlyRejected($$"""
class Box { readonly field value: {{type}}; construct() { this.value={{first}}; }
 public async method change(): void { this.value={{second}}; } }
function main(args: string[]): int { return 0; }
""");

    [TestCase("construct() { this.value=1; } private method helper(): void { this.value=2; }")]
    [TestCase("construct() { this.value=1; } public property Value: int { get { this.value=2; return this.value; } }")]
    [TestCase("construct(other: Box) { with(other) { .value=2; } }")]
    [TestCase("construct() { with(this) { .value=2; } }")]
    [TestCase("construct() { this.value=1; } private method change(other: Box): void { with(other) { .value=2; } }")]
    public void HelpersGettersAndWithWritesAreRejected(string members)
        => AssertReadonlyRejected("class Box { readonly field value: int; " + members + " } function main(args: string[]): int { return 0; }");

    [Test]
    public void DerivedConstructorCannotWriteInheritedReadonlyField()
        => AssertReadonlyRejected("""
class Base { readonly field value: int; construct() { this.value=1; } }
class Derived extends Base { construct() { this.value=2; } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void DerivedConstructorMayInitializeItsOwnReadonlyField()
        => AssertResult("""
class Base { }
class Derived extends Base {
 readonly field value: int; construct() { this.value=42; }
 public property Value: int { get { return this.value; } }
}
function main(args: string[]): int { var box=new Derived(); return box.Value; }
""");

    [Test]
    public void OrdinaryFieldsRemainMutableThroughConstructorHelpersAndWith()
        => AssertResult("""
class Box {
 field value: int;
 construct() { this.value=1; var unused=change(); }
 private method change(): int { with(this) { .value=42; } return 0; }
 public property Value: int { get { return this.value; } }
}
function main(args: string[]): int { var box=new Box(); return box.Value; }
""");

    [TestCase("readonly field value: pipe<int>;")]
    [TestCase("readonly static field value: int;")]
    [TestCase("readonly private method change(): void { }")]
    [TestCase("readonly construct() { }")]
    [TestCase("readonly public property Value: int { get { return 1; } }")]
    [TestCase("readonly field value: int=1;")]
    [TestCase("public readonly field value: int;")]
    public void UnsupportedTypesAndModifiersAreRejected(string member)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            "enum Tone { One } struct Point { field x: int; } class Box { " + member + " } function main(args: string[]): int { return 0; }"));

    [Test]
    public void ReadonlyStructFieldsRemainUnsupported()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            "struct Point { readonly field x: int; } function main(args: string[]): int { return 0; }"));

    [Test]
    public void BooleanAliasUsesOrdinaryScalarInitialization()
        => AssertResult("""
class Box { readonly field value: boolean; construct(value: boolean) { this.value=value; }
 public property Value: boolean { get { return this.value; } } }
function main(args: string[]): int { var box=new Box(true); if(box.Value) { return 42; } return 0; }
""");

    [Test]
    public void ClassDefiniteAssignmentIsNotIntroduced()
        => Assert.DoesNotThrow(()=>new AloeCompiler().Compile(
            "class Box { readonly field value: int; } function main(args: string[]): int { return 0; }"));

    [Test]
    public void ReadonlyFloatPreservesNegativeZeroBits()
    {
        var compiled=new AloeCompiler().Compile("""
class Box { readonly field value: float; construct() { this.value=-0.0; }
 public property Value: float { get { return this.value; } } }
function negative(): float { var box=new Box(); return box.Value; }
function main(args: string[]): int { return 0; }
""");
        var index=compiled.Functions.ToList().FindIndex(f=>f.Name=="negative");
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new Module(compiled.Constants,compiled.Code,compiled.Functions,index)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint();
        Assert.That(BitConverter.SingleToInt32Bits((float)vm.Pop().AsFloat), Is.EqualTo(int.MinValue));
    }

    private static void AssertReadonlyRejected(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("readonly field"));
    }
    private static void AssertResult(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
    }
}
