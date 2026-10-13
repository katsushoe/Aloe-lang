using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ArrayExpressionWriteTests
{
    [Test]
    public void ArrayExpressionWrites_DocumentedFunctionPropertyAndParenthesizedTargets()
    {
        const string source = """
function identity(values: int[]): int[] { return values; }
class Box {
    field values: int[];
    construct(values: int[]) { this.values = values; }
    public property Values: int[] { get { return this.values; } }
}
function main(args: string[]): int {
    var values = [1, 2]; var box = new Box(values);
    identity(values)[0] = 20;
    (box.Values)[1] = 22;
    return values[0] + values[1];
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ArrayExpressionWrites_EvaluateTargetIndexAndValueOnceInOrderAndRetainThroughGc()
    {
        const string source = """
function target(): int[] { print(10); return [1]; }
function index(): int { print(20); return 0; }
function value(): int { print(30); return 42; }
function main(args: string[]): int {
    target()[index()] = value();
    ([1, 2])[0] = 42;
    return 42;
}
""";
        var result = Run(source, collectDuringOperands: true);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("10\n20\n30\n"));
    }

    [Test]
    public void ArrayExpressionWrites_ByteAdaptationAndOtherPrimitiveWritesKeepKindsAndAliases()
    {
        const string source = """
function bytes(values: byte[]): byte[] { return values; }
function chars(values: char[]): char[] { return values; }
function floats(values: float[]): float[] { return values; }
function decimals(values: decimal[]): decimal[] { return values; }
function flags(values: bool[]): bool[] { return values; }
function main(args: string[]): int {
    let b: byte[] = [0]; var c = ['a']; var f = [1.0]; var d = [0.1:d]; var flag = [false];
    bytes(b)[0] = 255; (bytes(b))[0] = 42;
    chars(c)[0] = 'z'; floats(f)[0] = 2.5; decimals(d)[0] = 0.2:d; flags(flag)[0] = true;
    if (b[0] == 42 and c[0] == 'z' and f[0] == 2.5 and d[0] == 0.2:d and flag[0]) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ArrayExpressionWrites_ThisFieldsWithPropertiesAndTickPreserveCommittedQueries()
    {
        const string source = """
class Box {
    field values: int[];
    construct(values: int[]) { this.values = values; }
    public property Values: int[] { get { return this.values; } }
    public property First: int { get { return this.values[0]; } }
    public async method update(query: Box): void {
        this.values[0] = 20; print(query.First);
        with (query) { .Values[0] = 42; }
        print(query.First); print(query.Values[0]);
    }
}
function main(args: string[]): int {
    var values = [1]; var box = new Box(values); box.update(box); tick();
    return values[0];
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n1\n42\n"));
    }

    [Test]
    public void ArrayExpressionWrites_RejectInvalidLvaluesKindsGetterMutationAndBounds()
    {
        const string declarations = "function values(): int[] { return [1]; } function bytes(): byte[] { let result: byte[] = [0]; return result; } ";
        string[] statements =
        [
            "values()[0] = true;", "bytes()[0] = 256;", "bytes()[0] = 1 + 2;",
            "values()[true] = 1;", "values().Length = 1;", "values().Length[0] = 1;",
            "1 + values()[0] = 1;", "-values()[0] = 1;", "values()[0][0] = 1;",
            "(1)[0] = 1;", "values()[0] == values()[0] = true;", "[1, false][0] = 1;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { " + statement + " return 0; }"), statement);
        const string getter = "class Box { field values: int[]; construct(values: int[]) { this.values = values; } public property First: int { get { this.values[0] = 2; return 0; } } }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(getter + "function main(args: string[]): int { return 0; }"));
        foreach (var statement in new[] { "values()[-1] = 2;", "values()[1] = 2;", "values()[0] = 2147483648;" })
            Assert.Throws<VmException>(() => Run(declarations + "function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations + "function main(args: string[]): int { values()[0] = 1; return 0; }"));
    }

    private static (long ExitCode, string Output) Run(string source, bool collectDuringOperands = false)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var output = new StringBuilder(); var vm = new AloeVm(module, settings); var collections = 0;
        vm.OutputWriter = line =>
        {
            output.AppendLine(line);
            if (collectDuringOperands && line is "20" or "30") { vm.RequireGc(); vm.FinishGc(); collections++; }
        };
        vm.RunFromEntryPoint();
        if (collectDuringOperands) Assert.That(collections, Is.EqualTo(2));
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
