using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class ByteShuffleFilterTests
{
    [TestCase("|: shuffle |", "")]
    [TestCase("|: shuffle |", "255")]
    [TestCase("|: shuffle |", "255,0,255,1,127,0")]
    [TestCase("| filter(shuffle) |", "255,0,255,1,127,0")]
    public void ByteShuffle_PreservesValuesAndMultiplicity(string pipeline, string values)
    {
        var expected = values.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToArray();
        var writes = string.Join(" ", values.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((value, index) => $"let byte{index}: byte = {value}; source.write(byte{index});"));
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<byte> = pipe<byte>.create(1);
    source {{pipeline}} result;
    {{writes}}
    source.close();
    foreach(value in result) { let item: byte = value; print(item); }
    return 0;
}
""";
        var module = new AloeCompiler().Compile(source);
        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var vm = new AloeVm(candidate);
            var output = new List<int>();
            vm.OutputWriter = line => output.Add(int.Parse(line));
            vm.RunFromEntryPoint();
            Assert.That(output.Order().ToArray(), Is.EqualTo(expected));
            Assert.That(vm.Pop().AsInt, Is.Zero);
        }
    }

    [TestCase("pipe<int>")]
    [TestCase("pipe<string>")]
    public void ByteShuffle_RejectsMismatchedOutput(string outputType)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: {{outputType}} = {{outputType}}.create(1);
    source |: shuffle | result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void ByteShuffle_RejectsArgumentsAndMismatchedBytecodePipes()
    {
        var source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<byte> = pipe<byte>.create(1);
    source |: shuffle | result;
    source.close();
    foreach(value in result) { print(value); }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source.Replace("|: shuffle |", "|: shuffle: \"key\" |")));
        var module = new AloeCompiler().Compile(source);
        var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString == "pipe<byte>"
            ? AloeValue.FromString("pipe<int>") : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("requires pipe<byte>"));
    }
}
