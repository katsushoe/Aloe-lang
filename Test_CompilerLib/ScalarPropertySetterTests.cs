using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ScalarPropertySetterTests
{
    [TestCase("int","0","42")]
    [TestCase("bool","false","true")]
    [TestCase("byte","0","255")]
    [TestCase("char","'a'","'字'")]
    [TestCase("float","0.0","-1.5")]
    [TestCase("decimal","0:d","1.2300:d")]
    [TestCase("string","\"\"","\"Aloe字\"")]
    public void SetterImmediatelyUpdatesSameReceiver(string type,string initial,string value)
        => AssertResult(Source(type,initial,$"box.Value={value}; if(box.Value == {value}) {{ return 42; }} return 0;"));

    [Test]
    public void ValueExpressionIsEvaluatedOnceAndFinallyRunsImmediately()
    {
        var source=Source("int","0","box.Value=produce(); if(box.Value == 42) { return 42; } return 0;",
            "try { this.stored=value; } finally { print(\"set\"); }")+
            "\nfunction produce(): int { print(\"value\"); return 42; }";
        AssertResult(source,new[]{"value","set"});
    }

    [Test]
    public void ThisSetterMayCallPrivateHelperAndReturnVoid()
        => AssertResult(Source("int","0","box.Value=42; return box.Value;","this.stored=adjust(value); return;")
            .Replace("construct() { this.stored=0; }","construct() { this.Value=0; } private method adjust(value: int): int { return value; }"));

    [Test]
    public void InheritedSetterUsesBaseImplementation()
        => AssertResult(Source("int","0","box.Value=42; return box.Value;")
            .Replace("function main", "class Derived extends Box { }\nfunction main").Replace("var box=new Box();","var box=new Derived();"));

    [Test]
    public void ExceptionsPropagateAndCompletedWritesRemain()
        => AssertResult(Source("int","0","try { box.Value=42; } catch(ZeroDivisionException) { return box.Value; } return 0;",
            "this.stored=value; var fail=1/0;"));

    [Test]
    public void CatchAndFinallyInsideSetterUseExistingLowering()
        => AssertResult(Source("int","0","box.Value=1; return box.Value;",
            "try { var fail=1/0; } catch(ZeroDivisionException) { this.stored=41; } finally { this.stored=this.stored+1; }"));

    [Test]
    public void IndependentInstancesKeepSeparateState()
        => AssertResult(Source("int","0","var other=new Box(); box.Value=42; other.Value=7; if(other.Value == 7) { return box.Value; } return 0;"));

    [Test]
    public void ExistingThisFieldPrecedenceIsRetainedWhenNamesMatch()
        => AssertResult(Source("int","0","box.Value=42; return box.Value;").Replace("stored","Value"));

    [Test]
    public void InvalidReceiverFailsBeforeValueEvaluation()
    {
        var source=Source("int","0","delete box; try { box.Value=produce(); } catch(InvalidReferenceException) { return 42; } return 0;")+
            "\nfunction produce(): int { print(\"must-not-run\"); return 42; }";
        AssertResult(source,Array.Empty<string>());
    }

    [Test]
    public void ValueExpressionDeletingOwnAliasPreservesCapturedReceiver()
    {
        var source=Source("int","0","box.Value=erase(box); if(box.Value == 1) { return 42; } return 0;",
            "print(\"setter\"); this.stored=value;")+"\nfunction erase(box: Box): int { delete box; return 1; }";
        AssertResult(source,new[]{"setter"});
    }

    [TestCase("set { this.stored=value; } set { }")]
    [TestCase("set(value) { this.stored=value; }")]
    [TestCase("set { return 1; }")]
    [TestCase("set { this.stored=missing; }")]
    public void InvalidSetterBodiesAndDuplicateAccessorsAreRejected(string setter)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            Source("int","0","return 0;").Replace("set { this.stored=value; }",setter)));

    [TestCase("box.Value=true;")]
    [TestCase("box.Value=\"wrong\";")]
    [TestCase("box.Value+=1;")]
    [TestCase("Box.Value=1;")]
    public void InvalidAssignmentsAreRejected(string statement)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","0",statement+" return 0;")));

    [Test]
    public void GetterOnlyAssignmentIsRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","0","box.Value=1; return 0;").Replace("set { this.stored=value; }","")));

    [Test]
    public void ReadonlyCannotBeWrittenBySetterEvenWhenConstructorCallsIt()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","0","return 0;")
            .Replace("field stored:","readonly field stored:").Replace("construct() { this.stored=0; }","construct() { this.Value=0; }")));

    [TestCase("public static property")]
    [TestCase("public async property")]
    [TestCase("public virtual property")]
    public void UnsupportedPropertyModifiersAreRejected(string modifier)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","0","return 0;").Replace("public property",modifier)));

    [Test]
    public void ObjectSetterIsRejected()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("object","new Plain()","return 0;")+"\nclass Plain { }"));

    [Test]
    public void StaticMethodCannotUseThisSetter()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(Source("int","0","return 0;")+
            "\nclass Utility { public static method invalid(): void { this.Value=1; } }"));

    [Test]
    public void DirectWasmSetterRemainsUnsupported()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().CompileForWasm(Source("int","0","return 0;")));

    [Test]
    public void StructSetterRemainsUnsupported()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(
            "struct S { field x: int; public property Value: int { get { return this.x; } set { this.x=value; } } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void SetterRetainsExceptionalInterfaceFlow()
    {
        var source=Source("int","0","let marker: object=new C(); if(marker is I) { try { box.Value=1; } catch { marker=new Plain(); } marker.emit(); } return 0;")+
            "\ninterface I { method emit(): void; } class C implements I { public async method emit(): void { } } class Plain { }";
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Source(string type,string initial,string main,string setter="this.stored=value;") => $$"""
class Box {
 field stored: {{type}};
 construct() { this.stored={{initial}}; }
 public property Value: {{type}} { get { return this.stored; } set { {{setter}} } }
}
function main(args: string[]): int { var box=new Box(); {{main}} }
""";

    private static void AssertResult(string source,string[]? output=null)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);var lines=new List<string>();vm.OutputWriter=lines.Add;vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
        if(output!=null) Assert.That(lines,Is.EqualTo(output));
    }
}
