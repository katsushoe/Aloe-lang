using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceScalarReadInitializerFlowTests
{
    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    public void DirectTypedRead_RetainsInterfaceFact(string type, string initial)
    {
        Assert.That(Run(Program(type, initial, $"let alias: {type} = ((input)); let copy: {type} = alias;", "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    public void ShadowedBranchAndLoopTypes_DoNotEscape(string type, string initial)
    {
        var body = $"if(flag) {{ let input: bool = true; }} while(flag) {{ let input: bool = false; break; }} let alias: {type} = input;";
        Assert.That(Run(Program(type, initial, body, "value = new Plain();", "value.emit();")), Is.EqualTo("safe\nsafe\n"));
    }

    [TestCase("byte", "255")]
    [TestCase("char", "'A'")]
    [TestCase("float", "1.5")]
    [TestCase("decimal", "1.5:d")]
    public void ObjectUnboxing_RemainsConservative(string type, string initial)
    {
        var source = Program(type, initial, $"let alias: {type} = boxed;", "value = new Plain();", "value.emit();")
            .Replace("let value: object = new C();", "let boxed: object = input; let value: object = new C();");
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string type, string initial, string body, string handler, string after) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("safe"); } }
class Plain { }
function choose(flag: bool, input: {{type}}): int {
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
