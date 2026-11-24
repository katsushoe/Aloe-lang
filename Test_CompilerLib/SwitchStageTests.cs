using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0056: `|: switch (key) : value [or value] => action, ..., default|_ => action`.
public sealed class SwitchStageTests
{
    private static string Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new System.Text.StringBuilder();
        var vm = new AloeVm(module) { OutputWriter = line => output.Append(line).Append('\n') };
        vm.RunFromEntryPoint();
        return output.ToString();
    }

    private static string Words(string stage, params string[] values)
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
    public void MatchingValueAppliesItsActionAndDefaultHandlesTheRest()
        => Assert.That(Run(Words("|: switch (.) : \"a\" => toupper, \"b\" => drop, default => trim", "a", "b", " c ")),
            Is.EqualTo("A\nc\n"));

    [Test]
    public void UnderscoreIsADefaultAlias()
        => Assert.That(Run(Words("|: switch (.) : \"a\" => toupper, _ => drop", "a", "b")), Is.EqualTo("A\n"));

    [Test]
    public void WithoutDefaultNonMatchingElementsPassThrough()
        => Assert.That(Run(Words("|: switch (.) : \"a\" or \"b\" => toupper", "a", "x", "b")), Is.EqualTo("A\nx\nB\n"));

    [Test]
    public void EnumFieldKeysWork()
    {
        const string source = """
enum Kind { Note, Raw, Bin }
struct Item { field kind: Kind; field size: int; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create();
    let result: pipe<Item> = pipe<Item>.create();
    source |: switch (.kind) : Kind.Raw or Kind.Bin => drop | result;
    source.write(Item { kind: Kind.Note, size: 1 });
    source.write(Item { kind: Kind.Raw, size: 2 });
    source.write(Item { kind: Kind.Bin, size: 3 });
    source.write(Item { kind: Kind.Note, size: 4 });
    source.close();
    foreach (item in result) { print(item.size); }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo("1\n4\n"));
    }

    [Test]
    public void IntKeysOnScalarPipes()
    {
        const string source = """
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: switch (.) : 2 => drop, -1 => drop | result;
    source.write(1); source.write(2); source.write(-1); source.write(3);
    source.close();
    foreach (value in result) { print(value); }
    return 0;
}
""";
        Assert.That(Run(source), Is.EqualTo("1\n3\n"));
    }

    [TestCase("|: switch (.) : 1 => drop")]                                   // value type mismatch
    [TestCase("|: switch (.) : \"a\" => drop, \"a\" => toupper")]            // duplicate value
    [TestCase("|: switch (.) : \"a\" => drop, default => drop, _ => drop")]   // default not last
    [TestCase("|: switch (.) : default => drop")]                            // no value branch
    [TestCase("|: switch (\"x\") : \"a\" => drop")]                          // key is not . or .Field
    [TestCase("|: switch (.) : \"a\" => sort")]                              // unsupported action
    public void InvalidSwitchStagesAreCompileErrors(string stage)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Words(stage, "a")));
}
