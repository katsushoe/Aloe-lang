using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ThisMethodCallTests
{
    [TestCase("int", "42")]
    [TestCase("bool", "true")]
    [TestCase("byte", "255")]
    [TestCase("char", "'字'")]
    [TestCase("float", "-1.5")]
    [TestCase("decimal", "1.0000000000000000000000000001:d")]
    [TestCase("string", "\"Aloe字\"")]
    public void ConstructorVoidHelperAndGetterValueCalls(string type,string literal)
        => AssertResult($$"""
class Box {
 field value: {{type}};
 construct(value: {{type}}) { this.initialize(value); }
 private method initialize(value: {{type}}): void { this.value=value; }
 private method identity(value: {{type}}): {{type}} { return value; }
 public property Value: {{type}} { get { return this.identity(this.value); } }
}
function main(args: string[]): int { var box=new Box({{literal}}); if(box.Value == {{literal}}) { return 42; } return 0; }
""");

    [Test]
    public void ExplicitThisSelectsMethodDespiteSameNameGlobalFunction()
        => AssertResult("""
function answer(): int { return 9; }
class Box { private method answer(): int { return 42; } public property Value: int { get { return this.answer(); } } }
function main(args: string[]): int { var box=new Box(); return box.Value; }
""");

    [Test]
    public void ProtectedInheritedMethodRetainsExistingResolution()
        => AssertResult("""
class Base { protected method answer(): int { return 42; } }
class Derived extends Base { public property Value: int { get { return this.answer(); } } }
function main(args: string[]): int { var box=new Derived(); return box.Value; }
""");

    [Test]
    public void ArgumentsAreEvaluatedOnce()
        => AssertResult("""
function next(counts: int[]): int { counts[0]=counts[0]+1; return counts[0]; }
class Box {
 private method echo(value: int): int { return value; }
 construct(counts: int[]) { var value=this.echo(next(counts)); }
}
function main(args: string[]): int { var counts=[0]; var box=new Box(counts); if(counts[0] == 1) { return 42; } return 0; }
""");

    [Test]
    public void PublicAsyncSelfCallUsesExistingQueue()
    {
        var result=Run("""
class Box { construct() { this.emit(); } public async method emit(): void { print(42); } }
function main(args: string[]): int { var box=new Box(); print("before"); tick(); tick(); return 42; }
""");
        Assert.That(result.Output, Is.EqualTo(new[]{"before","42"})); Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void VirtualAsyncSelfCallUsesRuntimeReceiverDispatch()
    {
        var result=Run("""
class Base {
 public async method schedule(): void { this.emit(); }
 public virtual async method emit(): void { print("base"); }
}
class Derived extends Base { public override async method emit(): void { print("derived"); } }
function main(args: string[]): int { var box=new Derived(); box.schedule(); tick(); tick(); return 42; }
""");
        Assert.That(result.Output, Is.EqualTo(new[]{"derived"})); Assert.That(result.Exit, Is.EqualTo(42));
    }

    [Test]
    public void AsyncSelfCycleIsRejectedByExistingGraphValidation()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            "class Box { public async method emit(): void { this.emit(); } } function main(args: string[]): int { return 0; }"));
        Assert.That(error!.Message, Does.Contain("cycle"));
    }

    [Test]
    public void ConstructorHelperDoesNotGainReadonlyWritePrivilege()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("""
class Box { readonly field value: int; construct() { this.initialize(); }
 private method initialize(): void { this.value=42; } }
function main(args: string[]): int { return 0; }
"""));
        Assert.That(error!.Message, Does.Contain("readonly field"));
    }

    [TestCase("private method run(): void { this.answer(); }")]
    [TestCase("private method run(): int { return this.nothing(); }")]
    [TestCase("private method run(): int { return this.echo(); }")]
    [TestCase("private method run(): int { return this.echo(1,2); }")]
    [TestCase("private method run(): int { return this.echo(true); }")]
    [TestCase("private method run(): int { return this.missing(); }")]
    [TestCase("private method run(): int { return this.emit(); }")]
    [TestCase("private method run(): int { return this.staticAnswer(); }")]
    [TestCase("public static method run(): int { return this.answer(); }")]
    public void InvalidResultArityTypeOrReceiverIsRejected(string member)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("""
class Box {
 private method answer(): int { return 42; }
 private method nothing(): void { }
 private method echo(value: int): int { return value; }
 public static method staticAnswer(): int { return 42; }
 public async method emit(): void { }
""" + member + " } function main(args: string[]): int { return 0; }"));

    [Test]
    public void GlobalFunctionDoesNotSupplyMissingExplicitInstanceMethod()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("""
function answer(): int { return 42; }
class Box { public property Value: int { get { return this.answer(); } } }
function main(args: string[]): int { return 0; }
"""));
        Assert.That(error!.Message, Does.Contain("Unknown instance method"));
    }

    [Test]
    public void ThisInGlobalCodeIsRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            "function main(args: string[]): int { return this.answer(); }"));

    private static void AssertResult(string source) => Assert.That(Run(source).Exit, Is.EqualTo(42));
    private static (long Exit,List<string> Output) Run(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); var output=new List<string>(); vm.OutputWriter=output.Add; vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); return (vm.Pop().AsInt,output);
    }
}
