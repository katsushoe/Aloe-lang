using Aloe.CommonLib;
using Aloe.RuntimeLib;
namespace Aloe.CompilerLib.Tests;

public sealed class ReadonlyFieldTypeTests
{
    [TestCase("object","new C()")]
    [TestCase("C","new C()")]
    [TestCase("I","new C()")]
    [TestCase("int[]","[7]")]
    [TestCase("C[]","[new C()]")]
    [TestCase("int[][]","[[7]]")]
    [TestCase("int{}","{7}")]
    [TestCase("int()","(7,8)")]
    [TestCase("map<int>","[\"key\":7]")]
    [TestCase("Integer","new Integer(7)")]
    [TestCase("E","E.One")]
    [TestCase("Point","Point { x: 42 }")]
    public void ConstructorStoresValueAndMethodCannotReplaceIt(string type,string value)
    {
        Safe(Source(type,value));Rejected(Source(type,value,method:"this.held=input;"));
    }

    [TestCase("delete this.held;")]
    [TestCase("delete (((this.held)));")]
    [TestCase("let n: int=unbox this.held;")]
    [TestCase("with(this) { delete .held; }")]
    [TestCase("with(this) { let n: int=unbox .held; }")]
    public void MethodCannotInvalidateReadonlyBinding(string body)=>Rejected(Source("object","new Integer(7)",method:body));

    [TestCase("delete this.held; this.held=input;")]
    [TestCase("let n: int=unbox this.held; this.held=input;")]
    public void ConstructorMayInvalidateAndReassign(string body)=>Safe(Source("object","new Integer(7)",constructor:body));

    [TestCase("var alias=this.held; delete alias;")]
    [TestCase("var alias=this.held; let n: int=unbox alias;")]
    public void LocalAliasInvalidationPreservesField(string body)=>Safe(Source("object","new Integer(7)",method:body,constructor:"var n=change(input);"));

    [TestCase("with(this) { .held=input; }")]
    [TestCase("with(this) { delete .held; }")]
    public void WithCannotWriteBindingEvenInConstructor(string body)=>Rejected(Source("object","new C()",constructor:body));

    [Test]
    public void ArrayContentsRemainMutable()=>Safe("""
class Box { readonly field items: int[]; construct(input: int[]) { this.items=input; }
 public property First: int { get { var alias=this.items; return alias[0]; } set { var alias=this.items; alias[0]=value; } } }
function main(args: string[]): int { var input=[1]; var box=new Box(input); box.First=42; return input[0]; }
""");

    [Test]
    public void ReferencedObjectRemainsMutable()=>Safe("""
class Item { field stored: int; construct() { this.stored=0; }
 public property Value: int { get { return this.stored; } set { this.stored=value; } } }
class Box { readonly field held: Item; construct(input: Item) { this.held=input; }
 public property Value: int { get { var alias=this.held; return alias.Value; } set { var alias=this.held; alias.Value=value; } } }
function main(args: string[]): int { var item=new Item(); var box=new Box(item); box.Value=42; return item.Value; }
""");

    [Test]
    public void StructAliasMutationPreservesStoredSnapshot()=>Safe("""
struct Point { field x: int; }
class Box { readonly field held: Point; construct() { this.held=Point { x: 42 }; }
 public property Value: Point { get { return this.held; } } }
function main(args: string[]): int { var box=new Box(); var copy=box.Value; copy.x=7; var stored=box.Value; return stored.x; }
""");

    [Test]
    public void DerivedConstructorCannotRebindBaseReference()=>Rejected("""
class Item { } class Base { readonly field held: object; construct() { this.held=new Item(); } }
class Derived extends Base { construct() { this.held=new Item(); } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void SetterCannotRebindReference()=>Rejected("""
class Item { } class Box { readonly field held: object; construct() { this.held=new Item(); }
 public property Value: int { get { return 0; } set { this.held=new Item(); } } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void GetterCannotInvalidateReference()=>Rejected(Source("object","new C()").Replace("get { return this.held; }","get { delete this.held; return this.held; }"));

    private static string Source(string type,string value,string method="",string constructor="")=>$$"""
interface I { method emit(): void; } class C implements I { public async method emit(): void { } }
enum E { One } struct Point { field x: int; }
class Box { readonly field held: {{type}}; construct(input: {{type}}) { this.held=input; this.held=input; {{constructor}} }
 public property Value: {{type}} { get { return this.held; } }
 private method change(input: {{type}}): int { {{method}} return 0; } }
function main(args: string[]): int { let input: {{type}} = {{value}}; var box=new Box(input); let result: {{type}} = box.Value;
 if({{(type=="Point"?"result.x == 42":"result == input")}}) { return 42; } return 0; }
""";
    private static void Rejected(string source)
    {
        var e=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));Assert.That(e!.Message,Does.Contain("readonly field"));
    }
    private static void Safe(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.EqualTo(42));
    }
}
