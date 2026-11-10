using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aloe.CommonLib;

public static partial class AloePackageDatabase
{
    /// <summary>Pins the exact local closure and the bytes observed by the resolver.</summary>
    public static AloePackageLockDocument CreateLock(string indexPath, string name, string version)
        => LockPlan(Resolve(indexPath, name, version));

    /// <summary>Selects the highest root once, then pins its exact local closure.</summary>
    public static AloePackageLockDocument CreateRangeLock(string indexPath, string name, string versionRange)
        => LockPlan(ResolveRange(indexPath, name, versionRange).Plan);

    private static AloePackageLockDocument LockPlan(AloePackageResolutionPlan plan)
        => new(plan.Root, plan.Packages.Select(p => new AloeLockedPackage(p.Name,p.Version,
            p.AbcSha256,p.MetaSha256,p.Dependencies.ToArray())).ToArray());

    /// <summary>Creates a new UTF-8 lock atomically; an existing destination is never replaced.</summary>
    public static void WriteLock(string lockPath, AloePackageLockDocument document)
    {
        ValidateLock(document);
        var fullPath=Path.GetFullPath(lockPath);
        if(File.Exists(fullPath)) throw new IOException("The package lock already exists.");
        var directory=Path.GetDirectoryName(fullPath)!;
        var temporary=Path.Combine(directory,"."+Path.GetFileName(fullPath)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(document,JsonOptions);
                stream.Write(bytes);stream.WriteByte((byte)'\n');stream.Flush(flushToDisk:true);
            }
            File.Move(temporary,fullPath,overwrite:false);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Reads a strict, path-free local lock and validates its entire graph.</summary>
    public static AloePackageLockDocument ReadLock(string lockPath)
    {
        using var stream=File.OpenRead(lockPath);
        using var json=JsonDocument.Parse(stream);
        var root=json.RootElement;
        LockProperties(root,"kind","formatVersion","root","packages");
        if(LockText(root,"kind")!="aloe-package-lock" ||
            root.GetProperty("formatVersion").ValueKind!=JsonValueKind.Number ||
            !root.GetProperty("formatVersion").TryGetInt32(out var format) || format!=1)
            throw new InvalidDataException("Unsupported Aloe package lock kind or formatVersion.");
        var identity=LockIdentity(root.GetProperty("root"));
        var nodes=root.GetProperty("packages");
        if(nodes.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("Lock packages must be an array.");
        var packages=new List<AloeLockedPackage>();
        foreach(var node in nodes.EnumerateArray())
        {
            LockProperties(node,"name","version","abcSha256","metaSha256","dependencies");
            var dependencies=node.GetProperty("dependencies");
            if(dependencies.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("Lock dependencies must be an array.");
            packages.Add(new AloeLockedPackage(LockText(node,"name"),LockText(node,"version"),
                LockText(node,"abcSha256"),LockText(node,"metaSha256"),
                dependencies.EnumerateArray().Select(LockIdentity).ToArray()));
        }
        var result=new AloePackageLockDocument(identity,packages.ToArray());
        ValidateLock(result);return result;
    }

    /// <summary>Replays the locked exact root and compares all selected content without mutation.</summary>
    public static AloePackageResolutionPlan VerifyLock(string indexPath, string lockPath)
    {
        var expected=ReadLock(lockPath);
        var plan=Resolve(indexPath,expected.Root.Name,expected.Root.Version);
        var actual=LockPlan(plan);
        if(expected.Packages.Count!=actual.Packages.Count)
            throw new InvalidDataException("The locked package graph changed.");
        for(var i=0;i<expected.Packages.Count;i++)
        {
            var a=expected.Packages[i];var b=actual.Packages[i];
            if(a.Name!=b.Name || a.Version!=b.Version || a.AbcSha256!=b.AbcSha256 ||
                a.MetaSha256!=b.MetaSha256 || !a.Dependencies.SequenceEqual(b.Dependencies))
                throw new InvalidDataException($"Locked package content or dependencies changed: '{a.Name}@{a.Version}'.");
        }
        return plan;
    }

    private static void LockProperties(JsonElement value, params string[] expected)
    {
        if(value.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("A package lock object was expected.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in value.EnumerateObject())
            if(!names.Add(property.Name) || !expected.Contains(property.Name,StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown or duplicate package lock property '{property.Name}'.");
        if(names.Count!=expected.Length) throw new InvalidDataException("A required package lock property is missing.");
    }

    private static string LockText(JsonElement value,string name)
    {
        var item=value.GetProperty(name);
        if(item.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            throw new InvalidDataException($"Package lock property '{name}' must be a nonempty string.");
        return item.GetString()!;
    }

    private static AloePackageIdentity LockIdentity(JsonElement value)
    {
        LockProperties(value,"name","version");
        return new AloePackageIdentity(LockText(value,"name"),LockText(value,"version"));
    }

    private static void ValidateLockIdentity(AloePackageIdentity? identity)
    {
        if(identity==null || string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.Version))
            throw new InvalidDataException("A nonempty exact package identity is required in a lock.");
        RequireExactPackageVersion(identity.Version);
    }

    private static void ValidateLock(AloePackageLockDocument? document)
    {
        if(document==null || document.Packages==null) throw new InvalidDataException("A package lock document is required.");
        ValidateLockIdentity(document.Root);
        var nodes=new Dictionary<string,(AloeLockedPackage Package,int Index)>(StringComparer.Ordinal);
        for(var i=0;i<document.Packages.Count;i++)
        {
            var node=document.Packages[i];
            if(node==null || node.Dependencies==null) throw new InvalidDataException("A package lock node is required.");
            ValidateLockIdentity(new AloePackageIdentity(node.Name,node.Version));
            if(node.AbcSha256==null || node.MetaSha256==null ||
                !Regex.IsMatch(node.AbcSha256,@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking) ||
                !Regex.IsMatch(node.MetaSha256,@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking))
                throw new InvalidDataException("Lock hashes must be lowercase SHA256 hexadecimal strings.");
            if(!nodes.TryAdd(node.Name,(node,i))) throw new InvalidDataException("A package name occurs more than once in the lock.");
            foreach(var dependency in node.Dependencies) ValidateLockIdentity(dependency);
            var ordered=node.Dependencies.Distinct().OrderBy(d=>d.Name,StringComparer.Ordinal)
                .ThenBy(d=>d.Version,StringComparer.Ordinal).ToArray();
            if(!node.Dependencies.SequenceEqual(ordered)) throw new InvalidDataException("Lock dependencies must be unique and canonically ordered.");
        }
        if(!nodes.TryGetValue(document.Root.Name,out var root) || root.Package.Version!=document.Root.Version)
            throw new InvalidDataException("The locked root is absent from its package graph.");
        foreach(var node in nodes.Values)
            foreach(var dependency in node.Package.Dependencies)
                if(!nodes.TryGetValue(dependency.Name,out var target) || target.Package.Version!=dependency.Version || target.Index>=node.Index)
                    throw new InvalidDataException("Lock dependencies must exist before their dependents; cycles are forbidden.");
        var visited=new HashSet<string>(StringComparer.Ordinal);var order=new List<string>();
        var work=new Stack<(string Name,bool Complete)>();work.Push((document.Root.Name,false));
        while(work.TryPop(out var step))
        {
            if(step.Complete){order.Add(step.Name);continue;}
            if(!visited.Add(step.Name)) continue;
            work.Push((step.Name,true));
            var dependencies=nodes[step.Name].Package.Dependencies;
            for(var i=dependencies.Count-1;i>=0;i--) work.Push((dependencies[i].Name,false));
        }
        if(!order.SequenceEqual(document.Packages.Select(p=>p.Name)))
            throw new InvalidDataException("The lock graph must contain exactly the root closure in canonical dependency order.");
    }
}

/// <summary>A portable, content-pinned local dependency closure with no filesystem paths.</summary>
public sealed record AloePackageLockDocument(
    [property: JsonPropertyName("root")] AloePackageIdentity Root,
    [property: JsonPropertyName("packages")] IReadOnlyList<AloeLockedPackage> Packages)
{
    [JsonPropertyName("kind"),JsonPropertyOrder(-2)] public string Kind=>"aloe-package-lock";
    [JsonPropertyName("formatVersion"),JsonPropertyOrder(-1)] public int FormatVersion=>1;
}

/// <summary>A locked exact identity, content hashes and ordered dependency identities.</summary>
public sealed record AloeLockedPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("abcSha256")] string AbcSha256,
    [property: JsonPropertyName("metaSha256")] string MetaSha256,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<AloePackageIdentity> Dependencies);
