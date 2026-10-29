using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceScalarComparisonFlowTests
{
    [Test]
    public void RawFloatComparisons_NonfiniteAndSignedZeroHaveNoExceptionEdge()
    {
        double[] numbers = [double.NegativeInfinity, double.MinValue, -0.0, 0.0,
            double.MaxValue, double.PositiveInfinity, double.NaN];
        EnumOpcode[] operators = [EnumOpcode.CmpLt, EnumOpcode.CmpLe, EnumOpcode.CmpGt,
            EnumOpcode.CmpGe, EnumOpcode.CmpEq, EnumOpcode.CmpNe];
        foreach (var left in numbers)
        foreach (var right in numbers)
        foreach (var op in operators)
        {
            AssertComparisonReturnsBoolean(AloeValue.FromFloat(left), AloeValue.FromFloat(right), op);
        }
    }

    [Test]
    public void RawDecimalComparisons_MinMaxBoundariesHaveNoOverflowEdge()
    {
        decimal[] numbers = [decimal.MinValue, -1m, 0m, 1m, decimal.MaxValue];
        EnumOpcode[] operators = [EnumOpcode.CmpLt, EnumOpcode.CmpLe, EnumOpcode.CmpGt,
            EnumOpcode.CmpGe, EnumOpcode.CmpEq, EnumOpcode.CmpNe];
        foreach (var left in numbers)
        foreach (var right in numbers)
        foreach (var op in operators)
            AssertComparisonReturnsBoolean(AloeValue.FromDecimal(left), AloeValue.FromDecimal(right), op);
    }

    [Test]
    public void DecimalVariableArithmetic_RetainsPossibleOverflow()
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program("decimal", "-1.5:d", "2.5:d", "let result: bool = (left + right) < right;",
                "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static void AssertComparisonReturnsBoolean(AloeValue left, AloeValue right, EnumOpcode op)
    {
        // Raw VM boundaries are tested without source-literal or JSON restrictions.
        var module = new Module([left, right],
            [new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
                new Instruction(op), new Instruction(EnumOpcode.Return)],
            [new FunctionInfo("main", 0, 0, 0)], 0);
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().IsBool, Is.True);
    }

    private static readonly (string Low, string High)[] StringSamples =
        [("a", "b"), ("same", "same"), ("", "a")];

    public static IEnumerable<TestCaseData> StringPairs()
    {
        foreach (var pair in StringSamples) yield return new TestCaseData(pair.Low, pair.High);
    }

    public static IEnumerable<TestCaseData> StringEqualities()
    {
        foreach (var pair in StringSamples)
        foreach (var op in new[] { "==", "!=" })
        foreach (var locals in new[] { false, true })
            yield return new TestCaseData(pair.Low, pair.High, op, locals);
    }

    [TestCaseSource(nameof(StringEqualities))]
    public void DirectStringEquality_HasNoCatchEdge(string low, string high, string op, bool locals)
    {
        var expression = locals ? $"((first)) {op} ((second))" : $"left {op} right";
        Assert.That(Run(StringProgram(low, high, $"let comparison: bool = {expression};",
            "catch { value = new Plain(); }")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCaseSource(nameof(StringPairs))]
    public void StringEqualityOptionalFault_PreservesExceptionFamily(string low, string high)
    {
        Assert.That(Run(StringProgram(low, high,
            "let comparison: bool = not (left == right) and (1 / 0 == 0);", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
""")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCaseSource(nameof(StringPairs))]
    public void StringEqualityCall_RemainsConservative(string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            StringProgram(low, high, "let comparison: bool = left == source();", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCaseSource(nameof(StringPairs))]
    public void StringEqualityBranchWrite_RemainsConservative(string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            StringProgram(low, high, "if (left == right) { value = new Plain(); }", "catch { }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("<")]
    [TestCase("<=")]
    [TestCase(">")]
    [TestCase(">=")]
    public void StringOrderedComparison_RemainsRejected(string op)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            StringProgram("a", "b", $"let comparison: bool = left {op} right;", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("numeric"));
    }

    [Test]
    public void RawStringEquality_UnicodeAndNullBoundariesHaveNoExceptionEdge()
    {
        AloeValue[] values = [AloeValue.FromString(""), AloeValue.FromString("a"), AloeValue.FromString("A"),
            AloeValue.FromString("same"), AloeValue.FromString(char.ConvertFromUtf32(0x1F600)),
            AloeValue.FromString(new string((char)0xd800, 1)), AloeValue.FromString(new string((char)0xdc00, 1)), AloeValue.Null];
        foreach (var left in values)
        foreach (var right in values)
        foreach (var op in new[] { EnumOpcode.CmpEq, EnumOpcode.CmpNe })
            AssertComparisonReturnsBoolean(left, right, op);
    }

    private static string StringProgram(string low, string high, string body, string handlers)
        => Program("string", System.Text.Json.JsonSerializer.Serialize(low),
            System.Text.Json.JsonSerializer.Serialize(high), body, handlers);

    private static readonly (string Left, string Right)[] MixedTypes =
        [("int", "byte"), ("int", "float"), ("int", "decimal"), ("byte", "float"), ("byte", "decimal"),
         ("byte", "int"), ("float", "int"), ("decimal", "int"), ("float", "byte"), ("decimal", "byte")];

    public static IEnumerable<TestCaseData> MixedPairs()
    {
        foreach (var pair in MixedTypes) yield return new TestCaseData(pair.Left, pair.Right);
    }

    public static IEnumerable<TestCaseData> MixedComparisons()
    {
        foreach (var pair in MixedTypes)
        foreach (var op in new[] { "<", "<=", ">", ">=", "==", "!=" })
        foreach (var locals in new[] { false, true })
            yield return new TestCaseData(pair.Left, pair.Right, op, locals);
    }

    [TestCaseSource(nameof(MixedComparisons))]
    public void CompatibleMixedReads_HaveNoCatchEdge(string left, string right, string op, bool locals)
    {
        var expression = locals ? $"((first)) {op} ((second))" : $"left {op} right";
        Assert.That(Run(MixedProgram(left, right, $"let comparison: bool = {expression};",
            "catch { value = new Plain(); }")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCaseSource(nameof(MixedPairs))]
    public void CompatibleMixedOptionalFault_PreservesExceptionFamily(string left, string right)
    {
        Assert.That(Run(MixedProgram(left, right,
            "let comparison: bool = not (left >= right) and (1 / 0 == 0);", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
""")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCaseSource(nameof(MixedPairs))]
    public void CompatibleMixedCall_RemainsConservative(string left, string right)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            MixedProgram(left, right, "let comparison: bool = left < source();", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCaseSource(nameof(MixedPairs))]
    public void CompatibleMixedBranchWrite_RemainsConservative(string left, string right)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            MixedProgram(left, right, "if (left < right) { value = new Plain(); }", "catch { }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("float", "decimal")]
    [TestCase("decimal", "float")]
    public void DecimalFloatMixing_RemainsRejected(string left, string right)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            MixedProgram(left, right, "let comparison: bool = left < right;", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("decimal and float cannot be mixed"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("float", "-1.5", "2.5")]
    [TestCase("decimal", "-1.5:d", "2.5:d")]
    public void SafeConstantIntOperand_CanPromoteWithoutCatchEdge(string type, string low, string high)
    {
        Assert.That(Run(Program(type, low, high, "let comparison: bool = left < ((1 + 2));",
            "catch { value = new Plain(); }")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [Test]
    public void RawCompatibleMixedComparisons_BoundariesHaveNoExceptionEdge()
    {
        var values = new Dictionary<string, AloeValue[]>
        {
            ["int"] = [AloeValue.FromInt(long.MinValue), AloeValue.FromInt(int.MinValue), AloeValue.FromInt(0),
                AloeValue.FromInt(int.MaxValue), AloeValue.FromInt(long.MaxValue)],
            ["byte"] = [AloeValue.FromByte(0), AloeValue.FromByte(255)],
            ["float"] = [AloeValue.FromFloat(double.NegativeInfinity), AloeValue.FromFloat(double.MinValue),
                AloeValue.FromFloat(-0.0), AloeValue.FromFloat(0.0), AloeValue.FromFloat(double.MaxValue),
                AloeValue.FromFloat(double.PositiveInfinity), AloeValue.FromFloat(double.NaN)],
            ["decimal"] = [AloeValue.FromDecimal(decimal.MinValue), AloeValue.FromDecimal(0), AloeValue.FromDecimal(decimal.MaxValue)]
        };
        EnumOpcode[] operators = [EnumOpcode.CmpLt, EnumOpcode.CmpLe, EnumOpcode.CmpGt,
            EnumOpcode.CmpGe, EnumOpcode.CmpEq, EnumOpcode.CmpNe];
        foreach (var pair in MixedTypes)
        foreach (var left in values[pair.Left])
        foreach (var right in values[pair.Right])
        foreach (var op in operators) AssertComparisonReturnsBoolean(left, right, op);
    }

    private static (string Low, string High) MixedLiterals(string type) => type switch
    {
        "int" => ("-1", "2"), "byte" => ("1", "255"), "float" => ("-1.5", "2.5"),
        "decimal" => ("-1.5:d", "2.5:d"), _ => throw new ArgumentException(type)
    };

    private static string MixedProgram(string left, string right, string body, string handlers)
    {
        var a = MixedLiterals(left); var b = MixedLiterals(right);
        return $$"""
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
function source(): {{right}} { return {{b.Low}}; }
function choose(left: {{left}}, right: {{right}}): int {
    let first: {{left}} = left; let second: {{right}} = right;
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        {{handlers}}
        value.emit("scalar-flow");
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose({{a.Low}}, {{b.High}}) + choose({{a.High}}, {{b.Low}});
    tick(); return result;
}
""";
    }

    private static readonly (string Type, string Low, string High)[] Scalars =
        [("byte", "1", "255"), ("char", "'a'", "'z'"), ("float", "-1.5", "2.5"),
            ("decimal", "-1.5:d", "2.5:d")];

    public static IEnumerable<TestCaseData> Comparisons()
    {
        foreach (var scalar in Scalars)
        foreach (var op in new[] { "<", "<=", ">", ">=", "==", "!=" })
        foreach (var locals in new[] { false, true })
            yield return new TestCaseData(scalar.Type, scalar.Low, scalar.High, op, locals);
    }

    [TestCaseSource(nameof(Comparisons))]
    public void DirectSameTypeReads_HaveNoCatchEdge(string type, string low, string high, string op, bool locals)
    {
        var expression = locals ? $"((first)) {op} ((second))" : $"left {op} right";
        Assert.That(Run(Program(type, low, high, $"let result: bool = {expression};",
            "catch { value = new Plain(); }")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    [TestCase("decimal", "-1.5:d", "2.5:d")]
    public void OptionalFailure_PreservesExactExceptionFamily(string type, string low, string high)
    {
        Assert.That(Run(Program(type, low, high,
            "let result: bool = not (left >= right) and (1 / 0 == 0);", """
catch (InvalidCastException) { value = new Plain(); }
catch (OverflowException) { value = new Plain(); }
catch (ZeroDivisionException) { }
catch { value = new Plain(); }
""")), Is.EqualTo("scalar-flow\nscalar-flow\n"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    [TestCase("decimal", "-1.5:d", "2.5:d")]
    public void ReachableCall_RemainsConservative(string type, string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(type, low, high, "let result: bool = left < source();", "catch { value = new Plain(); }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    [TestCase("byte", "1", "255")]
    [TestCase("char", "'a'", "'z'")]
    [TestCase("float", "-1.5", "2.5")]
    [TestCase("decimal", "-1.5:d", "2.5:d")]
    public void PossibleBranchWrite_RemainsConservative(string type, string low, string high)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program(type, low, high, "if (left < right) { value = new Plain(); }", "catch { }")));
        Assert.That(error!.Message, Does.Contain("Async member call requires a statically known class instance"));
    }

    private static string Program(string type, string low, string high, string body, string handlers) => $$"""
interface I { method emit(message: string): void; }
class C implements I { public async method emit(message: string): void { print(message); } }
class Plain { }
function source(): {{type}} { return {{low}}; }
function choose(left: {{type}}, right: {{type}}): int {
    let first: {{type}} = left;
    let second: {{type}} = right;
    let value: object = new C();
    if (value is I) {
        try { {{body}} }
        {{handlers}}
        value.emit("scalar-flow");
    }
    return 0;
}
function main(args: string[]): int {
    let result: int = choose({{low}}, {{high}}) + choose({{high}}, {{low}});
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
