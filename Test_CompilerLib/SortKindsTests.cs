using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0054: decimal/char/float element and key sorts, descending order, stable ties.
public sealed class SortKindsTests
{
    private static (string Output, long Exit) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var output = new System.Text.StringBuilder();
        var vm = new AloeVm(module) { OutputWriter = line => output.Append(line).Append('\n') };
        vm.RunFromEntryPoint();
        return (output.ToString(), vm.Pop().AsInt);
    }

    private static string MainSource(string body) => "function main(args: string[]): int {\n" + body + "\n    return 0;\n}\n";

    [TestCase("|: sort", "1.5\n2.00\n2.0\n10\n")]
    [TestCase("| filter(sort)", "1.5\n2.00\n2.0\n10\n")]
    [TestCase("|: sort: desc", "10\n2.00\n2.0\n1.5\n")]
    public void DecimalSort_IsNumericStableAndPreservesRepresentation(string stage, string expected)
    {
        var source = MainSource($$"""
    let source: pipe<decimal> = pipe<decimal>.create(1);
    let result: pipe<decimal> = pipe<decimal>.create(1);
    source {{stage}} | result;
    source.write(10:d); source.write(2.00:d); source.write(1.5:d); source.write(2.0:d);
    source.close();
    foreach (value in result) { print(value); }
""");
        Assert.That(Run(source).Output, Is.EqualTo(expected));
    }

    [TestCase("|: sort", "A\nZ\na\nz\n")]
    [TestCase("|: sort: desc", "z\na\nZ\nA\n")]
    public void CharSort_UsesUtf16CodeUnits(string stage, string expected)
    {
        var source = MainSource($$"""
    let source: pipe<char> = pipe<char>.create(1);
    let result: pipe<char> = pipe<char>.create(1);
    source {{stage}} | result;
    source.write('z'); source.write('A'); source.write('a'); source.write('Z');
    source.close();
    foreach (value in result) { print(value); }
""");
        Assert.That(Run(source).Output, Is.EqualTo(expected));
    }

    [Test]
    public void FloatSort_AscendingDescendingAndEmptyInput()
    {
        var source = MainSource("""
    let source: pipe<float> = pipe<float>.create(1);
    let up: pipe<float> = pipe<float>.create();
    source |: sort | up;
    source.write(2.5); source.write(-1.25); source.write(0.5);
    source.close();
    foreach (value in up) { print(value); }
    let other: pipe<float> = pipe<float>.create();
    let down: pipe<float> = pipe<float>.create();
    other |: sort: desc | down;
    other.write(2.5); other.write(-1.25); other.write(0.5);
    other.close();
    foreach (value in down) { print(value); }
    let empty: pipe<float> = pipe<float>.create();
    let none: pipe<float> = pipe<float>.create();
    empty |: sort | none;
    empty.close();
    var count = 0;
    foreach (value in none) { count = count + 1; }
    print(count);
""");
        Assert.That(Run(source).Output, Is.EqualTo("-1.25\n0.5\n2.5\n2.5\n0.5\n-1.25\n0\n"));
    }

    [TestCase("int", "3, 1, 2", "3\n2\n1\n")]
    [TestCase("string", "\"b\", \"a\", \"c\"", "c\nb\na\n")]
    [TestCase("byte", "3, 1, 2", "3\n2\n1\n")]
    [TestCase("bool", "false, true, false", "true\nfalse\nfalse\n")]
    public void DescendingSort_ExistingScalarKinds(string kind, string values, string expected)
    {
        var writes = string.Join(" ", values.Split(", ").Select((v, i) =>
            kind == "byte" ? $"let b{i}: byte = {v}; source.write(b{i});" : $"source.write({v});"));
        var source = MainSource($$"""
    let source: pipe<{{kind}}> = pipe<{{kind}}>.create(1);
    let result: pipe<{{kind}}> = pipe<{{kind}}>.create(1);
    source |: sort: desc | result;
    {{writes}}
    source.close();
    foreach (value in result) { print(value); }
""");
        Assert.That(Run(source).Output, Is.EqualTo(expected));
    }

    [Test]
    public void KeyedSort_CharFloatDecimalKeysAscendingAndDescendingAreStable()
    {
        const string source = """
struct Item {
    field name: string;
    field grade: char;
    field score: float;
    field price: decimal;
}

function emit(input: pipe<Item>): void {
    input.write(Item { name: "a", grade: 'B', score: 2.5, price: 3.00:d });
    input.write(Item { name: "b", grade: 'A', score: 1.5, price: 1.5:d });
    input.write(Item { name: "c", grade: 'B', score: 2.5, price: 3.0:d });
    input.close();
}

function main(args: string[]): int {
    let g: pipe<Item> = pipe<Item>.create(1);
    let gr: pipe<Item> = pipe<Item>.create(1);
    g |: sort: .grade | gr;
    emit(g);
    foreach (item in gr) { print(item.name); }
    let s: pipe<Item> = pipe<Item>.create(1);
    let sr: pipe<Item> = pipe<Item>.create(1);
    s |: sort: .score desc | sr;
    emit(s);
    foreach (item in sr) { print(item.name); }
    let p: pipe<Item> = pipe<Item>.create(1);
    let pr: pipe<Item> = pipe<Item>.create(1);
    p |: sort: .price | pr;
    emit(p);
    foreach (item in pr) { print(item.price); }
    return 0;
}
""";
        Assert.That(Run(source).Output, Is.EqualTo("b\na\nc\na\nc\nb\n1.5\n3.00\n3.0\n"));
    }

    [TestCase("pipe<decimal>", "pipe<int>", "|: sort")]
    [TestCase("pipe<char>", "pipe<string>", "|: sort")]
    [TestCase("pipe<float>", "pipe<float>", "|: sort: up")]
    [TestCase("pipe<float>", "pipe<float>", "|: shuffle")]
    public void Sort_RejectsMismatchedTypesAndUnknownArguments(string inputType, string outputType, string stage)
    {
        var source = MainSource($$"""
    let source: {{inputType}} = {{inputType}}.create();
    let result: {{outputType}} = {{outputType}}.create();
    source {{stage}} | result;
""");
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void KeyedSort_RejectsDescOnUnsupportedKeyAndExtraTokens()
    {
        const string source = """
struct Inner { field v: int; }
struct Item { field inner: Inner; }
function main(args: string[]): int {
    let s: pipe<Item> = pipe<Item>.create();
    let r: pipe<Item> = pipe<Item>.create();
    s |: sort: .inner desc | r;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void PipeOfCharAndFloat_RejectsWrongElementWrites()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(MainSource("""
    let source: pipe<char> = pipe<char>.create();
    source.write("ab");
""")));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(MainSource("""
    let source: pipe<float> = pipe<float>.create();
    source.write("1.5");
""")));
    }

    [Test]
    public void ExistingAscendingSortsKeepTheirOriginalBytecodeIndices()
    {
        var module = new AloeCompiler().Compile(MainSource("""
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: sort | result;
    source.close();
"""));
        var hasLegacyIndex = module.Constants.Any(c => c.IsInt && c.AsInt == -32) ||
            module.Code.Any(i => i.Opcode == Aloe.CommonLib.Constants.EnumOpcode.PushConst &&
                module.Constants[i.Operand0].IsInt && module.Constants[i.Operand0].AsInt == -32);
        Assert.That(hasLegacyIndex, Is.True);
        Assert.That(module.Constants.Any(c => c.IsInt && c.AsInt == -43), Is.False);
    }
}
