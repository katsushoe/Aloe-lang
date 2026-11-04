using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class StructBoolKeySortTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void StableFalseBeforeTrueKeysPreserveRecordsThroughCapacityOne(bool serialized)
    {
        var module = Compile("""
    let low: bool = false;
    let middle: bool = true;
    let high: bool = true;
    source.write(Item { key: high, tag: 1 });
    source.write(Item { key: middle, tag: 2 });
    source.write(Item { key: low, tag: 3 });
    source.write(Item { key: high, tag: 4 });
    source.write(Item { key: low, tag: 5 });
    source.write(Item { key: high, tag: 1 });
""", """
    let tags: int[] = [3, 5, 1, 2, 4, 1];
    var count = 0;
    foreach (item in result) {
        if (count >= tags.Length or item.tag != tags[count]) { return -1; }
        if ((item.tag == 3 or item.tag == 5) and item.key != low) { return -2; }
        if (item.tag == 2 and item.key != middle) { return -3; }
        if ((item.tag == 1 or item.tag == 4) and item.key != high) { return -4; }
        count = count + 1;
    }
    return count;
""");
        if (serialized) module = AloeBcCodec.Read(AloeBcCodec.Write(module));
        var vm = CreateVm(module);
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(6));
    }

    [Test]
    public void EmptyBoolKeySortClosesOutput()
    {
        var vm = CreateVm(Compile("", "var count = 0; foreach (item in result) { count = count + 1; } return count;"));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }

    [Test]
    public void SingletonBoolKeyRetainsRecord()
    {
        var vm = CreateVm(Compile("let key: bool = true; source.write(Item { key: key, tag: 42 });", "var count = 0; foreach (item in result) { if (item.tag != 42) { return -1; } count = count + 1; } return count;"));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
    }

    [Test]
    public void BoolKeySortRejectsClosedOutput()
    {
        var vm = CreateVm(Compile("result.close(); let key: bool = true; source.write(Item { key: key, tag: 2 });", "foreach (item in result) { } return 0;"));
        Assert.That(() => vm.RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("closed pipe"));
    }

    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"int\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"string\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":1,\"fieldType\":\"bool\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"Bool\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"bool\",\"extra\":0}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"bool\",\"fieldType\":\"bool\"}")]
    public void RejectsMismatchedAndMalformedBoolSchemas(string schema)
    {
        var module = Compile("let key: bool = true; source.write(Item { key: key, tag: 2 });", "foreach (item in result) { } return 0;");
        var invalid = new Module(module.Constants.Select(value => value.IsString && value.AsString.Contains("fieldName", StringComparison.Ordinal) ? AloeValue.FromString(schema) : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        Assert.Throws<VmException>(() => CreateVm(AloeBcCodec.Read(AloeBcCodec.Write(invalid))).RunFromEntryPoint());
    }

    [TestCase(".Key")]
    [TestCase(".key.Length")]
    public void RejectsUnknownOrNestedKeys(string key)
    {
        Assert.Throws<AloeCompileException>(() => Compile("", "return 0;", key));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void EqualKeysRetainSourceOrder(bool key, bool serialized)
    {
        var literal = key ? "true" : "false";
        var module = Compile($"source.write(Item {{ key: {literal}, tag: 2 }}); source.write(Item {{ key: {literal}, tag: 1 }}); source.write(Item {{ key: {literal}, tag: 3 }});",
            "let tags: int[]=[2,1,3]; var count=0; foreach(item in result) { if(item.tag != tags[count]) { return -1; } count=count+1; } return count;");
        if (serialized) module = AloeBcCodec.Read(AloeBcCodec.Write(module));
        var vm = CreateVm(module); vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.EqualTo(3));
    }

    [TestCase("int")]
    [TestCase("byte")]
    [TestCase("string")]
    public void RuntimeKeyMustRemainBool(string kind)
    {
        var module = Compile("let key: bool=true; source.write(Item { key: key, tag: 2 });", "foreach(item in result) { } return 0;");
        Assert.That(module.Constants.Any(value => value.IsBool), Is.True);
        var replacement = kind switch { "int" => AloeValue.FromInt(1), "byte" => AloeValue.FromByte(1), _ => AloeValue.FromString("true") };
        var malformed = new Module(module.Constants.Select(value => value.IsBool ? replacement : value).ToArray(), module.Code, module.Functions, module.EntryPointIndex);
        Assert.Throws<VmException>(() => CreateVm(AloeBcCodec.Read(AloeBcCodec.Write(malformed))).RunFromEntryPoint());
    }

    private static Module Compile(string writes, string read, string key = ".key") => new AloeCompiler().Compile($$"""
struct Item { field key: bool; field tag: int; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create(1);
    let intermediate: pipe<Item> = pipe<Item>.create(1);
    let result: pipe<Item> = pipe<Item>.create(1);
    source |: sort: {{key}} | intermediate;
    intermediate |: sort: {{key}} | result;
    {{writes}}
    source.close();
    {{read}}
}
""");

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        return new AloeVm(module, settings);
    }
}
