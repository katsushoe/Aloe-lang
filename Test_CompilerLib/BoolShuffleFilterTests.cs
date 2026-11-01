using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class BoolShuffleFilterTests
{
    [TestCase("")]
    [TestCase("false")]
    [TestCase("true")]
    [TestCase("false,false,false")]
    [TestCase("true,true,true")]
    [TestCase("true,false")]
    [TestCase("false,true,false,true,true,false,false")]
    public void BoolShuffle_PreservesExactKindsAndMultiplicity(string values)
    {
        var inputs = values.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var falseCount = inputs.Count(value => value == "false");
        var trueCount = inputs.Length - falseCount;
        var writes = string.Join(" ", inputs.Select(value => $"source.write({value});"));
        foreach (var pipeline in new[] { "|: shuffle |", "| filter(shuffle) |" })
        {
            var module = new AloeCompiler().Compile($$"""
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source {{pipeline}} result;
    {{writes}} source.close();
    var falses = 0; var trues = 0;
    foreach(value in result) {
        let item: bool = value;
        if(item) { trues = trues + 1; } else { falses = falses + 1; }
    }
    if(falses != {{falseCount}} or trues != {{trueCount}}) { return -1; }
    return falses + trues;
}
""");
            foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
            {
                var vm = new AloeVm(candidate);
                vm.RunFromEntryPoint();
                Assert.That(vm.Pop().AsInt, Is.EqualTo(inputs.Length), pipeline);
            }
        }
    }

    [Test]
    public void BoolShuffle_CapacityOneChainsDrainCompleteMultiset()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let middle: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source |: shuffle | middle;
    middle | filter(shuffle) | result;
    var n = 0;
    while(n < 1000) { source.write(n % 2 == 0); n = n + 1; }
    source.close();
    var falses = 0; var trues = 0;
    foreach(value in result) {
        if(value) { trues = trues + 1; } else { falses = falses + 1; }
    }
    if(falses != 500 or trues != 500) { return -1; }
    return falses + trues;
}
""");
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(1000));
    }

    private const string Program = """
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source |: shuffle | result;
    source.write(true); source.close();
    foreach(value in result) { if(value) { print(true); } else { print(false); } }
    return 0;
}
""";

    [TestCase("pipe<int>")]
    [TestCase("pipe<byte>")]
    [TestCase("pipe<string>")]
    public void BoolShuffle_RejectsIncompatibleSourceAndBytecodeBindings(string pipeType)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(
            Program.Replace("let result: pipe<bool> = pipe<bool>", $"let result: {pipeType} = {pipeType}")));
        var module = new AloeCompiler().Compile(Program);
        var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString == "pipe<bool>"
            ? AloeValue.FromString(pipeType) : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(invalid))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("requires pipe<bool>"));
    }

    [TestCase(".Field")]
    [TestCase("\"key\"")]
    [TestCase("true")]
    public void BoolShuffle_RejectsArguments(string argument)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program.Replace("|: shuffle |", $"|: shuffle: {argument} |")));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program.Replace("|: shuffle |", $"| filter(shuffle: {argument}) |")));
    }

    [TestCase("int")]
    [TestCase("byte")]
    [TestCase("string")]
    public void BoolShuffle_RejectsNonbooleanBytecodePayloads(string kind)
    {
        var module = new AloeCompiler().Compile(Program);
        Assert.That(module.Constants.Any(value => value.IsBool && value.AsBool), Is.True);
        var replacement = kind switch
        {
            "int" => AloeValue.FromInt(1),
            "byte" => AloeValue.FromByte(1),
            _ => AloeValue.FromString("true")
        };
        var invalid = new Module(module.Constants.Select(value => value.IsBool && value.AsBool
            ? replacement : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(invalid))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("bool"));
    }

    [Test]
    public void BoolShuffle_RejectsClosedOutputWhenEmitting()
    {
        var module = new AloeCompiler().Compile(Program.Replace("source.write(true);", "result.close(); source.write(true);"));
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("closed pipe"));
    }
}
