using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskEntryTests
{
    [Test]
    public void ModuleTaskMetadataRoundTripsAndRegistersWithoutConstructingInstances()
    {
        var module = Bound("construct(value: int) { print(value); } public async method run(): void { print(9); }", new[] { EnumValueKind.Int });
        var copy = AloeBcCodec.Read(AloeBcCodec.Write(module));
        Assert.That(copy.TaskTypes.Single().Name, Is.EqualTo("Worker")); Assert.That(copy.TaskTypes[0].ConstructorParameters, Is.EqualTo(new[] { EnumValueKind.Int }));
        var vm = new AloeVm(copy); Assert.That(vm.TaskPool.HasType("Worker"), Is.True); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [Test]
    public void ConstructorRunsSynchronouslyAndStartsStoppedBeforeFirstRun()
    {
        var vm = new AloeVm(Bound("construct(value: int) { print(value); } public async method run(): void { print(9); }", new[] { EnumValueKind.Int }));
        var output = new List<string>(); vm.OutputWriter = output.Add;
        var task = Construct(vm, AloeValue.FromInt(42)); Assert.That(output, Is.EqualTo(new[] { "42" }));
        Assert.That(vm.TaskPool.StateOf(task.AsTaskId), Is.EqualTo(AloeTaskState.Stopped)); vm.Tick(); Assert.That(output.Count, Is.EqualTo(1));
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "42", "9" }));
    }

    [Test]
    public void ConstructorFailureRunsFinallyAndReturnsSlotWithoutReturningHandle()
    {
        var vm = new AloeVm(Bound("construct() { try { var bad=1/0; } finally { print(7); } } public async method run(): void { }"));
        var output = new List<string>(); vm.OutputWriter = output.Add;
        Assert.Throws<ZeroDivisionException>(() => Construct(vm)); Assert.That(output, Is.EqualTo(new[] { "7" }));
        Assert.That(vm.TaskPool.LiveCount, Is.Zero); Assert.That(vm.ValueStack.Count, Is.Zero); Assert.That(vm.LastTaskFailure, Is.Null);
        vm.Push(AloeValue.FromString("Worker")); vm.InvokeSyscall(EnumSyscall.TaskAllocate); var bare = vm.Pop();
        Assert.That(bare.AsTaskId, Is.EqualTo(2)); Assert.That(vm.TaskPool.LocationOf(bare.AsTaskId).SlotIndex, Is.Zero);
    }

    [Test]
    public void ConstructorFailureReleasesPooledObjectRootsAndPreservesSharedEffects()
    {
        var vm = new AloeVm(Bound("construct(value: object) { print(7); var bad=1/0; } public async method run(): void { }", new[] { EnumValueKind.Object }, new[] { EnumValueKind.Object }));
        var obj = vm.Heap.Allocate(new byte[8], fieldCount: 1); vm.Heap.SetField(obj, 0, AloeValue.FromInt(1)); vm.Heap.SetExternalReferenceCount(obj, 1);
        vm.OutputWriter = _ =>
        {
            var task = vm.CurrentFrame!.Locals[0]; vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromObject(obj)); vm.InvokeSyscall(EnumSyscall.TaskFieldSet);
            vm.Heap.SetField(obj, 0, AloeValue.FromInt(9));
        };
        Assert.Throws<ZeroDivisionException>(() => Construct(vm, AloeValue.FromObject(obj))); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
        Assert.That(vm.TaskPool.GetObjectRoots(), Is.Empty); Assert.That(vm.Heap.GetField(obj, 0).AsInt, Is.EqualTo(9));
        vm.Heap.SetExternalReferenceCount(obj, 0); vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.False);
    }

    [Test]
    public void ConstructorFailurePropagatesToOriginalCallerCatchAfterCleanup()
    {
        var source = "class Worker { construct() { var bad=1/0; } public async method run(): void { } } function main(args: string[]): int { try { print(1); } catch(ZeroDivisionException) { print(2); } return 0; }";
        var vm = new AloeVm(BindSource(source)); var output = new List<string>();
        vm.OutputWriter = line => { if (line == "1") Construct(vm); else output.Add(line); };
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero); Assert.That(output, Is.EqualTo(new[] { "2" })); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [Test]
    public void FailedConstructionDoesNotDiscardAlreadyQueuedObjectCalls()
    {
        var source = "class Target { public async method emit(): void { print(7); } } class Worker { construct(target: Target) { target.emit(); var bad=1/0; } public async method run(): void { } } function main(args: string[]): int { return 0; }";
        var vm = new AloeVm(BindSource(source, new[] { EnumValueKind.Object })); var output = new List<string>(); vm.OutputWriter = output.Add;
        var obj = vm.Heap.Allocate(new byte[8], typeName: "Target");
        Assert.Throws<ZeroDivisionException>(() => Construct(vm, AloeValue.FromObject(obj))); Assert.That(output, Is.Empty);
        vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "7" }));
    }

    [Test]
    public void ConstructorCanReceiveOrdinaryObjectAndQueueItsExistingAsyncMethod()
    {
        var source = "class Target { public async method emit(): void { print(7); } } class Worker { construct(target: Target) { target.emit(); } public async method run(): void { } } function main(args: string[]): int { return 0; }";
        var vm = new AloeVm(BindSource(source, new[] { EnumValueKind.Object })); var output = new List<string>(); vm.OutputWriter = output.Add;
        var obj = vm.Heap.Allocate(new byte[8], typeName: "Target"); var task = Construct(vm, AloeValue.FromObject(obj));
        Assert.That(task.IsTask, Is.True); Assert.That(output, Is.Empty); vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "7" }));
    }

    [Test]
    public void ConstructorArgumentKindsAndCountAreCheckedBeforeAllocation()
    {
        var vm = new AloeVm(Bound("construct(value: int) { } public async method run(): void { }", new[] { EnumValueKind.Int }));
        Assert.Throws<VmException>(() => Construct(vm)); Assert.Throws<VmException>(() => Construct(vm, AloeValue.FromBool(true)));
        Assert.Throws<VmException>(() => Construct(vm, AloeValue.FromTask(1))); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
        Assert.That(Construct(vm, AloeValue.FromInt(1)).AsTaskId, Is.EqualTo(1));
    }

    [Test]
    public void ExternalTaskMethodIsDeferredEvenWhileStopped()
    {
        var vm = new AloeVm(Bound("public async method run(): void { } public async method send(value: int): void { print(value); }", methods: new[] { ("send", new[] { EnumValueKind.Int }) }));
        var output = new List<string>(); vm.OutputWriter = output.Add; var task = Construct(vm); Invoke(vm, task, "send", AloeValue.FromInt(42));
        Assert.That(output, Is.Empty); vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void RuntimeIdentityMakesLocalSelfAliasCallSynchronous()
    {
        var vm = new AloeVm(SelfModule()); var output = new List<string>(); vm.OutputWriter = output.Add; var task = Construct(vm);
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "inner", "after" }));
    }

    [Test]
    public void ExternalNotificationRunsBeforeCapturedRunAndItsSynchronousSelfCall()
    {
        var vm = new AloeVm(SelfModule()); var output = new List<string>(); vm.OutputWriter = output.Add; var task = Construct(vm);
        Invoke(vm, task, "inner"); vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick();
        Assert.That(output, Is.EqualTo(new[] { "inner", "inner", "after" }));
    }

    [Test]
    public void UncaughtNotificationFaultIsReportedOnlyAtTick()
    {
        var vm = new AloeVm(Bound("public async method run(): void { } public async method fail(): void { var bad=1/0; }", methods: new[] { ("fail", Array.Empty<EnumValueKind>()) }));
        var task = Construct(vm); Assert.DoesNotThrow(() => Invoke(vm, task, "fail")); Assert.Throws<ZeroDivisionException>(() => vm.Tick());
        Assert.That(vm.LastTaskFailure?.TaskId, Is.EqualTo(task.AsTaskId)); Assert.That(vm.LastTaskFailure?.MethodName, Is.EqualTo("fail"));
        Assert.Throws<ZeroDivisionException>(() => vm.Tick()); vm.RunFromEntryPoint(); vm.Pop(); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void TaskEntryCaughtExceptionAndFinallyPreserveExistingSemantics()
    {
        var vm = new AloeVm(Bound("public async method run(): void { try { var bad=1/0; } catch(error: Exception) { print(error.TypeName); } finally { print(7); } }"));
        var output = new List<string>(); vm.OutputWriter = output.Add; var task = Construct(vm); vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick();
        Assert.That(output, Is.EqualTo(new[] { "ZeroDivisionException", "7" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void SynchronousSelfCallFailureReturnsToTaskCallerCatchAndFinally()
    {
        var vm = new AloeVm(Bound("public async method run(): void { try { print(1); } catch(ZeroDivisionException) { print(2); } finally { print(3); } } public async method fail(): void { var bad=1/0; }", methods: new[] { ("fail", Array.Empty<EnumValueKind>()) }));
        var output = new List<string>(); vm.OutputWriter = line => { if (line == "1") Invoke(vm, vm.CurrentFrame!.Locals[0], "fail"); else output.Add(line); };
        var task = Construct(vm); vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick();
        Assert.That(output, Is.EqualTo(new[] { "2", "3" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [TestCase("run")]
    [TestCase("start")]
    [TestCase("stop")]
    public void ReservedMethodsCannotBeInvokedAsNotifications(string method)
    {
        var vm = new AloeVm(SelfModule()); var task = Construct(vm); Assert.Throws<VmException>(() => Invoke(vm, task, method));
    }

    [Test]
    public void InvalidTargetAndUnknownMethodAreRejectedBeforeNotification()
    {
        var vm = new AloeVm(SelfModule()); var task = Construct(vm);
        Assert.Throws<VmException>(() => Invoke(vm, task, "missing")); vm.TaskPool.Delete(task.AsTaskId);
        Assert.Throws<InvalidReferenceException>(() => Invoke(vm, task, "inner"));
    }

    [Test]
    public void TaskModuleReplacementIsExplicitlyDeferred()
    {
        var module = SelfModule(); var vm = new AloeVm(module); Assert.Throws<InvalidOperationException>(() => vm.ReplaceModule(module));
        var ordinary = new AloeCompiler().Compile("function main(args: string[]): int { return 0; }");
        Assert.Throws<InvalidOperationException>(() => new AloeVm(ordinary).ReplaceModule(module));
    }

    [Test]
    public void DirectWasmCannotSilentlyDiscardTaskMetadata()
        => Assert.Throws<NotSupportedException>(() => new Aloe.Aloe2Wasm.AloeWasmCompiler().Compile(SelfModule()));

    [Test]
    public void VmLocalConstructorParentsRemainGcRoots()
    {
        var source = "class Worker { construct() { print(7); } public async method run(): void { } } function main(args: string[]): int { let parent: object = new ObjectHolder(); print(1); return 0; } class ObjectHolder { }";
        var vm = new AloeVm(BindSource(source)); long obj = 0;
        vm.OutputWriter = line =>
        {
            if (line == "1") { obj = vm.Heap.ObjectTable.Values.Single(value => value.TypeName == "ObjectHolder").ObjectId; Construct(vm); }
            else { vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.True); }
        };
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero); Assert.That(obj, Is.GreaterThan(0));
    }

    [Test]
    public void SourceTaskExecutionEmitsRegisteredTaskMetadata()
        => Assert.That(new AloeCompiler().Compile("task Worker { method run(): void { } } function main(args: string[]): int { return 0; }").TaskTypes.Single().Name, Is.EqualTo("Worker"));

    [Test]
    public void TaskEntryHonorsCooperativeHostHaltWithoutExecutingRemainingBody()
    {
        var vm = new AloeVm(Bound("public async method run(): void { print(1); print(2); }")); var output = new List<string>();
        vm.OutputWriter = line => { output.Add(line); vm.RequestHalt(); }; var task = Construct(vm); vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart);
        vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "1" })); Assert.That(vm.LastTaskFailure, Is.Null);
    }

    [Test]
    public void SelfDeleteUnwindIsExplicitlyRejectedBeforeInvalidation()
    {
        var vm = new AloeVm(Bound("public async method run(): void { print(1); print(2); }")); var output = new List<string>();
        vm.OutputWriter = line => { output.Add(line); vm.Push(vm.CurrentFrame!.Locals[0]); vm.InvokeSyscall(EnumSyscall.TaskDelete); };
        var task = Construct(vm); vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart);
        var error = Assert.Throws<VmException>(() => vm.Tick()); Assert.That(error!.Message, Does.Contain("not implemented"));
        Assert.That(vm.TaskPool.IsLive(task.AsTaskId), Is.True); Assert.That(output, Is.EqualTo(new[] { "1" }));
    }

    [Test]
    public void ModuleTaskOrderControlsSchedulingAcrossTypes()
    {
        var code = new[] { new Instruction(EnumOpcode.Return), new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.Return),
            new Instruction(EnumOpcode.PushConst, 1), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.Return) };
        var types = new[] { new AloeTaskTypeInfo("Second", Array.Empty<EnumValueKind>(), null, Array.Empty<EnumValueKind>(), new[] { new AloeTaskMethodInfo("run", 2, Array.Empty<EnumValueKind>()) }),
            new AloeTaskTypeInfo("First", Array.Empty<EnumValueKind>(), null, Array.Empty<EnumValueKind>(), new[] { new AloeTaskMethodInfo("run", 1, Array.Empty<EnumValueKind>()) }) };
        var module = new Module(new[] { AloeValue.FromInt(1), AloeValue.FromInt(2) }, code, new[] { new FunctionInfo("main", 0, 0, 0), new FunctionInfo("First.run", 1, 1, 1), new FunctionInfo("Second.run", 4, 1, 1) }, 0, types);
        var vm = new AloeVm(AloeBcCodec.Read(AloeBcCodec.Write(module))); var output = new List<string>(); vm.OutputWriter = output.Add;
        foreach (var name in new[] { "First", "Second" }) { vm.Push(AloeValue.FromString(name)); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.TaskConstruct); vm.InvokeSyscall(EnumSyscall.TaskStart); }
        vm.Tick(); Assert.That(output, Is.EqualTo(new[] { "2", "1" }));
    }

    private static Module Bound(string members, EnumValueKind[]? constructorParameters = null, EnumValueKind[]? fields = null,
        (string Name, EnumValueKind[] Kinds)[]? methods = null)
        // Task entries use synchronous bytecode; metadata supplies external notification routing.
        => BindSource("class Worker { " + members.Replace("public async method", "private method", StringComparison.Ordinal) + " } function main(args: string[]): int { return 0; }", constructorParameters, fields, methods);

    private static Module BindSource(string source, EnumValueKind[]? constructorParameters = null, EnumValueKind[]? fields = null,
        (string Name, EnumValueKind[] Kinds)[]? methods = null)
    {
        var module = new AloeCompiler().Compile(source);
        int Index(string name) => Array.FindIndex(module.Functions.ToArray(), function => function.Name == "Worker." + name);
        var constructor = Index("construct"); var entries = new List<AloeTaskMethodInfo> { new("run", Index("run"), Array.Empty<EnumValueKind>()) };
        foreach (var method in methods ?? Array.Empty<(string, EnumValueKind[])>()) entries.Add(new(method.Name, Index(method.Name), method.Kinds));
        return new Module(module.Constants, module.Code, module.Functions, module.EntryPointIndex, new[] { new AloeTaskTypeInfo("Worker", fields ?? Array.Empty<EnumValueKind>(), constructor < 0 ? null : constructor, constructorParameters ?? Array.Empty<EnumValueKind>(), entries) });
    }

    private static Module SelfModule()
    {
        var constants = new[] { AloeValue.FromString("inner"), AloeValue.FromInt(0), AloeValue.FromString("after") };
        var code = new[] { new Instruction(EnumOpcode.Return), new Instruction(EnumOpcode.LoadLocal, 0), new Instruction(EnumOpcode.StoreLocal, 1),
            new Instruction(EnumOpcode.LoadLocal, 1), new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.PushConst, 1),
            new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.TaskInvoke), new Instruction(EnumOpcode.PushConst, 2), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.Return),
            new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.Print), new Instruction(EnumOpcode.Return) };
        return new Module(constants, code, new[] { new FunctionInfo("main", 0, 0, 0), new FunctionInfo("Worker.run", 1, 1, 2), new FunctionInfo("Worker.inner", 10, 1, 1) }, 0,
            new[] { new AloeTaskTypeInfo("Worker", Array.Empty<EnumValueKind>(), null, Array.Empty<EnumValueKind>(), new[] { new AloeTaskMethodInfo("run", 1, Array.Empty<EnumValueKind>()), new AloeTaskMethodInfo("inner", 2, Array.Empty<EnumValueKind>()) }) });
    }

    private static AloeValue Construct(AloeVm vm, params AloeValue[] arguments)
    {
        foreach (var argument in arguments) vm.Push(argument); vm.Push(AloeValue.FromString("Worker")); vm.Push(AloeValue.FromInt(arguments.Length));
        vm.InvokeSyscall(EnumSyscall.TaskConstruct); return vm.Pop();
    }
    private static void Invoke(AloeVm vm, AloeValue task, string method, params AloeValue[] arguments)
    {
        vm.Push(task); foreach (var argument in arguments) vm.Push(argument); vm.Push(AloeValue.FromString(method)); vm.Push(AloeValue.FromInt(arguments.Length)); vm.InvokeSyscall(EnumSyscall.TaskInvoke);
    }
}
