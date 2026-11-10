using Aloe.CommonLib;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aloe.CompilerLib.Tests;

public sealed class PackageLockTests
{
    [Test]
    public void ExactLockIsPortableDeterministicAndReadOnly()
    {
        using var store=Graph();var before=store.Snapshot();
        var document=AloePackageDatabase.CreateLock(store.Index,"root","1.0.0");
        Assert.That(document.Packages.Select(p=>p.Name),Is.EqualTo(new[]{"shared","a","b","root"}));
        Assert.That(store.Snapshot(),Is.EqualTo(before));
        var text=JsonSerializer.Serialize(document);Assert.That(text,Does.Not.Contain(store.Root));
        Assert.That(text,Does.Not.Contain("abcPath"));Assert.That(text,Does.Not.Contain("metaPath"));
        AloePackageDatabase.WriteLock(store.Lock,document);var snapshot=store.Snapshot();
        var read=AloePackageDatabase.ReadLock(store.Lock);
        Assert.That(JsonSerializer.Serialize(read),Is.EqualTo(text));
        var verified=AloePackageDatabase.VerifyLock(store.Index,store.Lock);
        Assert.That(verified.Root,Is.EqualTo(document.Root));Assert.That(verified.Packages.Count,Is.EqualTo(4));
        Assert.That(store.Snapshot(),Is.EqualTo(snapshot));
        var other=Path.Combine(store.Root,"other.lock.json");AloePackageDatabase.WriteLock(other,document);
        Assert.That(File.ReadAllBytes(other),Is.EqualTo(File.ReadAllBytes(store.Lock)));
    }

    [Test]
    public void RangeLockKeepsExactRootAfterNewerAndUnrelatedEntries()
    {
        using var store=new Store();store.Add("root","1.0.0");store.Add("root","1.5.0");
        var document=AloePackageDatabase.CreateRangeLock(store.Index,"root",">=1.0.0 <2.0.0");
        AloePackageDatabase.WriteLock(store.Lock,document);
        store.Add("root","1.9.0");store.Add("other","1.0.0");
        Assert.That(AloePackageDatabase.VerifyLock(store.Index,store.Lock).Root.Version,Is.EqualTo("1.5.0"));
    }

    [Test]
    public void IdenticalContentMayRelocateWithItsRelativeIndex()
    {
        using var original=Graph();AloePackageDatabase.WriteLock(original.Lock,AloePackageDatabase.CreateLock(original.Index,"root","1.0.0"));
        using var moved=new Store();
        foreach(var path in Directory.GetFiles(original.Root)) File.Copy(path,Path.Combine(moved.Root,Path.GetFileName(path)));
        Assert.That(AloePackageDatabase.VerifyLock(moved.Index,moved.Lock).Root.Name,Is.EqualTo("root"));
    }

    [TestCase("abc")]
    [TestCase("meta")]
    [TestCase("dependency")]
    [TestCase("cycle")]
    [TestCase("missing-abc")]
    [TestCase("missing-meta")]
    [TestCase("missing-index")]
    public void ChangedOrMissingContentFailsWithoutFurtherMutation(string mode)
    {
        using var store=Graph();AloePackageDatabase.WriteLock(store.Lock,AloePackageDatabase.CreateLock(store.Index,"root","1.0.0"));
        var paths=store.Paths[("root","1.0.0")];
        switch(mode)
        {
            case "abc":File.AppendAllText(paths.Abc,"changed");break;
            case "meta":File.AppendAllText(paths.Meta," ");break;
            case "dependency":store.WriteMeta("root","1.0.0",[]);break;
            case "cycle":store.WriteMeta("root","1.0.0",[new("root","1.0.0")]);break;
            case "missing-abc":File.Delete(paths.Abc);break;
            case "missing-meta":File.Delete(paths.Meta);break;
            case "missing-index":File.Delete(store.Index);break;
        }
        var before=store.Snapshot();Assert.That(()=>AloePackageDatabase.VerifyLock(store.Index,store.Lock),Throws.Exception);
        Assert.That(store.Snapshot(),Is.EqualTo(before));
    }

    [TestCase("unknown-top")]
    [TestCase("unknown-node")]
    [TestCase("unknown-identity")]
    [TestCase("missing-root")]
    [TestCase("missing-packages")]
    [TestCase("wrong-kind")]
    [TestCase("wrong-version")]
    [TestCase("null-root")]
    [TestCase("null-packages")]
    [TestCase("null-node")]
    [TestCase("null-dependencies")]
    [TestCase("invalid-hash")]
    [TestCase("uppercase-hash")]
    [TestCase("short-hash")]
    [TestCase("empty-name")]
    [TestCase("range-version")]
    [TestCase("duplicate-name")]
    [TestCase("missing-root-node")]
    [TestCase("missing-dependency")]
    [TestCase("conflicting-version")]
    [TestCase("unordered-dependencies")]
    [TestCase("unordered-packages")]
    [TestCase("unreachable")]
    [TestCase("cycle")]
    [TestCase("duplicate-dependency")]
    [TestCase("wrong-property-type")]
    public void MalformedSchemaAndNoncanonicalGraphsAreRejected(string mode)
    {
        using var store=Graph();var json=JsonNode.Parse(JsonSerializer.Serialize(AloePackageDatabase.CreateLock(store.Index,"root","1.0.0")))!.AsObject();
        var packages=json["packages"]!.AsArray();var first=packages[0]!.AsObject();var root=packages[packages.Count-1]!.AsObject();
        var dependencies=root["dependencies"]!.AsArray();
        static JsonNode Copy(JsonNode node)=>JsonNode.Parse(node.ToJsonString())!;
        switch(mode)
        {
            case "unknown-top":json["path"]="ignored";break;
            case "unknown-node":first["abcPath"]="ignored";break;
            case "unknown-identity":json["root"]!["url"]="ignored";break;
            case "missing-root":json.Remove("root");break;
            case "missing-packages":json.Remove("packages");break;
            case "wrong-kind":json["kind"]="other";break;
            case "wrong-version":json["formatVersion"]=2;break;
            case "null-root":json["root"]=null;break;
            case "null-packages":json["packages"]=null;break;
            case "null-node":packages[0]=null;break;
            case "null-dependencies":first["dependencies"]=null;break;
            case "invalid-hash":first["abcSha256"]=new string('z',64);break;
            case "uppercase-hash":first["abcSha256"]=first["abcSha256"]!.GetValue<string>().ToUpperInvariant();break;
            case "short-hash":first["metaSha256"]="00";break;
            case "empty-name":first["name"]=" ";break;
            case "range-version":first["version"]="1.*";break;
            case "duplicate-name":packages.Add(Copy(first));break;
            case "missing-root-node":json["root"]!["version"]="9.0.0";break;
            case "missing-dependency":dependencies[0]!["name"]="absent";break;
            case "conflicting-version":dependencies[0]!["version"]="2.0.0";break;
            case "unordered-dependencies":var a=Copy(dependencies[0]!);var b=Copy(dependencies[1]!);dependencies[0]=b;dependencies[1]=a;break;
            case "unordered-packages":var left=Copy(packages[1]!);var right=Copy(packages[2]!);packages[1]=right;packages[2]=left;break;
            case "unreachable":var extra=Copy(first);extra["name"]="extra";packages.Add(extra);break;
            case "cycle":first["dependencies"]=new JsonArray(JsonNode.Parse("{\"name\":\"root\",\"version\":\"1.0.0\"}"));break;
            case "duplicate-dependency":dependencies.Add(Copy(dependencies[0]!));break;
            case "wrong-property-type":first["version"]=1;break;
        }
        File.WriteAllText(store.Lock,json.ToJsonString());var before=store.Snapshot();
        Assert.Throws<InvalidDataException>(()=>AloePackageDatabase.ReadLock(store.Lock));Assert.That(store.Snapshot(),Is.EqualTo(before));
    }

    [TestCase("kind")]
    [TestCase("name")]
    [TestCase("version")]
    [TestCase("abcSha256")]
    [TestCase("metaSha256")]
    public void DuplicateStringPropertiesAreRejected(string name)
    {
        using var store=Graph();var text=JsonSerializer.Serialize(AloePackageDatabase.CreateLock(store.Index,"root","1.0.0"));
        var pattern="\\\""+Regex.Escape(name)+"\\\":\\\"[^\\\"]*\\\"";
        var regex=new Regex(pattern);Assert.That(regex.IsMatch(text),Is.True);
        text=regex.Replace(text,m=>m.Value+","+m.Value,1);File.WriteAllText(store.Lock,text);
        Assert.Throws<InvalidDataException>(()=>AloePackageDatabase.ReadLock(store.Lock));
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{")]
    public void InvalidDocumentsFail(string text)
    {
        using var store=new Store();File.WriteAllText(store.Lock,text);
        Assert.That(()=>AloePackageDatabase.ReadLock(store.Lock),Throws.Exception);
    }

    [Test]
    public void ExistingOutputAndInvalidInMemoryInputNeverOverwrite()
    {
        using var store=Graph();var document=AloePackageDatabase.CreateLock(store.Index,"root","1.0.0");
        File.WriteAllText(store.Lock,"preserve");var before=store.Snapshot();
        Assert.Throws<IOException>(()=>AloePackageDatabase.WriteLock(store.Lock,document));Assert.That(store.Snapshot(),Is.EqualTo(before));
        var invalid=new AloePackageLockDocument(document.Root,[document.Packages[0] with { AbcSha256="bad" }]);
        var missing=Path.Combine(store.Root,"must-not-exist.json");
        Assert.Throws<InvalidDataException>(()=>AloePackageDatabase.WriteLock(missing,invalid));Assert.That(File.Exists(missing),Is.False);
        Assert.That(Directory.GetFiles(store.Root,"*.tmp"),Is.Empty);
    }

    [Test]
    public async Task ConcurrentCreatorsPublishOneCompleteLock()
    {
        using var store=Graph();var document=AloePackageDatabase.CreateLock(store.Index,"root","1.0.0");
        var attempts=Enumerable.Range(0,2).Select(_=>Task.Run(()=>
        {try{AloePackageDatabase.WriteLock(store.Lock,document);return true;}catch(IOException){return false;}}));
        var results=await Task.WhenAll(attempts);Assert.That(results.Count(x=>x),Is.EqualTo(1));
        Assert.That(AloePackageDatabase.ReadLock(store.Lock).Packages.Count,Is.EqualTo(4));
        Assert.That(Directory.GetFiles(store.Root,"*.tmp"),Is.Empty);
    }

    private static Store Graph()
    {
        var store=new Store();store.Add("shared","1.0.0");store.Add("b","1.0.0",[new("shared","1.0.0")]);
        store.Add("a","1.0.0",[new("shared","1.0.0")]);store.Add("root","1.0.0",[new("b","1.0.0"),new("a","1.0.0")]);return store;
    }
    private sealed class Store:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"AloeLockTests-"+Guid.NewGuid().ToString("N"));
        public string Index=>Path.Combine(Root,"package-db.json");public string Lock=>Path.Combine(Root,"aloe.lock.json");
        public Dictionary<(string,string),(string Abc,string Meta)> Paths {get;}=new();
        public Store()=>Directory.CreateDirectory(Root);
        public void Add(string name,string version,AloePackageIdentity[]? dependencies=null)
        {
            var id=Guid.NewGuid().ToString("N");var abc=Path.Combine(Root,id+".abc");var meta=Path.Combine(Root,id+".meta.json");
            Paths.Add((name,version),(abc,meta));File.WriteAllBytes(abc,[1,2,3]);WriteMeta(name,version,dependencies??[]);
            AloePackageDatabase.Add(Index,name,version,abc,meta);
        }
        public void WriteMeta(string name,string version,AloePackageIdentity[] dependencies)
            =>File.WriteAllText(Paths[(name,version)].Meta,JsonSerializer.Serialize(new{formatVersion=1,package=new{name,version},
                dependencies=dependencies.Select(d=>new{name=d.Name,versionRange=d.Version})}));
        public string[] Snapshot()=>Directory.GetFiles(Root).Order(StringComparer.Ordinal)
            .Select(p=>Path.GetFileName(p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        public void Dispose()=>Directory.Delete(Root,recursive:true);
    }
}
