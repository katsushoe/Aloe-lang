using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ArrayExpressionTests
{
    [Test]
    public void ArrayExpressions_DocumentedFunctionsLiteralsAndParenthesesSupportReadAccess()
    {
        const string source = """
function values(): int[] { return [40, 2]; }
function main(args: string[]): int {
    if ([true, false][0] and values().Length == 2) {
        return values()[0] + ([1, 2]).Length;
    }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void ArrayExpressions_EvaluateReceiversAndIndexesOnceAndRetainTemporaryHandlesThroughGc()
    {
        const string source = """
function make(): int[] { print(10); return [10, 32]; }
function index(): int { print(20); return 1; }
class Box { public property Values: int[] { get { print(30); return [40, 2]; } } }
function main(args: string[]): int {
    var left = make()[index()];
    var box = new Box();
    var right = box.Values[index()];
    with (box) { print(.Values.Length); }
    return left + right + 8;
}
""";
        var result = Run(source, collectAtIndex: true);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("10\n20\n30\n20\n30\n2\n"));
    }

    [Test]
    public void ArrayExpressions_ComposeWithOperatorsArgumentsAndCommittedGetterReads()
    {
        const string source = """
function values(): int[] { return [9, 42]; }
function flag(): bool[] { return [false, true]; }
function echo(value: int): int { return value; }
class Box {
    field values: int[];
    construct(values: int[]) { this.values = values; }
    public property Values: int[] { get { return this.values; } }
    public property First: int { get { return this.values[0]; } }
    public async method update(query: Box): void {
        var target = this.values; target[0] = 42;
        print(query.First); print(query.Values[0]);
    }
}
function main(args: string[]): int {
    var target = [1]; var box = new Box(target); box.update(box); tick();
    if (flag()[values().Length - 1] and echo(values()[1]) == 42 and (target)[0] == 42 and
        box.Values[0] == 42 and box.First == 42) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("1\n42\n"));
    }

    [Test]
    public void ArrayExpressions_RejectInvalidTypesAssignmentsAndMembersAndKeepBoundsChecks()
    {
        const string declarations = "function values(): int[] { return [1]; } function flags(): bool[] { return [true]; } ";
        string[] statements =
        [
            "var result = values()[true];", "let result: bool = values()[0];",
            "let result: int = flags()[0];", "var result = values().Count;",
            "var result = values().Length();", "var result = values()[0][0];",
            "var result = [1].missing;", "var result = [1, true][0];",
            "var result = [][0];", "values()[0] = true;", "([1])[0] = false;",
            "var result = values().Length[0];", "let result: bool[] = values();"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(declarations + "function main(args: string[]): int { " + statement + " return 0; }"), statement);
        foreach (var expression in new[] { "values()[-1]", "values()[1]", "[1][2147483648]" })
            Assert.Throws<VmException>(() => Run(declarations + "function main(args: string[]): int { return " + expression + "; }"), expression);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm(declarations + "function main(args: string[]): int { return values()[0]; }"));
    }

    private static (long ExitCode, string Output) Run(string source, bool collectAtIndex = false)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var output = new StringBuilder();
        var vm = new AloeVm(module, settings);
        var collections = 0;
        vm.OutputWriter = line =>
        {
            output.AppendLine(line);
            if (collectAtIndex && line == "20")
            {
                vm.RequireGc();
                vm.FinishGc();
                collections++;
            }
        };
        vm.RunFromEntryPoint();
        if (collectAtIndex) Assert.That(collections, Is.EqualTo(2));
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
