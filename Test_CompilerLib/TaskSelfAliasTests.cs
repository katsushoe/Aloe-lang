using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskSelfAliasTests
{
    [TestCase("var alias=this;")]
    [TestCase("let alias: Worker=this;")]
    [TestCase("var alias=(this);")]
    [TestCase("var first=this; var alias=first;")]
    public void StableLocalAliasIsSynchronousInsideTryCatchFinally(string declaration)
    {
        var source="task Worker { method fail(): void { var bad=1/0; } method run(): void { "+declaration+" try { alias.fail(); } catch(ZeroDivisionException) { print(7); } finally { print(8); } this.stop(); } } function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); return 0; }";
        Assert.That(Run(source),Is.EqualTo(new[]{"7","8"}));
    }

    [Test]
    public void StableAliasCanBeReadAcrossLoopIterations()
    {
        var source="task Worker { method send(): void { print(7); } method run(): void { var alias=this; var index=0; while(index < 2) { try { alias.send(); } catch(Exception) { } index=index+1; } this.stop(); } } function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); return 0; }";
        Assert.That(Run(source),Is.EqualTo(new[]{"7","7"}));
    }

    [TestCase("var alias=new Worker();")]
    [TestCase("var alias=this; if(true) { alias=new Worker(); }")]
    [TestCase("var alias=this; alias=new Worker();")]
    public void ExternalOrRetargetedAliasCannotEnqueueInsideProtectedRegion(string declaration)
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task Worker { method send(): void { } method run(): void { "+declaration+" try { alias.send(); } catch(Exception) { } } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void LaterLoopRetargetingCannotMakeFirstLexicalReadAppearStable()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task Worker { method send(): void { } method run(): void { var alias=this; var index=0; while(index < 2) { try { alias.send(); } catch(Exception) { } alias=new Worker(); index=index+1; } } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void ProofDoesNotLeakBetweenFunctionsOrTaskTypes()
        => Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task First { method send(): void { } method run(): void { var alias=this; try { alias.send(); } catch(Exception) { } } } task Second { method run(): void { var alias=new First(); try { alias.send(); } catch(Exception) { } } } function main(args: string[]): int { return 0; }"));

    [Test]
    public void ConstructorStableAliasCanCallSelfSynchronouslyWhileStopped()
    {
        var source="task Worker { construct() { var alias=this; try { alias.send(); } catch(Exception) { } } method send(): void { print(7); } method run(): void { } } function main(args: string[]): int { var worker=new Worker(); return 0; }";
        Assert.That(Run(source),Is.EqualTo(new[]{"7"}));
    }

    private static List<string> Run(string source)
    {
        var vm=new AloeVm(new AloeCompiler().Compile(source));var output=new List<string>();vm.OutputWriter=output.Add;vm.RunFromEntryPoint();Assert.That(vm.Pop().AsInt,Is.Zero);return output;
    }
}
