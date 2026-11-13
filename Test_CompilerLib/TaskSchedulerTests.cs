using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskSchedulerTests
{
    [Test]
    public void StoppedTaskReceivesFifoNotificationsWithoutRun()
    {
        var (pool, scheduler) = Create(); var output = new List<long>();
        scheduler.Bind("Worker", "send", (_, args) => output.Add(args[0].AsInt), EnumValueKind.Int);
        scheduler.Bind("Worker", "run", (_, _) => output.Add(-1));
        var id = pool.Create("Worker"); scheduler.Enqueue(id, "send", AloeValue.FromInt(1));
        scheduler.Enqueue(id, "send", AloeValue.FromInt(2)); scheduler.Tick(); scheduler.Tick();
        Assert.That(output, Is.EqualTo(new long[] { 1, 2 }));
    }

    [Test]
    public void RunningTaskReceivesNotificationsBeforeExactlyOneRun()
    {
        var (pool, scheduler) = Create(); var output = new List<string>();
        scheduler.Bind("Worker", "send", (_, _) => output.Add("send"));
        scheduler.Bind("Worker", "run", (_, _) => output.Add("run"));
        var id = pool.Create("Worker"); pool.Start(id); scheduler.Enqueue(id, "send");
        scheduler.Tick(); scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { "send", "run", "run" }));
    }

    [Test]
    public void TypeAndSlotOrderOverridesGlobalEnqueueOrder()
    {
        var (pool, scheduler) = Create(); pool.RegisterType("Other"); var output = new List<long>();
        scheduler.Bind("Worker", "send", (id, _) => output.Add(id));
        scheduler.Bind("Other", "send", (id, _) => output.Add(id));
        var other = pool.Create("Other"); var a = pool.Create("Worker"); var b = pool.Create("Worker");
        scheduler.Enqueue(other, "send"); scheduler.Enqueue(b, "send"); scheduler.Enqueue(a, "send");
        scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { a, b, other }));
    }

    [Test]
    public void StartingStoppedTaskDuringTickDefersItsRun()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var a = pool.Create("Worker"); var b = pool.Create("Worker");
        scheduler.Bind("Worker", "run", (id, _) => { output.Add(id); if (id == a) pool.Start(b); });
        pool.Start(a); scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { a }));
        scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { a, a, b }));
    }

    [Test]
    public void StoppingCapturedTaskDuringTickTakesEffectNextTick()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var a = pool.Create("Worker"); var b = pool.Create("Worker");
        scheduler.Bind("Worker", "run", (id, _) => { output.Add(id); if (id == a) pool.Stop(b); });
        pool.Start(a); pool.Start(b); scheduler.Tick(); scheduler.Tick();
        Assert.That(output, Is.EqualTo(new[] { a, b, a }));
    }

    [Test]
    public void NotificationStartAndStopDoNotChangeCapturedRunEligibility()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var a = pool.Create("Worker"); var b = pool.Create("Worker");
        scheduler.Bind("Worker", "toggle", (id, _) => { if (id == a) pool.Stop(a); else pool.Start(b); });
        scheduler.Bind("Worker", "run", (id, _) => output.Add(id)); pool.Start(a);
        scheduler.Enqueue(a, "toggle"); scheduler.Enqueue(b, "toggle"); scheduler.Tick(); scheduler.Tick();
        Assert.That(output, Is.EqualTo(new[] { a, b }));
    }

    [Test]
    public void NotificationsAddedToLaterTaskDuringTickWaitUntilNextTick()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var a = pool.Create("Worker"); var b = pool.Create("Worker");
        scheduler.Bind("Worker", "run", (id, _) => { if (id == a) scheduler.Enqueue(b, "send"); });
        scheduler.Bind("Worker", "send", (id, _) => output.Add(id)); pool.Start(a);
        scheduler.Tick(); Assert.That(output, Is.Empty); scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { b }));
    }

    [Test]
    public void SelfQueuedNotificationIsNotDrainedToQuiescence()
    {
        var (pool, scheduler) = Create(); var count = 0; var id = pool.Create("Worker");
        scheduler.Bind("Worker", "send", (target, _) => { count++; scheduler.Enqueue(target, "send"); });
        scheduler.Enqueue(id, "send"); scheduler.Tick(); Assert.That(count, Is.EqualTo(1));
        scheduler.Tick(); Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void NewStartedTaskCannotRunInCreationTick()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var first = pool.Create("Worker"); long added = 0;
        scheduler.Bind("Worker", "run", (id, _) => { output.Add(id); if (added == 0) { added = pool.Create("Worker"); pool.Start(added); } });
        pool.Start(first); scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { first }));
        scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { first, first, added }));
    }

    [Test]
    public void DeletedLaterTaskAndItsReusedSlotAreSkipped()
    {
        var (pool, scheduler) = Create(); var output = new List<long>(); var a = pool.Create("Worker"); var b = pool.Create("Worker"); long replacement = 0;
        var slot = pool.LocationOf(b);
        scheduler.Bind("Worker", "run", (id, _) => { output.Add(id); if (replacement == 0) { pool.Delete(b); replacement = pool.Create("Worker"); pool.Start(replacement); } });
        scheduler.Bind("Worker", "send", (id, _) => output.Add(id)); pool.Start(a); pool.Start(b); scheduler.Enqueue(b, "send");
        scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { a })); Assert.That(pool.LocationOf(replacement), Is.EqualTo(slot));
        scheduler.Tick(); Assert.That(output, Is.EqualTo(new[] { a, a, replacement }));
    }

    [Test]
    public void ActiveDeletionWithholdsSlotUntilCallbackFinallyCompletes()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker"); var slot = pool.LocationOf(id); long during = 0;
        scheduler.Bind("Worker", "run", (target, _) => { try { pool.Delete(target); } finally { during = pool.Create("Worker"); } });
        pool.Start(id); scheduler.Tick(); Assert.That(pool.LocationOf(during), Is.Not.EqualTo(slot));
        Assert.That(pool.LocationOf(pool.Create("Worker")), Is.EqualTo(slot));
    }

    [Test]
    public void NotificationDeletionSkipsRemainingNotificationsAndRun()
    {
        var (pool, scheduler) = Create(); var output = new List<string>(); var id = pool.Create("Worker");
        scheduler.Bind("Worker", "remove", (target, _) => { output.Add("delete"); pool.Delete(target); });
        scheduler.Bind("Worker", "send", (_, _) => output.Add("send")); scheduler.Bind("Worker", "run", (_, _) => output.Add("run"));
        pool.Start(id); scheduler.Enqueue(id, "remove"); scheduler.Enqueue(id, "send"); scheduler.Tick();
        Assert.That(output, Is.EqualTo(new[] { "delete" }));
    }

    [Test]
    public void DeleteClearsPendingArgumentsImmediately()
    {
        var pool = new AloeTaskPool(_ => true); pool.RegisterType("Worker"); var scheduler = new AloeTaskScheduler(pool);
        scheduler.Bind("Worker", "send", (_, _) => { }, EnumValueKind.Object); var id = pool.Create("Worker");
        scheduler.Enqueue(id, "send", AloeValue.FromObject(5)); pool.Delete(id);
        Assert.That(scheduler.GetObjectRoots(), Is.Empty);
        Assert.Throws<InvalidReferenceException>(() => scheduler.Enqueue(id, "send", AloeValue.Null));
    }

    [Test]
    public void QueuedArgumentsAreCopiedAndDuplicateObjectRootsArePreserved()
    {
        var pool = new AloeTaskPool(_ => true); pool.RegisterType("Worker"); var scheduler = new AloeTaskScheduler(pool);
        long received = 0; scheduler.Bind("Worker", "send", (_, args) => received = args[0].AsObjectId, EnumValueKind.Object, EnumValueKind.Object);
        var id = pool.Create("Worker"); var args = new[] { AloeValue.FromObject(5), AloeValue.FromObject(5) };
        scheduler.Enqueue(id, "send", args); args[0] = AloeValue.FromObject(9);
        Assert.That(scheduler.GetObjectRoots(), Is.EqualTo(new long[] { 5, 5 })); scheduler.Tick();
        Assert.That(received, Is.EqualTo(5)); Assert.That(scheduler.GetObjectRoots(), Is.Empty);
    }

    [Test]
    public void SnapshotAndActiveArgumentsRetainRootsUntilEntryFinishes()
    {
        var pool = new AloeTaskPool(_ => true); pool.RegisterType("Worker"); var scheduler = new AloeTaskScheduler(pool);
        scheduler.Bind("Worker", "send", (id, _) => { Assert.That(scheduler.GetObjectRoots(), Is.EquivalentTo(new long[] { 5, 7 })); pool.Delete(id); Assert.That(scheduler.GetObjectRoots(), Is.EqualTo(new long[] { 5 })); }, EnumValueKind.Object);
        var id = pool.Create("Worker"); scheduler.Enqueue(id, "send", AloeValue.FromObject(5)); scheduler.Enqueue(id, "send", AloeValue.FromObject(7));
        scheduler.Tick(); Assert.That(scheduler.GetObjectRoots(), Is.Empty);
    }

    [TestCase("run")]
    [TestCase("start")]
    [TestCase("stop")]
    public void ReservedMethodsCannotBeQueued(string method)
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker"); Assert.Throws<VmException>(() => scheduler.Enqueue(id, method));
    }

    [Test]
    public void NotificationValidatesSignatureAndLiveObjectArguments()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker");
        scheduler.Bind("Worker", "send", (_, _) => { }, EnumValueKind.Object);
        Assert.Throws<VmException>(() => scheduler.Enqueue(id, "missing"));
        Assert.Throws<VmException>(() => scheduler.Enqueue(id, "send"));
        Assert.Throws<InvalidReferenceException>(() => scheduler.Enqueue(id, "send", AloeValue.FromObject(123)));
        scheduler.Enqueue(id, "send", AloeValue.Null); scheduler.Tick();
    }

    [TestCase(EnumValueKind.Unknown)]
    [TestCase(EnumValueKind.Struct)]
    [TestCase(EnumValueKind.Null)]
    public void UnsupportedMethodParameterKindsAreRejected(EnumValueKind kind)
    {
        var (_, scheduler) = Create(); Assert.Throws<VmException>(() => scheduler.Bind("Worker", "send", (_, _) => { }, kind));
    }

    [Test]
    public void BindingValidatesTypeReservedNamesAndDuplicateMethods()
    {
        var (_, scheduler) = Create();
        Assert.Throws<VmException>(() => scheduler.Bind("Unknown", "send", (_, _) => { }));
        Assert.Throws<VmException>(() => scheduler.Bind("Worker", "start", (_, _) => { }));
        Assert.Throws<VmException>(() => scheduler.Bind("Worker", "run", (_, _) => { }, EnumValueKind.Int));
        scheduler.Bind("Worker", "run", (_, _) => { }); Assert.Throws<VmException>(() => scheduler.Bind("Worker", "run", (_, _) => { }));
    }

    [Test]
    public void ScalarArgumentDomainAndKindAreCheckedBeforeEnqueue()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker"); scheduler.Bind("Worker", "send", (_, _) => { }, EnumValueKind.Int);
        Assert.Throws<VmException>(() => scheduler.Enqueue(id, "send", AloeValue.FromBool(true)));
        Assert.Throws<VmException>(() => scheduler.Enqueue(id, "send", AloeValue.FromInt(long.MaxValue)));
        Assert.That(scheduler.GetObjectRoots(), Is.Empty);
    }

    [Test]
    public void UnhandledNotificationFailureIsReportedAtTickAndFaultsUntilReset()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker"); var exception = new InvalidOperationException("task failure"); var calls = 0;
        scheduler.Bind("Worker", "send", (_, _) => throw exception); scheduler.Bind("Worker", "run", (_, _) => calls++);
        pool.Start(id); Assert.DoesNotThrow(() => scheduler.Enqueue(id, "send"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => scheduler.Tick()), Is.SameAs(exception));
        Assert.That(scheduler.LastFailure, Is.EqualTo(new AloeTaskFailure(id, "Worker", "send", "InvalidOperationException", "task failure")));
        Assert.That(calls, Is.Zero); Assert.Throws<VmException>(() => scheduler.Tick()); Assert.Throws<VmException>(() => scheduler.Enqueue(id, "send"));
        scheduler.Reset(); Assert.That(scheduler.LastFailure, Is.Null); var fresh = pool.Create("Worker"); pool.Start(fresh); scheduler.Tick(); Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void NestedTickCanBeCaughtSynchronouslyAndOuterTickCompletes()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker"); var count = 0;
        scheduler.Bind("Worker", "run", (_, _) => { Assert.Throws<VmException>(() => scheduler.Tick()); count++; }); pool.Start(id);
        scheduler.Tick(); Assert.That(count, Is.EqualTo(1)); Assert.That(scheduler.LastFailure, Is.Null);
    }

    [Test]
    public void ResetDuringTaskEntryIsRejectedWithoutLosingLiveTasks()
    {
        var (pool, scheduler) = Create(); var id = pool.Create("Worker");
        scheduler.Bind("Worker", "run", (_, _) => Assert.Throws<InvalidOperationException>(() => scheduler.Reset())); pool.Start(id);
        scheduler.Tick(); Assert.That(pool.IsLive(id), Is.True); scheduler.Reset(); Assert.That(pool.IsLive(id), Is.False);
    }

    [Test]
    public void VmTickRunsTaskPhaseBeforeVolatileCommit()
    {
        var vm = Vm(); var obj = vm.Heap.Allocate(new byte[8], fieldCount: 1); vm.Heap.SetField(obj, 0, AloeValue.FromInt(1));
        vm.TaskPool.RegisterType("Worker"); var id = vm.TaskPool.Create("Worker");
        vm.TaskScheduler.Bind("Worker", "run", (_, _) => { vm.Heap.SetVolatileField(obj, 0, AloeValue.FromInt(2)); Assert.That(vm.Heap.GetCommittedField(obj, 0).AsInt, Is.EqualTo(1)); });
        vm.TaskPool.Start(id); vm.Tick(); Assert.That(vm.Heap.GetCommittedField(obj, 0).AsInt, Is.EqualTo(2));
    }

    [Test]
    public void VmTaskFailureSkipsCommitAndPersistsUntilRuntimeReset()
    {
        var vm = Vm(); var obj = vm.Heap.Allocate(new byte[8], fieldCount: 1); vm.Heap.SetField(obj, 0, AloeValue.FromInt(1));
        vm.TaskPool.RegisterType("Worker"); var id = vm.TaskPool.Create("Worker"); var exception = new InvalidOperationException("run fault");
        vm.TaskScheduler.Bind("Worker", "run", (_, _) => { vm.Heap.SetVolatileField(obj, 0, AloeValue.FromInt(2)); throw exception; }); vm.TaskPool.Start(id);
        Assert.That(Assert.Throws<InvalidOperationException>(() => vm.Tick()), Is.SameAs(exception));
        Assert.That(vm.Heap.GetCommittedField(obj, 0).AsInt, Is.EqualTo(1)); Assert.That(vm.LastTaskFailure?.MethodName, Is.EqualTo("run"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => vm.Tick()), Is.SameAs(exception));
        vm.RunFromEntryPoint(); vm.Pop(); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void VmGcRetainsNotificationArgumentsAndReleasesThemAfterDelivery()
    {
        var vm = Vm(); var obj = vm.Heap.Allocate(new byte[8]); vm.Heap.SetDestructionCandidate(obj);
        vm.TaskPool.RegisterType("Worker"); var id = vm.TaskPool.Create("Worker");
        vm.TaskScheduler.Bind("Worker", "send", (_, args) => Assert.That(vm.Heap.ContainsObject(args[0].AsObjectId), Is.True), EnumValueKind.Object);
        vm.TaskScheduler.Enqueue(id, "send", AloeValue.FromObject(obj)); vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.True);
        vm.Tick(); vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.False);
    }

    [Test]
    public void VmResetClearsNotificationsButPreservingSubmissionKeepsThem()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var id = vm.TaskPool.Create("Worker"); var count = 0;
        vm.TaskScheduler.Bind("Worker", "send", (_, _) => count++); vm.TaskScheduler.Enqueue(id, "send");
        var module = new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"); vm.ReplaceModule(module); var entry = module.Functions[module.EntryPointIndex];
        vm.RunFromEntryPoint(entry.EntryIp, new AloeValue[entry.LocalCount], true); vm.Pop(); vm.Tick(); Assert.That(count, Is.EqualTo(1));
        vm.TaskScheduler.Enqueue(id, "send"); vm.RunFromEntryPoint(); vm.Pop(); vm.Tick(); Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void TaskQueuedObjectCallRunsInSameTickAndCommitsAfterTaskPhase()
    {
        var module = new AloeCompiler().Compile("class Worker { field n: int; public async method write(): void { this.n = 9; print(9); } } function main(args: string[]): int { return 0; }");
        var vm = new AloeVm(module); var output = new List<string>(); vm.OutputWriter = output.Add;
        var obj = vm.Heap.Allocate(new byte[8], fieldCount: 1, typeName: "Worker"); vm.Heap.SetField(obj, 0, AloeValue.FromInt(1));
        var method = Array.FindIndex(module.Functions.ToArray(), function => function.Name.EndsWith(".write", StringComparison.Ordinal));
        Assert.That(method, Is.GreaterThanOrEqualTo(0)); vm.TaskPool.RegisterType("Driver"); var id = vm.TaskPool.Create("Driver");
        vm.TaskScheduler.Bind("Driver", "run", (_, _) =>
        {
            output.Add("task"); vm.Push(AloeValue.FromObject(obj)); vm.Push(AloeValue.FromInt(method)); vm.Push(AloeValue.FromInt(0));
            vm.InvokeSyscall(EnumSyscall.InstanceAsyncEnqueue); Assert.That(output, Is.EqualTo(new[] { "task" }));
        });
        vm.TaskPool.Start(id); vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "task", "9" }));
        Assert.That(vm.Heap.GetCommittedField(obj, 0).AsInt, Is.EqualTo(9));
    }

    [Test]
    public void TaskFailureDiscardsObjectCallsQueuedEarlierInTaskPhase()
    {
        var module = new AloeCompiler().Compile("class Worker { public async method emit(): void { print(9); } } function main(args: string[]): int { return 0; }");
        var vm = new AloeVm(module); var output = new List<string>(); vm.OutputWriter = output.Add;
        var obj = vm.Heap.Allocate(new byte[8], typeName: "Worker");
        var method = Array.FindIndex(module.Functions.ToArray(), function => function.Name.EndsWith(".emit", StringComparison.Ordinal));
        Assert.That(method, Is.GreaterThanOrEqualTo(0)); vm.TaskPool.RegisterType("Driver"); var id = vm.TaskPool.Create("Driver");
        vm.TaskScheduler.Bind("Driver", "run", (_, _) =>
        {
            vm.Push(AloeValue.FromObject(obj)); vm.Push(AloeValue.FromInt(method)); vm.Push(AloeValue.FromInt(0));
            vm.InvokeSyscall(EnumSyscall.InstanceAsyncEnqueue); throw new InvalidOperationException("stop");
        });
        vm.TaskPool.Start(id); Assert.Throws<InvalidOperationException>(() => vm.Tick()); Assert.That(output, Is.Empty);
        vm.TaskPool.Delete(id); vm.RunFromEntryPoint(); vm.Pop(); vm.Tick(); Assert.That(output, Is.Empty);
    }

    [Test]
    public void VmRejectsNestedTickDuringTaskCallback()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var id = vm.TaskPool.Create("Worker");
        vm.TaskScheduler.Bind("Worker", "run", (_, _) => Assert.Throws<VmException>(() => vm.Tick()));
        vm.TaskPool.Start(id); vm.Tick(); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    private static (AloeTaskPool Pool, AloeTaskScheduler Scheduler) Create()
    {
        var pool = new AloeTaskPool(_ => false, 2); pool.RegisterType("Worker"); return (pool, new AloeTaskScheduler(pool));
    }

    private static AloeVm Vm()
    {
        var settings = new AloeVmSettings(); settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        return new AloeVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }
}
