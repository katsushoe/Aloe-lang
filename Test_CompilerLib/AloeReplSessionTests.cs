using Aloe.Repl;
using NUnit.Framework;


namespace Aloe.CompilerLib.Tests;


[TestFixture]
public sealed class AloeReplSessionTests
{
    [Test]
    public void Variables_PersistAcrossSubmissions()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var x = 10;").Success, Is.True);
        Assert.That(repl.Submit("x = x + 5;").Success, Is.True);


        var result = repl.Submit("x");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("15\n"));
    }


    [Test]
    public void ComparisonExpression_IsEvaluatedAsExpression()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var x = 10;").Success, Is.True);


        var result = repl.Submit("x == 10");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("true\n"));
    }


    [Test]
    public void IfElse_AndModulo_WorkAcrossSubmissions()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var x = 15;").Success, Is.True);


        var result = repl.Submit("""
if (x % 15 == 0) {
    print("FizzBuzz");
} else {
    print("other");
}
""");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("FizzBuzz\n"));
    }


    [Test]
    public void LogicalWords_WorkAsExpressions()
    {
        var repl = new AloeReplSession();


        var result = repl.Submit("not false and true");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("true\n"));
    }


    [Test]
    public void UserFunction_PersistsAcrossSubmissions()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var baseValue = 10;").Success, Is.True);
        Assert.That(repl.Submit("""
function add(a: int, b: int): int {
    return a + b;
}
""").Success, Is.True);


        var result = repl.Submit("add(baseValue, 32)");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("42\n"));
    }


    [Test]
    public void UserFunction_WithNestedEarlyReturn_PersistsAcrossSubmissions()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("""
function classify(value: int): int {
    if (value < 0) {
        return -1;
    }
    if (value == 0) {
        return 0;
    }
    return 1;
}
""").Success, Is.True);


        Assert.That(repl.Submit("classify(-2)").Output, Is.EqualTo("-1\n"));
        Assert.That(repl.Submit("classify(0)").Output, Is.EqualTo("0\n"));
        Assert.That(repl.Submit("classify(9)").Output, Is.EqualTo("1\n"));
    }


    [Test]
    public void VoidFunction_CanPrintFromReplStatement()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("""
function announce(value: string): void {
    print(value);
}
""").Success, Is.True);


        var result = repl.Submit("announce(\"Aloe\");");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("Aloe\n"));
    }


    [Test]
    public void BreakAndContinue_WorkInsideReplWhile()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var i = 0;").Success, Is.True);
        Assert.That(repl.Submit("var sum = 0;").Success, Is.True);


        var result = repl.Submit("""
while (i < 10) {
    i = i + 1;
    if (i == 3 or i == 5) { continue; }
    if (i >= 8 and not (i == 9)) { break; }
    sum = sum + i;
}
""");


        Assert.That(result.Success, Is.True);
        Assert.That(repl.Submit("sum").Output, Is.EqualTo("20\n"));
    }


    [Test]
    public void PreviousPrintOutput_IsNotRepeated()
    {
        var repl = new AloeReplSession();


        var first = repl.Submit("print(1);");
        var second = repl.Submit("print(2);");


        Assert.That(first.Output, Is.EqualTo("1\n"));
        Assert.That(second.Output, Is.EqualTo("2\n"));
    }


    [Test]
    public void WhileAndAssignments_Persist()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var sum = 0;").Success, Is.True);
        Assert.That(repl.Submit("let i: int = 1;").Success, Is.True);


        Assert.That(repl.Submit("""
while (i <= 10) {
    sum = sum + i;
    i = i + 1;
}
""").Success, Is.True);


        Assert.That(repl.Submit("sum").Output, Is.EqualTo("55\n"));
    }


    [Test]
    public void Reset_ClearsSessionState()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var x = 1;").Success, Is.True);


        repl.Reset();


        var result = repl.Submit("x");


        Assert.That(result.Success, Is.False);
    }


    [Test]
    public void CompleteProgramWithMain_IsExecutedAsEntered()
    {
        var repl = new AloeReplSession();


        var result = repl.Submit("""
function main(args: string[]): int {
    print(42);
    return 0;
}
""");


        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Is.EqualTo("42\n"));
        Assert.That(repl.History, Is.Empty);
    }


    [Test]
    public void CompleteProgramWithMain_DoesNotReplaceInteractiveSession()
    {
        var repl = new AloeReplSession();


        Assert.That(repl.Submit("var x = 10;").Success, Is.True);

        var standalone = repl.Submit("""
function main(args: string[]): int {
    print(99);
    return 0;
}
""");


        Assert.That(standalone.Success, Is.True);
        Assert.That(standalone.Output, Is.EqualTo("99\n"));
        Assert.That(repl.Submit("x").Output, Is.EqualTo("10\n"));
    }


    [Test]
    public void ClassDeclaration_PersistsAtModuleScope()
    {
        var repl = new AloeReplSession();

        Assert.That(repl.Submit("""
class Node {
    construct() {
    }
}
""").Success, Is.True);

        var result = repl.Submit("var node = new Node();");

        Assert.That(result.Success, Is.True);
        Assert.That(repl.CurrentProgramSource, Does.Contain("class Node"));
        Assert.That(repl.CurrentProgramSource, Does.Contain("var node = new Node();"));
    }


    [Test]
    public void CompleteMain_CanUsePersistedClassAndFunctionDeclarations()
    {
        var repl = new AloeReplSession();

        Assert.That(repl.Submit("""
class Node {
    construct() {
    }
}
""").Success, Is.True);

        Assert.That(repl.Submit("""
function makeGarbage(): void {
    var node = new Node();
}
""").Success, Is.True);

        var result = repl.Submit("""
function main(args: string[]): int {
    makeGarbage();
    tick();
    GC.require();
    return 0;
}
""");

        Assert.That(result.Success, Is.True);
    }


    [Test]
    public void GcDebug_EmitsReplPerfAllocationBreakdown()
    {
        var repl = new AloeReplSession();

        var result = repl.Submit("""
function main(args: string[]): int {
    GC.debug = true;
    GC.require();
    GC.debug = false;
    return 0;
}
""");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Does.Contain("[REPL-PERF]"));
        Assert.That(result.Output, Does.Contain("compileAllocatedBytes="));
        Assert.That(result.Output, Does.Contain("vmCreateAllocatedBytes="));
        Assert.That(result.Output, Does.Contain("runAllocatedBytes="));
        Assert.That(result.Output, Does.Contain("totalAllocatedBytes="));
        Assert.That(result.Output, Does.Contain("managedDeltaBytes="));
    }


    [Test]
    public void HostGcCommand_ReportsBeforeAfterAndCollections()
    {
        var repl = new AloeReplSession();

        var result = repl.Submit(":hostgc");

        Assert.That(result.Success, Is.True);
        Assert.That(result.Output, Does.StartWith("[HOST-GC]"));
        Assert.That(result.Output, Does.Contain("beforeBytes="));
        Assert.That(result.Output, Does.Contain("afterBytes="));
        Assert.That(result.Output, Does.Contain("reclaimedBytes="));
        Assert.That(result.Output, Does.Contain("gen0="));
        Assert.That(result.Output, Does.Contain("gen1="));
        Assert.That(result.Output, Does.Contain("gen2="));
    }


    [Test]
    public void MultilineInput_WaitsForClosingBrace()
    {
        Assert.That(
            AloeReplInput.IsComplete("if (true) {"),
            Is.False);


        Assert.That(
            AloeReplInput.IsComplete(
                "if (true) {\nprint(1);\n} else {\nprint(2);\n}"),
            Is.True);
    }
}