using Aloe.CommonLib;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskNotificationEffectTests
{
    private const string Target = "class Target { public async method emit(): void { } } ";
    private const string Main = " function main(args: string[]): int { return 0; }";
    private const string Sender = "class Sender { field target: Target; construct(target: Target) { this.target=target; } public property Value: int { get { var target=this.target; target.emit(); return 1; } set { var target=this.target; target.emit(); } } } ";

    [TestCase("try { send(target); } catch { }")]
    [TestCase("try { var bad=1/0; } catch { send(target); }")]
    [TestCase("try { } finally { send(target); }")]
    public void FreeHelperNotificationIsRejectedInEveryProtectedRegion(string body)
        => Reject(Target+"function send(target: Target): void { target.emit(); } "+Worker(body)+Main);

    [TestCase("function send(target: Target): void { relay(target); } function relay(target: Target): void { target.emit(); }")]
    [TestCase("function relay(target: Target): void { target.emit(); } function send(target: Target): void { relay(target); }")]
    [TestCase("function send(target: Target): void { relay(target); } function relay(target: Target): void { if(false) { send(target); } target.emit(); }")]
    public void FixedPointHandlesForwardOrderAndNotificationCycles(string helpers)
        => Reject(Target+helpers+Worker("try { send(target); } catch { }")+Main);

    [TestCase("this.send();")]
    [TestCase("send();")]
    [TestCase("var self=this; self.send();")]
    public void SynchronousSelfHelpersAreCheckedTransitively(string call)
        => Reject(Target+Worker("try { "+call+" } catch { }","method send(): void { var target=this.target; target.emit(); }")+Main);

    [Test]
    public void SynchronousStaticHelperIsChecked()
        => Reject(Target+"class Helpers { public static method send(): void { var target=new Target(); target.emit(); } } "+Worker("try { Helpers.send(); } catch { }")+Main);

    [Test]
    public void ClassConstructorIsChecked()
        => Reject(Target+"class Emitter { construct(target: Target) { target.emit(); } } "+Worker("try { var sender=new Emitter(target); } catch { }")+Main);

    [Test]
    public void TaskConstructorIsChecked()
        => Reject(Target+"task Emitter { construct(target: Target) { target.emit(); } method run(): void { } } "+Worker("try { var sender=new Emitter(target); } catch { }")+Main);

    [Test]
    public void TaskProtectedConstructorIsChecked()
        => Reject(Target+"function send(target: Target): void { target.emit(); } task Worker { construct(target: Target) { try { send(target); } catch { } } method run(): void { } }"+Main);

    [TestCase("var sender=new Sender(target); try { var value=sender.Value; } catch { }")]
    [TestCase("var sender=new Sender(target); try { with(sender) { var value=.Value; } } catch { }")]
    [TestCase("var sender=new Sender(target); let senders: Sender[]=[sender]; try { var value=senders[0].Value; } catch { }")]
    public void CommittedGetterRoutesAreChecked(string body)
        => Reject(Target+Sender+Worker(body)+Main);

    [Test]
    public void SetterIsChecked()
        => Reject(Target+Sender+Worker("var sender=new Sender(target); try { sender.Value=1; } catch { }")+Main);

    [Test]
    public void ArgumentEvaluationIsChecked()
        => Reject(Target+"function send(target: Target): int { target.emit(); return 1; } function receive(value: int): void { } "+Worker("try { receive(send(target)); } catch { }")+Main);

    [Test]
    public void StaticNotificationSinkIsChecked()
        => Reject("class Target { public static async method emit(): void { } } function send(): void { Target.emit(); } task Worker { method run(): void { try { send(); } catch { } } }"+Main);

    [Test]
    public void ExternalTaskNotificationInHelperIsChecked()
        => Reject("task Other { method send(): void { } method run(): void { } } function send(): void { var other=new Other(); other.send(); } task Worker { method run(): void { try { send(); } catch { } } }"+Main);

    [TestCase("let stream: pipe<int> = pipe<int>.create(); stream.write(1);")]
    [TestCase("let stream: pipe<int> = pipe<int>.create(); stream.close();")]
    public void UnresolvedCallbackExecutionIsRejected(string action)
        => Reject("function send(): void { "+action+" } task Worker { method run(): void { try { send(); } catch { } } }"+Main);

    [Test]
    public void DirectUnknownCallbackExecutionIsRejected()
        => Reject("task Worker { method run(): void { let stream: pipe<int> = pipe<int>.create(); try { stream.close(); } catch { } } }"+Main);

    [TestCase("try { this.send(); } catch { }")]
    [TestCase("var self=this; try { self.send(); } catch { }")]
    [TestCase("try { send(); } catch { }")]
    public void PureSelfAndStableAliasRemainAccepted(string body)
        => Accept(Worker(body,"method send(): void { print(1); }")+Target+Main);

    [Test]
    public void PureMutualRecursionAndForwardReferenceRemainAccepted()
        => Accept("function one(value: int): int { if(value==0) { return 0; } return two(value-1); } function two(value: int): int { return one(value); } task Worker { method run(): void { try { var result=one(1); } catch { } } }"+Main);

    [Test]
    public void PureGetterAndSetterRemainAccepted()
        => Accept("class Box { field value: int; construct() { this.value=0; } public property Value: int { get { return this.value; } set { this.value=value; } } } task Worker { method run(): void { var box=new Box(); try { box.Value=1; var value=box.Value; } catch { } } }"+Main);

    [TestCase("struct Sender { field value: int; construct() { var target=new Target(); target.emit(); this.value=0; } }", "try { var sender=new Sender(); } catch { }")]
    [TestCase("struct Sender { field value: int; method send(): void { var target=new Target(); target.emit(); } }", "var sender=Sender { value: 0 }; try { with(sender) { .send(); } } catch { }")]
    [TestCase("struct Sender { field value: int; public property Value: int { get { var target=new Target(); target.emit(); return 1; } } }", "var sender=Sender { value: 0 }; try { with(sender) { var value=.Value; } } catch { }")]
    public void StructConstructorMethodAndGetterAreChecked(string declaration,string body)
        => Reject(Target+declaration+Worker(body)+Main);

    [Test]
    public void NotificationsOutsideProtectedRegionsRemainAccepted()
        => Accept(Target+Worker("target.emit(); try { this.pure(); } catch { }","method pure(): void { print(1); }")+Main);

    [Test]
    public void OrdinaryObjectTryNotificationRemainsAccepted()
        => Accept(Target+"function send(target: Target): void { try { target.emit(); } catch { } }"+Main);

    [Test]
    public void LifecycleOperationsRemainSynchronous()
        => Accept("task Worker { method run(): void { try { this.start(); this.stop(); delete this; } finally { print(1); } } }"+Main);

    [Test]
    public void CompilerRecoversAfterRejectedEffectGraph()
    {
        var compiler=new AloeCompiler();
        Assert.Throws<AloeCompileException>(()=>compiler.Compile(Target+"function send(target: Target): void { target.emit(); } "+Worker("try { send(target); } catch { }")+Main));
        Assert.DoesNotThrow(()=>compiler.Compile("task Worker { method run(): void { try { print(1); } catch { } } }"+Main));
    }

    [Test]
    public void AmbiguousTaskAliasInsideHelperRemainsUnknown()
        => Reject(Target+Worker("try { this.forward(); } catch { }","method send(): void { } method forward(): void { var self=this; self=new Worker(this.target); self.send(); }")+Main);

    [Test]
    public void PureHelperActuallyExecutesThroughTaskScheduler()
    {
        var source="function answer(): int { return 42; } task Worker { method run(): void { try { print(answer()); this.stop(); } catch { print(99); } } } function main(args: string[]): int { var worker=new Worker(); worker.start(); tick(); return 0; }";
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source))),settings);
        var output=new List<string>();vm.OutputWriter=output.Add;vm.RunFromEntryPoint();
        Assert.That(vm.Pop().AsInt,Is.Zero);Assert.That(output,Is.EqualTo(new[]{"42"}));Assert.That(vm.LastTaskFailure,Is.Null);
    }

    [Test]
    public void ExistingTickLocationRestrictionIsUnchanged()
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile("task Worker { method run(): void { tick(); } }"+Main));
        Assert.That(error!.Message,Does.Contain("tick() is only callable from main"));
    }

    private static string Worker(string body,string methods="")
        => "task Worker { field target: Target; construct(target: Target) { this.target=target; } "+methods+" method run(): void { var target=this.target; "+body+" } } ";

    private static void Reject(string source)
    {
        var error=Assert.Throws<AloeCompileException>(()=>new AloeCompiler().Compile(source));
        Assert.That(error!.Message,Does.Contain("Task protected call must not transitively notify"));
    }

    private static void Accept(string source)
    {
        var module=AloeBcCodec.Read(AloeBcCodec.Write(new AloeCompiler().Compile(source)));
        var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;
        var vm=new AloeVm(module,settings);vm.RunFromEntryPoint();Assert.That(vm.Pop().AsInt,Is.Zero);
    }
}
