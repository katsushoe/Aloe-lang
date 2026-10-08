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
    }
}
