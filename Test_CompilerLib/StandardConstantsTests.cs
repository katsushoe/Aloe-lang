using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0060: standard global constants and magic constants.
public sealed class StandardConstantsTests
{
    private static string Run(string body, string? path = null, bool debug = false)
    {
        var compiler = new AloeCompiler { DebugBuild = debug };
        if (path != null) compiler.SourcePath = path;
        var module = AloeBcCodec.Read(AloeBcCodec.Write(compiler.Compile(body)));
        var output = new System.Text.StringBuilder();
        var vm = new AloeVm(module) { OutputWriter = line => output.Append(line).Append('\n') };
        vm.RunFromEntryPoint();
        return output.ToString();
    }

    private static string MainSource(string statements) => "function main(args: string[]): int {\n" + statements + "\n    return 0;\n}\n";

    [Test]
    public void CompileTimeConstantsHaveDefinedValues()
    {
        var output = Run(MainSource("""
    print(ALOE_INT_MIN); print(ALOE_INT_MAX); print(ALOE_EOL == "\n"); print(ALOE_DEBUG);
    print(ALOE_FLOAT_MAX > 1000000.0); print(ALOE_FLOAT_MIN > 0.0);
    print(ALOE_MAJOR_VERSION >= 0); print(ALOE_VERSION != "");
"""));
        Assert.That(output, Is.EqualTo("-2147483648\n2147483647\ntrue\nfalse\ntrue\ntrue\ntrue\ntrue\n"));
        Assert.That(Run(MainSource("print(ALOE_DEBUG);"), debug: true), Is.EqualTo("true\n"));
    }

    [Test]
    public void RuntimeConstantsComeFromTheExecutingHost()
    {
        var output = Run(MainSource("""
    print(ALOE_OS); print(ALOE_TARGET); print(ALOE_DIR_SEPARATOR); print(ALOE_MAX_PATHLEN > 0); print(ALOE_BINDIR != "");
"""));
        var expectedOs = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Darwin" : "Unknown";
        var lines = output.Split('\n');
        Assert.That(lines[0], Is.EqualTo(expectedOs));
        Assert.That(lines[1], Is.Not.Empty);
        Assert.That(lines[2], Is.EqualTo(Path.DirectorySeparatorChar.ToString()));
        Assert.That(lines[3], Is.EqualTo("true"));
        Assert.That(lines[4], Is.EqualTo("true"));
    }

    [Test]
    public void MagicConstantsDescribeTheSourceLocation()
    {
        const string source = """
class Shop {
    construct() { }
    public async method buy(): void { print(__CLASS__); print(__FUNCTION__); print(__METHOD__); }
}
function main(args: string[]): int {
    print(__FILE__); print(__DIR__);
    print(__LINE__);
    print(__FUNCTION__);
    var shop = new Shop();
    shop.buy();
    tick();
    return 0;
}
""";
        Assert.That(Run(source, path: "app/src/main.aloe"),
            Is.EqualTo("app/src/main.aloe\napp/src\n7\nmain\nShop\nbuy\nShop.buy\n"));
        Assert.That(Run(MainSource("print(__FILE__); print(__DIR__);")), Is.EqualTo("<stdin>\n.\n"));
    }

    [TestCase("print(__TRAIT__);")]
    [TestCase("print(ALOE_UNKNOWN);")]
    [TestCase("var ALOE_MINE = 1;")]
    [TestCase("var __mine__ = 1;")]
    [TestCase("var x: int = ALOE_VERSION;")]
    public void ReservedAndUnavailableNamesAreCompileErrors(string statement)
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(MainSource(statement)));

    [Test]
    public void ClassConstantOutsideAClassIsACompileError()
        => Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(MainSource("print(__CLASS__);")));
}
