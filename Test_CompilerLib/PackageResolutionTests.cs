using Aloe.CommonLib;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aloe.CompilerLib.Tests;

public sealed class PackageResolutionTests
{
    [Test, Combinatorial]
    public void UnsupportedVersions_AreRejectedForRootsAndDependencies(
        [Values("1.0", "01.0.0", "1.00.0", "1.0.0-beta", "1.0.0+build", ">=1.0.0", "1.*", " 1.0.0", "1.0.0\n", "١.0.0", "1.0.-1")] string version,
        [Values(false, true)] bool dependency)
    {
        using var store = new Store();
        store.Add("root", "1.0.0", dependency ? [new("dep", version)] : []);
        var before = store.Snapshot();
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", dependency ? "1.0.0" : version));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [TestCase("0.0.0")]
    [TestCase("999999999999999999999.0.0")]
    public void ExactVersions_UseStringIdentityWithoutNumericOverflow(string version)
    {
        using var store = new Store();
        store.Add("root", version);
        Assert.That(AloePackageDatabase.Resolve(store.Index, "root", version).Root.Version, Is.EqualTo(version));
    }

    [Test]
    public void Diamond_ProducesDeterministicDependencyFirstReadOnlyPlan()
    {
        using var store = new Store();
        store.Add("shared", "1.0.0");
        store.Add("b", "1.0.0", [new("shared", "1.0.0")]);
        store.Add("a", "1.0.0", [new("shared", "1.0.0")]);
        store.Add("root", "1.0.0", [new("b", "1.0.0"), new("a", "1.0.0"), new("a", "1.0.0")]);
        var before = store.Snapshot();
        var plan = AloePackageDatabase.Resolve(store.Index, "root", "1.0.0");
        Assert.That(plan.Packages.Select(package => package.Name), Is.EqualTo(new[] { "shared", "a", "b", "root" }));
        Assert.That(plan.Packages[^1].Dependencies, Has.Count.EqualTo(2));
        Assert.That(JsonSerializer.Serialize(plan), Is.EqualTo(JsonSerializer.Serialize(AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"))));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Cycles_ReportDependencyPathWithoutMutation(bool indirect)
    {
        using var store = new Store();
        store.Add("root", "1.0.0", [new(indirect ? "child" : "root", "1.0.0")]);
        if (indirect) store.Add("child", "1.0.0", [new("root", "1.0.0")]);
        var before = store.Snapshot();
        var error = Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(error!.Message, Does.Contain("root@1.0.0 ->"));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [Test]
    public void DifferentVersionsOfOneName_Conflict()
    {
        using var store = new Store();
        store.Add("a", "1.0.0");
        store.Add("a", "2.0.0");
        store.Add("b", "1.0.0", [new("a", "2.0.0")]);
        store.Add("root", "1.0.0", [new("a", "1.0.0"), new("b", "1.0.0")]);
        var error = Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(error!.Message, Does.Contain("1.0.0").And.Contain("2.0.0").And.Contain("conflict"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingRootOrDependency_IsAnError(bool dependency)
    {
        using var store = new Store();
        store.Add("root", "1.0.0", dependency ? [new("missing", "1.0.0")] : []);
        Assert.Throws<KeyNotFoundException>(() => AloePackageDatabase.Resolve(store.Index, dependency ? "root" : "missing", "1.0.0"));
    }

    [Test]
    public void MetadataDependencies_AreReadAfreshInsteadOfIndexCopies()
    {
        using var store = new Store();
        store.Add("root", "1.0.0", [new("stale", "1.0.0")]);
        store.Add("fresh", "1.0.0");
        store.WriteMetadata("root", "1.0.0", [new("fresh", "1.0.0")]);
        var plan = AloePackageDatabase.Resolve(store.Index, "root", "1.0.0");
        Assert.That(plan.Packages.Select(package => package.Name), Is.EqualTo(new[] { "fresh", "root" }));
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{")]
    public void InvalidMetadata_IsRejected(string json)
    {
        using var store = new Store();
        store.Add("root", "1.0.0");
        File.WriteAllText(store.Meta("root", "1.0.0"), json);
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
    }

    [Test]
    public void MetadataIdentityMismatch_IsRejected()
    {
        using var store = new Store();
        store.Add("root", "1.0.0");
        File.WriteAllText(store.Meta("root", "1.0.0"), """{"formatVersion":1,"package":{"name":"other","version":"1.0.0"},"dependencies":[]}""");
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingFile_PreservesOriginalIoFailureAndIndex(bool metadata)
    {
        using var store = new Store();
        store.Add("root", "1.0.0");
        File.Delete(metadata ? store.Meta("root", "1.0.0") : store.Abc("root", "1.0.0"));
        var before = store.Snapshot();
        Assert.Throws<FileNotFoundException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [Test]
    public void Hashes_IdentifyCurrentFileBytes()
    {
        using var store = new Store();
        store.Add("root", "1.0.0");
        var first = AloePackageDatabase.Resolve(store.Index, "root", "1.0.0").Packages[0];
        File.WriteAllBytes(store.Abc("root", "1.0.0"), [9, 8, 7]);
        File.AppendAllText(store.Meta("root", "1.0.0"), "\n ");
        var second = AloePackageDatabase.Resolve(store.Index, "root", "1.0.0").Packages[0];
        Assert.That(second.AbcSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second.AbcPath))).ToLowerInvariant()));
        Assert.That(second.MetaSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second.MetaPath))).ToLowerInvariant()));
        Assert.That(second.AbcSha256, Is.Not.EqualTo(first.AbcSha256));
        Assert.That(second.MetaSha256, Is.Not.EqualTo(first.MetaSha256));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LockedFiles_PreserveIoFailureWithoutMutation(bool metadata)
    {
        using var store = new Store();
        store.Add("root", "1.0.0");
        var before = store.Snapshot();
        using (var locked = new FileStream(metadata ? store.Meta("root", "1.0.0") : store.Abc("root", "1.0.0"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [Test]
    public void MissingIndex_IsNotCreated()
    {
        using var store = new Store();
        Assert.Throws<FileNotFoundException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(File.Exists(store.Index), Is.False);
    }

    [TestCase("""{"formatVersion":1,"packages":[null]}""")]
    [TestCase("""{"formatVersion":1,"packages":[{"name":"r","version":"1.0.0","abcPath":"r.abc","metaPath":"r.meta.json","dependencies":[null]}]}""")]
    public void NullIndexEntries_AreFormatErrors(string json)
    {
        using var store = new Store();
        File.WriteAllText(store.Index, json);
        var before = store.Snapshot();
        Assert.Throws<InvalidDataException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
        Assert.That(store.Snapshot(), Is.EqualTo(before));
    }

    [Test]
    public void Names_AreCaseSensitive()
    {
        using var store = new Store();
        store.Add("Root", "1.0.0");
        Assert.Throws<KeyNotFoundException>(() => AloePackageDatabase.Resolve(store.Index, "root", "1.0.0"));
    }

    private sealed class Store : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "aloe-resolve-" + Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(root, "package-db.json");
        public Store() => Directory.CreateDirectory(root);
        public string Abc(string name, string version) => Path.Combine(root, name + "@" + version + ".abc");
        public string Meta(string name, string version) => Path.Combine(root, name + "@" + version + ".meta.json");
        public void Add(string name, string version, AloePackageIdentity[]? dependencies = null)
        {
            File.WriteAllBytes(Abc(name, version), [1, 2, 3]);
            WriteMetadata(name, version, dependencies ?? []);
            AloePackageDatabase.Add(Index, name, version, Abc(name, version), Meta(name, version));
        }
        public void WriteMetadata(string name, string version, AloePackageIdentity[] dependencies) =>
            File.WriteAllText(Meta(name, version), JsonSerializer.Serialize(new
            {
                formatVersion = 1, package = new { name, version },
                dependencies = dependencies.Select(dependency => new { name = dependency.Name, versionRange = dependency.Version })
            }));
        public string[] Snapshot() => Directory.GetFiles(root).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()).ToArray();
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
