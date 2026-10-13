using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class StringArrayTests
{
    [Test]
    public void StringArrays_DocumentedAliasesWritesAndForeach()
    {
        const string source = """
function identity(values: string[]): string[] { return values; }
function main(args: string[]): int {
    let words: string[] = ["hello", "world"];
    var alias = words; identity(words)[0] = "Aloe";
    var count = 0;
    foreach (word in alias) { print(word); count = count + 1; }
    if (words[0] == "Aloe" and count == 2) { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("Aloe\nworld\n"));
    }

    [Test]
    public void StringArrays_EmptyUnicodeEscapeAndValueCopiesKeepExactStrings()
    {
        const string source = """
function main(args: string[]): int {
    let empty: string[] = []; empty = ["", "日本語😀", "A\nB"];
    var copy = empty[1]; empty[1] = "new";
    var count = 0; foreach (word in empty) { count = count + 1; }
    GC.require(); GC.finish();
    if (empty[0] == "" and copy == "日本語😀" and empty[1] == "new" and empty[2] == "A\nB" and count == 3) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StringArrays_NestedRowsEmptyContextsAndSoleOuterRootsSurviveGc()
    {
        const string source = """
function make(): string[][] { return [["Aloe"], ["lang", ""]]; }
function main(args: string[]): int {
    let words: string[][][] = [[[], ["start"]]];
    words[0][0] = ["Aloe"]; words[0][1][0] = "lang";
    var rows = make(); GC.require(); GC.finish();
    var count = 0; foreach (row in rows) { foreach (word in row) { count = count + 1; } }
    if (rows[0][0] == words[0][0][0] and rows[1][0] == words[0][1][0] and count == 3 and rows[1].Length == 2) { return 42; }
    return 0;
}
""";
        Assert.That(Run(source).ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void StringArrays_ClassAsyncWithAndTickQueriesReadCommittedStrings()
    {
        const string source = """
class Box {
    field values: string[];
    construct(values: string[]) { this.values = values; }
    public property First: string { get { return this.values[0]; } }
    public property Values: string[] { get { return this.values; } }
    public async method update(values: string[], query: Box): void {
        values[0] = "new"; print(query.First); print(values[0]);
    }
}
function main(args: string[]): int {
    var values = ["old"]; var box = new Box(values); var before = values[0];
    with (box) { .Values[0] = "ready"; } values[0] = "old";
    box.update(values, box); GC.require(); GC.finish(); tick();
    if (values[0] == "new" and box.First == "new" and before == "old") { return 42; }
    return 0;
}
""";
        var result = Run(source);
        Assert.That(result.ExitCode, Is.EqualTo(42));
        Assert.That(result.Output, Is.EqualTo("old\nnew\n"));
    }

    [Test]
    public void StringArrays_RejectMixedKindsCharConversionsWrongRankAndQueryMutation()
    {
        string[] statements =
        [
            "var values = [\"one\", 1];", "var values = [\"one\", 'a'];",
            "let values: string[] = [1];", "let values: string[] = ['a'];",
            "var values = [\"one\"]; values[0] = 1;", "var values = [\"one\"]; values[0] = 'a';",
            "var values = [\"one\"]; let other: char[] = values;",
            "var values = [\"one\"]; let other: int = values[0];",
            "let values: string[][] = [[1]];", "var values = [[\"one\"]]; values[0] = [true];",
            "foreach (word in [\"one\"]) { let wrong: char = word; }",
            "var values = [\"one\"]; values.Length = 0;"
        ];
        foreach (var statement in statements)
            Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args: string[]): int { " + statement + " return 0; }"), statement);
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().CompileForWasm("function main(args: string[]): int { var words = [\"one\"]; return 0; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args: string[]): int { return args.Length; }"));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("class Box { field words: string[]; construct(words: string[]) { this.words = words; } public property First: string { get { this.words[0] = \"new\"; return \"old\"; } } } function main(args: string[]): int { return 0; }"));
        Assert.Throws<VmException>(() => Run("function mutate(words: string[]): string { words[0] = \"new\"; return \"old\"; } class Box { field words: string[]; construct(words: string[]) { this.words = words; } public property First: string { get { return mutate(this.words); } } } function main(args: string[]): int { var box = new Box([\"old\"]); print(box.First); return 0; }"));
    }

    [Test]
    public void StringArrays_RuntimeRejectsWrongValuesAndPreservesLeafStorageWithoutObjectEdges()
    {
        foreach (var value in new[] { AloeValue.Null, AloeValue.FromInt(1), AloeValue.FromChar('a'), AloeValue.FromObject(long.MaxValue) })
        {
            var vm = CreateVm();
            vm.Push(value); vm.Push(AloeValue.FromString("string[]")); vm.Push(AloeValue.FromInt(1));
            Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.ArrayCreate));
            Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        }
        var valid = CreateVm();
        valid.Push(AloeValue.FromString("日本語😀")); valid.Push(AloeValue.FromString("string[]")); valid.Push(AloeValue.FromInt(1));
        valid.InvokeSyscall(EnumSyscall.ArrayCreate); var array = valid.Pop();
        Assert.That(valid.Heap.GetEntry(array.AsObjectId).OutgoingReferences.Count, Is.EqualTo(0));
        foreach (var value in new[] { AloeValue.Null, AloeValue.FromChar('a') })
        {
            valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.Push(value);
            Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArraySet));
        }
        valid.Push(array); valid.Push(AloeValue.FromInt(0)); valid.InvokeSyscall(EnumSyscall.ArrayGet);
        Assert.That(valid.Pop().AsString, Is.EqualTo("日本語😀"));
        valid.Push(array); valid.Push(AloeValue.FromInt(1));
        Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArrayGet));
        valid.Heap.SetField(array.AsObjectId, 0, AloeValue.FromInt(1));
        valid.Push(array); valid.Push(AloeValue.FromInt(0));
        Assert.Throws<VmException>(() => valid.InvokeSyscall(EnumSyscall.ArrayGet));
    }

    private static AloeVm CreateVm(Module? module = null)
    {
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        return new AloeVm(module ?? new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }
    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var vm = CreateVm(module); var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
