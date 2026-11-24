using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0055: `|: (condition) => action { elseif ... } [else => action]` and `|: (condition)`.
public sealed class ConditionalStageTests
{
    private static string Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new System.Text.StringBuilder();
        var vm = new AloeVm(module) { OutputWriter = line => output.Append(line).Append('\n') };
        vm.RunFromEntryPoint();
        return output.ToString();
    }

    private static string StringPipeline(string stage, params string[] values)
    {
        var writes = string.Join(" ", values.Select(v => $"source.write(\"{v}\");"));
        return $$"""
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source {{stage}} | result;
    {{writes}}
    source.close();
    foreach (value in result) { print(value); }
    return 0;
}
""";
    }

    [Test]
    public void MatchingElementsAreTransformedAndOthersPassThroughInOrder()
        => Assert.That(Run(StringPipeline("|: (. != \"\") => toupper", "a", "", "b")), Is.EqualTo("A\n\nB\n"));

    [Test]
    public void FilterOnlyFormKeepsMatchingElements()
        => Assert.That(Run(StringPipeline("|: (. != \"x\")", "a", "x", "b", "x")), Is.EqualTo("a\nb\n"));

    [Test]
    public void FirstMatchingBranchIsExclusiveAndElseAppliesToTheRest()
        => Assert.That(Run(StringPipeline("|: (. == \"a\") => toupper elseif (. < \"m\") => drop else => trim", "a", "b", "z  ")),
            Is.EqualTo("A\nz\n"));

    [Test]
    public void DropActionRemovesMatchingElements()
        => Assert.That(Run(StringPipeline("|: (. == \"b\" or . == \"c\") => drop", "a", "b", "c", "d")), Is.EqualTo("a\nd\n"));

    [Test]
    public void StructFieldEnumAndBoolConditionsWork()
    {
        const string source = """
enum Kind { Note, Raw }
struct Item { field kind: Kind; field size: int; field hot: bool; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create();
    let result: pipe<Item> = pipe<Item>.create();
    source |: (.kind == Kind.Note and (.size >= 10 or .hot)) | result;
    source.write(Item { kind: Kind.Note, size: 12, hot: false });
    source.write(Item { kind: Kind.Raw, size: 50, hot: true });
    source.write(Item { kind: Kind.Note, size: 1, hot: true });
    source.write(Item { kind: Kind.Note, size: 1, hot: false });
    source.close();
    foreach (item in result) { print(item.size); }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo("12\n1\n"));
    }

    [Test]
    public void ScalarPipesAndNotWork()
    {
        const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: (not (. > 2)) | result;
    source.write(1); source.write(3); source.write(-5); source.write(2);
    source.close();
    foreach (value in result) { print(value); }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo("1\n-5\n2\n"));
    }

    [Test]
    public void EmptyInputClosesOutputAndStagesChain()
    {
        Assert.That(Run(StringPipeline("|: (. != \"\")")), Is.EqualTo(""));
        Assert.That(Run(StringPipeline("|: (. != \"\") => trim |: (. == \"b\") => toupper", " a ", "b", "")), Is.EqualTo("a\nB\n\n"));
    }

    [TestCase("|: (. == 1)")]                         // type mismatch
    [TestCase("|: (.Name == \"a\")")]                 // field on a scalar pipe
    [TestCase("|: (. == \"a\") => sort")]             // buffering filter
    [TestCase("|: (. == \"a\") => sha256")]           // type-changing filter
    [TestCase("|: (. == \"a\") => drop else => drop elseif (. == \"b\") => drop")]
    [TestCase("|: (. == \"a\" and 1)")]
    [TestCase("|: (len(.) == 1)")]                    // function calls are not allowed
    [TestCase("|: (. == \"a\") => startswith")]       // filters with arguments
    public void InvalidConditionalStagesAreCompileErrors(string stage)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(StringPipeline(stage, "a")));

    [Test]
    public void OrderingOnBoolOrEnumIsRejected()
    {
        const string source = """
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create();
    let result: pipe<bool> = pipe<bool>.create();
    source |: (. < true) | result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void StringActionOnNonStringPipeIsRejected()
    {
        const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: (. > 1) => toupper | result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }
}
