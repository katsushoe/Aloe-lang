using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceInheritanceTests
{
    [Test]
    public void InterfaceInheritance_DiamondContractsUpcastsAndRuntimeTestsPreserveDispatch()
    {
        const string source = """
interface Root { method emit(message: string): void; }
interface Left extends Root { method left(): void; }
interface Right extends Root { method right(): void; }
interface Leaf extends Left, Right { method leaf(): void; }
class Base implements Leaf {
    public virtual async method emit(message: string): void { print("base"); }
    public async method left(): void { print("left"); }
    public async method right(): void { print("right"); }
    public async method leaf(): void { print("leaf"); }
}
class Derived extends Base { public override async method emit(message: string): void { print("derived"); } }
function dispatch(value: Root): void { value.emit("ignored"); }
function main(args: string[]): int {
    let child: Leaf = new Derived(); let parent: Root = child;
    let reference: object = child; print(reference is Root); print(reference is Leaf);
    let cast: Left = reference as Left;
    dispatch(parent); cast.left(); child.right(); child.leaf();
    let values: Root[] = [child]; foreach (value in values) { value.emit("array"); }
    GC.require(); GC.finish(); tick();
    return values.Length;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Output, Is.EqualTo("true\ntrue\nderived\nleft\nright\nleaf\nderived\n"));
    }

    [Test]
    public void InterfaceInheritance_IdenticalParentContractsIgnoreParameterNames()
    {
        const string source = """
interface A { method emit(first: int): void; }
interface B { method emit(second: int): void; }
interface C extends A, B { }
class Item implements C { public async method emit(value: int): void { print(value); } }
function main(args: string[]): int {
    let item: C = new Item(); let a: A = item; let b: B = item;
    a.emit(1); b.emit(2); item.emit(3); tick(); return 0;
}
""";
        Assert.That(Run(source).Output, Is.EqualTo("1\n2\n3\n"));
    }

    [Test]
    public void InterfaceInheritance_ParentConformanceDoesNotImplyChildConformance()
    {
        const string source = """
interface Parent { method emit(): void; }
interface Child extends Parent { }
class ParentOnly implements Parent { public async method emit(): void { print("parent"); } }
class ChildItem implements Child { public async method emit(): void { print("child"); } }
function main(args: string[]): int {
    let value: object = new ParentOnly(); print(value is Parent); print(value is Child);
    let fallback: Child = value as Child ifnot new ChildItem();
    fallback.emit(); tick(); return 0;
}
""";
        Assert.That(Run(source).Output, Is.EqualTo("true\nfalse\nchild\n"));
    }

    [TestCase("interface A extends Missing { }")]
    [TestCase("interface A extends A { }")]
    [TestCase("interface A { } interface B extends A, A { }")]
    [TestCase("interface A extends B { } interface B { }")]
    [TestCase("class A { } interface B extends A { }")]
    [TestCase("interface A { method run(value: int): void; } interface B { method run(value: string): void; } interface C extends A, B { }")]
    [TestCase("interface A { method run(): void; } interface B extends A { method run(): void; }")]
    [TestCase("interface A { method run(): void; } interface B extends A { } class C implements B { }")]
    [TestCase("interface A { method run(): void { } }")]
    public void InterfaceInheritance_RejectsInvalidParentsConflictsMissingContractsAndDefaultBodies(string declarations)
    {
        Assert.That(() => new AloeCompiler().Compile(declarations + " function main(args: string[]): int { return 0; }"),
            Throws.TypeOf<AloeCompileException>());
    }

    [TestCase("let parent: A = new Item(); let child: B = parent;")]
    [TestCase("let children: B[] = [new Item()]; let parents: A[] = children;")]
    public void InterfaceInheritance_RejectsImplicitDowncastsAndCollectionCovariance(string body)
    {
        var source = "interface A { } interface B extends A { } class Item implements B { } function main(args: string[]): int { " + body + " return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
