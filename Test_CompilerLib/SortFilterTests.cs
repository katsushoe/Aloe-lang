using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class SortFilterTests
{
    [TestCase("en-US")]
    [TestCase("tr-TR")]
    public void StringSortUsesOrdinalOrderAndPreservesContentsThroughChainedCapacityOnePipes(string cultureName)
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(cultureName);
            var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let middle: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source | filter(sort) | middle;
    middle |: sort | result;
    foreach (value in ["ı", "a", "é", "I", "𐀀", "Z", "", "ä", "i", "é", "z", "A", "a", ""]) { source.write(value); }
    source.close();
    let expected: string[] = ["", "A", "I", "Z", "a", "a", "é", "i", "z", "ä", "é", "ı", "𐀀", ""];
    var index = 0;
    foreach (value in result) {
        if (index >= expected.Length or value != expected[index]) { return -1; }
        index = index + 1;
    }
    return index;
}
""");
            foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
            {
                var vm = new AloeVm(candidate);
                vm.RunFromEntryPoint();
                Assert.That(vm.Pop().AsInt, Is.EqualTo(14));
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public void EmptyStringSortClosesWithoutOutput()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source |: sort | result;
    source.close();
    var count = 0;
    foreach (value in result) { count = count + 1; }
    return count;
}
""");
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
        foreach (var pipeType in new[] { "pipe<int>", "pipe<byte>" })
        {
            var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString == "pipe<string>"
                ? AloeValue.FromString(pipeType) : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
            var error = Assert.Throws<VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
            Assert.That(error!.Message, Does.Contain("requires pipe<string>"));
        }
    }

    [Test]
    public void StringSortRejectsKeyArgumentsAndClosedOutput()
    {
        var source = """
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<string> = pipe<string>.create();
    source |: sort | result;
    result.close();
    source.write("value"); source.close();
    foreach (value in result) { }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source.Replace("|: sort |", "|: sort: .key |")));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source.Replace("|: sort |", "|: sort: \"key\" |")));
        Assert.Throws<VmException>(() => new AloeVm(new AloeCompiler().Compile(source)).RunFromEntryPoint());
    }

    [Test]
    public void ChainedSortsDrainLargeReverseInputThroughCapacityOnePipes()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let middle: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(sort) | middle;
    middle |: sort | result;
    var index = 2000;
    while (index > 0) { source.write(index); index = index - 1; }
    source.close();
    var expected = 1;
    foreach (value in result) {
        if (value != expected) { return -1; }
        expected = expected + 1;
    }
    return expected - 1;
}
""");
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(2000));
    }

    [TestCase("pipe<string>")]
    [TestCase("pipe<byte>")]
    public void SortBytecodeRejectsUnsupportedPipeBindings(string pipeType)
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: sort | result;
    return 0;
}
""");
        var constants = module.Constants.Select(value => value.IsString && value.AsString == "pipe<int>"
            ? AloeValue.FromString(pipeType) : value).ToArray();
        var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
        var exception = Assert.Throws<VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
        Assert.That(exception!.Message, Does.Contain("requires pipe<int>"));
    }

    [TestCase("5, -1, 5, 0, -2147483648, 2147483647", "-2147483648, -1, 0, 5, 5, 2147483647")]
    [TestCase("1", "1")]
    [TestCase("-3, -2, -1, 0, 1", "-3, -2, -1, 0, 1")]
    [TestCase("3, 2, 1", "1, 2, 3")]
    public void SortOrdersFullInputWithCapacityOneAndAloeBcRoundTrip(string input, string expected)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source |: sort | result;
    foreach (value in [{{input}}]) { source.write(value); }
    source.close();
    let expected: int[] = [{{expected}}];
    var index = 0;
    foreach (value in result) {
        if (index >= expected.Length) { return -1; }
        if (value != expected[index]) { return -2; }
        index = index + 1;
    }
    return index;
}
""";
        var module = new AloeCompiler().Compile(source);
        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var vm = new AloeVm(candidate);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(expected.Split(',').Length));
        }
    }

    [Test]
    public void EmptySortClosesOutputWithoutValues()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source |: sort | result;
    source.close();
    var count = 0;
    foreach (value in result) { count = count + 1; }
    return count;
}
""");
        var vm = new AloeVm(module);
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }

    [Test]
    public void KeyedStructSortIsStableAndPreservesRecordsThroughAloeBcRoundTrip()
    {
        var module = new AloeCompiler().Compile("""
struct Item { field key: int; field tag: string; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create(1);
    let result: pipe<Item> = pipe<Item>.create(1);
    source |: sort: .key | result;
    source.write(Item { key: 5, tag: "first" });
    source.write(Item { key: -2147483648, tag: "min" });
    source.write(Item { key: 5, tag: "second" });
    source.write(Item { key: 2147483647, tag: "max" });
    source.write(Item { key: 5, tag: "first" });
    source.close();
    let expectedKeys: int[] = [-2147483648, 5, 5, 5, 2147483647];
    let expectedTags: string[] = ["min", "first", "second", "first", "max"];
    var index = 0;
    foreach (item in result) {
        if (index >= expectedKeys.Length or item.key != expectedKeys[index] or item.tag != expectedTags[index]) { return -1; }
        index = index + 1;
    }
    return index;
}
""");

        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var vm = new AloeVm(candidate);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(5));
        }
    }

    [Test]
    public void KeyedStructSortRejectsMissingOrUnsupportedFieldsAndNonStructPipes()
    {
        Assert.Throws<AloeCompileException>(() => CompileKeyedSort("struct Item { field key: int; }", "Item", ".missing"));
        Assert.Throws<AloeCompileException>(() => CompileKeyedSort("struct Item { field key: int; }", "Item", ".Key"));
        Assert.Throws<AloeCompileException>(() => CompileKeyedSort("struct Item { field key: bool; }", "Item", ".key"));
        Assert.Throws<AloeCompileException>(() => CompileKeyedSort("struct Item { field nested: Inner; } struct Inner { field key: int; }", "Item", ".nested.key"));
        Assert.Throws<AloeCompileException>(() => CompileKeyedSort("", "int", ".key"));
    }

    [Test]
    public void EmptyKeyedStructSortClosesOutputWithoutValues()
    {
        var vm = new AloeVm(CompileKeyedSort("struct Item { field key: int; }", "Item", ".key"));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }

    [Test]
    public void KeyedStructSortRejectsCorruptedRawBytecodeSchema()
    {
        var module = CompileKeyedSort("struct Item { field key: int; }", "Item", ".key");
        foreach (var schema in new[]
        {
            "{broken",
            "{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"extra\":0}",
            "{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldName\":\"other\",\"fieldIndex\":0}",
            "{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldN\\u0061me\":\"other\",\"fieldIndex\":0}"
        })
        {
            var constants = module.Constants.Select(value => value.IsString && value.AsString.Contains("fieldName", StringComparison.Ordinal)
                ? AloeValue.FromString(schema)
                : value).ToArray();
            var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
            var exception = Assert.Throws<VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
            Assert.That(exception!.Message, Does.Contain("sort filter schema is invalid"), schema);
        }
    }

    [Test]
    public void KeyedStructSortRejectsRawSchemaIndexOutsideTheRuntimeStruct()
    {
        var module = new AloeCompiler().Compile("""
struct Item { field key: int; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create(1);
    let result: pipe<Item> = pipe<Item>.create(1);
    source |: sort: .key | result;
    source.write(Item { key: 1 });
    source.close();
    foreach (item in result) { return item.key; }
    return 0;
}
""");
        var constants = module.Constants.Select(value => value.IsString && value.AsString.Contains("fieldName", StringComparison.Ordinal)
            ? AloeValue.FromString("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":4}")
            : value).ToArray();
        var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
        var exception = Assert.Throws<VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
        Assert.That(exception!.Message, Does.Contain("expects struct 'Item' with field 'key'"));
    }

    private static Module CompileKeyedSort(string declaration, string typeName, string key)
    {
        var pipeType = $"pipe<{typeName}>";
        return new AloeCompiler().Compile($$"""
{{declaration}}
function main(args: string[]): int {
    let source: {{pipeType}} = {{pipeType}}.create();
    let result: {{pipeType}} = {{pipeType}}.create();
    source |: sort: {{key}} | result;
    source.close();
    foreach (item in result) { return 1; }
    return 0;
}
""");
    }

    [TestCase("pipe<string>", "pipe<int>")]
    [TestCase("pipe<int>", "pipe<string>")]
    public void SortRejectsUnsupportedPipeTypes(string input, string output)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile($$"""
function main(args: string[]): int {
    let source: {{input}} = {{input}}.create();
    let result: {{output}} = {{output}}.create();
    source |: sort | result;
    return 0;
}
"""));
    }

    [Test]
    public void SortRejectsKeyArgument()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: sort: "key" | result;
    return 0;
}
"""));
    }

    [TestCase(2147483648L)]
    [TestCase(long.MinValue)]
    [TestCase(long.MaxValue)]
    public void IntSortRejectsOutOfDomainRawBytecodeValues(long value)
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(); let result: pipe<int> = pipe<int>.create(); source |: sort | result; source.write(7); source.close(); foreach (item in result) {} return 0; }");
        var constants = module.Constants.Select(item => item.IsInt && item.AsInt == 7 ? AloeValue.FromInt(value) : item).ToArray();
        var invalid = AloeBcCodec.Read(AloeBcCodec.Write(new Module(constants, module.Code, module.Functions, module.EntryPointIndex)));
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        Assert.That(() => new AloeVm(invalid, settings).RunFromEntryPoint(),
            Throws.TypeOf<VmException>().With.Message.Contains("int32"));
    }

    [Test]
    public void SortRejectsClosedOutputWithValues()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create();
    let result: pipe<int> = pipe<int>.create();
    source |: sort | result;
    result.close();
    source.write(1);
    source.close();
    foreach (value in result) {}
    return 0;
}
""");
        Assert.Throws<VmException>(() => new AloeVm(module).RunFromEntryPoint());
    }
}
