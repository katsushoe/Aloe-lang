using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.VmHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        var settings = AloeVmSettings.Load();
        var logger = new AloeLogger(settings.Logging, settings.ConfigDirectory);

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var path = RequireInputFile(args[0], ".aloebc");
            logger.Info($"VM load: {path}");

            var bytes = File.ReadAllBytes(path);
            var vm = AloeVmLoader.FromAloeBc(bytes, settings);
            vm.RunFromEntryPoint();

            logger.Info("VM execution completed.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string RequireInputFile(string input, string extension)
    {
        var path = Path.GetFullPath(input);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Input file not found: {path}", path);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"aloevm expects a {extension} file: {path}");
        return path;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Aloe VM");
        Console.WriteLine("  aloevm <module.aloebc>");
    }
}
