using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class InterfaceCollectionBindingReturnTests
{
    public static IEnumerable<TestCaseData> Shapes()
    {
        yield return new TestCaseData("set", "int", "7", "int{}", "{7}", "{}", "item in items");
        yield return new TestCaseData("tuple", "int", "7", "int()", "(7,7)", "()", "item in items");
        yield return new TestCaseData("map-key", "int", "7", "map<int/int>", "[7:7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "int", "7", "map<int/int>", "[7:7]", "[]", "key,item in items");
        yield return new TestCaseData("set", "bool", "true", "bool{}", "{true}", "{}", "item in items");
        yield return new TestCaseData("tuple", "bool", "true", "bool()", "(true,true)", "()", "item in items");
        yield return new TestCaseData("map-key", "bool", "true", "map<bool/int>", "[true:7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "bool", "true", "map<int/bool>", "[7:true]", "[]", "key,item in items");
        yield return new TestCaseData("set", "byte", "255", "byte{}", "{255}", "{}", "item in items");
        yield return new TestCaseData("tuple", "byte", "255", "byte()", "(255,255)", "()", "item in items");
        yield return new TestCaseData("map-key", "byte", "255", "map<byte/int>", "[255:7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "byte", "255", "map<int/byte>", "[7:255]", "[]", "key,item in items");
        yield return new TestCaseData("set", "char", "'字'", "char{}", "{'字'}", "{}", "item in items");
        yield return new TestCaseData("tuple", "char", "'字'", "char()", "('字','字')", "()", "item in items");
        yield return new TestCaseData("map-key", "char", "'字'", "map<char/int>", "['字':7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "char", "'字'", "map<int/char>", "[7:'字']", "[]", "key,item in items");
        yield return new TestCaseData("set", "float", "1.5", "float{}", "{1.5}", "{}", "item in items");
        yield return new TestCaseData("tuple", "float", "1.5", "float()", "(1.5,1.5)", "()", "item in items");
        yield return new TestCaseData("map-key", "float", "1.5", "map<float/int>", "[1.5:7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "float", "1.5", "map<int/float>", "[7:1.5]", "[]", "key,item in items");
        yield return new TestCaseData("set", "decimal", "1.25:d", "decimal{}", "{1.25:d}", "{}", "item in items");
        yield return new TestCaseData("tuple", "decimal", "1.25:d", "decimal()", "(1.25:d,1.25:d)", "()", "item in items");
        yield return new TestCaseData("map-key", "decimal", "1.25:d", "map<decimal/int>", "[1.25:d:7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "decimal", "1.25:d", "map<int/decimal>", "[7:1.25:d]", "[]", "key,item in items");
        yield return new TestCaseData("set", "string", "\"value\"", "string{}", "{\"value\"}", "{}", "item in items");
        yield return new TestCaseData("tuple", "string", "\"value\"", "string()", "(\"value\",\"value\")", "()", "item in items");
        yield return new TestCaseData("map-key", "string", "\"value\"", "map<string/int>", "[\"value\":7]", "[]", "item in items");
        yield return new TestCaseData("map-value", "string", "\"value\"", "map<int/string>", "[7:\"value\"]", "[]", "key,item in items");
    }

    public static IEnumerable<TestCaseData> Inputs()
    {
        foreach (var row in Shapes())
            foreach (var empty in new[] { false, true })
                yield return new TestCaseData(row.Arguments.Append(empty).ToArray());
    }

    public static IEnumerable<TestCaseData> SingleInputs()
        => Inputs().Where(row => (string)row.Arguments[6]! == "item in items");

    [TestCaseSource(nameof(Inputs))]
    public void DirectBinding(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,header,"return item;"),empty);

    [TestCaseSource(nameof(Inputs))]
    public void AliasBinding(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,header,$"let alias: {type}=item; return alias;"),empty);

    [TestCaseSource(nameof(Inputs))]
    public void ParameterBinding(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,header,"return item;",parameter:true),empty);

    [TestCaseSource(nameof(SingleInputs))]
    public void BindingCanShadowTheSource(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,"items in items","return items;"),empty);

    [TestCaseSource(nameof(Shapes))]
    public void ObjectShadow(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"let item: object=new Plain(); return item;"));

    [TestCaseSource(nameof(Shapes))]
    public void UnknownCall(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"return unknown(item);"));

    [TestCaseSource(nameof(Shapes))]
    public void IndexedFailure(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"return missing[0];"));

    [TestCaseSource(nameof(Shapes))]
    public void BreakWrite(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"return item;",beforeTry:"if(flag) { value=new Plain(); break; }",harmless:true));

    [TestCaseSource(nameof(Shapes))]
    public void ContinueWrite(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"return item;",beforeTry:"if(flag) { value=new Plain(); continue; }",harmless:true));

    [TestCaseSource(nameof(Shapes))]
    public void OuterHeaderRemainsConservative(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected(Source(type,literal,collection,full,header,"return item;",harmless:true)
            .Replace("  foreach(","  try { foreach(").Replace("  value.emit();"," } catch { value=new Plain(); } value.emit();"));


    public static IEnumerable<TestCaseData> PairedInputs()
        => Inputs().Where(row => (string)row.Arguments[6]! == "key,item in items");

    [TestCaseSource(nameof(PairedInputs))]
    public void ValueBindingCanShadowTheSource(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,"key,items in items","return items;"),empty);

    [TestCaseSource(nameof(Shapes))]
    public void ParenthesizedSource(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertReturns(Source(type,literal,collection,full,header.Replace("in items","in (items)"),"return item;"),false);

    [TestCaseSource(nameof(Shapes))]
    public void CalledSourceRemainsConservative(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected($"function identity(items: {collection}): {collection} {{ return items; }}\n" +
            Source(type,literal,collection,full,header.Replace("in items","in identity(items)"),"return item;"));

    [TestCase("Tone","Tone.One","Tone{}","{Tone.One}","item in items")]
    [TestCase("Tone","Tone.One","Tone()","(Tone.One,Tone.One)","item in items")]
    [TestCase("Tone","Tone.One","map<Tone/int>","[Tone.One:7]","item in items")]
    [TestCase("Tone","Tone.One","map<int/Tone>","[7:Tone.One]","key,item in items")]
    [TestCase("Plain","new Plain()","Plain{}","{new Plain()}","item in items")]
    [TestCase("Plain","new Plain()","Plain()","(new Plain(),new Plain())","item in items")]
    [TestCase("Plain","new Plain()","map<Plain/int>","[new Plain():7]","item in items")]
    [TestCase("Plain","new Plain()","map<int/Plain>","[7:new Plain()]","key,item in items")]
    [TestCase("object","new Plain()","object()","(new Plain() as object,new Plain() as object)","item in items")]
    [TestCase("object","new Plain()","map<int/object>","[7:new Plain() as object]","key,item in items")]
    public void NominalAndObjectBindingsRemainConservative(string type,string literal,string collection,string full,string header)
        => AssertRejected("enum Tone { One }\n"+Source(type,literal,collection,full,header,"return item;"));

    [TestCase(false)]
    [TestCase(true)]
    public void PrimitiveKeyDoesNotRequirePrimitiveValue(bool empty)
        => AssertReturns(Source("int","7","map<int/Plain>",empty?"[]":"[7:new Plain()]",
            "item in items","return item;"),empty);

    [TestCase(false)]
    [TestCase(true)]
    public void PrimitiveValueDoesNotRequirePrimitiveKey(bool empty)
        => AssertReturns(Source("int","7","map<Plain/int>",empty?"[]":"[new Plain():7]",
            "key,item in items","return item;"),empty);

    [TestCaseSource(nameof(Inputs))]
    public void NestedParenthesizedParameter(string name, string type, string literal, string collection, string full, string none, string header, bool empty)
        => AssertReturns(Source(type,literal,collection,empty?none:full,header.Replace("in items","in (((items)))"),"return item;",parameter:true),empty);

    [TestCaseSource(nameof(Shapes))]
    public void ParenthesizedCalledSourceRemainsConservative(string name, string type, string literal, string collection, string full, string none, string header)
        => AssertRejected($"function identity(items: {collection}): {collection} {{ return items; }}\n" +
            Source(type,literal,collection,full,header.Replace("in items","in ((identity(items)))"),"return item;"));

    [TestCase(false,false)]
    [TestCase(false,true)]
    [TestCase(true,false)]
    [TestCase(true,true)]
    public void ParenthesizedArray(bool empty,bool parameter)
        => AssertReturns(Source("int","7","int[]",empty?"[]":"[7]","item in (((items)))","return item;",parameter:parameter),empty);

    [TestCase(false)]
    [TestCase(true)]
    public void ParenthesizedMapSourceShadow(bool empty)
        => AssertReturns(Source("string","\"value\"","map<int/string>",empty?"[]":"[7:\"value\"]",
            "key,items in (((items)))","return items;",parameter:true),empty);

    private static string Source(string type,string literal,string collection,string data,string header,string body,
        bool parameter=false,string beforeTry="",bool harmless=false) => $$"""
interface I { method emit(): void; }
class C implements I { public async method emit(): void { print("after-empty"); } }
class Plain { }
function unknown(input: {{type}}): {{type}} { return input; }
function choose(flag: bool{{(parameter?", items: "+collection:"")}}): {{type}} {
 {{(parameter?"":"let items: "+collection+" = "+data+";")}}
 let missing: {{type}}[]=[]; let value: object=new C();
 if(value is I) {
  foreach({{header}}) { {{beforeTry}} try { {{body}} } catch { {{(harmless?"":"value=new Plain();")}} } }
  value.emit();
 }
 return {{literal}};
}
function main(args: string[]): int {
 {{(parameter?"let items: "+collection+" = "+data+";":"")}}
 let result: {{type}}=choose(true{{(parameter?", items":"")}}); tick(); if(result != {{literal}}) { return 9; } print("returned"); return 0;
}
""";

    private static void AssertRejected(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Async member call requires a statically known class instance"));
    }
    private static void AssertReturns(string source,bool empty)
    {
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var module=new AloeCompiler().Compile(source);var vm=new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module)),settings);
        var output=new List<string>();vm.OutputWriter=output.Add;vm.RunFromEntryPoint();
        Assert.That(output,Is.EqualTo(empty?new[]{"after-empty","returned"}:new[]{"returned"}));
        Assert.That(vm.ValueStack.Count,Is.EqualTo(1));Assert.That(vm.Pop().AsInt,Is.Zero);
    }
}
