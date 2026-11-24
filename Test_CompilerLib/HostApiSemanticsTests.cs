using AloeHostCapability = Aloe.RuntimeLib.AloeHostCapability;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

// Decision 0067: host APIs are static synchronous calls executed at the call site;
// their exceptions are ordinary exceptions catchable where the call is made.
public sealed class HostApiSemanticsTests
{
    private static (string Output, List<int> Sleeps) Run(string source, AloeHostCapability capabilities = AloeHostCapability.All)
    {
        var output = new System.Text.StringBuilder();
        var sleeps = new List<int>();
        var vm = new AloeVm(new AloeCompiler().Compile(source), new AloeVmSettings { AllowedHostCapabilities = capabilities })
        {
            OutputWriter = line => output.Append(line).Append('\n'),
            InputReader = () => "line",
            SleepHandler = ms => sleeps.Add(ms)
        };
        vm.RunFromEntryPoint();
        return (output.ToString(), sleeps);
    }

    [Test]
    public void HostCallsInsideATickRunImmediatelyInCallOrder()
    {
        const string source = """
class Worker {
    construct() { }
    public async method work(): void {
        print("before");
        sleep(5);
        print(readLine());
    }
}
function main(args: string[]): int {
    var worker = new Worker();
    worker.work();
    print("queued");
    tick();
    print("after");
    return 0;
}
""";
        var (output, sleeps) = Run(source);
        Assert.That(output, Is.EqualTo("queued\nbefore\nline\nafter\n"));
        Assert.That(sleeps, Is.EqualTo(new[] { 5 }));
    }

    [Test]
    public void HostCallExceptionIsCatchableAtTheCallSiteInsideAsyncCode()
    {
        const string source = """
class Worker {
    field failures: int;
    construct() { this.failures = 0; }
    private method trySleep(): int {
        try { sleep(1); return 0; } catch (e: Exception) { return 1; }
    }
    public async method work(): void {
        this.failures = this.failures + trySleep();   // the host exception is caught at its call site
    }
    public property Failures: int { get { return this.failures; } }
}
function main(args: string[]): int {
    var worker = new Worker();
    worker.work();
    tick();
    print(worker.Failures);
    return 0;
}
""";
        var (output, sleeps) = Run(source, AloeHostCapability.None);
        Assert.That(output, Is.EqualTo("1\n"));
        Assert.That(sleeps, Is.Empty);
    }

    [Test]
    public void HostCallExceptionIsCatchableInMain()
    {
        const string source = """
function main(args: string[]): int {
    try { var text = readLine(); print(text); } catch (e: Exception) { print("denied"); }
    return 0;
}
""";
        Assert.That(Run(source, AloeHostCapability.None).Output, Is.EqualTo("denied\n"));
    }

    [Test]
    public void HostSideEffectsAreNotRolledBackWhenATickFails()
    {
        const string source = """
class Worker {
    construct() { }
    public async method work(): void {
        sleep(7);
        var zero = 0;
        var fail = 1 / zero;
    }
}
function main(args: string[]): int {
    var worker = new Worker();
    worker.work();
    try { tick(); } catch (e: Exception) { print("tick failed"); }
    return 0;
}
""";
        var (output, sleeps) = Run(source);
        Assert.That(output, Is.EqualTo("tick failed\n"));
        Assert.That(sleeps, Is.EqualTo(new[] { 7 }), "the host call already ran and is not undone");
    }
}
