using Aloe.Aloe2Wasm;
using Aloe.CommonLib;
using Aloe.CompilerLib;

namespace Aloe.Aloe2WasmHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        var settings = Aloe2WasmSettings.Load();
        var logger = new AloeLogger(settings.Logging, settings.ConfigDirectory);
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var input = RequireInputFile(args[0]);
            var output = GetOutputPath(args, input);
            logger.Info($"WASM compile: {input} -> {output}");

            Module module;
            if (string.Equals(Path.GetExtension(input), ".aloebc", StringComparison.OrdinalIgnoreCase))
                module = AloeBcCodec.Read(File.ReadAllBytes(input));
            else
                module = new AloeCompiler().Compile(File.ReadAllText(input));

            var wasm = new AloeWasmCompiler().Compile(module);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, wasm);
            Console.WriteLine(output);
            logger.Info($"WASM compile completed: {wasm.Length} bytes.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string RequireInputFile(string input)
    {
        var path = Path.GetFullPath(input);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Input file not found: {path}", path);

        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".aloe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".aloebc", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"aloe2wasm expects a .aloe or .aloebc input file: {path}");
        }

        return path;
    }

    private static string GetOutputPath(string[] args, string input)
    {
        for (var i = 1; i + 1 < args.Length; i++)
            if (args[i] is "-o" or "--output") return Path.GetFullPath(args[i + 1]);
        return Path.ChangeExtension(input, ".wasm");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Aloe to WebAssembly compiler");
        Console.WriteLine("  aloe2wasm <source.aloe|module.aloebc> [-o output.wasm]");
    }
}
