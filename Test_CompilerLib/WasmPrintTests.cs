using Aloe.Aloe2Wasm;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.RuntimeLib;
using System.Diagnostics;
using System.Text.Json;

namespace Aloe.CompilerLib.Tests;

public sealed class WasmPrintTests
{
    [Test]
    public void ScalarPrintImportsPreserveTypesOrderCallsAndAloeBc()
    {
        const string source = """
function emit(value: int): int { print(value); return 40; }
function main(args: string[]): int {
    let answer: int = emit(-2147483648) + 2;
    print(2147483647);
    print(1.5);
    print(true);
    print(false);
    let value: byte = 255;
    print(value);
    print('A');
    return answer;
}
""";
        var compiler = new AloeCompiler();
        var expected = new[] { "-2147483648", "2147483647", "1.5", "true", "false", "255", "A" };
        var regular = QuietVm(compiler.Compile(source));
        var regularOutput = new List<string>();
        regular.OutputWriter = regularOutput.Add;
        regular.RunFromEntryPoint();
        Assert.That(regular.Pop().AsInt, Is.EqualTo(42));
        Assert.That(regularOutput, Is.EqualTo(expected));
        foreach (var module in new[] { compiler.CompileForWasm(source), AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source))) })
        {
            var vm = QuietVm(module);
            var output = new List<string>();
            vm.OutputWriter = output.Add;
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
            Assert.That(output, Is.EqualTo(expected));
            using var actual = RunNode(module, """
const output = [], kinds = [];
const instance = new WebAssembly.Instance(module, { aloe: {
 print_int(value) { kinds.push(typeof value); output.push(String(value)); },
 print_float(value) { kinds.push(typeof value); output.push(String(value)); },
 print_bool(value) { kinds.push(typeof value); output.push(value ? 'true' : 'false'); },
 print_byte(value) { kinds.push(typeof value); output.push(String(value)); },
 print_char(value) { kinds.push(typeof value); output.push(String.fromCharCode(value)); }
} });
const first = String(instance.exports.main());
const second = String(instance.exports._start());
result = { imports: WebAssembly.Module.imports(module), output, kinds, first, second };
""");
            var root = actual.RootElement;
            Assert.That(root.GetProperty("first").GetString(), Is.EqualTo("42"));
            Assert.That(root.GetProperty("second").GetString(), Is.EqualTo("42"));
            Assert.That(root.GetProperty("output").EnumerateArray().Select(v => v.GetString()), Is.EqualTo(expected.Concat(expected)));
            var imports = root.GetProperty("imports").EnumerateArray().ToArray();
            Assert.That(imports.Select(v => v.GetProperty("module").GetString()), Is.All.EqualTo("aloe"));
            Assert.That(imports.Select(v => v.GetProperty("kind").GetString()), Is.All.EqualTo("function"));
            Assert.That(imports.Select(v => v.GetProperty("name").GetString()), Is.EquivalentTo(new[] { "print_int", "print_float", "print_bool", "print_byte", "print_char" }));
            Assert.That(root.GetProperty("kinds").EnumerateArray().Select(v => v.GetString()),
                Is.EqualTo(new[] { "bigint", "bigint", "number", "number", "number", "number", "number" }.Concat(new[] { "bigint", "bigint", "number", "number", "number", "number", "number" })));
        }
    }

    [TestCase("fraction() + 2", "3.5", "print_float")]
    [TestCase("fraction() < 2", "true", "print_bool")]
    [TestCase("fraction() % 1", "0.5", "print_float")]
    public void PrintExpressionsUseFloatReturnTypesFromCalls(string expression, string expected, string importName)
    {
        var source = $$"""
function fraction(): float { return 1.5; }
function main(args: string[]): int { print({{expression}}); return 0; }
""";
        var compiler = new AloeCompiler();
        var vm = QuietVm(compiler.Compile(source));
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(output, Is.EqualTo(new[] { expected }));
        using var actual = RunNode(compiler.CompileForWasm(source), $$"""
const output = [];
const instance = new WebAssembly.Instance(module, { aloe: { {{importName}}(value) { output.push('{{importName}}' === 'print_bool' ? String(value === 1) : String(value)); } } });
instance.exports.main(); result = output;
""");
        Assert.That(actual.RootElement.EnumerateArray().Select(v => v.GetString()), Is.EqualTo(new[] { expected }));
    }

    [TestCase(0)]
    [TestCase(0xD800)]
    [TestCase(0xFFFF)]
    public void CharPrintPreservesOneUtf16CodeUnit(int value)
    {
        var module = PrintModule(AloeValue.FromChar((char)value));
        var vm = QuietVm(module);
        var output = new List<string>();
        vm.OutputWriter = output.Add;
        vm.RunFromEntryPoint();
        Assert.That(output.Single(), Is.EqualTo(new string((char)value, 1)));
        using var actual = RunNode(module, """
let observed;
const instance = new WebAssembly.Instance(module, { aloe: { print_char(value) { observed = value; } } });
instance.exports.main();
result = observed;
""");
        Assert.That(actual.RootElement.GetInt32(), Is.EqualTo(value));
    }

    [Test]
    public void NoPrintModuleRemainsImportFree()
    {
        var module = new AloeCompiler().CompileForWasm("function main(args: string[]): int { return 42; }");
        using var actual = RunNode(module, "result = { imports: WebAssembly.Module.imports(module), value: String(new WebAssembly.Instance(module).exports.main()) };");
        Assert.That(actual.RootElement.GetProperty("imports").GetArrayLength(), Is.Zero);
        Assert.That(actual.RootElement.GetProperty("value").GetString(), Is.EqualTo("42"));
    }

    [TestCase("missing")]
    [TestCase("notCallable")]
    public void ScalarPrintRequiresExplicitCallableHostImport(string scenario)
    {
        var imports = scenario == "missing" ? "{}" : "{ aloe: { print_int: 7 } }";
        using var actual = RunNode(PrintModule(AloeValue.FromInt(1)), $$"""
try { new WebAssembly.Instance(module, {{imports}}); result = 'accepted'; }
catch (error) { result = error.name; }
""");
        Assert.That(actual.RootElement.GetString(), Is.AnyOf("TypeError", "LinkError"));
    }

    [Test]
    public void ThrowingPrintCallbackPreservesCompletedPrivateGcState()
    {
        var module = new AloeCompiler().CompileForWasm("function main(args: string[]): int { if (GC.debug) { return 42; } GC.debug = true; print(1); return 0; }");
        using var actual = RunNode(module, """
let calls = 0;
const failure = new Error('host output failure');
const instance = new WebAssembly.Instance(module, { aloe: { print_int() { calls++; throw failure; } } });
let propagated = false;
try { instance.exports.main(); } catch (error) { propagated = error === failure; }
result = { propagated, calls, next: String(instance.exports.main()) };
""");
        Assert.That(actual.RootElement.GetProperty("propagated").GetBoolean(), Is.True);
        Assert.That(actual.RootElement.GetProperty("calls").GetInt32(), Is.EqualTo(1));
        Assert.That(actual.RootElement.GetProperty("next").GetString(), Is.EqualTo("42"));
    }

    [Test]
    public void MissingRawPrintOperandIsRejected()
    {
        var module = new Module(Array.Empty<AloeValue>(),
            new[] { new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.Return) },
            new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), null) }, 0);
        Assert.That(() => new AloeWasmCompiler().Compile(module), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Print requires"));
    }

    private static Module PrintModule(AloeValue value) => new(
        new[] { value, AloeValue.FromInt(0) },
        new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.PushConst, 1), new Instruction(EnumOpcode.Return) },
        new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) }, 0);

    private static AloeVm QuietVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        return new AloeVm(module, settings);
    }

    private static JsonDocument RunNode(Module module, string script)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ALOE_TEST_NODE") ?? "node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("const fs = require('node:fs'); const module = new WebAssembly.Module(Buffer.from(fs.readFileSync(0, 'utf8'), 'base64')); let result;\n" + script + "\nprocess.stdout.write(JSON.stringify(result));");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Node.js WASM test engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(Convert.ToBase64String(new AloeWasmCompiler().Compile(module)));
        process.StandardInput.Close();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Node.js print-import tests exceeded 60 seconds.");
        }
        Assert.That(process.ExitCode, Is.Zero, error.GetAwaiter().GetResult());
        return JsonDocument.Parse(output.GetAwaiter().GetResult());
    }
}
