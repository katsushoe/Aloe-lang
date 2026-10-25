using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class JsonFilterTests
{
    [Test]
    public void JsonFilterDecodesStrictStructsInOrderThroughAloeBcAndBoundedPipes()
    {
        var module = new AloeCompiler().Compile("""
struct User { field id: int; field active: bool; field name: string; }
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let users: pipe<User> = pipe<User>.create(1);
    source |: json: User | users;
    source.write("{\"id\":2147483647,\"active\":true,\"name\":\"\\u732B\"}");
    source.write("{\"name\":\"B\",\"active\":false,\"id\":-2147483648}");
    source.close();
    var count = 0;
    foreach (user in users) {
        if (count == 0 and (user.id != 2147483647 or not user.active or user.name != "猫")) { return -1; }
        if (count == 1 and (user.id != -2147483648 or user.active or user.name != "B")) { return -2; }
        count = count + 1;
    }
    return count;
}
""");

        foreach (var candidate in new[] { module, AloeBcCodec.Read(AloeBcCodec.Write(module)) })
        {
            var vm = new AloeVm(candidate);
            vm.RunFromEntryPoint();
            Assert.That(vm.Pop().AsInt, Is.EqualTo(2));
        }
    }

    [Test]
    public void JsonFilterClosesEmptyOutput()
    {
        var vm = Run("struct Item { field value: string; }", []);
        Assert.That(vm.Pop().AsInt, Is.Zero);
    }

    [Test]
    public void JsonFilterDecodesEmptyObjectIntoZeroFieldStruct()
    {
        var module = new AloeCompiler().Compile("""
struct Empty { }
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<Empty> = pipe<Empty>.create(1);
    source |: json: Empty | result;
    source.write("{}");
    source.close();
    var count = 0;
    foreach (value in result) { count = count + 1; }
    return count;
}
""");
        var vm = new AloeVm(module);
        vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt, Is.EqualTo(1));
    }

    [TestCase("[]", "must be a JSON object")]
    [TestCase("{\"id\":", "is malformed")]
    [TestCase("{\"id\":1,\"active\":true}", "member 'name' is missing")]
    [TestCase("{\"id\":1,\"active\":true,\"name\":\"A\",\"other\":0}", "member 'other' is unknown")]
    [TestCase("{\"id\":1,\"id\":2,\"active\":true,\"name\":\"A\"}", "member 'id' is duplicated")]
    [TestCase("{\"id\":1,\"i\\u0064\":2,\"active\":true,\"name\":\"A\"}", "member 'id' is duplicated")]
    [TestCase("{\"id\":1.0,\"active\":true,\"name\":\"A\"}", "member 'id' must be a signed int32 JSON integer")]
    [TestCase("{\"id\":1e0,\"active\":true,\"name\":\"A\"}", "member 'id' must be a signed int32 JSON integer")]
    [TestCase("{\"id\":2147483648,\"active\":true,\"name\":\"A\"}", "member 'id' must be a signed int32 JSON integer")]
    [TestCase("{\"id\":1,\"active\":\"true\",\"name\":\"A\"}", "member 'active' must be a JSON boolean")]
    [TestCase("{\"id\":1,\"active\":true,\"name\":null}", "member 'name' must be a JSON string")]
    [TestCase("{\"ID\":1,\"active\":true,\"name\":\"A\"}", "member 'ID' is unknown")]
    public void JsonFilterRejectsMalformedAndMismatchedRecords(string json, string expected)
    {
        var exception = Assert.Throws<VmException>(() => Run(
            "struct User { field id: int; field active: bool; field name: string; }", [json]));
        Assert.That(exception!.Message, Does.Contain("input item 1"));
        Assert.That(exception.Message, Does.Contain(expected));
    }

    [Test]
    public void JsonFilterErrorsIdentifyTheInputIndexAfterEarlierValidRecords()
    {
        var exception = Assert.Throws<VmException>(() => Run(
            "struct User { field id: int; field active: bool; field name: string; }",
            ["{\"id\":1,\"active\":true,\"name\":\"A\"}", "{broken"]));
        Assert.That(exception!.Message, Does.Contain("input item 2"));
    }

    [Test]
    public void JsonFilterRejectsUnsupportedTypesAndMissingTypeArgument()
    {
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("""
struct Bad { field value: decimal; }
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<Bad> = pipe<Bad>.create();
    source |: json: Bad | result;
    return 0;
}
"""));
        Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile("""
struct Item { field value: int; }
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<Item> = pipe<Item>.create();
    source |: json | result;
    return 0;
}
"""));
    }

    [TestCase("{broken", "schema is invalid")]
    [TestCase("{\"typeName\":\"Item\",\"typeName\":\"Other\",\"fields\":[{\"name\":\"value\",\"type\":\"string\"}]}", "schema is invalid")]
    [TestCase("{\"typeName\":\"Item\",\"t\\u0079peName\":\"Other\",\"fields\":[{\"name\":\"value\",\"type\":\"string\"}]}", "schema is invalid")]
    [TestCase("{\"typeName\":\"Item\",\"fields\":[{\"name\":\"value\",\"name\":\"value\",\"type\":\"string\"}]}", "schema is invalid")]
    [TestCase("{\"typeName\":\"Item\",\"fields\":[{\"name\":\"value\",\"type\":\"string\",\"extra\":0}]}", "schema is invalid")]
    [TestCase("{\"typeName\":\"Item\",\"fields\":[{\"name\":\"value\",\"type\":\"string\"}],\"extra\":0}", "schema is invalid")]
    [TestCase("{\"typeName\":\"Other\",\"fields\":[{\"name\":\"value\",\"type\":\"string\"}]}", "matching struct output")]
    public void JsonFilterRejectsCorruptedAloeBcSchemas(string schema, string expected)
    {
        var module = new AloeCompiler().Compile("""
struct Item { field value: string; }
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create();
    let result: pipe<Item> = pipe<Item>.create();
    source |: json: Item | result;
    return 0;
}
""");
        var constants = module.Constants.Select(value => value.IsString && value.AsString.StartsWith("{\"typeName\":\"Item\"", StringComparison.Ordinal)
            ? AloeValue.FromString(schema)
            : value).ToArray();
        var corrupted = new Module(constants, module.Code, module.Functions, module.EntryPointIndex);
        var exception = Assert.Throws<VmException>(() => new AloeVm(corrupted).RunFromEntryPoint());
        Assert.That(exception!.Message, Does.Contain(expected));
    }

    private static AloeVm Run(string declaration, IReadOnlyList<string> inputs)
    {
        static string Literal(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var writes = string.Join("\n", inputs.Select(value => $"    source.write({Literal(value)});"));
        var typeName = declaration.Contains("struct User", StringComparison.Ordinal) ? "User" : "Item";
        var source = $$"""
{{declaration}}
function main(args: string[]): int {
    let source: pipe<string> = pipe<string>.create(1);
    let result: pipe<{{typeName}}> = pipe<{{typeName}}>.create(1);
    source |: json: {{typeName}} | result;
{{writes}}
    source.close();
    var count = 0;
    foreach (value in result) { count = count + 1; }
    return count;
}
""";
        var vm = new AloeVm(new AloeCompiler().Compile(source));
        vm.RunFromEntryPoint();
        return vm;
    }
}
