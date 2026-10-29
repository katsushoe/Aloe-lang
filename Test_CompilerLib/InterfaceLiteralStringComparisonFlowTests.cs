using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceLiteralStringComparisonFlowTests
{
    [TestCase("\"\"", "==", false)]
    [TestCase("\"\"", "==", true)]
    [TestCase("\"\"", "!=", false)]
    [TestCase("\"\"", "!=", true)]
    [TestCase("\"a\"", "==", false)]
    [TestCase("\"a\"", "==", true)]
    [TestCase("\"a\"", "!=", false)]
    [TestCase("\"a\"", "!=", true)]
    [TestCase("\"b\"", "==", false)]
    [TestCase("\"b\"", "==", true)]
    [TestCase("\"b\"", "!=", false)]
    [TestCase("\"b\"", "!=", true)]
    [TestCase("\"line\\nnext\"", "==", false)]
    [TestCase("\"line\\nnext\"", "==", true)]
    [TestCase("\"line\\nnext\"", "!=", false)]
    [TestCase("\"line\\nnext\"", "!=", true)]
    [TestCase("\"quote\\\"x\"", "==", false)]
    [TestCase("\"quote\\\"x\"", "==", true)]
    [TestCase("\"quote\\\"x\"", "!=", false)]
    [TestCase("\"quote\\\"x\"", "!=", true)]
    [TestCase("\"😀\"", "==", false)]
    [TestCase("\"😀\"", "==", true)]
    [TestCase("\"😀\"", "!=", false)]
    [TestCase("\"😀\"", "!=", true)]
    public void LiteralEquality_HasNoCatchEdge(string literal, string op, bool reverse)
    {
        var expression = reverse ? $"(({literal})) {op} ((left))" : $"((left)) {op} (({literal}))";
        Assert.That(Run(Program($"let same: bool = {expression};", "value = new Plain();", "value.emit();")),
            Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("==")]
    [TestCase("!=")]
    public void OptionalLiteralComparisonFault_RetainsOnlyZeroDivision(string op)
    {
        var source = Program($"let same: bool = flag and (left {op} \"a\") and (1 / 0 == 0);", "", "value.emit();")
            .Replace("catch {  }", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
""");
        Assert.That(Run(source), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("source() == \"a\"")]
    [TestCase("\"a\" != source()")]
    [TestCase("if(left == \"a\") { value = new Plain(); }")]
    [TestCase("if(left != \"b\") { value = new Plain(); }")]
    public void CallsAndReferenceWrites_RemainConservative(string expression)
    {
        var body = expression.StartsWith("if(", StringComparison.Ordinal) ? expression : $"let same: bool = {expression};";
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(body, "value = new Plain();", "value.emit();")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("<")]
    [TestCase("<=")]
    [TestCase(">")]
    [TestCase(">=")]
    public void OrderedLiteralStringComparison_IsNotAdded(string op)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program($"let same: bool = left {op} \"a\";", "", "")));
    }

    [TestCase("left")]
    [TestCase("new Plain()")]
    public void CheckedCastOperand_RemainsConservative(string initializer)
    {
        var source = Program("let same: bool = (boxed as string) == \"a\";", "value = new Plain();", "value.emit();")
            .Replace("let maximum: int", $"let boxed: object = {initializer}; let maximum: int");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function fail(): int { return 1 / 0; }
function dangerous(): bool { let result: int = fail(); return true; }
function source(): string { let result: int = fail(); return "a"; }
function choose(flag: bool, left: string, right: string): int {
    let maximum: int = 2147483647;
    let value: object = new C();
    if(value is I) {
        try { {{body}} } catch { {{handler}} }
        {{after}}
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(false, "a", "a") + choose(true, "a", "b");
    tick();
    return result;
}
""";

    private static string Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder();
        vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.Zero);
        return output.ToString().Replace("\r\n", "\n");
    }
}
