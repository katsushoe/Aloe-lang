using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

public sealed class ByteSortFilterTests
{
    [TestCase("|: sort |", "", "")]
    [TestCase("|: sort |", "255", "255\n")]
    [TestCase("|: sort |", "255,0,255,1,127,0", "0\n0\n1\n127\n255\n255\n")]
    [TestCase("| filter(sort) |", "255,0,255,1,127,0", "0\n0\n1\n127\n255\n255\n")]
    [TestCase("| filter(sort) |", "0,1,127,255", "0\n1\n127\n255\n")]
    public void ByteSort_PreservesKindAndOrdersAfterInputCloses(string pipeline, string values, string expected)
    {
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
            var output = new StringBuilder();
            vm.OutputWriter = line => output.AppendLine(line);
            vm.RunFromEntryPoint();
            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo(expected));
            Assert.That(vm.Pop().AsInt, Is.Zero);
        }
    }

    [TestCase("pipe<int>", "|: sort |")]
    [TestCase("pipe<string>", "| filter(sort) |")]
    public void ByteSort_RejectsIncompatibleOutput(string outputType, string pipeline)
    {
        var source = $$"""
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: {{outputType}} = {{outputType}}.create(1);
    source {{pipeline}} result;
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
    }

    [Test]
    public void ByteSort_RejectsArgumentsAndMismatchedBytecodePipes()
    {
        var source = """
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<byte> = pipe<byte>.create(1);
    source |: sort | result;
    source.close();
    foreach(value in result) { print(value); }
    return 0;
}
""";
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source.Replace("|: sort |", "|: sort: \"key\" |")));
        var module = new AloeCompiler().Compile(source);
        var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString == "pipe<byte>"
            ? AloeValue.FromString("pipe<int>") : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<Aloe.CommonLib.Exceptions.VmException>(() => new AloeVm(invalid).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("requires pipe<byte>"));
    }
}
