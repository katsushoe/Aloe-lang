using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

// Decision 0063: interface property contracts `{ get; }` / `{ get; set; }`.
public sealed class InterfacePropertyContractTests
{
    private const string Shapes = """
interface Scored { property Score: int { get; } property Name: string { get; set; } }
class Small implements Scored {
    field name: string;
    construct() { this.name = "small"; }
    public property Score: int { get { return 1; } }
    public property Name: string { get { return this.name; } set { this.name = value; } }
}
class Large implements Scored {
    field name: string;
    construct() { this.name = "large"; }
    public property Score: int { get { return 40; } }
    public property Name: string { get { return this.name; } set { print("large set"); this.name = value; } }
}
""";

    [Test]
    public void SpecificationExample_CompilesAndRuns()
    {
        // Same program as the 7.4 example in Documents/en|ja/Aloe-lang-spec.html.
        var result = Run("""
interface Scored {
    property Score: int { get; }
    property Name: string { get; set; }
    method reset(): void;
}

class Player implements Scored {
    field name: string;
    construct() { this.name = "p1"; }
    public property Score: int { get { return 10; } }
    public property Name: string { get { return this.name; } set { this.name = value; } }
    public async method reset(): void { }
}

function main(args: string[]): int {
    let target: Scored = new Player();
    target.Name = "renamed";
    print(target.Name);
    return target.Score;
}
""");
        Assert.That(result, Is.EqualTo((10L, "renamed\n")));
    }

    [Test]
    public void Read_SelectsGetterOfRuntimeClass()
    {
        var result = Run(Shapes + """
function score(value: Scored): int { return value.Score; }
function main(args: string[]): int {
    let a: Scored = new Small(); let b: Scored = new Large();
    print(a.Name); print(b.Name);
    return score(a) + score(b) + 1;
}
""");
        Assert.That(result, Is.EqualTo((42L, "small\nlarge\n")));
    }

    [Test]
    public void Write_RunsSetterOfRuntimeClassImmediately()
    {
        var result = Run(Shapes + """
function main(args: string[]): int {
    let a: Scored = new Small(); let b: Scored = new Large();
    a.Name = "x"; b.Name = "y";
    print(a.Name); print(b.Name);
    return 0;
}
""");
        Assert.That(result, Is.EqualTo((0L, "large set\nx\ny\n")));
    }

    [Test]
    public void ArrayListAndForeachElements_ReadThroughContract()
    {
        var result = Run(Shapes + """
function pick(values: Scored[]): Scored { return values[1]; }
function main(args: string[]): int {
    let values: Scored[] = [new Small(), new Large()];
    var total = values[0].Score + pick(values).Score;
    foreach (value in values) { print(value.Name); }
    return total + 1;
}
""");
        Assert.That(result, Is.EqualTo((42L, "small\nlarge\n")));
    }

    [Test]
    public void InheritedAndDerivedClasses_SatisfyAndDispatch()
    {
        var result = Run("""
interface Scored { property Score: int { get; } }
class Base { public property Score: int { get { return 2; } } }
class FromBase extends Base implements Scored { }
class Direct implements Scored { public property Score: int { get { return 10; } } }
class Child extends Direct { }
function main(args: string[]): int {
    let a: Scored = new FromBase(); let b: Scored = new Child();
    return a.Score + b.Score * 4;
}
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void InterfaceInheritance_FlattensAndComposesContracts()
    {
        var result = Run("""
interface Named { property Name: string { get; } }
interface Counted { property Count: int { get; set; } }
interface Both extends Named, Counted { method emit(): void; }
class Item implements Both {
    field count: int;
    construct() { this.count = 1; }
    public property Name: string { get { return "item"; } }
    public property Count: int { get { return this.count; } set { this.count = value; } }
    public async method emit(): void { print("emit"); }
}
function main(args: string[]): int {
    let both: Both = new Item(); let named: Named = both; let counted: Counted = both;
    counted.Count = 41; both.Count = both.Count + 1;
    print(named.Name); both.emit(); tick();
    return counted.Count;
}
""");
        Assert.That(result, Is.EqualTo((42L, "item\nemit\n")));
    }

    [Test]
    public void IdenticalInheritedContracts_AreMerged()
    {
        var result = Run("""
interface A { property Value: int { get; } }
interface B { property Value: int { get; } }
interface C extends A, B { }
class Item implements C { public property Value: int { get { return 42; } } }
function main(args: string[]): int { let item: C = new Item(); let a: A = item; return a.Value; }
""");
        Assert.That(result.ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void GetOnlyContract_AcceptsPropertyWithSetterButRejectsAssignment()
    {
        const string types = """
interface Reading { property Value: int { get; } }
class Box implements Reading {
    field stored: int;
    construct() { this.stored = 42; }
    public property Value: int { get { return this.stored; } set { this.stored = value; } }
}
""";
        Assert.That(Run(types + "function main(args: string[]): int { let r: Reading = new Box(); return r.Value; }").ExitCode,
            Is.EqualTo(42));
        AssertRejected(types + "function main(args: string[]): int { let r: Reading = new Box(); r.Value = 1; return 0; }",
            "getter-only");
    }

    [Test]
    public void ReadDuringTick_UsesCommittedState()
    {
        var result = Run("""
interface Valued { property Value: int { get; } }
class Counter implements Valued {
    field value: int;
    construct() { this.value = 0; }
    public async method add(amount: int): void { this.value = this.value + amount; }
    public property Value: int { get { return this.value; } }
}
class Observer { public async method inspect(target: Valued): void { print(target.Value); } }
function main(args: string[]): int {
    var counter = new Counter(); var observer = new Observer(); let valued: Valued = counter;
    counter.add(5); observer.inspect(valued); tick();
    print(valued.Value);
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("0\n5\n"));
    }

    [Test]
    public void AccessorExceptionsAndInvalidReceivers_AreCatchable()
    {
        var result = Run("""
interface Valued { property Value: int { get; } }
class Broken implements Valued { public property Value: int { get { var zero = 0; return 1 / zero; } } }
function main(args: string[]): int {
    let value: Valued = new Broken();
    try { print(value.Value); } catch (ZeroDivisionException) { print("zero"); }
    delete value;
    try { print(value.Value); } catch (InvalidReferenceException) { print("invalid"); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("zero\ninvalid\n"));
    }

    [TestCase("Broken")]
    [TestCase("Valued")]
    public void GetterException_ResumesAtCallerCatchForClassAndInterfaceReceivers(string type)
    {
        var result = Run("""
interface Valued { property Value: int { get; } }
class Broken implements Valued { public property Value: int { get { var zero = 0; return 1 / zero; } } }
function main(args: string[]): int {
    let value: TYPE = new Broken();
    try { var read = value.Value; print(read); } catch (ZeroDivisionException) { print("zero"); }
    print("after");
    return 42;
}
""".Replace("TYPE", type));
        Assert.That(result, Is.EqualTo((42L, "zero\nafter\n")));
    }

    [Test]
    public void SetterException_ResumesAtCallerCatchAndKeepsCompletedWrites()
    {
        var result = Run("""
interface Rw { property Value: int { get; set; } }
class Box implements Rw {
    field stored: int;
    construct() { this.stored = 0; }
    public property Value: int { get { return this.stored; } set { this.stored = value; var zero = 0; var fail = 1 / zero; } }
}
function main(args: string[]): int {
    let box: Rw = new Box();
    try { box.Value = 42; } catch (ZeroDivisionException) { print("zero"); }
    return box.Value;
}
""");
        Assert.That(result, Is.EqualTo((42L, "zero\n")));
    }

    [TestCase("class Item implements Scored { }", "must provide public property 'Score: int' with { get; }")]
    [TestCase("class Item implements Scored { public property Score: string { get { return \"\"; } } }", "must provide public property")]
    [TestCase("class Item implements Rw { public property Value: int { get { return 0; } } }", "with { get; set; }")]
    public void MissingOrMismatchedImplementations_AreRejected(string declaration, string message)
        => AssertRejected("""
interface Scored { property Score: int { get; } }
interface Rw { property Value: int { get; set; } }
""" + declaration + "\nfunction main(args: string[]): int { return 0; }", message);

    [TestCase("interface A { property P: int { get; } } interface B { property P: string { get; } } interface C extends A, B { }", "conflicting types or accessors")]
    [TestCase("interface A { property P: int { get; } } interface B { property P: int { get; set; } } interface C extends A, B { }", "conflicting types or accessors")]
    [TestCase("interface A { property P: int { get; } } interface B { method P(): void; } interface C extends A, B { }", "both a method and a property")]
    [TestCase("interface A { method P(): void; } interface B { property P: int { get; } } interface C extends A, B { }", "both a method and a property")]
    [TestCase("interface A { property P: int { get; } property P: int { get; } }", "already declared or inherited")]
    [TestCase("interface A { property P: int { get; } method P(): void; }", "already declared or inherited")]
    [TestCase("interface A { property P: int { get; } } interface B extends A { property P: int { get; set; } }", "already declared or inherited")]
    [TestCase("interface A { property P: int { get { return 1; } } }", "have no bodies")]
    [TestCase("interface A { property P: int { set; } }", "expected 'get'")]
    [TestCase("interface A { property P: int[] { get; set; } }", "seven scalar types only")]
    [TestCase("interface A { method f(): int; }", "require void returns")]
    [TestCase("interface A { method f(): void { } }", "")]
    [TestCase("interface A { public method f(): void; }", "async method and property contracts")]
    public void InvalidInterfaceDeclarations_AreRejected(string declarations, string message)
        => AssertRejected(declarations + "\nfunction main(args: string[]): int { return 0; }", message);

    [TestCase("let s: Scored = new Small(); return s.Missing;", "is not a property contract")]
    [TestCase("let s: Scored = new Small(); s.Name = 1; return 0;", "")]
    [TestCase("let s: Scored = new Small(); var text: string = s.Score; return 0;", "")]
    public void InvalidUses_AreRejected(string main, string message)
        => AssertRejected(Shapes + "function main(args: string[]): int { " + main + " }", message);

    private static void AssertRejected(string source, string message)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain(message));
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
