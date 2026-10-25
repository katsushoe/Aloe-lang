using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Globalization;

namespace Aloe.CompilerLib.Tests;

public sealed class StructStringSortTests
{
    [TestCase("en-US")]
    [TestCase("tr-TR")]
    [TestCase("ja-JP")]
    public void OrdinalStringKeysPreserveStableRecordsThroughCapacityOneAndAloeBc(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var module = new AloeCompiler().Compile("""
struct Item { field key: string; field tag: int; field keep: bool; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create(1);
    let middle: pipe<Item> = pipe<Item>.create(1);
    let result: pipe<Item> = pipe<Item>.create(1);
    source |: sort: .key | middle;
    middle |: sort: .key | result;
    source.write(Item { key: "a", tag: 0, keep: true });
    source.write(Item { key: "A", tag: 1, keep: false });
    source.write(Item { key: "a", tag: 2, keep: true });
    source.write(Item { key: "", tag: 3, keep: false });
    source.write(Item { key: "𐀀", tag: 4, keep: true });
    source.write(Item { key: "", tag: 5, keep: false });
    source.write(Item { key: "é", tag: 6, keep: true });
    source.write(Item { key: "é", tag: 7, keep: false });
    source.write(Item { key: "ı", tag: 8, keep: true });
    source.write(Item { key: "I", tag: 9, keep: false });
    source.write(Item { key: "i", tag: 10, keep: true });
    source.write(Item { key: "a", tag: 0, keep: true });
    source.close();
    let keys: string[] = ["", "A", "I", "a", "a", "a", "é", "i", "é", "ı", "𐀀", ""];
    let tags: int[] = [3, 1, 9, 0, 2, 0, 7, 10, 6, 8, 4, 5];
    var index = 0;
    foreach (item in result) {
        if (index >= keys.Length or item.key != keys[index] or item.tag != tags[index]) { return -1; }
        if (item.keep != (item.tag % 2 == 0)) { return -2; }
        index = index + 1;
    }
    return index;
}
""");
            foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
            {
                var vm = CreateVm(candidate);
                vm.RunFromEntryPoint();
                Assert.That(vm.Pop().AsInt, Is.EqualTo(12));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public void EmptyStringKeySortClosesOutput()
    {
        var vm = CreateVm(CompileStringSort(""));
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }

    [TestCase("{broken")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"String\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":true}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":null}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":-1,\"fieldType\":\"string\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"string\",\"extra\":0}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"string\",\"fieldT\\u0079pe\":\"int\"}")]
    public void StringKeySortRejectsMalformedRawSchema(string schema)
    {
        var invalid = ReplaceSchema(CompileStringSort(""), schema);
        Assert.That(() => CreateVm(invalid).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("sort filter schema is invalid"));
    }

    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"int\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":1,\"fieldType\":\"string\"}")]
    [TestCase("{\"typeName\":\"Item\",\"fieldName\":\"key\",\"fieldIndex\":99,\"fieldType\":\"string\"}")]
    [TestCase("{\"typeName\":\"Other\",\"fieldName\":\"key\",\"fieldIndex\":0,\"fieldType\":\"string\"}")]
    public void StringKeySortRejectsMismatchedRuntimeValuesOrBindings(string schema)
    {
        var module = CompileStringSort("source.write(Item { key: \"payload\", tag: 7 });");
        Assert.Throws<VmException>(() => CreateVm(ReplaceSchema(module, schema)).RunFromEntryPoint());
    }

    [Test]
    public void StringKeySortRejectsClosedOutputAndCompilerKeyMismatch()
    {
        var module = CompileStringSort("result.close(); source.write(Item { key: \"value\", tag: 1 });");
        Assert.That(() => CreateVm(module).RunFromEntryPoint(), Throws.TypeOf<VmException>().With.Message.Contains("closed pipe"));
        Assert.Throws<AloeCompileException>(() => CompileStringSort("", ".Key"));
        Assert.Throws<AloeCompileException>(() => CompileStringSort("", ".key.Length"));
    }

    private static Module CompileStringSort(string writes, string key = ".key") => new AloeCompiler().Compile($$"""
struct Item { field key: string; field tag: int; }
function main(args: string[]): int {
    let source: pipe<Item> = pipe<Item>.create(1);
    let result: pipe<Item> = pipe<Item>.create(1);
    source |: sort: {{key}} | result;
    {{writes}}
    source.close();
    var count = 0;
    foreach (item in result) { count = count + 1; }
    return count;
}
""");

    private static Module ReplaceSchema(Module module, string schema) => AloeBcCodec.Read(AloeBcCodec.Write(new Module(
        module.Constants.Select(value => value.IsString && value.AsString.Contains("fieldName", StringComparison.Ordinal) ? AloeValue.FromString(schema) : value).ToArray(),
        module.Code, module.Functions, module.EntryPointIndex)));

    private static AloeVm CreateVm(Module module)
    {
        var settings = new AloeVmSettings();
        settings.Logging.ConsoleEnabled = false;
        settings.Logging.FileEnabled = false;
        return new AloeVm(module, settings);
    }
}
