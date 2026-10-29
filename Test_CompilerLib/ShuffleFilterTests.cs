using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Globalization;
using System.Text.Json;

namespace Aloe.CompilerLib.Tests;

public sealed class ShuffleFilterTests
{
    [TestCase("")]
    [TestCase("7")]
    [TestCase("9, 9, 9")]
    [TestCase("5, -1, 5, 0, -2147483648, 2147483647")]
    [TestCase("0, 1, 2, 3, 4, 5, 6, 7, 8, 9")]
    public void ShufflePreservesMultisetThroughCapacityOneAndAloeBc(string input)
    {
        var values = input.Length == 0 ? Array.Empty<int>() : input.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var writes = string.Join(" ", values.Select(value => $"source.write({value.ToString(CultureInfo.InvariantCulture)});"));
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source |: shuffle | result;
    {{writes}}
    source.close();
    var count = 0;
    foreach (value in result) { print(value); count = count + 1; }
    return count;
}
""";
        var module = new AloeCompiler().Compile(source);
        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var actual = new List<int>();
            var vm = CreateVm(candidate);
            vm.OutputWriter = line => actual.Add(int.Parse(line, CultureInfo.InvariantCulture));
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(values.Length));
            Assert.That(actual.Order(), Is.EqualTo(values.Order()));
        }
    }

    [Test]
    public void ShuffleAndSortChainPreservesEveryItemAcrossCapacityOnePipes()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<int> = pipe<int>.create(1);
    let middle: pipe<int> = pipe<int>.create(1);
    let result: pipe<int> = pipe<int>.create(1);
    source | filter(shuffle) | middle;
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
        var vm = CreateVm(AloeBcCodec.Read(AloeBcCodec.Write(module)));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(2000));
    }

    [TestCase("pipe<string>", "pipe<int>")]
    [TestCase("pipe<int>", "pipe<string>")]
    [TestCase("pipe<byte>", "pipe<int>")]
    public void ShuffleRejectsUnsupportedPipeTypes(string input, string output)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile($$"""
function main(args: string[]): int {
    let source: {{input}} = {{input}}.create();
    let result: {{output}} = {{output}}.create();
    source |: shuffle | result;
    return 0;
}
"""));
    }

    [TestCase("pipe<string>")]
    [TestCase("pipe<byte>")]
    public void ShuffleBytecodeRejectsUnsupportedPipeBindings(string pipeType)
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(); let result: pipe<int> = pipe<int>.create(); source |: shuffle | result; return 0; }");
        var constants = module.Constants.Select(value => value.IsString && value.AsString == "pipe<int>"
            ? AloeValue.FromString(pipeType) : value).ToArray();
        var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
        Assert.That(() => CreateVm(invalid).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("requires pipe<int>"));
    }

    [Test]
    public void ShuffleRejectsSeedArgument()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(); let result: pipe<int> = pipe<int>.create(); source |: shuffle: \"seed\" | result; return 0; }"));
    }

    [Test]
    public void ShuffleDoesNotEmitBeforeInputCloses()
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(1); let result: pipe<int> = pipe<int>.create(1); source |: shuffle | result; source.write(7); foreach (value in result) { break; } source.close(); return 0; }");
        Assert.That(() => CreateVm(module).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("deadlock"));
    }

    [TestCase(2147483648L)]
    [TestCase(long.MinValue)]
    [TestCase(long.MaxValue)]
    public void ShuffleRejectsOutOfDomainRawBytecodeValues(long value)
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(); let result: pipe<int> = pipe<int>.create(); source |: shuffle | result; source.write(7); source.close(); foreach (value in result) {} return 0; }");
        var constants = module.Constants.Select(item => item.IsInt && item.AsInt == 7 ? AloeValue.FromInt(value) : item).ToArray();
        var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
        Assert.That(() => CreateVm(invalid).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("valid int32"));
    }

    [Test]
    public void ShuffleRejectsClosedOutputWithValues()
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<int> = pipe<int>.create(); let result: pipe<int> = pipe<int>.create(); source |: shuffle | result; result.close(); source.write(1); source.close(); foreach (value in result) {} return 0; }");
        Assert.That(() => CreateVm(module).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("closed pipe"));
    }

    [TestCase("")]
    [TestCase("\"one\"")]
    [TestCase("\"same\", \"same\", \"same\"")]
    [TestCase("\"z\", \"\", \"é\", \"é\", \"𐀀\", \"é\"")]
    public void StringShufflePreservesExactMultisetThroughChainedCapacityOnePipesAndAloeBc(string input)
    {
        var expected = JsonSerializer.Deserialize<string[]>($"[{input}]")!;
        var module = new AloeCompiler().Compile($$"""
function main(args: string[]): int {
    let values: string[] = [{{input}}];
    let source: pipe<string> = pipe<string>.create(1);
    let middle: pipe<string> = pipe<string>.create(1);
    let result: pipe<string> = pipe<string>.create(1);
    source | filter(shuffle) | middle;
    middle |: shuffle | result;
    foreach (value in values) { source.write(value); }
    source.close();
    var count = 0;
    foreach (value in result) { print(value); count = count + 1; }
    return count;
}
""");
        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var actual = new List<string>();
            var vm = CreateVm(candidate);
            vm.OutputWriter = actual.Add;
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(expected.Length));
            Assert.That(actual.Order(StringComparer.Ordinal), Is.EqualTo(expected.Order(StringComparer.Ordinal)));
        }
    }

    [Test]
    public void StringShuffleRejectsWrongRawPipeTypesAndNonStringValues()
    {
        var module = new AloeCompiler().Compile("function main(args: string[]): int { let source: pipe<string> = pipe<string>.create(); let result: pipe<string> = pipe<string>.create(); source |: shuffle | result; source.write(\"payload\"); source.close(); foreach (value in result) {} return 0; }");
        foreach (var pipeType in new[] { "pipe<int>", "pipe<byte>" })
        {
            var constants = module.Constants.Select(value => value.IsString && value.AsString == "pipe<string>" ? AloeValue.FromString(pipeType) : value).ToArray();
            var invalid = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
            Assert.That(() => CreateVm(invalid).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("requires pipe<string>"));
        }
        var wrongValues = module.Constants.Select(value => value.IsString && value.AsString == "payload" ? AloeValue.FromInt(7) : value).ToArray();
        Assert.That(() => CreateVm(new Module(wrongValues, module.Code, module.Functions, module.EntryPointIndex)).RunFromEntryPoint(), Throws.TypeOf<VmException>());
    }

    [Test]
    public void StringShuffleRejectsSeedClosedOutputAndReadingBeforeClosure()
    {
        const string source = "function main(args: string[]): int { let source: pipe<string> = pipe<string>.create(1); let result: pipe<string> = pipe<string>.create(1); source |: shuffle | result; source.write(\"value\"); source.close(); foreach (value in result) {} return 0; }";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source.Replace("|: shuffle |", "|: shuffle: \"seed\" |")));
        var closed = new AloeCompiler().Compile(source.Replace("source.write", "result.close(); source.write"));
        Assert.That(() => CreateVm(closed).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("closed pipe"));
        var open = new AloeCompiler().Compile(source.Replace("source.close();", ""));
        Assert.That(() => CreateVm(open).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("deadlock"));
    }

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        return new AloeVm(module, settings);
    }
}
