using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskValueTests
{
    [Test]
    public void TaskValuesHaveDistinctPositiveInt64Identities()
    {
        var value = AloeValue.FromTask(long.MaxValue);
        Assert.That(value.Kind, Is.EqualTo(EnumValueKind.Task)); Assert.That(value.IsTask, Is.True);
        Assert.That(value.IsObject || value.IsNumber || value.IsInt || value.IsNull, Is.False);
        Assert.That(value.AsTaskId, Is.EqualTo(long.MaxValue)); Assert.That(value.ToString(), Is.EqualTo("task#9223372036854775807"));
        Assert.Throws<VmException>(() => { _ = value.AsObjectId; }); Assert.Throws<VmException>(() => { _ = value.AsInt; });
        Assert.Throws<VmException>(() => { _ = AloeValue.FromObject(1).AsTaskId; });
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(long.MinValue)]
    public void NonPositiveTaskIdentitiesAreRejected(long identity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => AloeValue.FromTask(identity));

    [Test]
    public void LowLevelAllocationAndFieldAccessUsePoolWithoutOrdinaryHeapAllocation()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker", EnumValueKind.Int, EnumValueKind.Object); var before = vm.Heap.ObjectTable.Count;
        var value = Allocate(vm); Assert.That(value.IsTask, Is.True); Assert.That(vm.TaskPool.StateOf(value.AsTaskId), Is.EqualTo(AloeTaskState.Stopped));
        vm.Push(value); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromInt(42)); vm.InvokeSyscall(EnumSyscall.TaskFieldSet);
        vm.Push(value); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.TaskFieldGet); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
        Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(before));
    }

    [Test]
    public void TaskAndObjectWithSameNumericIdentityCannotAlias()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker", EnumValueKind.Int); var task = Allocate(vm);
        var obj = vm.Heap.Allocate(new byte[8]); Assert.That(task.AsTaskId, Is.EqualTo(obj));
        vm.Push(AloeValue.FromObject(obj)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskStart));
        vm.Push(task); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(EnumSyscall.ReferenceRequireLive));
        Assert.That(vm.Heap.ContainsObject(obj) && vm.TaskPool.IsLive(task.AsTaskId), Is.True);
    }

    [Test]
    public void DeleteInvalidatesAliasesAndLifoReuseDoesNotReviveThem()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var task = Allocate(vm); var slot = vm.TaskPool.LocationOf(task.AsTaskId);
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskDelete); var replacement = Allocate(vm);
        Assert.That(vm.TaskPool.LocationOf(replacement.AsTaskId), Is.EqualTo(slot));
        vm.Push(task); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(EnumSyscall.TaskRequireLive));
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskDelete); vm.Push(AloeValue.Null); vm.InvokeSyscall(EnumSyscall.TaskDelete);
        Assert.That(vm.TaskPool.IsLive(replacement.AsTaskId), Is.True);
    }

    [TestCase(EnumSyscall.TaskStart)]
    [TestCase(EnumSyscall.TaskStop)]
    [TestCase(EnumSyscall.TaskRequireLive)]
    public void DeletedAndNullTaskOperationsRejectInvalidReferences(EnumSyscall syscall)
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var task = Allocate(vm); vm.TaskPool.Delete(task.AsTaskId);
        vm.Push(task); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(syscall));
        vm.Push(AloeValue.Null); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(syscall));
        vm.Push(AloeValue.FromInt(1)); Assert.Throws<VmException>(() => vm.InvokeSyscall(syscall));
    }

    [Test]
    public void LifecycleSyscallsDriveExistingScheduler()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var task = Allocate(vm); var runs = 0;
        vm.TaskScheduler.Bind("Worker", "run", (_, _) => runs++);
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStart); vm.Tick(); Assert.That(runs, Is.EqualTo(1));
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskStop); vm.Tick(); Assert.That(runs, Is.EqualTo(1));
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskRequireLive); Assert.That(vm.Pop().AsTaskId, Is.EqualTo(task.AsTaskId));
    }

    [TestCase(-1L)]
    [TestCase(long.MaxValue)]
    public void FieldIndexMustFitNonNegativeInt32(long index)
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker", EnumValueKind.Int); var task = Allocate(vm);
        vm.Push(task); vm.Push(AloeValue.FromInt(index)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskFieldGet));
    }

    [Test]
    public void FieldKindDomainAndTransferConstraintsAreRetained()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker", EnumValueKind.Int, EnumValueKind.Object); var task = Allocate(vm);
        vm.Push(task); vm.Push(AloeValue.FromInt(0)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskFieldGet));
        vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromInt(long.MaxValue)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskFieldSet));
        vm.Push(task); vm.Push(AloeValue.FromInt(1)); vm.Push(task); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(EnumSyscall.TaskFieldSet));
        vm.Push(task); vm.Push(AloeValue.FromInt(2)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskFieldGet));
    }

    [Test]
    public void AllocationRequiresRegisteredTypeName()
    {
        var vm = Vm(); vm.Push(AloeValue.FromInt(1)); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskAllocate));
        vm.Push(AloeValue.FromString("Unknown")); Assert.Throws<VmException>(() => vm.InvokeSyscall(EnumSyscall.TaskAllocate)); Assert.That(vm.TaskPool.LiveCount, Is.Zero);
    }

    [Test]
    public void TaskIdentityCannotBeEmbeddedInModuleConstantOrSerialized()
    {
        var module = new Module(new[] { AloeValue.FromTask(1) }, new[] { new Instruction(EnumOpcode.Return) }, new[] { new FunctionInfo("main", 0, 0, 0) }, 0);
        Assert.Throws<VmException>(() => AloeBytecodeVerifier.Verify(module)); Assert.Throws<VmException>(() => AloeBcCodec.Write(module));
    }

    [Test]
    public void BytecodeRoundTripCanAllocateStoreAndReadTaskLocal()
    {
        var constants = new[] { AloeValue.FromString("Worker"), AloeValue.FromInt(0), AloeValue.FromInt(42) };
        var code = new[] { new Instruction(EnumOpcode.PushConst, 0), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.TaskAllocate),
            new Instruction(EnumOpcode.StoreLocal, 0), new Instruction(EnumOpcode.LoadLocal, 0), new Instruction(EnumOpcode.PushConst, 1),
            new Instruction(EnumOpcode.PushConst, 2), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.TaskFieldSet),
            new Instruction(EnumOpcode.LoadLocal, 0), new Instruction(EnumOpcode.PushConst, 1), new Instruction(EnumOpcode.Syscall, (int)EnumSyscall.TaskFieldGet), new Instruction(EnumOpcode.Return) };
        var function = new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), new[] { EnumValueKind.Task }, EnumValueKind.Int);
        var module = AloeBcCodec.Read(AloeBcCodec.Write(new Module(constants, code, new[] { function }, 0)));
        Assert.That(module.Functions[0].LocalTypes, Is.EqualTo(new[] { EnumValueKind.Task }));
        var vm = new AloeVm(module); vm.TaskPool.RegisterType("Worker", EnumValueKind.Int); vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.EqualTo(42));
    }

    [Test]
    public void BytecodeCreatedTaskRetainsOrdinaryObjectRootUntilDeletion()
    {
        var vm = Vm(); var obj = vm.Heap.Allocate(new byte[8]); vm.Heap.SetDestructionCandidate(obj);
        vm.TaskPool.RegisterType("Worker", EnumValueKind.Object); var task = Allocate(vm);
        vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromObject(obj)); vm.InvokeSyscall(EnumSyscall.TaskFieldSet);
        vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.True);
        vm.Push(task); vm.InvokeSyscall(EnumSyscall.TaskDelete); vm.RequireGc(); vm.FinishGc(); Assert.That(vm.Heap.ContainsObject(obj), Is.False);
    }

    [Test]
    public void RuntimeResetNeverRevivesTaskValue()
    {
        var vm = Vm(); vm.TaskPool.RegisterType("Worker"); var task = Allocate(vm); vm.RunFromEntryPoint(); vm.Pop();
        var replacement = Allocate(vm); Assert.That(replacement.AsTaskId, Is.GreaterThan(task.AsTaskId));
        vm.Push(task); Assert.Throws<InvalidReferenceException>(() => vm.InvokeSyscall(EnumSyscall.TaskRequireLive));
    }

    [Test]
    public void ExistingBytecodeAndValueIdentifiersStayFixed()
    {
        Assert.That((int)EnumValueKind.Struct, Is.EqualTo(10)); Assert.That((int)EnumValueKind.Task, Is.EqualTo(11));
        Assert.That((int)EnumSyscall.CaughtExceptionInfo, Is.EqualTo(52)); Assert.That((int)EnumSyscall.TaskAllocate, Is.EqualTo(53));
        Assert.That((int)EnumSyscall.TaskRequireLive, Is.EqualTo(59));
    }

    [TestCase(EnumValueKind.Int)]
    [TestCase(EnumValueKind.Byte)]
    [TestCase(EnumValueKind.Char)]
    [TestCase(EnumValueKind.Float)]
    [TestCase(EnumValueKind.Decimal)]
    [TestCase(EnumValueKind.Bool)]
    [TestCase(EnumValueKind.String)]
    public void FieldSyscallsPreserveEachScalarKind(EnumValueKind kind)
    {
        var value = kind switch
        {
            EnumValueKind.Int => AloeValue.FromInt(-42), EnumValueKind.Byte => AloeValue.FromByte(255), EnumValueKind.Char => AloeValue.FromChar('あ'),
            EnumValueKind.Float => AloeValue.FromFloat(1.5), EnumValueKind.Decimal => AloeValue.FromDecimal(1.25m), EnumValueKind.Bool => AloeValue.FromBool(true),
            _ => AloeValue.FromString("Task文字列")
        };
        var vm = Vm(); vm.TaskPool.RegisterType("Worker", kind); var task = Allocate(vm);
        vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.Push(value); vm.InvokeSyscall(EnumSyscall.TaskFieldSet);
        vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.TaskFieldGet); var actual = vm.Pop();
        Assert.That(actual.Kind, Is.EqualTo(kind)); Assert.That(actual, Is.EqualTo(value));
    }

    [Test]
    public void WorkerThreadTaskAllocationsAreSerializedWithoutCorruptingPool()
    {
        var module = new AloeCompiler().Compile("class Worker { public async method alloc(value: int): void { print(value); } } function main(args: string[]): int { var index=0; while(index < 128) { var worker=new Worker(); worker.alloc(index); index=index+1; } tick(); return 0; }");
        var vm = new AloeVm(module) { MaxVmThreads = 4 }; vm.TaskPool.RegisterType("Worker", EnumValueKind.Int);
        var identities = new System.Collections.Concurrent.ConcurrentBag<long>();
        vm.OutputWriter = line =>
        {
            var index = int.Parse(line, System.Globalization.CultureInfo.InvariantCulture);
            var task = Allocate(vm); identities.Add(task.AsTaskId);
            vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.Push(AloeValue.FromInt(index)); vm.InvokeSyscall(EnumSyscall.TaskFieldSet);
            vm.Push(task); vm.Push(AloeValue.FromInt(0)); vm.InvokeSyscall(EnumSyscall.TaskFieldGet); Assert.That(vm.Pop().AsInt, Is.EqualTo(index));
        };
        vm.RunFromEntryPoint(); Assert.That(vm.Pop().AsInt, Is.Zero);
        Assert.That(identities.Distinct().Count(), Is.EqualTo(128)); Assert.That(vm.TaskPool.LiveCount, Is.EqualTo(128));
    }

    [Test]
    public void DirectWasmRejectsTaskTypeMetadata()
    {
        var module = new Module(Array.Empty<AloeValue>(), new[] { new Instruction(EnumOpcode.Return) },
            new[] { new FunctionInfo("main", 0, Array.Empty<EnumValueKind>(), new[] { EnumValueKind.Task }, null) }, 0);
        Assert.Throws<NotSupportedException>(() => new Aloe.Aloe2Wasm.AloeWasmCompiler().Compile(module));
    }

    private static AloeValue Allocate(AloeVm vm) { vm.Push(AloeValue.FromString("Worker")); vm.InvokeSyscall(EnumSyscall.TaskAllocate); return vm.Pop(); }
    private static AloeVm Vm()
    {
        var settings = new AloeVmSettings(); settings.Logging.ConsoleEnabled = false; settings.Logging.FileEnabled = false;
        return new AloeVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"), settings);
    }
}
