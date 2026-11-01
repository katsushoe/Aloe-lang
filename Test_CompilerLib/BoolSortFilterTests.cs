using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class BoolSortFilterTests
{
    [TestCase("", 0, 0)]
    [TestCase("false", 1, 0)]
    [TestCase("true", 0, 1)]
    [TestCase("false,true", 1, 1)]
    [TestCase("true,false", 1, 1)]
    [TestCase("true,false,true,false,false,true", 3, 3)]
    public void BoolSort_PreservesExactKindsCountsAndOrder(string values, int falseCount, int trueCount)
    {
        var writes = string.Join(" ", values.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => $"source.write({value});"));
        foreach (var pipeline in new[] { "|: sort |", "| filter(sort) |" })
        {
            var module = new AloeCompiler().Compile($$"""
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source {{pipeline}} result;
    {{writes}} source.close();
    var count = 0;
    foreach(value in result) {
        let item: bool = value;
        if (count < {{falseCount}}) { if (item) { return -1; } }
        else { if (not item) { return -2; } }
        count = count + 1;
    }
    return count;
}
""");
            foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
            {
                var vm = new AloeVm(candidate);
                vm.RunFromEntryPoint();
                Assert.That(vm.Pop().AsInt, Is.EqualTo(falseCount + trueCount), pipeline);
            }
        }
    }

    [Test]
    public void BoolSort_ChainedCapacityOnePipesDrainLargeInput()
    {
        var module = new AloeCompiler().Compile("""
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let middle: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source |: sort | middle;
    middle | filter(sort) | result;
    var n = 0;
    while (n < 1000) { source.write(n % 2 == 0); n = n + 1; }
    source.close();
    var count = 0;
    foreach(value in result) {
        let item: bool = value;
        if (item != (count >= 500)) { return -1; }
        count = count + 1;
    }
    return count;
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
    source |: sort | result;
    source.write(true); source.close();
    foreach(value in result) { let item: bool = value; print(item); }
    return 0;
}
""";

    [TestCase("pipe<int>")]
    [TestCase("pipe<byte>")]
    [TestCase("pipe<string>")]
    public void BoolSort_RejectsIncompatibleOutputAndBytecodePipes(string pipeType)
    {
        var invalidSource = Program.Replace("let result: pipe<bool> = pipe<bool>", $"let result: {pipeType} = {pipeType}");
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(invalidSource));
        var module = new AloeCompiler().Compile(Program);
        var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString == "pipe<bool>"
            ? AloeValue.FromString(pipeType) : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(invalid))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("requires pipe<bool>"));
    }

    [TestCase(".Field")]
    [TestCase("\"key\"")]
    [TestCase("true")]
    public void BoolSort_RejectsArguments(string argument)
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(Program.Replace("|: sort |", $"|: sort: {argument} |")));
    }

    [Test]
    public void BoolSort_RejectsClosedOutput()
    {
        var module = new AloeCompiler().Compile(Program.Replace("source.write(true);", "result.close(); source.write(true);"));
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("closed pipe"));
    }

    [TestCase("int")]
    [TestCase("byte")]
    [TestCase("string")]
    public void BoolSort_RejectsNonbooleanBytecodePayloads(string kind)
    {
        var module = new AloeCompiler().Compile(Program);
        var replacement = kind switch
        {
            "int" => AloeValue.FromInt(1),
            "byte" => AloeValue.FromByte(1),
            _ => AloeValue.FromString("true")
        };
        Assert.That(module.Constants.Any(value => value.IsBool && value.AsBool), Is.True);
        var invalid = new Module(module.Constants.Select(value => value.IsBool && value.AsBool
            ? replacement : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        var error = Assert.Throws<VmException>(() => new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(invalid))).RunFromEntryPoint());
        Assert.That(error!.Message, Does.Contain("bool"));
    }
}
