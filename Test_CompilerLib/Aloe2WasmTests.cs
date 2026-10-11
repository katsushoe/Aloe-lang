using Aloe.Aloe2Wasm;
using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Diagnostics;
using System.Text.Json;

namespace Aloe.CompilerLib.Tests;

public sealed class Aloe2WasmTests
{
    [Test]
    public void IntegerBitwise_SourceBoundariesAndPrecedenceMatchVmAndWasm()
    {
        var cases = new (string Expression, long Expected)[]
        {
            ("0 & -1", 0), ("2147483647 & -2147483648", 0),
            ("-2147483648 | 2147483647", -1), ("-2147483648 ^ 2147483647", -1),
            ("~0", -1), ("~-1", 0), ("~2147483647", int.MinValue),
            ("~-2147483648", int.MaxValue), ("~~1", 1),
            ("1 << 0", 1), ("1 << 31", int.MinValue), ("-2147483648 << 1", 0),
            ("2147483647 << 1", -2), ("-1 << 31", int.MinValue),
            ("-2147483648 >> 0", int.MinValue), ("-2147483648 >> 1", -1073741824),
            ("-2147483648 >> 31", -1), ("2147483647 >> 31", 0), ("-1 >> 1", -1),
            ("1 | 2 ^ 3 & 1", 3), ("1 << 1 + 2", 8), ("1 << 3 >> 1", 4),
            ("(1 | 2) << 1", 6), ("~1 & 7", 6), ("1 | 1 << 2", 5),
            ("1 + 2 << 2 * 2", 48), ("7 & 3 & 1", 1)
        };
        var modules = cases.Select(c => AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(
            "function main(args: string[]): int { return " + c.Expression + "; }")))).ToArray();
        for (var i = 0; i < modules.Length; i++)
            Assert.That(RunVm(modules[i]), Is.EqualTo(cases[i].Expected), cases[i].Expression);
        Assert.That(RunWasm(modules), Is.EqualTo(cases.Select(c => c.Expected.ToString()).ToArray()));
    }

    [Test]
    public void IntegerBitwise_SourceCallsLocalsLoopsAndBranchesKeepIntType()
    {
        const string source = """
bitfield enum Flags { A, B }
function mask(value: int, count: int): int { return (value << count) ^ ~value; }
function main(args: string[]): int {
    var total = 0; var index = 0;
    while (index < 4) { total = total | 1 << index; index = index + 1; }
    var inverted = ~total;
    let bits: Flags = Flags.A | Flags.B;
    if ((total & 7) == 7 and mask(3, 1) == -6 and inverted == -16 and bits == (Flags.A | Flags.B)) {
        return (total ^ 5) << 2 | 2;
    }
    return 0;
}
""";
        var regular = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void IntegerBitwise_SourceInvalidOperandsAndCountsFailInVmAndWasm()
    {
        var expressions = new List<string> { "~2147483648", "~-2147483649" };
        foreach (var value in new[] { "2147483648", "-2147483649" })
        foreach (var op in new[] { "&", "|", "^", "<<", ">>" })
        {
            expressions.Add(value + " " + op + " 1");
            expressions.Add("1 " + op + " " + value);
        }
        foreach (var count in new[] { "-1", "32" })
        foreach (var op in new[] { "<<", ">>" }) expressions.Add("1 " + op + " " + count);
        var modules = expressions.Select(expr => AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(
            "function main(args: string[]): int { return " + expr + "; }")))).ToArray();
        foreach (var module in modules) Assert.Throws<VmException>(() => RunVm(module));
        Assert.That(RunWasm(modules), Is.All.EqualTo("trap"));
    }

    [Test]
    public void IntegerBitwise_SourceRejectsMixedAndNonIntTypes()
    {
        const string declarations = "bitfield enum A { First } bitfield enum B { First } enum Normal { First } ";
        string[] statements =
        [
            "return 1 | A.First;", "return A.First & 1;", "return 1 ^ true;", "return true | false;",
            "return 1 & 1.0;", "return 1 | 1m;", "return 1 ^ 'a';", "return ~true;", "return ~Normal.First;",
            "let value: byte = 1; return ~value;", "let value: byte = 1; return value | 1;",
            "let value: byte = 1; return 1 << value;", "let value: byte = 1; return value >> 1;",
            "return 1 << true;", "return 1 >> 1.0;", "return 1 << A.First;", "return Normal.First >> 1;",
            "let value: A = 1 | 2; return 0;", "let value: int = A.First | A.First; return 0;",
            "let value: B = A.First << 1; return 0;", "return 1 & 1 == 1;",
            "return \"x\" | \"y\";", "return A.First | B.First;"
        ];
        foreach (var statement in statements)
        {
            var source = declarations + "function main(args: string[]): int { " + statement + " }";
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source), statement);
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(source), statement);
        }
    }

    [Test]
    public void IntegerArithmetic_Int32BoundariesAndSignedDivisionMatchVm()
    {
        var cases = new (EnumOpcode Op, long Left, long Right, long Expected)[]
        {
            (EnumOpcode.Add, int.MaxValue, 0, int.MaxValue),
            (EnumOpcode.Add, int.MinValue, 0, int.MinValue),
            (EnumOpcode.Add, int.MaxValue, int.MinValue, -1),
            (EnumOpcode.Sub, int.MinValue, 0, int.MinValue),
            (EnumOpcode.Sub, int.MaxValue, 0, int.MaxValue),
            (EnumOpcode.Sub, int.MinValue, int.MinValue, 0),
            (EnumOpcode.Mul, int.MinValue, 1, int.MinValue),
            (EnumOpcode.Mul, int.MaxValue, 1, int.MaxValue),
            (EnumOpcode.Mul, 46340, 46340, 2147395600),
            (EnumOpcode.Mul, -46340, 46340, -2147395600),
            (EnumOpcode.Div, 7, 2, 3), (EnumOpcode.Div, -7, 2, -3),
            (EnumOpcode.Div, 7, -2, -3), (EnumOpcode.Div, -7, -2, 3),
            (EnumOpcode.Div, 1, 2, 0), (EnumOpcode.Div, -1, 2, 0),
            (EnumOpcode.Div, int.MinValue, 1, int.MinValue),
            (EnumOpcode.Div, int.MinValue, int.MaxValue, -1),
            (EnumOpcode.Mod, 7, 2, 1), (EnumOpcode.Mod, -7, 2, -1),
            (EnumOpcode.Mod, 7, -2, 1), (EnumOpcode.Mod, -7, -2, -1),
            (EnumOpcode.Mod, int.MinValue, -1, 0),
            (EnumOpcode.Mod, int.MinValue, int.MaxValue, -1),
        };
        var modules = cases.Select(c => AloeBcCodec.Read(AloeBcCodec.Write(BitwiseModule(c.Op, c.Left, c.Right)))).ToArray();
        for (var i = 0; i < modules.Length; i++)
            Assert.That(RunVm(modules[i]), Is.EqualTo(cases[i].Expected), cases[i].ToString());
        Assert.That(RunWasm(modules), Is.EqualTo(cases.Select(c => c.Expected.ToString()).ToArray()));
    }

    [Test]
    public void IntegerArithmetic_OverflowAndOutOfRangeOperandsFailInVmAndWasm()
    {
        var cases = new List<(EnumOpcode Op, long Left, long Right)>
        {
            (EnumOpcode.Add, int.MaxValue, 1), (EnumOpcode.Add, int.MinValue, -1),
            (EnumOpcode.Sub, int.MinValue, 1), (EnumOpcode.Sub, int.MaxValue, -1),
            (EnumOpcode.Sub, 0, int.MinValue),
            (EnumOpcode.Mul, 46341, 46341), (EnumOpcode.Mul, int.MinValue, -1),
            (EnumOpcode.Mul, int.MaxValue, 2), (EnumOpcode.Div, int.MinValue, -1),
        };
        foreach (var op in new[] { EnumOpcode.Add, EnumOpcode.Sub, EnumOpcode.Mul, EnumOpcode.Div, EnumOpcode.Mod })
        foreach (var value in new[] { 2147483648L, -2147483649L, long.MinValue, long.MaxValue })
        {
            cases.Add((op, value, 1));
            cases.Add((op, 1, value));
        }
        var modules = cases.Select(c => AloeBcCodec.Read(AloeBcCodec.Write(BitwiseModule(c.Op, c.Left, c.Right)))).ToArray();
        foreach (var module in modules) Assert.Throws<OverflowException>(() => RunVm(module));
        Assert.That(RunWasm(modules), Is.All.EqualTo("trap"));
    }

    [Test]
    public void IntegerArithmetic_SourceCallsBranchesAndResultsKeepIntType()
    {
        const string source = """
            function divide(left: int, right: int): int { return left / right; }
            function main(args: string[]): int {
                var n = divide(-7, 2);
                var min = -2147483648;
                if (n == -3 and divide(1, 2) == 0 and min % -1 == 0) {
                    return 40 + (divide(7, 2) - 1) * 1;
                }
                return 0;
            }
            """;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(RunVm(new AloeCompiler().Compile(source)), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
        var invalid = new[] { "2147483647 + 1", "-2147483648 - 1", "46341 * 46341", "-2147483648 / -1" }
            .Select(expr => new AloeCompiler().CompileForWasm("function main(args: string[]): int { return " + expr + "; }")).ToArray();
        foreach (var badModule in invalid) Assert.Throws<OverflowException>(() => RunVm(badModule));
        Assert.That(RunWasm(invalid), Is.All.EqualTo("trap"));
    }

    [Test]
    public void IntegerArithmetic_BytePromotionAndFloatingDecimalDivisionRemainTyped()
    {
        Assert.That((AloeValue.FromByte(7) / AloeValue.FromByte(2)).AsInt, Is.EqualTo(3));
        Assert.That((AloeValue.FromByte(7) + AloeValue.FromInt(-2)).AsInt, Is.EqualTo(5));
        Assert.That((AloeValue.FromInt(7) / AloeValue.FromFloat(2)).AsFloat, Is.EqualTo(3.5));
        Assert.That((AloeValue.FromInt(7) / AloeValue.FromDecimal(2)).AsDecimal, Is.EqualTo(3.5m));
        Assert.Throws<OverflowException>(() => { _ = AloeValue.FromInt(int.MaxValue) + AloeValue.FromInt(1); });
        Assert.Throws<OverflowException>(() => { _ = AloeValue.FromInt(int.MinValue) - AloeValue.FromInt(1); });
        Assert.Throws<OverflowException>(() => { _ = AloeValue.FromInt(int.MaxValue) * AloeValue.FromInt(2); });
        Assert.Throws<OverflowException>(() => { _ = AloeValue.FromInt(int.MinValue) / AloeValue.FromInt(-1); });
    }

    [Test]
    public void IntegerArithmetic_ZeroDivisorsFailInVmAndWasm()
    {
        var modules = new List<Module>();
        foreach (var op in new[] { EnumOpcode.Div, EnumOpcode.Mod })
        foreach (var value in new[] { 0L, 1L, -1L, int.MinValue, int.MaxValue })
        {
            var module = BitwiseModule(op, value, 0);
            module = AloeBcCodec.Read(AloeBcCodec.Write(module));
            Assert.Throws<ZeroDivisionException>(() => RunVm(module));
            modules.Add(module);
        }
        Assert.That(RunWasm(modules), Is.All.EqualTo("trap"));
    }

    [Test]
    public void BitfieldOpcodes_ExecuteLikeVmAfterAloeBcRoundTrip()
    {
        var cases = new (EnumOpcode Op, long Left, long Right, long Expected)[]
        {
            (EnumOpcode.BitAnd, -1, int.MinValue, int.MinValue),
            (EnumOpcode.BitOr, int.MinValue, 3, -2147483645),
            (EnumOpcode.BitXor, -1, int.MinValue, int.MaxValue),
            (EnumOpcode.BitNot, 0, 0, -1),
            (EnumOpcode.BitNot, 0, int.MinValue, int.MaxValue),
            (EnumOpcode.BitShiftLeft, 1, 0, 1),
            (EnumOpcode.BitShiftLeft, 1, 31, int.MinValue),
            (EnumOpcode.BitShiftLeft, int.MinValue, 1, 0),
            (EnumOpcode.BitShiftLeft, -1, 31, int.MinValue),
            (EnumOpcode.BitShiftRight, int.MinValue, 0, int.MinValue),
            (EnumOpcode.BitShiftRight, int.MinValue, 1, -1073741824),
            (EnumOpcode.BitShiftRight, int.MinValue, 31, -1),
            (EnumOpcode.BitShiftRight, int.MaxValue, 31, 0),
        };
        var modules = cases.Select(c => AloeBcCodec.Read(AloeBcCodec.Write(BitwiseModule(c.Op, c.Left, c.Right)))).ToArray();
        for (var i = 0; i < modules.Length; i++)
            Assert.That(RunVm(modules[i]), Is.EqualTo(cases[i].Expected), cases[i].ToString());
        Assert.That(RunWasm(modules), Is.EqualTo(cases.Select(c => c.Expected.ToString()).ToArray()));
    }

    [Test]
    public void BitfieldOpcodes_InvalidCountsAndOperandsTrapLikeVm()
    {
        var modules = new List<Module>();
        foreach (var op in new[] { EnumOpcode.BitShiftLeft, EnumOpcode.BitShiftRight })
        foreach (var count in new[] { -1L, 32L, 64L, 2147483648L, -2147483649L, long.MinValue, long.MaxValue })
            modules.Add(BitwiseModule(op, 1, count));
        foreach (var op in new[] { EnumOpcode.BitAnd, EnumOpcode.BitOr, EnumOpcode.BitXor, EnumOpcode.BitNot,
            EnumOpcode.BitShiftLeft, EnumOpcode.BitShiftRight })
        foreach (var invalid in new[] { 2147483648L, -2147483649L, long.MinValue, long.MaxValue })
        {
            modules.Add(BitwiseModule(op, invalid, op == EnumOpcode.BitNot ? invalid : 1));
            if (op is EnumOpcode.BitAnd or EnumOpcode.BitOr or EnumOpcode.BitXor)
                modules.Add(BitwiseModule(op, 1, invalid));
        }
        foreach (var module in modules) Assert.Throws<VmException>(() => RunVm(module));
        Assert.That(RunWasm(modules), Is.All.EqualTo("trap"));
    }

    [Test]
    public void BitfieldOpcodes_PreserveParametersLocalsCallsAndControlStack()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromInt(-9), AloeValue.FromInt(1), AloeValue.FromInt(31) },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0),
                new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(EnumOpcode.Call, 1),
                new Instruction(EnumOpcode.Add),
                new Instruction(EnumOpcode.Return),
                new Instruction(EnumOpcode.LoadLocal, 0),
                new Instruction(EnumOpcode.StoreLocal, 1),
                new Instruction(EnumOpcode.Block),
                new Instruction(EnumOpcode.LoadLocal, 1),
                new Instruction(EnumOpcode.PushConst, 2),
                new Instruction(EnumOpcode.BitShiftLeft),
                new Instruction(EnumOpcode.StoreLocal, 1),
                new Instruction(EnumOpcode.End),
                new Instruction(EnumOpcode.LoadLocal, 1),
                new Instruction(EnumOpcode.BitNot),
                new Instruction(EnumOpcode.Return),
            },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int),
                new FunctionInfo("shift", 5, new[] { EnumValueKind.Int }, new[] { EnumValueKind.Int, EnumValueKind.Int }, EnumValueKind.Int)
            }, entryPointIndex: 0);
        module = AloeBcCodec.Read(AloeBcCodec.Write(module));
        Assert.That(RunVm(module), Is.EqualTo(2147483638L));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "2147483638" }));
    }

    [Test]
    public void BitfieldSource_WithCompilerFunctionMetadata_ExecutesInWasm()
    {
        const string source = """
            bitfield enum Flags { A, B, High = 1 << 31 }
            function evaluate(): int {
                var mask = Flags.A | Flags.B;
                mask = (mask << 1) ^ Flags.B;
                if ((mask & ~Flags.A) == (Flags.B << 1)) {
                    if ((Flags.High >> 31) == ~Flags.A << 0) { return 0; }
                    return 42;
                }
                return 7;
            }
            function main(args: string[]): int { return evaluate(); }
            """;
        var module = new AloeCompiler().CompileForWasm(source);
        module = AloeBcCodec.Read(AloeBcCodec.Write(module));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void SourceMetadata_TracksParametersScopedLocalsAndShortCircuitTemps()
    {
        const string source = """
            function test(n: int, enabled: bool): bool {
                var yes = enabled and n > 0;
                if (true) { var enabled = false or yes; if (enabled) { return true; } }
                return false;
            }
            function main(args: string[]): int {
                var value = 40;
                if (test(value, true) and (true or 1 / 0 == 0)) { return value + 2; }
                return 7;
            }
            """;
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var helper = regular.Functions.Single(f => f.Name == "test");
        Assert.That(helper.HasTypeMetadata, Is.True);
        Assert.That(helper.ParameterTypes, Is.EqualTo(new[] { EnumValueKind.Int, EnumValueKind.Bool }));
        Assert.That(helper.LocalTypes, Is.EqualTo(new[] { EnumValueKind.Int, EnumValueKind.Bool,
            EnumValueKind.Bool, EnumValueKind.Bool, EnumValueKind.Bool, EnumValueKind.Bool }));
        Assert.That(helper.ReturnType, Is.EqualTo(EnumValueKind.Bool));
        Assert.That(regular.Functions[regular.EntryPointIndex].ParameterCount, Is.Zero);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(module.Functions.All(f => f.HasTypeMetadata), Is.True);
        Assert.That(module.Functions[module.EntryPointIndex].ParameterCount, Is.Zero);
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void SourceMetadata_ExecutesRecursionLoopsAndVoidFunctions()
    {
        const string source = """
            function empty(): void { return; }
            function fib(n: int): int {
                if (n < 2) { return n; }
                return fib(n - 1) + fib(n - 2);
            }
            function main(args: string[]): int {
                var total = 0;
                var i = 0;
                while (i < 5) { i = i + 1; if (i == 2) { continue; } total = total + fib(i); }
                empty();
                return total;
            }
            """;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(module.Functions.Single(f => f.Name == "empty").ReturnType, Is.Null);
        Assert.That(RunVm(new AloeCompiler().Compile(source)), Is.EqualTo(11));
        Assert.That(RunVm(module), Is.EqualTo(11));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "11" }));
    }

    [Test]
    public void SourceMetadata_RejectsUnsupportedTypesAndMainArgumentUsage()
    {
        var bodies = new[]
        {
            "var value = 1.5; return 0;", "var value = 1.5:d; return 0;",
            "var value = 'a'; return 0;", "let value: byte = 1; return 0;",
            "var value = \"text\"; return 0;"
        };
        foreach (var body in bodies)
        {
            var source = "function main(args: string[]): int { " + body + " }";
            Assert.DoesNotThrow(() => new AloeCompiler().Compile(source));
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(source));
        }
        foreach (var body in new[] { "var value = args; return 0;", "args = args; return 0;" })
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { " + body + " }"));
        const string classSource = "class Box {} function main(args: string[]): int { return 0; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(classSource));
        const string unusedFunction = "function helper(value: string): void {} function main(args: string[]): int { return 0; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(unusedFunction));
    }

    [Test]
    public void SourceMetadata_HostSyscallsRemainExplicitlyUnsupported()
    {
        var module = new AloeCompiler().CompileForWasm("function main(args: string[]): int { print(1); return 0; }");
        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }

    [Test]
    public void BitfieldOpcodes_RejectNonIntStackTypes()
    {
        foreach (var op in new[] { EnumOpcode.BitAnd, EnumOpcode.BitOr, EnumOpcode.BitXor, EnumOpcode.BitNot,
            EnumOpcode.BitShiftLeft, EnumOpcode.BitShiftRight })
        {
            var module = new Module(
                constants: new[] { AloeValue.FromBool(true) },
                code: op == EnumOpcode.BitNot
                    ? new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(op), new Instruction(EnumOpcode.Return) }
                    : new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 0), new Instruction(op), new Instruction(EnumOpcode.Return) },
                functions: new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) },
                entryPointIndex: 0);
            Assert.Throws<InvalidOperationException>(() => new AloeWasmCompiler().Compile(module));
        }
    }

    private static Module BitwiseModule(EnumOpcode opcode, long left, long right) => new(
        constants: new[] { AloeValue.FromInt(left), AloeValue.FromInt(right) },
        code: opcode == EnumOpcode.BitNot
            ? new[] { new Instruction(EnumOpcode.PushConst, 1), new Instruction(opcode), new Instruction(EnumOpcode.Return) }
            : new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1), new Instruction(opcode), new Instruction(EnumOpcode.Return) },
        functions: new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) },
        entryPointIndex: 0);

    private static long RunVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        vm.RunFromEntryPoint();
        return vm.Pop().AsInt;
    }

    private static string[] RunWasm(IEnumerable<Module> modules)
    {
        const string script = """
            const fs = require('node:fs');
            const results = JSON.parse(fs.readFileSync(0, 'utf8')).map(encoded => {
                const module = new WebAssembly.Module(Buffer.from(encoded, 'base64'));
                const instance = new WebAssembly.Instance(module);
                try { return instance.exports.main().toString(); }
                catch (error) { if (error instanceof WebAssembly.RuntimeError) return 'trap'; throw error; }
            });
            process.stdout.write(JSON.stringify(results));
            """;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ALOE_TEST_NODE") ?? "node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Node.js WASM test engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(JsonSerializer.Serialize(modules.Select(m => Convert.ToBase64String(new AloeWasmCompiler().Compile(m))).ToArray()));
        process.StandardInput.Close();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Node.js WASM tests exceeded 60 seconds.");
        }
        Assert.That(process.ExitCode, Is.Zero, error.GetAwaiter().GetResult());
        return JsonSerializer.Deserialize<string[]>(output.GetAwaiter().GetResult())!;
    }

    [Test]
    public void Compile_IntFunction_ProducesWasmModule()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromInt(40), AloeValue.FromInt(2) },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0),
                new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(EnumOpcode.Add),
                new Instruction(EnumOpcode.Return),
            },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int)
            },
            entryPointIndex: 0);

        var wasm = new AloeWasmCompiler().Compile(module);

        Assert.That(wasm.Take(8).ToArray(), Is.EqualTo(new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 }));
        Assert.That(wasm.Length, Is.GreaterThan(8));
    }

    [Test]
    public void Compile_StringConstant_IsRejectedExplicitly()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromString("hello") },
            code: new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Halt) },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), null)
            },
            entryPointIndex: 0);

        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }
}
