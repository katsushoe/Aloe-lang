using Aloe.CommonLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceInferredPrimitiveInitializerTests
{
    [TestCase("var alias = input; alias = input;", true)]
    [TestCase("var alias = (2 + 3) * 4; alias = 0;", true)]
    [TestCase("var alias = \"literal\"; alias = \"changed\";", true)]
    [TestCase("var alias = true; alias = false;", true)]
    [TestCase("var alias = left == right; alias = not flag;", true)]
    [TestCase("var alias = true or dangerous();", true)]
    [TestCase("if(true) { var alias = input; alias = input; }", true)]
    [TestCase("var alias = input; if(flag) { var alias = alias; alias = 1; }", true)]
    [TestCase("let alias: int = input; if(flag) { var alias = false; alias = flag; } alias = 1;", true)]
    [TestCase("var alias = dangerous();", false)]
    [TestCase("var alias = input + 1;", false)]
    [TestCase("var alias = boxed as string;", false)]
    [TestCase("var alias = value;", false)]
    [TestCase("var alias = items[0];", false)]
    [TestCase("var alias = item.text;", false)]
    [TestCase("var alias = 1 / 0;", false)]
    [TestCase("var alias = flag and (1 / 0 == 0);", false)]
    public void InferredInitializer_HasBoundedNonthrowingFlow(string body, bool accepted)
    {
        var source = Program("int", "1", body, "value = new Plain();", "value.emit();");
        if (accepted) Assert.That(Run(source), Is.EqualTo("safe\nsafe\n"));
        else
        {
            var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
            Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
        }
    }

    [TestCase("int", "2147483647")]
    [TestCase("bool", "true")]
    [TestCase("string", "\"text\"")]
    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    public void InferredPrimitiveRead_PreservesTypeThroughBranchesAndLoops(string type, string initial)
    {
        var body = "var alias = ((input)); alias = input; if(flag) { var inner = alias; inner = input; } while(flag) { var inner = alias; inner = input; break; }";
        Assert.That(Run(Program(type, initial, body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    private static string Program(string type, string initial, string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
struct Item { field text: string; }
function source(): string { let fail: int = 1 / 0; return "never"; }
function dangerous(): bool { let fail: int = 1 / 0; return true; }
function choose(flag: bool, input: {{type}}): int {
    let boxed: object = new Plain();
    let left: string = "a"; let right: string = "b";
    let items: string[] = ["a"]; let item: Item = Item { text: "a" };
    let value: object = new C();
    if(value is I) {
        try { {{body}} } catch { {{handler}} }
        {{after}}
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose(false, {{initial}}) + choose(true, {{initial}});
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
