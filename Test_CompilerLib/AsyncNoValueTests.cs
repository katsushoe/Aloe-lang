using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0064: async methods return no values; results flow through properties or explicit call-backs.
public sealed class AsyncNoValueTests
{
    private static void Rejects(string source)
        => Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>(),
            "value-returning async must not compile");

    [Test]
    public void InstanceAsyncMethodCannotReturnAValue()
        => Rejects("""
class Shop { construct() { } public async method price(id: int): int { return 1; } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void StaticAsyncMethodCannotReturnAValue()
        => Rejects("""
class Shop { public static async method price(id: int): int { return 1; } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void InterfaceAsyncContractCannotReturnAValue()
        => Rejects("""
interface Pricing { method price(id: int): int; }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void TaskMethodCannotReturnAValue()
        => Rejects("""
task Worker { construct() { } method run(): void { } method price(): int { return 1; } }
function main(args: string[]): int { return 0; }
""");

    [Test]
    public void AsyncCallCannotBeUsedAsAValue()
        => Rejects("""
class Shop { construct() { } public async method buy(id: int): void { } }
function main(args: string[]): int { var shop = new Shop(); var x = shop.buy(1) + 1; return 0; }
""");

    [Test]
    public void ResultsFlowThroughPropertiesAndCallBacks()
    {
        const string source = """
class Caller {
    field received: int;
    construct() { this.received = 0; }
    public async method onPrice(value: int): void { this.received = value; }
    public property Received: int { get { return this.received; } }
}
class Shop {
    field last: int;
    construct() { this.last = 0; }
    public async method price(id: int, caller: Caller): void {
        this.last = id * 10;
        caller.onPrice(id * 10);
    }
    public property Last: int { get { return this.last; } }
}
function main(args: string[]): int {
    var caller = new Caller();
    var shop = new Shop();
    shop.price(4, caller);
    tick();
    print(shop.Last);
    print(caller.Received);
    return 0;
}
""";
        var output = new System.Text.StringBuilder();
        var vm = new AloeVm(new AloeCompiler().Compile(source)) { OutputWriter = line => output.Append(line).Append('\n') };
        vm.RunFromEntryPoint();
        Assert.That(output.ToString(), Is.EqualTo("40\n40\n"));
    }
}
