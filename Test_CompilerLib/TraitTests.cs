using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

// Decision 0062: stateless traits flattened into classes at compile time.
public sealed class TraitTests
{
    private const string Traits = """
trait Named { require method name(): string; method label(): string { return this.name(); } }
trait Greeter with Named { public async method greet(): void { print(this.label()); } }
trait Counter { method one(): int { return 1; } }
""";

    private const string Player = """
class Player with Greeter, Counter {
    private method name(): string { return "player"; }
    public async method run(): void { print(this.one()); }
}
""";

    [Test]
    public void DeclarationApplicationCompositionAndRequirements_Run()
    {
        var result = Run(Traits + Player + """
function main(args: string[]): int { var p = new Player(); p.run(); p.greet(); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("1\nplayer\n"));
    }

    [Test]
    public void ClassMethodTakesPriorityOverTraitMethod()
    {
        var result = Run(Traits + """
class Player with Greeter {
    private method name(): string { return "unused"; }
    private method label(): string { return "class label"; }
}
function main(args: string[]): int { var p = new Player(); p.greet(); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("class label\n"));
    }

    [Test]
    public void TraitTypes_AssignPassStoreCastTestAndDispatch()
    {
        var result = Run(Traits + Player + """
class Other with Greeter { private method name(): string { return "other"; } }
class Child extends Player { }
function hello(value: Greeter): void { value.greet(); }
function main(args: string[]): int {
    let first: Greeter = new Player(); let named: Named = first;
    let values: Greeter[] = [first, new Other(), new Child()];
    foreach (value in values) { hello(value); }
    let any: object = values[1];
    print(any is Greeter); print(any is Counter);
    let back: Greeter = any as Greeter; back.greet();
    tick();
    return values.Length;
}
""");
        // Calls to different Objects in one Tick have no mutual order; compare them as a multiset.
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(result.ExitCode, Is.EqualTo(3));
        Assert.That(lines.Take(2), Is.EqualTo(new[] { "true", "false" }));
        Assert.That(lines.Skip(2), Is.EquivalentTo(new[] { "player", "other", "player", "other" }));
    }

    [Test]
    public void SpecificationExample_CompilesAndRuns()
    {
        // Same program as the 7.5 example in Documents/en|ja/Aloe-lang-spec.html.
        var result = Run("""
trait Named {
    require method name(): string;
    method label(): string { return this.name(); }
}

trait Greeter with Named {
    public async method greet(): void { print(this.label()); }
}

class Player with Greeter {
    private method name(): string { return "player"; }
}

function hello(value: Greeter): void { value.greet(); }

function main(args: string[]): int {
    hello(new Player());
    tick();
    return 0;
}
""");
        Assert.That(result, Is.EqualTo((0L, "player\n")));
    }

    [Test]
    public void TraitConstantNamesDeclaringTraitAndClassConstantNamesClass()
    {
        var result = Run("""
trait Inner { method where(): string { return __TRAIT__ + ":" + __CLASS__; } }
trait Outer with Inner { public async method show(): void { print(__TRAIT__); print(this.where()); } }
class Host with Outer { }
function main(args: string[]): int { var h = new Host(); h.show(); tick(); return 0; }
""".Replace("return __TRAIT__ + \":\" + __CLASS__;", "print(__TRAIT__); return __CLASS__;"));
        Assert.That(result.Output, Is.EqualTo("Outer\nInner\nHost\n"));
    }

    [Test]
    public void TraitMethodsSatisfyInterfaceContracts()
    {
        var result = Run("""
interface Resettable { method reset(): void; property Score: int { get; } }
trait ResetDefaults { public async method reset(): void { print("reset"); } }
class Game implements Resettable with ResetDefaults { public property Score: int { get { return 7; } } }
function main(args: string[]): int { let r: Resettable = new Game(); r.reset(); tick(); return r.Score; }
""");
        Assert.That(result, Is.EqualTo((7L, "reset\n")));
    }

    [Test]
    public void RequirementsMaySatisfyFromBaseClassOrAnotherTrait()
    {
        var result = Run("""
trait Needs { require method value(): int; public async method show(): void { print(this.value()); } }
trait Gives { method value(): int { return 5; } }
class Base { private method value(): int { return 9; } }
class FromBase extends Base with Needs { }
class FromTrait with Needs, Gives { }
function main(args: string[]): int { var a = new FromBase(); var b = new FromTrait(); a.show(); b.show(); tick(); return 0; }
""");
        Assert.That(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EquivalentTo(new[] { "9", "5" }));
    }

    [Test]
    public void SharedIncludedTraitIsNotAConflict()
    {
        var result = Run("""
trait Root { method id(): int { return 3; } }
trait Left with Root { }
trait Right with Root { }
class Both with Left, Right { public async method show(): void { print(this.id()); } }
function main(args: string[]): int { var b = new Both(); b.show(); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("3\n"));
    }

    [Test]
    public void ConflictsResolvedByClassDefinition()
    {
        var result = Run("""
trait A { method id(): int { return 1; } }
trait B { method id(): int { return 2; } }
class C with A, B { private method id(): int { return 42; } public async method show(): void { print(this.id()); } }
function main(args: string[]): int { var c = new C(); c.show(); tick(); return 0; }
""");
        Assert.That(result.Output, Is.EqualTo("42\n"));
    }

    [Test]
    public void AsyncCycleThroughExpandedTraitMethodsIsRejected()
        => AssertRejected("""
trait Ping { public async method ping(): void { this.pong(); } public async method pong(): void { this.ping(); } }
class Loop with Ping { }
function main(args: string[]): int { return 0; }
""", "Async CallGraph cycle");

    [TestCase("trait A { method id(): int { return 1; } } trait B { method id(): int { return 2; } } class C with A, B { }", "conflict in class 'C'")]
    [TestCase("trait A { method id(): int { return 1; } } class Base { private method id(): int { return 0; } } class C extends Base with A { }", "conflicts with a method inherited")]
    [TestCase("trait A { require method id(): int; } class C with A { }", "matching the requirement of trait 'A'")]
    [TestCase("trait A { require method id(): int; } class C with A { private method id(): string { return \"\"; } }", "matching the requirement of trait 'A'")]
    [TestCase("trait A { field x: int; }", "Traits may declare only methods")]
    [TestCase("trait A { construct() { } }", "Traits may declare only methods")]
    [TestCase("trait A { public property P: int { get { return 1; } } }", "Traits may declare only methods")]
    [TestCase("trait A { public static async method s(): void { } }", "cannot be static")]
    [TestCase("trait A { public virtual async method v(): void { } }", "cannot be virtual")]
    [TestCase("trait A { public method p(): void { } }", "must be declared async")]
    [TestCase("trait A { private async method p(): void { } }", "Only public async trait methods")]
    [TestCase("trait A { public async method p(): int { return 1; } }", "must return void")]
    [TestCase("trait A { method p(): void; }", "need a body")]
    [TestCase("trait A { method p(): void { } method p(): void { } }", "already declared")]
    [TestCase("trait A with A { }", "previously declared traits")]
    [TestCase("trait A with B { } trait B { }", "previously declared traits")]
    [TestCase("trait A { } trait A { }", "already declared")]
    [TestCase("trait A { } class C implements A { }", "applied with 'with'")]
    [TestCase("interface I { } class C with I { }", "implemented with 'implements'")]
    [TestCase("trait A { } interface I extends A { }", "cannot extend trait")]
    [TestCase("trait A { } class C with A, A { }", "listed more than once")]
    [TestCase("class C { field x: int; } trait A { method f(): int { return this.x; } } class D with A { field x: int; }", "cannot access class fields")]
    [TestCase("trait A { method f(): int { return this.secret(); } } class D with A { private method secret(): int { return 1; } }", "may call only its own")]
    public void InvalidTraitDeclarationsAndApplications_AreRejected(string declarations, string message)
        => AssertRejected(declarations + "\nfunction main(args: string[]): int { return 0; }", message);

    [Test]
    public void StructsCannotApplyTraits()
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            "trait A { }\nstruct S with A { field x: int; }\nfunction main(args: string[]): int { return 0; }"));

    [Test]
    public void PrivateTraitMethodsAreNotCallableThroughTraitTypes()
        => AssertRejected(Traits + Player + """
function main(args: string[]): int { let g: Greeter = new Player(); g.label(); return 0; }
""", "is not a public async instance method");

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
