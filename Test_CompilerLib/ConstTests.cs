using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ConstTests
{
    [TestCase("int", "-2147483648")]
    [TestCase("bool", "true")]
    [TestCase("boolean", "false")]
    [TestCase("byte", "255")]
    [TestCase("char", "'字'")]
    [TestCase("float", "-1.5")]
    [TestCase("decimal", "1.0000000000000000000000000001:d")]
    [TestCase("string", "\"Aloe字\"")]
    public void ScalarValuesCanBeReadAndCopied(string type, string literal)
        => AssertResult($$"""
function copy(value: {{type}}): {{type}} { const result: {{type}}=value; return result; }
function main(args: string[]): int {
 const value: {{type}}={{literal}}; let other: {{type}}=copy(value);
 if(value == {{literal}} and other == value) { return 42; } return 0;
}
""");

    [TestCase("int", "1")]
    [TestCase("bool", "true")]
    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    [TestCase("string", "\"a\"")]
    public void ReassignmentIsRejected(string type, string literal)
        => AssertConstWriteRejected($$"""
function main(args: string[]): int { const value: {{type}}={{literal}}; value={{literal}}; return 0; }
""");

    [TestCase("swap(a,b);")]
    [TestCase("swap(b,a);")]
    [TestCase("swap(a,a);")]
    public void SwapTreatsEitherConstantOperandAsAWrite(string statement)
        => AssertConstWriteRejected("function main(args: string[]): int { const a: int=1; var b=2; " + statement + " return 0; }");

    [TestCase("const a=1;")]
    [TestCase("const a: int;")]
    [TestCase("const a: int=true;")]
    [TestCase("const a: byte=256;")]
    [TestCase("const a: byte=-1;")]
    [TestCase("const a: int=missing;")]
    [TestCase("const a: int=a;")]
    [TestCase("const a: int=1; const a: int=2;")]
    public void InvalidDeclarationsAreRejected(string declaration)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "function main(args: string[]): int { " + declaration + " return 0; }"));

    [TestCase("object", "1")]
    [TestCase("int[]", "[]")]
    [TestCase("int{}", "{}")]
    [TestCase("int()", "()")]
    [TestCase("map<int>", "[]")]
    [TestCase("Box", "new Box()")]
    [TestCase("Point", "Point { x: 1 }")]
    [TestCase("Tone", "Tone.One")]
    public void ReferenceAndNominalConstantsAreRejected(string type, string expression)
    {
        var error=Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile($$"""
class Box { }
struct Point { field x: int; }
enum Tone { One }
function main(args: string[]): int { const value: {{type}} = {{expression}}; return 0; }
"""));
        Assert.That(error!.Message, Does.Contain("const supports local scalar types only"));
    }

    [Test]
    public void GlobalConstantsRemainUnsupported()
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "const value: int=1; function main(args: string[]): int { return value; }"));

    [Test]
    public void MutableShadowAndLaterSiblingSlotsRemainWritable()
        => AssertResult("""
function first(): int { const a: int=9; return a; }
function second(a: int): int { a=a+1; return a; }
function main(args: string[]): int {
 const a: int=1;
 if(true) { var a=10; var b=20; swap(a,b); a=a+1; if(a != 21) { return 0; } }
 if(true) { const b: int=2; if(b != 2) { return 0; } }
 if(true) { var b=3; b=4; if(b != 4) { return 0; } }
 if(a == 1 and first() == 9 and second(3) == 4) { return 42; } return 0;
}
""");

    [Test]
    public void ConstantShadowDoesNotMakeOuterVariableImmutable()
        => AssertResult("""
function main(args: string[]): int {
 var value=1; if(true) { const value: int=value+1; if(value != 2) { return 0; } }
 value=42; return value;
}
""");

    [Test]
    public void InitializerRunsOncePerExecutedDeclaration()
        => AssertResult("""
function next(counts: int[]): int { counts[0]=counts[0]+1; return counts[0]; }
function main(args: string[]): int {
 var counts=[0]; const first: int=next(counts); var sum=0; var i=0;
 while(i < 3) { const value: int=next(counts); sum=sum+value; i=i+1; }
 if(false) { const unused: int=next(counts); }
 if(first == 1 and sum == 9 and counts[0] == 4) { return 42; } return 0;
}
""");

    [Test]
    public void ThrowingInitializerUsesOrdinaryRuntimeExceptionHandling()
        => AssertResult("""
function fail(): int { return 1/0; }
function main(args: string[]): int {
 var value=0; try { const failed: int=fail(); value=1; } catch { value=42; }
 return value;
}
""");

    [TestCase("int", "7")]
    [TestCase("bool", "true")]
    [TestCase("byte", "255")]
    [TestCase("char", "'字'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.25:d")]
    [TestCase("string", "\"value\"")]
    public void ConstPrimitiveReturnDoesNotInventAnExceptionalEdge(string type, string literal)
        => AssertResult(FlowSource(type, literal, "const alias: " + type + "=item; return alias;"));

    [Test]
    public void UnknownConstInitializerRetainsExceptionalEdge()
    {
        var error=Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            FlowSource("int", "7", "const alias: int=unknown(item); return alias;")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string FlowSource(string type, string literal, string body) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { } }
class Plain { }
function unknown(input: {{type}}): {{type}} { return input; }
function choose(): {{type}} {
 let items: {{type}}[]=[{{literal}}]; let value: object=new C();
 if(value is I) {
  foreach(item in items) { try { {{body}} } catch { value=new Plain(); } }
  value.emit();
 }
 return {{literal}};
}
function main(args: string[]): int { if(choose() == {{literal}}) { return 42; } return 0; }
""";

    private static void AssertConstWriteRejected(string source)
    {
        var error=Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Cannot write to const local"));
    }
    private static void AssertResult(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings(); settings.Logging.ConsoleEnabled=false; settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings); vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
    }
}
