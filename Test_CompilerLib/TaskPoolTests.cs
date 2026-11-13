using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.CommonLib.Exceptions;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class TaskPoolTests
{
    [Test]
    public void CreationStartsStoppedAndStartStopAreIdempotent()
    {
        var pool=Pool();var id=pool.Create("Worker");Assert.That(pool.StateOf(id),Is.EqualTo(AloeTaskState.Stopped));
        pool.Start(id);pool.Start(id);Assert.That(pool.StateOf(id),Is.EqualTo(AloeTaskState.Running));
        pool.Stop(id);pool.Stop(id);Assert.That(pool.StateOf(id),Is.EqualTo(AloeTaskState.Stopped));
    }

    [Test]
    public void DeletedAliasesCannotReachReusedSlot()
    {
        var pool=Pool();var id=pool.Create("Worker");var alias=id;var slot=pool.LocationOf(id);pool.Delete(id);
        var replacement=pool.Create("Worker");Assert.That(pool.LocationOf(replacement),Is.EqualTo(slot));
        Assert.That(replacement,Is.Not.EqualTo(alias));Assert.That(pool.IsLive(alias),Is.False);
        Assert.Throws<InvalidReferenceException>(()=>pool.Start(alias));Assert.Throws<InvalidReferenceException>(()=>pool.GetField(alias,0));
        Assert.That(pool.IsLive(replacement),Is.True);
    }

    [Test]
    public void DeletedSlotsAreReusedInLifoOrderWithoutCompaction()
    {
        var pool=Pool();var a=pool.Create("Worker");var b=pool.Create("Worker");var c=pool.Create("Worker");
        var aSlot=pool.LocationOf(a);var bSlot=pool.LocationOf(b);var cSlot=pool.LocationOf(c);pool.Delete(a);pool.Delete(c);
        Assert.That(pool.LocationOf(pool.Create("Worker")),Is.EqualTo(cSlot));
        Assert.That(pool.LocationOf(pool.Create("Worker")),Is.EqualTo(aSlot));Assert.That(pool.LocationOf(b),Is.EqualTo(bSlot));
    }

    [Test]
    public void ChunkGrowthPreservesReferencesAndFieldValues()
    {
        var pool=Pool();var ids=Enumerable.Range(0,40).Select(_=>pool.Create("Worker")).ToArray();
        for(var i=0;i<ids.Length;i++)pool.SetField(ids[i],0,AloeValue.FromInt(i));
        for(var i=0;i<ids.Length;i++)Assert.That(pool.GetField(ids[i],0).AsInt,Is.EqualTo(i));
        Assert.That(ids.Distinct().Count(),Is.EqualTo(40));Assert.That(pool.LiveCount,Is.EqualTo(40));
    }

    [Test]
    public void TypePoolsHaveIndependentFreeStacksAndDeclaredOrder()
    {
        var pool=Pool();pool.RegisterType("Enemy",EnumValueKind.Int);var enemy=pool.Create("Enemy");var worker=pool.Create("Worker");
        pool.Start(enemy);pool.Start(worker);Assert.That(pool.CaptureRunningTasks(),Is.EqualTo(new[]{worker,enemy}));
        pool.Delete(worker);Assert.That(pool.LocationOf(pool.Create("Enemy")).SlotIndex,Is.EqualTo(1));
        Assert.That(pool.LocationOf(pool.Create("Worker")).SlotIndex,Is.EqualTo(0));
    }

    [Test]
    public void PerTypeChunkCapacitiesPreserveTheirOwnFieldAddresses()
    {
        var pool=Pool();pool.RegisterType("Enemy",3,EnumValueKind.Int);
        var workers=Enumerable.Range(0,9).Select(_=>pool.Create("Worker")).ToArray();
        var enemies=Enumerable.Range(0,9).Select(_=>pool.Create("Enemy")).ToArray();
        for(var i=0;i<9;i++){pool.SetField(workers[i],0,AloeValue.FromInt(i));pool.SetField(enemies[i],0,AloeValue.FromInt(100+i));}
        for(var i=0;i<9;i++){Assert.That(pool.GetField(workers[i],0).AsInt,Is.EqualTo(i));Assert.That(pool.GetField(enemies[i],0).AsInt,Is.EqualTo(100+i));}
        var oldSlot=pool.LocationOf(enemies[4]);pool.Delete(enemies[4]);Assert.That(pool.LocationOf(pool.Create("Enemy")),Is.EqualTo(oldSlot));
    }

    [Test]
    public void InvalidPerTypeCapacityDoesNotRegisterType()
    {
        var pool=Pool();Assert.Throws<ArgumentOutOfRangeException>(()=>pool.RegisterType("Enemy",0,EnumValueKind.Int));
        pool.RegisterType("Enemy",3,EnumValueKind.Int);Assert.That(pool.IsLive(pool.Create("Enemy")),Is.True);
    }

    [Test]
    public void TickSnapshotKeepsStoppedTargetsAndExcludesNewStarts()
    {
        var pool=Pool();var first=pool.Create("Worker");var second=pool.Create("Worker");pool.Start(first);
        var snapshot=pool.CaptureRunningTasks();pool.Stop(first);pool.Start(second);
        Assert.That(snapshot,Is.EqualTo(new[]{first}));Assert.That(pool.IsLive(first),Is.True);
        Assert.That(pool.CaptureRunningTasks(),Is.EqualTo(new[]{second}));
    }

    [Test]
    public void DeletedSnapshotIdentityDoesNotBecomeReplacement()
    {
        var pool=Pool();var original=pool.Create("Worker");pool.Start(original);var snapshot=pool.CaptureRunningTasks();
        pool.Delete(original);var replacement=pool.Create("Worker");pool.Start(replacement);
        Assert.That(snapshot,Is.EqualTo(new[]{original}));Assert.That(pool.IsLive(snapshot[0]),Is.False);
        Assert.That(pool.CaptureRunningTasks(),Is.EqualTo(new[]{replacement}));
    }

    [Test]
    public void ActiveDeletionDefersReuseUntilAllSelfCallsExit()
    {
        var pool=Pool();var id=pool.Create("Worker");var slot=pool.LocationOf(id);pool.BeginExecution(id);pool.BeginExecution(id);pool.Delete(id);
        var other=pool.Create("Worker");Assert.That(pool.LocationOf(other),Is.Not.EqualTo(slot));
        Assert.Throws<InvalidReferenceException>(()=>pool.SetField(id,0,AloeValue.FromInt(1)));
        pool.EndExecution(id);var third=pool.Create("Worker");Assert.That(pool.LocationOf(third),Is.Not.EqualTo(slot));
        pool.EndExecution(id);Assert.That(pool.LocationOf(pool.Create("Worker")),Is.EqualTo(slot));
    }

    [Test]
    public void RepeatedDeleteDoesNotDuplicateFreeSlots()
    {
        var pool=Pool();var id=pool.Create("Worker");pool.Delete(id);pool.Delete(id);pool.Delete(0);
        var first=pool.Create("Worker");var second=pool.Create("Worker");Assert.That(pool.LocationOf(first),Is.Not.EqualTo(pool.LocationOf(second)));
        Assert.That(pool.LiveCount,Is.EqualTo(2));
    }

    [TestCase("start")]
    [TestCase("stop")]
    [TestCase("enter")]
    public void DeletedHandlesRejectExecutionOperations(string operation)
    {
        var pool=Pool();var id=pool.Create("Worker");pool.Delete(id);
        Assert.Throws<InvalidReferenceException>(()=>{if(operation=="start")pool.Start(id);else if(operation=="stop")pool.Stop(id);else pool.BeginExecution(id);});
    }

    [Test]
    public void ReuseDoesNotExposeOldFields()
    {
        var pool=Pool();var id=pool.Create("Worker");pool.SetField(id,0,AloeValue.FromInt(42));pool.Delete(id);
        var next=pool.Create("Worker");Assert.Throws<VmException>(()=>pool.GetField(next,0));
    }

    [TestCase(EnumValueKind.Int)]
    [TestCase(EnumValueKind.Byte)]
    [TestCase(EnumValueKind.Char)]
    [TestCase(EnumValueKind.Float)]
    [TestCase(EnumValueKind.Decimal)]
    [TestCase(EnumValueKind.Bool)]
    [TestCase(EnumValueKind.String)]
    public void ScalarKindsRoundTripWithoutConversions(EnumValueKind kind)
    {
        var pool=new AloeTaskPool(_=>false,2);pool.RegisterType("Value",kind);var id=pool.Create("Value");
        var value=kind switch {EnumValueKind.Int=>AloeValue.FromInt(42),EnumValueKind.Byte=>AloeValue.FromByte(42),EnumValueKind.Char=>AloeValue.FromChar('x'),
            EnumValueKind.Float=>AloeValue.FromFloat(1.5),EnumValueKind.Decimal=>AloeValue.FromDecimal(1.5m),EnumValueKind.Bool=>AloeValue.FromBool(true),_=>AloeValue.FromString("value")};
        pool.SetField(id,0,value);Assert.That(pool.GetField(id,0).Kind,Is.EqualTo(kind));
        Assert.That(pool.GetField(id,0),Is.EqualTo(value));
        Assert.Throws<VmException>(()=>pool.SetField(id,0,AloeValue.Null));
        Assert.That(pool.GetField(id,0).Kind,Is.EqualTo(kind));
    }

    [TestCase(long.MinValue)]
    [TestCase(long.MaxValue)]
    public void Int32FieldRejectsOutOfDomainValues(long value)
    {
        var pool=Pool();var id=pool.Create("Worker");pool.SetField(id,0,AloeValue.FromInt(42));
        Assert.Throws<VmException>(()=>pool.SetField(id,0,AloeValue.FromInt(value)));Assert.That(pool.GetField(id,0).AsInt,Is.EqualTo(42));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void FloatFieldRejectsNonFiniteValues(double value)
    {
        var pool=new AloeTaskPool(_=>false);pool.RegisterType("Value",EnumValueKind.Float);var id=pool.Create("Value");
        Assert.Throws<VmException>(()=>pool.SetField(id,0,AloeValue.FromFloat(value)));
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void FieldBoundsAreChecked(int index)
    {
        var pool=Pool();var id=pool.Create("Worker");Assert.Throws<VmException>(()=>pool.GetField(id,index));
        Assert.Throws<VmException>(()=>pool.SetField(id,index,AloeValue.FromInt(1)));
    }

    [Test]
    public void ObjectFieldsRequireLiveObjectsAndCountDuplicateRoots()
    {
        var pool=new AloeTaskPool(id=>id==7);pool.RegisterType("Owner",EnumValueKind.Object,EnumValueKind.Object);var id=pool.Create("Owner");
        Assert.That(pool.GetField(id,0).IsNull,Is.True);pool.SetField(id,0,AloeValue.FromObject(7));pool.SetField(id,1,AloeValue.FromObject(7));
        Assert.That(pool.GetObjectRoots(),Is.EqualTo(new long[]{7,7}));
        Assert.Throws<InvalidReferenceException>(()=>pool.SetField(id,0,AloeValue.FromObject(8)));
        Assert.Throws<InvalidReferenceException>(()=>pool.SetField(id,0,AloeValue.FromInt(7)));
        pool.SetField(id,0,AloeValue.Null);Assert.That(pool.GetObjectRoots(),Is.EqualTo(new long[]{7}));
    }

    [Test]
    public void DeletedActiveFieldsRemainRootsOnlyUntilEntryEnds()
    {
        var pool=new AloeTaskPool(_=>true);pool.RegisterType("Owner",EnumValueKind.Object);var id=pool.Create("Owner");pool.SetField(id,0,AloeValue.FromObject(7));
        pool.BeginExecution(id);pool.Delete(id);Assert.That(pool.GetObjectRoots(),Is.EqualTo(new long[]{7}));
        pool.EndExecution(id);Assert.That(pool.GetObjectRoots(),Is.Empty);
    }

    [Test]
    public void IdentityExhaustionPrecedesSlotConsumptionAndNeverWraps()
    {
        var pool=new AloeTaskPool(_=>false,2,long.MaxValue);pool.RegisterType("Worker",EnumValueKind.Int);var id=pool.Create("Worker");
        Assert.That(id,Is.EqualTo(long.MaxValue));Assert.Throws<VmException>(()=>pool.Create("Worker"));Assert.That(pool.LiveCount,Is.EqualTo(1));
        pool.ResetInstances();Assert.Throws<VmException>(()=>pool.Create("Worker"));
    }

    [Test]
    public void ResetPreservesLayoutsButInvalidatesIdentitiesAndClearsRoots()
    {
        var pool=new AloeTaskPool(_=>true,2);pool.RegisterType("Owner",EnumValueKind.Object);var id=pool.Create("Owner");pool.SetField(id,0,AloeValue.FromObject(7));
        pool.ResetInstances();Assert.That(pool.GetObjectRoots(),Is.Empty);Assert.That(pool.IsLive(id),Is.False);
        var next=pool.Create("Owner");Assert.That(next,Is.GreaterThan(id));Assert.That(pool.GetField(next,0).IsNull,Is.True);
    }

    [Test]
    public void ActiveResetAndUnbalancedExitAreRejectedWithoutCorruptingPool()
    {
        var pool=Pool();var id=pool.Create("Worker");pool.BeginExecution(id);Assert.Throws<InvalidOperationException>(()=>pool.ResetInstances());
        Assert.That(pool.IsLive(id),Is.True);pool.EndExecution(id);Assert.Throws<InvalidOperationException>(()=>pool.EndExecution(id));
    }

    [TestCase(EnumValueKind.Unknown)]
    [TestCase(EnumValueKind.Null)]
    [TestCase(EnumValueKind.Struct)]
    public void UnsupportedLayoutsAreRejected(EnumValueKind kind)=>Assert.Throws<ArgumentException>(()=>new AloeTaskPool(_=>false).RegisterType("Bad",kind));

    [Test]
    public void InvalidCapacityAndDuplicateTypesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>new AloeTaskPool(_=>false,0));var pool=Pool();
        Assert.Throws<ArgumentException>(()=>pool.RegisterType("Worker"));Assert.Throws<VmException>(()=>pool.Create("Unknown"));
    }

    [Test]
    public void VmGcRetainsTaskOwnedObjectAndReleasesItAfterDeletion()
    {
        var vm=Vm();var objectId=vm.Heap.Allocate(new byte[8]);vm.Heap.SetDestructionCandidate(objectId);
        vm.TaskPool.RegisterType("Owner",EnumValueKind.Object);var id=vm.TaskPool.Create("Owner");vm.TaskPool.SetField(id,0,AloeValue.FromObject(objectId));
        vm.RequireGc();vm.FinishGc();Assert.That(vm.Heap.ContainsObject(objectId),Is.True);
        vm.TaskPool.Delete(id);vm.RequireGc();vm.FinishGc();Assert.That(vm.Heap.ContainsObject(objectId),Is.False);
    }

    [Test]
    public void VmResetClearsTasksWhilePreservedSubmissionsKeepThem()
    {
        var vm=Vm();vm.TaskPool.RegisterType("Worker");var id=vm.TaskPool.Create("Worker");
        var module=new AloeCompiler().Compile("function main(args: string[]): int { return 0; }");vm.ReplaceModule(module);var entry=module.Functions[module.EntryPointIndex];
        vm.RunFromEntryPoint(entry.EntryIp,new AloeValue[entry.LocalCount],true);vm.Pop();Assert.That(vm.TaskPool.IsLive(id),Is.True);
        vm.RunFromEntryPoint();vm.Pop();Assert.That(vm.TaskPool.IsLive(id),Is.False);
    }

    [Test]
    public void TaskDeletionDoesNotDestroyAnExternallySharedObject()
    {
        var vm=Vm();var objectId=vm.Heap.Allocate(new byte[8]);vm.Heap.SetExternalReferenceCount(objectId,1);
        vm.TaskPool.RegisterType("Owner",EnumValueKind.Object);var task=vm.TaskPool.Create("Owner");vm.TaskPool.SetField(task,0,AloeValue.FromObject(objectId));
        vm.TaskPool.Delete(task);vm.RequireGc();vm.FinishGc();Assert.That(vm.Heap.ContainsObject(objectId),Is.True);
        vm.Heap.SetExternalReferenceCount(objectId,0);vm.RequireGc();vm.FinishGc();Assert.That(vm.Heap.ContainsObject(objectId),Is.False);
    }

    [Test]
    public void TaskRootRetainsAndReleasesAnObjectReferenceGroup()
    {
        var vm=Vm();var left=vm.Heap.Allocate(new byte[8],fieldCount:1);var right=vm.Heap.Allocate(new byte[8],fieldCount:1);
        vm.Heap.SetField(left,0,AloeValue.FromObject(right));vm.Heap.SetField(right,0,AloeValue.FromObject(left));
        vm.TaskPool.RegisterType("Owner",EnumValueKind.Object);var task=vm.TaskPool.Create("Owner");vm.TaskPool.SetField(task,0,AloeValue.FromObject(left));
        vm.RequireGc();vm.FinishGc();Assert.That(vm.Heap.ContainsObject(left)&&vm.Heap.ContainsObject(right),Is.True);
        vm.TaskPool.Delete(task);for(var i=0;i<2;i++){vm.RequireGc();vm.FinishGc();}
        Assert.That(vm.Heap.ContainsObject(left)||vm.Heap.ContainsObject(right),Is.False);
    }

    [Test]
    public void ActiveTaskCannotBeOverwrittenByVmReset()
    {
        var vm=Vm();vm.TaskPool.RegisterType("Worker");var id=vm.TaskPool.Create("Worker");vm.TaskPool.BeginExecution(id);
        Assert.Throws<InvalidOperationException>(()=>vm.RunFromEntryPoint());Assert.That(vm.TaskPool.IsLive(id),Is.True);
        Assert.That(vm.CallStack.IsEmpty,Is.True);vm.TaskPool.EndExecution(id);vm.RunFromEntryPoint();vm.Pop();Assert.That(vm.TaskPool.IsLive(id),Is.False);
    }

    private static AloeTaskPool Pool() {var pool=new AloeTaskPool(_=>false,2);pool.RegisterType("Worker",EnumValueKind.Int);return pool;}
    private static AloeVm Vm() {var settings=new AloeVmSettings();settings.Logging.ConsoleEnabled=false;settings.Logging.FileEnabled=false;return new AloeVm(new AloeCompiler().Compile("function main(args: string[]): int { return 0; }"),settings);}
}
