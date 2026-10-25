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
    [TestCase(0, "0")]
    [TestCase(1, "1")]
    [TestCase(2, "trap")]
    [TestCase(-1, "trap")]
    [TestCase(int.MinValue, "trap")]
    [TestCase(int.MaxValue, "trap")]
    public void Bool_ExportedWasmParametersRequireCanonicalBoolean(int value, string expected)
    {
        const string source = """
            function echo(prefix: int, value: bool): bool { return value; }
            function main(args: string[]): int { return 0; }
            """;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(RunWasmExport(module, "echo", int.MinValue, value), Is.EqualTo(expected));
    }

    [TestCase(0L, "0")]
    [TestCase((long)int.MinValue, "-2147483648")]
    [TestCase((long)int.MaxValue, "2147483647")]
    [TestCase(-2147483649L, "trap")]
    [TestCase(2147483648L, "trap")]
    [TestCase(long.MinValue, "trap")]
    [TestCase(long.MaxValue, "trap")]
    public void Int_ExportedWasmParametersRequireSignedInt32(long value, string expected)
    {
        const string source = """
            function echo(prefix: byte, value: int): int { return value; }
            function main(args: string[]): int { return 0; }
            """;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(RunWasmExport(module, "echo", 255, value), Is.EqualTo(expected));
    }

    [Test]
    public void Byte_SourceValuesAndComparisonsMatchVmAndWasm()
    {
        const string source = """
            function echo(value: byte): byte { return value; }
            function main(args: string[]): int {
                let value: byte = echo(255);
                let minimum: byte = 0;
                let high: byte = 128;
                let maximum: byte = 255;
                let promoted: int = value + 1;
                if (value == maximum and high < value and value >= maximum and minimum <= value and
                    value < promoted and promoted > value and value != promoted and promoted == 256) { return 42; }
                return 0;
            }
            """;
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void Byte_WasmRejectsByteAndBoolComparisons()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromByte(1), AloeValue.FromBool(true) },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(EnumOpcode.CmpEq), new Instruction(EnumOpcode.Return)
            },
            functions: new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Bool) },
            entryPointIndex: 0);

        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }

    [Test]
    public void Byte_ExportedWasmParametersRejectValuesOutsideByteRange()
    {
        const string source = """
            function echo(value: byte): byte { return value; }
            function main(args: string[]): int { return 0; }
            """;
        var module = new AloeCompiler().CompileForWasm(source);

        Assert.That(RunWasmExport(module, "echo", 0), Is.EqualTo("0"));
        Assert.That(RunWasmExport(module, "echo", 255), Is.EqualTo("255"));
        Assert.That(RunWasmExport(module, "echo", 256), Is.EqualTo("trap"));
        Assert.That(RunWasmExport(module, "echo", -1), Is.EqualTo("trap"));
    }

    [TestCase(0, "0")]
    [TestCase(65535, "65535")]
    [TestCase(55296, "55296")]
    [TestCase(65536, "trap")]
    [TestCase(-1, "trap")]
    [TestCase(int.MaxValue, "trap")]
    [TestCase(int.MinValue, "trap")]
    public void Char_ExportedWasmParametersRequireUtf16CodeUnits(int value, string expected)
    {
        const string source = """
            function echo(prefix: byte, value: char): char { return value; }
            function main(args: string[]): int { return 0; }
            """;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));
        Assert.That(RunWasmExport(module, "echo", 255, value), Is.EqualTo(expected));
    }

    [Test]
    public void Char_SourceValuesAndUnsignedComparisonsMatchVmAndWasm()
    {
        const string source = """
            function echo(value: char): char { return value; }
            function main(args: string[]): int {
                var value = echo('字');
                if (value == '字' and '\0' < 'A' and 'A' <= 'A' and 'B' > 'A' and 'B' >= 'B') { return 42; }
                return 0;
            }
            """;
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void Char_SurrogateCodeUnitIsPreservedByWasm()
    {
        const char surrogate = '\ud800';
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new Module(
            constants: new[] { AloeValue.FromChar(surrogate) },
            code: new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Return) },
            functions: new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Char) },
            entryPointIndex: 0)));

        Assert.That(RunVmValue(module).AsChar, Is.EqualTo(surrogate));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "55296" }));
    }

    [Test]
    public void PrimitiveIdentityCasts_MatchVmAndWasm()
    {
        const string source = """
            function echoInt(value: int): int { return value as int; }
            function echoFloat(value: float): float { return value as float; }
            function echoBool(value: bool): bool { return value as bool; }
            function echoByte(value: byte): byte { return value as byte; }
            function echoChar(value: char): char { return value as char; }
            function main(args: string[]): int {
                if (echoInt(42) == 42 and echoFloat(1.5) == 1.5 and echoBool(true) and
                    echoByte(255) == 255 and echoChar('Z') == 'Z') { return 42; }
                return 0;
            }
            """;
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var wasmModule = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));

        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(wasmModule), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { wasmModule }), Is.EqualTo(new[] { "42" }));
    }

    [TestCase("let value = 42 as float;")]
    [TestCase("let value = 42 as int ifnot 7;")]
    public void PrimitiveCasts_WasmRejectsConversionsAndFallbacks(string statement)
    {
        var source = $"function main(args: string[]): int {{ {statement} return 0; }}";

        Assert.That(() => new AloeCompiler().CompileForWasm(source), Throws.TypeOf<AloeCompileException>());
    }

    [Test]
    public void Char_WasmRejectsMixedCharAndBoolComparisons()
    {
        var module = new Module(
            constants: new[] { AloeValue.FromChar('A'), AloeValue.FromBool(true) },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(EnumOpcode.CmpEq), new Instruction(EnumOpcode.Return)
            },
            functions: new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Bool) },
            entryPointIndex: 0);

        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }

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
    public void Float32_SourceCallsArithmeticAndComparisonsMatchVmAndWasm()
    {
        const string source = """
function echo(value: float): float { return value; }
function main(args: string[]): int {
    let left: float = echo(1.5);
    let right: float = 2.25;
    let sum: float = left + right;
    let difference: float = right - left;
    let product: float = left * right;
    let quotient: float = right / left;
    let remainder: float = left % right;
    let reverseRemainder: float = right % left;
    let negative: float = 0 - left;
    let negativeRemainder: float = negative % right;
    if (sum == 3.75 and difference == 0.75 and product == 3.375 and quotient == 1.5 and
        remainder == 1.5 and reverseRemainder == 0.75 and negativeRemainder == -1.5 and
        left < right and right > left and left <= left and right >= right and left != right and
        left + 2 == 3.5 and 2 + left == 3.5 and right - 1 == 1.25 and 4 - left == 2.5 and
        left * 2 == 3 and 2 * left == 3 and right / 1.5 == 1.5 and 16777216.0 < 16777217 and
        16777217 > 16777216.0 and 16777216.0 != 16777217) { return 42; }
    return 0;
}
""";
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void Float32_ModuloMatchesVmAndWasmIncludingLargeQuotients()
    {
        var cases = new[]
        {
            (7.5f, 2f, 1.5f), (-7.5f, 2f, -1.5f), (7.5f, -2f, 1.5f),
            (-7.5f, -2f, -1.5f), (float.MaxValue, float.Epsilon, 0f),
            (1f, float.Epsilon, 0f), (float.Epsilon, 2f * float.Epsilon, float.Epsilon),
            (-float.Epsilon, 2f * float.Epsilon, -float.Epsilon),
            (3f * float.Epsilon, 2f * float.Epsilon, float.Epsilon), (-0f, 2f, -0f)
        };
        var modules = cases.Select(test => FloatModuloModule(test.Item1, test.Item2)).ToArray();
        var wasm = RunWasm(modules);
        foreach (var (module, test) in modules.Zip(cases))
            Assert.That(RunVmValue(module).AsFloat, Is.EqualTo(test.Item3));
        for (var i = 0; i < cases.Length; i++)
        {
            var actual = float.Parse(wasm[i], System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(BitConverter.SingleToInt32Bits(actual), Is.EqualTo(BitConverter.SingleToInt32Bits(cases[i].Item3)),
                $"WASM modulo mismatch for {cases[i].Item1:R} % {cases[i].Item2:R}.");
        }

        var zeroDivisor = FloatModuloModule(1f, 0f);
        Assert.Throws<ZeroDivisionException>(() => RunVmValue(zeroDivisor));
        Assert.That(RunWasm(new[] { zeroDivisor }), Is.EqualTo(new[] { "trap" }));

        var nonFinite = new[]
        {
            FloatModuloModule(float.NaN, 2f), FloatModuloModule(float.PositiveInfinity, 2f),
            FloatModuloModule(2f, float.NaN)
        };
        foreach (var module in nonFinite) Assert.Throws<OverflowException>(() => RunVmValue(module));
        Assert.That(RunWasm(nonFinite), Is.All.EqualTo("trap"));

        var infiniteDivisor = FloatModuloModule(2f, float.PositiveInfinity);
        Assert.That(RunVmValue(infiniteDivisor).AsFloat, Is.EqualTo(2f));
        Assert.That(RunWasm(new[] { infiniteDivisor }), Is.EqualTo(new[] { "2" }));
    }

    [Test]
    public void Float32_ModuloDeterministicFiniteBitPatternsMatchVmAndWasm()
    {
        var random = new Random(0xA10E);
        var cases = new List<(float Left, float Right)>();
        var bytes = new byte[sizeof(int)];
        while (cases.Count < 128)
        {
            random.NextBytes(bytes);
            var left = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes));
            random.NextBytes(bytes);
            var right = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes));
            if (float.IsFinite(left) && float.IsFinite(right) && right != 0f)
                cases.Add((left, right));
        }

        var modules = cases.Select(test => FloatModuloModule(test.Left, test.Right)).ToArray();
        var wasm = RunWasm(modules);
        Assert.That(wasm.Length, Is.EqualTo(cases.Count));
        for (var i = 0; i < cases.Count; i++)
        {
            var expected = (float)RunVmValue(modules[i]).AsFloat;
            var actual = float.Parse(wasm[i], System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(BitConverter.SingleToInt32Bits(actual), Is.EqualTo(BitConverter.SingleToInt32Bits(expected)),
                $"Modulo mismatch for {cases[i].Left:R} % {cases[i].Right:R}.");
        }
    }

    private static Module FloatModuloModule(float left, float right) => new(
        constants: new[] { AloeValue.FromFloat(left), AloeValue.FromFloat(right) },
        code: new[]
        {
            new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
            new Instruction(EnumOpcode.Mod), new Instruction(EnumOpcode.Return)
        },
        functions: new[]
        {
            new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Float)
        },
        entryPointIndex: 0);

    [Test]
    public void Float32_OverflowUnderflowAndDivisionByZeroTrapInWasm()
    {
        var cases = new[]
        {
            (EnumOpcode.Mul, AloeValue.FromFloat(float.MaxValue), AloeValue.FromFloat(2)),
            (EnumOpcode.Mul, AloeValue.FromFloat(float.Epsilon), AloeValue.FromFloat(0.5)),
            (EnumOpcode.Div, AloeValue.FromFloat(1), AloeValue.FromFloat(0))
        };
        var modules = cases.Select(test => new Module(
            constants: new[] { test.Item2, test.Item3 },
            code: new[]
            {
                new Instruction(EnumOpcode.PushConst, 0),
                new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(test.Item1),
                new Instruction(EnumOpcode.Return)
            },
            functions: new[]
            {
                new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Float)
            },
            entryPointIndex: 0)).ToArray();
        Assert.Throws<OverflowException>(() => RunVm(modules[0]));
        Assert.Throws<OverflowException>(() => RunVm(modules[1]));
        Assert.Throws<ZeroDivisionException>(() => RunVm(modules[2]));
        Assert.That(RunWasm(modules), Is.All.EqualTo("trap"));
    }

    [Test]
    public void Float32_NonFiniteParameterTrapsInWasm()
    {
        var module = new Module(
            constants: Array.Empty<AloeValue>(),
            code: new[]
            {
                new Instruction(EnumOpcode.LoadLocal, 0),
                new Instruction(EnumOpcode.Return)
            },
            functions: new[]
            {
                new FunctionInfo("main", 0, new[] { EnumValueKind.Float }, new[] { EnumValueKind.Float }, EnumValueKind.Float)
            },
            entryPointIndex: 0);

        // The Node runner invokes main() without arguments, which supplies NaN to this f32 parameter.
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "trap" }));
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
    public void Comparisons_SignedInt32BoundariesAndBooleanEqualityMatchVmAndWasm()
    {
        const string source = """
function main(args: string[]): int {
    let minimum: int = -2147483648;
    let maximum: int = 2147483647;
    if (minimum < maximum and minimum <= minimum and maximum > minimum and maximum >= maximum
        and minimum != maximum and maximum == 2147483647 and true == true and true != false) {
        return 42;
    }
    return 0;
}
""";

        var regular = new AloeCompiler().Compile(source);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().CompileForWasm(source)));

        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
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
            "var value = 1.5:d; return 0;", "var value = \"text\"; return 0;"
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
        const string interfaceSource = "interface I { method run(): void; } function main(args: string[]): int { return 0; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(interfaceSource));
        const string unusedFunction = "function helper(value: string): void {} function main(args: string[]): int { return 0; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(unusedFunction));
    }

    [Test]
    public void SourceMetadata_HostSyscallsRemainExplicitlyUnsupported()
    {
        var module = new Module(new[] { AloeValue.FromInt(0) },
            new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.HostSleep), new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Return) },
            new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) }, 0);
        Assert.Throws<NotSupportedException>(() => new AloeWasmCompiler().Compile(module));
    }

    [Test]
    public void SourceTickSyscall_MatchesVmAndWasmInScalarModule()
    {
        const string source = "function main(args: string[]): int { var value = 40 + 2; tick(); return value; }";
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var wasmModule = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));

        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(wasmModule), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { wasmModule }), Is.EqualTo(new[] { "42" }));
    }

    [TestCase("GC.require();")]
    [TestCase("GC.finish();")]
    [TestCase("GC.require(); tick(); GC.finish();")]
    public void SourceGcBarriers_MatchVmAndWasmWithoutHeap(string controls)
    {
        var source = $$"""
function main(args: string[]): int {
    var value = 40;
    var iteration = 0;
    while (iteration < 2) { {{controls}} value = value + 1; iteration = iteration + 1; }
    return value;
}
""";
        var compiler = new AloeCompiler();
        var regular = compiler.Compile(source);
        var wasmModule = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(wasmModule), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { wasmModule }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void GcDebug_ReadWriteAndBarriersMatchVmAndWasm()
    {
        const string source = """
function main(args: string[]): int {
    if (GC.debug) { return -1; }
    GC.debug = true;
    GC.require(); tick(); GC.finish();
    if (not GC.debug) { return -2; }
    GC.debug = false;
    if (GC.debug) { return -3; }
    return 42;
}
""";
        var compiler = new AloeCompiler();
        var regular = AloeBcCodec.Read(AloeBcCodec.Write(compiler.Compile(source)));
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source)));
        Assert.That(RunVm(regular), Is.EqualTo(42));
        Assert.That(RunVm(module), Is.EqualTo(42));
        Assert.That(RunWasm(new[] { module }), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void GcDebug_PersistsAcrossCallsAndIsIsolatedBetweenInstances()
    {
        const string source = """
function main(args: string[]): int {
    let previous: bool = GC.debug;
    GC.debug = not previous;
    if (previous) { return 2; }
    return 1;
}
""";
        var compiler = new AloeCompiler();
        foreach (var module in new[] { compiler.Compile(source), AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source))) })
        {
            foreach (var vm in new[] { CreateQuietVm(module), CreateQuietVm(module) })
            {
                foreach (var expected in new[] { 1, 2, 1 })
                {
                    vm.RunFromEntryPoint();
                    Assert.That(vm.Pop().AsInt, Is.EqualTo(expected));
                }
            }
        }
        var wasmModule = compiler.CompileForWasm(source);
        Assert.That(RunWasm(new[] { wasmModule, wasmModule }, callsPerInstance: 3),
            Is.EqualTo(new[] { "1", "2", "1", "1", "2", "1" }));
    }

    [Test]
    public void GcDebug_StateWrittenBeforeFailureRemainsVisibleOnNextCall()
    {
        const string source = """
function main(args: string[]): int {
    if (GC.debug) { GC.debug = false; return 42; }
    GC.debug = true;
    return 1 / 0;
}
""";
        var compiler = new AloeCompiler();
        foreach (var module in new[] { compiler.Compile(source), AloeBcCodec.Read(AloeBcCodec.Write(compiler.CompileForWasm(source))) })
        {
            var vm = CreateQuietVm(module);
            Assert.Throws<ZeroDivisionException>(() => vm.RunFromEntryPoint());
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        }
        Assert.That(RunWasm(new[] { compiler.CompileForWasm(source) }, callsPerInstance: 2),
            Is.EqualTo(new[] { "trap", "42" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GcDebug_RawBytecodeRejectsMissingOrNonBoolAssignment(bool hasValue)
    {
        var code = new List<Instruction>();
        if (hasValue) code.Add(new Instruction(EnumOpcode.PushConst, 0));
        code.Add(new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.GCSetDebug));
        code.Add(new Instruction(EnumOpcode.PushConst, 0));
        code.Add(new Instruction(EnumOpcode.Return));
        var module = new Module(new[] { AloeValue.FromInt(1) }, code,
            new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), Array.Empty<EnumValueKind>(), EnumValueKind.Int) }, 0);
        Assert.Throws<InvalidOperationException>(() => new AloeWasmCompiler().Compile(module));
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
        => RunVmValue(module).AsInt;

    private static AloeValue RunVmValue(Module module)
    {
        var vm = CreateQuietVm(module);
        vm.RunFromEntryPoint();
        return vm.Pop();
    }

    private static AloeVm CreateQuietVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module, settings);
    }

    private static string[] RunWasm(IEnumerable<Module> modules, int callsPerInstance = 1)
    {
        const string script = """
            const fs = require('node:fs');
            const input = JSON.parse(fs.readFileSync(0, 'utf8'));
            const results = input.modules.flatMap(encoded => {
                const module = new WebAssembly.Module(Buffer.from(encoded, 'base64'));
                const instance = new WebAssembly.Instance(module);
                return Array.from({ length: input.callsPerInstance }, () => {
                    try { const value = instance.exports.main(); return Object.is(value, -0) ? '-0' : value.toString(); }
                    catch (error) { if (error instanceof WebAssembly.RuntimeError) return 'trap'; throw error; }
                });
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
        process.StandardInput.Write(JsonSerializer.Serialize(new
        {
            modules = modules.Select(m => Convert.ToBase64String(new AloeWasmCompiler().Compile(m))).ToArray(),
            callsPerInstance
        }));
        process.StandardInput.Close();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Node.js WASM tests exceeded 60 seconds.");
        }
        Assert.That(process.ExitCode, Is.Zero, error.GetAwaiter().GetResult());
        return JsonSerializer.Deserialize<string[]>(output.GetAwaiter().GetResult())!;
    }

    private static string RunWasmExport(Module module, string exportName, params long[] arguments)
    {
        const string script = """
            const fs = require('node:fs');
            const input = JSON.parse(fs.readFileSync(0, 'utf8'));
            const compiled = new WebAssembly.Module(Buffer.from(input.module, 'base64'));
            const instance = new WebAssembly.Instance(compiled);
            const args = input.arguments.map((value, index) => input.integerArguments[index] ? BigInt(value) : Number(value));
            try { process.stdout.write(instance.exports[input.exportName](...args).toString()); }
            catch (error) { if (error instanceof WebAssembly.RuntimeError) process.stdout.write('trap'); else throw error; }
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
        var input = new
        {
            module = Convert.ToBase64String(new AloeWasmCompiler().Compile(module)),
            exportName,
            arguments = arguments.Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            integerArguments = module.Functions.First(function => function.Name == exportName).ParameterTypes
                .Select(kind => kind == EnumValueKind.Int).ToArray()
        };
        process.StandardInput.Write(JsonSerializer.Serialize(input));
        process.StandardInput.Close();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Node.js WASM test exceeded 60 seconds.");
        }
        Assert.That(process.ExitCode, Is.Zero, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
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
