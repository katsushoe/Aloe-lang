using Aloe.CommonLib;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;
using System.Text;

namespace Aloe.CompilerLib.Tests;

// Decision 0059: exception declarations, exception values and throw expressions.
public sealed class ExceptionValueTests
{
    private const string Types = """
exception AppError { field code: int; }
exception NotFound : AppError { field key: string; }
""";

    [Test]
    public void DecisionExample_CatchesDerivedValueWithFields()
    {
        var result = Run(Types + """
function find(key: string): int {
    throw NotFound { Message: "not found", code: 404, key: key };
}

function main(args: string[]): int {
    try { print(find("a")); }
    catch (e: NotFound) { print(e.key); print(e.code); print(e.Message); }
    catch (e: Exception) { print(e.TypeName); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("a\n404\nnot found\n"));
    }

    [Test]
    public void BaseCatchSeesBaseFieldsAndActualTypeName()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    try { throw NotFound { Message: "missing", code: 7, key: "k" }; }
    catch (e: AppError) { print(e.code); print(e.TypeName); print(e.Message); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("7\nNotFound\nmissing\n"));
    }

    [Test]
    public void ExceptionCatchStillBindsExceptionInfo()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    try { throw NotFound { Message: "m", code: 1, key: "k" }; }
    catch (e: Exception) { print(e.TypeName); print(e.Message); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("NotFound\nm\n"));
    }

    [Test]
    public void OmittedBaseIsExceptionAndNotSystemException()
    {
        var result = Run("""
exception Plain { }
function main(args: string[]): int {
    try { throw Plain { Message: "p" }; }
    catch (SystemException) { print("system"); }
    catch (e: Exception) { print(e.TypeName); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("Plain\n"));
    }

    [Test]
    public void BuiltinBaseIsCaughtByBuiltinAndDeclaredType()
    {
        var result = Run("""
exception BadArgument : ArgumentException { field index: int; }
function main(args: string[]): int {
    try { throw BadArgument { Message: "bad", index: 3 }; }
    catch (e: ArgumentException) { print(e.TypeName); }
    try { throw BadArgument { Message: "bad", index: 4 }; }
    catch (e: BadArgument) { print(e.index); }
    try { throw BadArgument { Message: "bad", index: 5 }; }
    catch (SystemException) { print("system"); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("BadArgument\n4\nsystem\n"));
    }

    [Test]
    public void BuiltinExceptionTypesCanBeThrown()
    {
        var result = Run("""
function main(args: string[]): int {
    try { throw ArgumentException { Message: "bad input" }; }
    catch (e: ArgumentException) { print(e.TypeName); print(e.Message); }
    try { throw ZeroDivisionException { Message: "zero" }; }
    catch (OverflowException) { print("overflow"); }
    catch (e: Exception) { print(e.TypeName); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("ArgumentException\nbad input\nZeroDivisionException\n"));
    }

    [Test]
    public void ThrowAcceptsVariablesParametersAndCallResults()
    {
        var result = Run(Types + """
function fail(error: AppError): void { throw error; }
function make(code: int): AppError { return AppError { Message: "made", code: code }; }
function main(args: string[]): int {
    let stored: AppError = AppError { Message: "stored", code: 1 };
    try { throw stored; } catch (e: AppError) { print(e.code); }
    try { fail(AppError { Message: "param", code: 2 }); } catch (e: AppError) { print(e.code); }
    try { throw make(3); } catch (e: AppError) { print(e.Message); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("1\n2\nmade\n"));
    }

    [Test]
    public void ExceptionValuesAreCopiedLikeStructs()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    var first = AppError { Message: "m", code: 1 };
    var second = first;
    second.code = 2;
    print(first.code); print(second.code); print(second.TypeName);
    try { throw second; } catch (e: AppError) { e.code = 9; print(e.code); }
    print(second.code);
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("1\n2\nAppError\n9\n2\n"));
    }

    [Test]
    public void DeclaredCatchDoesNotHandleVmExceptions()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    try { var zero = 0; var value = 1 / zero; print(value); }
    catch (e: AppError) { print("app"); }
    catch (ZeroDivisionException) { print("zero"); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("zero\n"));
    }

    [Test]
    public void CatchClausesMatchInSourceOrder()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    try { throw NotFound { Message: "m", code: 1, key: "k" }; }
    catch (e: AppError) { print("base first"); }
    catch (e: NotFound) { print("derived"); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("base first\n"));
    }

    [Test]
    public void NestedRethrowAndFinallyPreserveTheValue()
    {
        var result = Run(Types + """
function main(args: string[]): int {
    try {
        try { throw NotFound { Message: "m", code: 1, key: "deep" }; }
        catch (e: AppError) { print("inner"); throw; }
        finally { print("finally"); }
    }
    catch (e: NotFound) { print(e.key); }
    return 0;
}
""");
        Assert.That(result.Output, Is.EqualTo("inner\nfinally\ndeep\n"));
    }

    [Test]
    public void UncaughtExceptionValueReachesHostWithTypeAndMessage()
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(Types + """
function main(args: string[]): int { throw NotFound { Message: "gone", code: 1, key: "k" }; }
""")));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var error = Assert.Throws<AloeThrownException>(() => vm.RunFromEntryPoint());
        Assert.That(error!.Message, Is.EqualTo("NotFound: gone"));
        Assert.That(error.TypeChain, Is.EqualTo(new[] { "NotFound", "AppError", "Exception" }));
    }

    [TestCase("function main(args: string[]): int { throw 1; }", "throw requires an exception value")]
    [TestCase("struct S { field x: int; } function main(args: string[]): int { throw S { x: 1 }; }", "throw requires an exception value")]
    [TestCase("exception E { } function main(args: string[]): int { throw E { Message: \"m\", TypeName: \"X\" }; }", "TypeName is set automatically")]
    [TestCase("exception E { field c: int; } function main(args: string[]): int { throw E { c: 1 }; }", "must initialize Message")]
    [TestCase("exception E { field items: int[]; } function main(args: string[]): int { return 0; }", "references are forbidden")]
    [TestCase("exception E : Missing { } function main(args: string[]): int { return 0; }", "built-in or previously declared")]
    [TestCase("exception A : B { } exception B : A { } function main(args: string[]): int { return 0; }", "built-in or previously declared")]
    [TestCase("exception E { method f(): void { } } function main(args: string[]): int { return 0; }", "fields only")]
    [TestCase("exception E { construct() { } } function main(args: string[]): int { return 0; }", "fields only")]
    [TestCase("exception E { } function main(args: string[]): int { var e = new E(); return 0; }", "created with literals")]
    [TestCase("exception E { } struct S { field e: E; } function main(args: string[]): int { return 0; }", "cannot be used as struct or exception fields")]
    [TestCase("exception E { field Message: string; } function main(args: string[]): int { return 0; }", "unique identifier")]
    [TestCase("exception A { field c: int; } exception B : A { field c: int; } function main(args: string[]): int { return 0; }", "unique identifier")]
    [TestCase("exception E { } exception E { } function main(args: string[]): int { return 0; }", "unique type identifier")]
    [TestCase("exception Exception { } function main(args: string[]): int { return 0; }", "unique type identifier")]
    public void InvalidExceptionDeclarationsAndThrows_AreRejected(string source, string message)
    {
        var error = Assert.Throws<AloeCompileException>(() => new AloeCompiler().Compile(source));
        Assert.That(error!.Message, Does.Contain(message));
    }

    private static (long ExitCode, string Output) Run(string source)
    {
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings(); settings.Logging.FileEnabled = false; settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        var output = new StringBuilder(); vm.OutputWriter = line => output.AppendLine(line);
        vm.RunFromEntryPoint(); Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        return (vm.Pop().AsInt, output.ToString().Replace("\r\n", "\n"));
    }
}
