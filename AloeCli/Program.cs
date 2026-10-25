using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var settings = AloeCompilerSettings.Load();
        var logger = new AloeLogger(settings.Logging, settings.ConfigDirectory);

        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "build" => Build(args, logger),
                "run" => Run(args, logger),
                "package-db" => PackageDb(args),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Build(string[] args, AloeLogger logger)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: aloec build <source.aloe> [-o output.aloebc]");
            return 1;
        }

        var sourcePath = RequireSourceFile(args[1]);
        var outputPath = GetOutputPath(args, sourcePath);
        logger.Info($"Compile: {sourcePath} -> {outputPath}");

        var source = File.ReadAllText(sourcePath);
        var bytes = new AloeCompiler().CompileToAloeBc(source);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, bytes);

        Console.WriteLine(outputPath);
        logger.Info($"Compile completed: {bytes.Length} bytes.");
        return 0;
    }

    private static int Run(string[] args, AloeLogger logger)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: aloec run <source.aloe>");
            return 1;
        }

        var sourcePath = RequireSourceFile(args[1]);
        logger.Info($"Compile and run: {sourcePath}");
        var source = File.ReadAllText(sourcePath);
        var bytes = new AloeCompiler().CompileToAloeBc(source);
        var vm = AloeVmLoader.FromAloeBc(bytes);
        vm.RunFromEntryPoint();
        return 0;
    }

    private static int PackageDb(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Usage: aloec package-db <list|add|remove|resolve> [options]");

        var operation = args[1].ToLowerInvariant();
        var allowed = operation switch
        {
            "list" => new HashSet<string>(["--name", "--package-db"], StringComparer.Ordinal),
            "add" => new HashSet<string>(["--name", "--version", "--abc", "--meta", "--package-db"], StringComparer.Ordinal),
            "remove" => new HashSet<string>(["--name", "--version", "--package-db"], StringComparer.Ordinal),
            "resolve" => new HashSet<string>(["--name", "--version", "--package-db"], StringComparer.Ordinal),
            _ => throw new ArgumentException($"Unknown package-db operation: {operation}")
        };
        var options = ParsePackageDbOptions(args, allowed);
        var databasePath = options.TryGetValue("--package-db", out var explicitPath)
            ? Path.GetFullPath(explicitPath)
            : Path.Combine(Environment.CurrentDirectory, "package-db.json");

        switch (operation)
        {
            case "resolve":
                var plan = AloePackageDatabase.Resolve(databasePath,
                    RequirePackageDbOption(options, "--name"), RequirePackageDbOption(options, "--version"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plan,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                break;
            case "list":
                foreach (var package in AloePackageDatabase.List(databasePath, options.GetValueOrDefault("--name"))
                             .OrderBy(package => package.Name, StringComparer.Ordinal)
                             .ThenBy(package => package.Version, StringComparer.Ordinal))
                    Console.WriteLine($"{package.Name}\t{package.Version}\t{package.AbcPath}\t{package.MetaPath}");
                break;
            case "add":
                AloePackageDatabase.Add(databasePath,
                    RequirePackageDbOption(options, "--name"), RequirePackageDbOption(options, "--version"),
                    RequirePackageDbOption(options, "--abc"), RequirePackageDbOption(options, "--meta"));
                Console.WriteLine($"Added {options["--name"]}@{options["--version"]}");
                break;
            case "remove":
                AloePackageDatabase.Remove(databasePath,
                    RequirePackageDbOption(options, "--name"), RequirePackageDbOption(options, "--version"));
                Console.WriteLine($"Removed {options["--name"]}@{options["--version"]}");
                break;
        }
        return 0;
    }

    private static Dictionary<string, string> ParsePackageDbOptions(string[] args, HashSet<string> allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowed.Contains(name)) throw new ArgumentException($"Unknown package-db option: {name}");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {name} requires a value.");
            if (!result.TryAdd(name, args[++index])) throw new ArgumentException($"Option {name} was specified more than once.");
        }
        return result;
    }

    private static string RequirePackageDbOption(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required package-db option is missing: {name}");

    private static string RequireSourceFile(string input)
    {
        var path = Path.GetFullPath(input);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Input file not found: {path}", path);
        if (!string.Equals(Path.GetExtension(path), ".aloe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"aloec expects a .aloe source file: {path}");
        return path;
    }

    private static string GetOutputPath(string[] args, string sourcePath)
    {
        for (var i = 2; i + 1 < args.Length; i++)
        {
            if (args[i] is "-o" or "--output")
                return Path.GetFullPath(args[i + 1]);
        }

        return Path.ChangeExtension(sourcePath, ".aloebc");
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Aloe Compiler CLI");
        Console.WriteLine("  aloec build <source.aloe> [-o output.aloebc]");
        Console.WriteLine("  aloec run <source.aloe>");
        Console.WriteLine("  aloec package-db <list|add|remove|resolve> [--package-db path]");
        Console.WriteLine("  aloec package-db resolve --name name --version 1.2.3 [--package-db path]");
    }
}
