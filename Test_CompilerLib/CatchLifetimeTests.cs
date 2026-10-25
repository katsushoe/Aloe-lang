using Aloe.CommonLib;
using Aloe.CompilerLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class CatchLifetimeTests
{
    [TestCase("try { throw; } catch (ZeroDivisionException) { } try { throw; } catch (ZeroDivisionException) { }", false)]
    [TestCase("try { let n: int = 2147483647 + 1; } catch (OverflowException) { } throw;", true)]
    [TestCase("try { try { let n: int = 2147483647 + 1; } catch (OverflowException) { throw; } } catch (OverflowException) { } throw;", true)]
    [TestCase("try { try { let n: int = 2147483647 + 1; } catch (OverflowException) { let n: int = 1 / 0; } } catch (ZeroDivisionException) { } throw;", true)]
    [TestCase("while (true) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { break; } } throw;", true)]
    [TestCase("let active: bool = true; while (active) { active = false; try { let n: int = 2147483647 + 1; } catch (OverflowException) { continue; } } throw;", true)]
    [TestCase("let items: int[] = [1]; foreach (item in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { break; } } throw;", true)]
    [TestCase("let items: int[] = [1]; foreach (item in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { continue; } } throw;", true)]
    [TestCase("let items: map<int> = [\"a\":1]; foreach (key in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { break; } } throw;", true)]
    [TestCase("let items: map<int> = [\"a\":1]; foreach (key in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { continue; } } throw;", true)]
    [TestCase("let items: map<int> = [\"a\":1]; foreach (key,item in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { break; } } throw;", true)]
    [TestCase("let items: map<int> = [\"a\":1]; foreach (key,item in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { continue; } } throw;", true)]
    [TestCase("while (true) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { try { let n: int = 1 / 0; } catch (ZeroDivisionException) { break; } } } throw;", true)]
    [TestCase("let items: int[] = [1]; foreach (item in items) { try { let n: int = 2147483647 + 1; } catch (OverflowException) { try { let n: int = 1 / 0; } catch (ZeroDivisionException) { break; } } } throw;", true)]
    [TestCase("try { let n: int = 2147483647 + 1; } catch (OverflowException) { while (true) { break; } } throw;", true)]
    [TestCase("try { let n: int = 2147483647 + 1; } catch (OverflowException) { let items: int[] = [1]; foreach (item in items) { continue; } } throw;", true)]
    public void NestedCatch_RestoresEnclosingExceptionAfterHandledFault(string body, bool escapes)
    {
        var source = $$"""
function fail(): int { return 1 / 0; }
function main(args: string[]): int {
    try {
        try { let n: int = fail(); }
        catch (ZeroDivisionException) { {{body}} }
    }
    catch (OverflowException) { return 1; }
    catch (ZeroDivisionException) { return 73; }
    return 42;
}
""";
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings = new AloeVmSettings();
        settings.Logging.FileEnabled = false;
        settings.Logging.ConsoleEnabled = false;
        var vm = new AloeVm(module, settings);
        vm.RunFromEntryPoint();
        Assert.That(vm.ValueStack.Count, Is.EqualTo(1));
        Assert.That(vm.Pop().AsInt, Is.EqualTo(escapes ? 73 : 42));
    }
}
