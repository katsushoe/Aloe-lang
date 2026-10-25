using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceSlotFlowTests
{
    private const string Declarations = """
interface I { method emit(message: string): void; }
function array(number: int): Integer[] { return [42]; }
class Store implements I {
    field value: Integer;
    field items: Integer[];
    field flags: Boolean[];
    construct() { this.value = new Integer(42); this.items = [42]; this.flags = [true]; }
    public property Items: Integer[] { get { return this.items; } }
    public property Flags: Boolean[] { get { return this.flags; } }
    public async method emit(message: string): void { print(message); }
    METHOD
}
""";

    [TestCase("this.value = 42;", "alive")]
    [TestCase("with (this) { .value = 42; }", "alive")]
    [TestCase("with (value as Store) { delete .value; }", "alive")]
    [TestCase("delete ((value as Store).Items[0]);", "alive")]
    [TestCase("with (value as Store) { let number: int = unbox .value; print(number); }", "42\nalive")]
    [TestCase("let number: int = unbox ((value as Store).Items[0]); print(number);", "42\nalive")]
    [TestCase("let number: int = unbox (((value as Store).Items))[0]; print(number);", "42\nalive")]
    public void HeapSlotMutationsDoNotInvalidateReceiverOrSameNamedLocalFacts(string operation, string expected)
    {
        var method = """
    public async method exercise(value: object): void {
        if (value is I) {
            var count = 0;
            while (count < 1) { OPERATION count = count + 1; }
            value.emit("alive");
        }
    }
""".Replace("OPERATION", operation);
        Assert.That(Run(method), Is.EqualTo(expected.Split('\n')));
    }

    [Test]
    public void ConsumingHeapSlotInConditionKeepsTheReceiversInterfaceProof()
    {
        var method = """
    public async method exercise(value: object): void {
        while ((value is I) and unbox ((value as Store).Flags[0])) { value.emit("condition"); break; }
    }
""";
        Assert.That(Run(method), Is.EqualTo(new[] { "condition" }));
    }

    [TestCase("while ((value is I) and unbox (value)) { value.emit(\"bad\"); }")]
    public void RealLocalMutationsIncludingNestedConsumptionStillInvalidateFacts(string body)
    {
        var source = Declarations.Replace("METHOD", "") + "function main(args: string[]): int { let value: object = new Store(); if (value is I) { " + body + " } return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.TypeOf<AloeCompileException>());
        if (body.Contains("value.emit(\"bad\");"))
            Assert.That(() => new AloeCompiler().Compile(source.Replace("value.emit(\"bad\");", "")), Throws.Nothing);
    }

    [TestCase("while (false) { delete (((value))); } value.emit(\"after\");")]
    [TestCase("while (false) { let number: int = unbox (((value))); } value.emit(\"after\");")]
    [TestCase("let items: Integer[] = [42]; while (false) { let number: int = unbox items[unbox value]; } value.emit(\"after\");")]
    [TestCase("while (false) { let number: int = unbox array(unbox value)[0]; } value.emit(\"after\");")]
    [TestCase("while (false) { value = new Store(); } value.emit(\"after\");")]
    public void UnreachableWhileMutationsDoNotInvalidateProvenFacts(string body)
    {
        var source = Declarations.Replace("METHOD", "") + "function main(args: string[]): int { let value: object = new Store(); if (value is I) { " + body + " } return 0; }";
        Assert.That(() => new AloeCompiler().Compile(source), Throws.Nothing);
    }

    private static string[] Run(string method)
    {
        var source = Declarations.Replace("METHOD", method) + """
function main(args: string[]): int { var store = new Store(); store.exercise(store); tick(); return 0; }
""";
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1)); Assert.That(vm.Pop().AsInt, Is.Zero);
        return output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
