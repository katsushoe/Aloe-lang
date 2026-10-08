using Aloe.CommonLib;
using Aloe.CommonLib.Constants;
using Aloe.RuntimeLib;

namespace Aloe.CompilerLib.Tests;

public sealed class AloeGcTests
{
    [Test]
    public void HeapSlot_RoundsObjectSizeToEightBytes()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);
        var objectId = heap.Allocate(new byte[20]);
        var entry = heap.GetEntry(objectId);

        Assert.That(AloeHeap.HeapSlotSize, Is.EqualTo(8));
        Assert.That(entry.DataSizeBytes, Is.EqualTo(20));
        Assert.That(entry.SlotCount, Is.EqualTo(3));
        Assert.That(entry.AllocatedBytes, Is.EqualTo(24));
    }

    [Test]
    public void Gc_EvacuatesLowOccupancyBankAndFreesIt()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);

        var deadA = heap.Allocate(Fill(20, 0xA1)); // bank 1: 3 slots
        var live = heap.Allocate(Fill(20, 0xB2));  // bank 1: 3 slots
        var deadB = heap.Allocate(Fill(16, 0xC3)); // bank 1: 2 slots (full)

        // Bank 2 is exactly 50% occupied, so with the default < 50% evacuation
        // threshold it is a destination rather than another source Bank.
        var destinationRoots = new[]
        {
            heap.Allocate(Fill(8, 0x11)),
            heap.Allocate(Fill(8, 0x12)),
            heap.Allocate(Fill(8, 0x13)),
            heap.Allocate(Fill(8, 0x14)),
        };
        heap.SetRuntimeTemporaryRoots(destinationRoots.Append(live));

        var originalBank = heap.GetEntry(live).BankId;
        var payloadBefore = heap.ReadObject(live);

        heap.SetDestructionCandidate(deadA);
        heap.SetDestructionCandidate(deadB);

        var gc = new AloeGarbageCollector(heap);
        var result = gc.RunTick();

        Assert.That(result.SweptObjects, Is.EqualTo(2));
        Assert.That(result.MovedObjects, Is.EqualTo(1));
        Assert.That(result.FreedBanks, Is.EqualTo(1));
        Assert.That(heap.GetEntry(live).BankId, Is.Not.EqualTo(originalBank));
        Assert.That(heap.ReadObject(live), Is.EqualTo(payloadBefore));
        Assert.That(heap.Banks.Any(x => x.BankId == originalBank), Is.False);
    }

    [Test]
    public void Gc_MoveBudgetContinuesPlanAcrossTicks()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);

        var deadA = heap.Allocate(Fill(16, 0x21)); // 2 slots
        var liveA = heap.Allocate(Fill(8, 0x31));  // 1 slot
        var liveB = heap.Allocate(Fill(8, 0x32));  // 1 slot
        var deadB = heap.Allocate(Fill(32, 0x22)); // 4 slots, bank 1 full

        // Bank 2 = 50% occupied and leaves four destination slots.
        var destinationRoots = new List<long>();
        for (var i = 0; i < 4; i++)
            destinationRoots.Add(heap.Allocate(Fill(8, (byte)(0x40 + i))));
        heap.SetRuntimeTemporaryRoots(destinationRoots.Concat(new[] { liveA, liveB }));

        var sourceBank = heap.GetEntry(liveA).BankId;
        Assert.That(heap.GetEntry(liveB).BankId, Is.EqualTo(sourceBank));

        heap.SetDestructionCandidate(deadA);
        heap.SetDestructionCandidate(deadB);

        var gc = new AloeGarbageCollector(heap, new AloeGcSettings
        {
            MaxMovedObjectsPerTick = 1,
            MaxMovedBytesPerTick = 1024,
            MaxEvacuationOccupancy = 0.50
        });

        var tick1 = gc.RunTick();
        Assert.That(tick1.SweptObjects, Is.EqualTo(2));
        Assert.That(tick1.MovedObjects, Is.EqualTo(1));
        Assert.That(tick1.FreedBanks, Is.EqualTo(0));
        Assert.That(tick1.PlanCompleted, Is.False);
        Assert.That(heap.Banks.Any(x => x.BankId == sourceBank), Is.True);

        var tick2 = gc.RunTick();
        Assert.That(tick2.SweptObjects, Is.EqualTo(0));
        Assert.That(tick2.MovedObjects, Is.EqualTo(1));
        Assert.That(tick2.FreedBanks, Is.EqualTo(1));
        Assert.That(tick2.PlanCompleted, Is.True);
        Assert.That(heap.Banks.Any(x => x.BankId == sourceBank), Is.False);
    }

    [Test]
    public void VmGcTreatsStackObjectHandleAsTemporaryRoot()
    {
        var module = new Module(
            constants: Array.Empty<AloeValue>(),
            code: new[] { new Instruction(EnumOpcode.Halt) },
            functions: new[] { new FunctionInfo("main", 0, 0, 0) },
            entryPointIndex: 0);
        var vm = new AloeVm(module);
        var objectId = vm.Heap.Allocate(Fill(8, 0x77));
        vm.Heap.SetDestructionCandidate(objectId);
        vm.Push(AloeValue.FromObject(objectId));

        var rooted = vm.RunGcTick();
        Assert.That(rooted.SweptObjects, Is.EqualTo(0));
        Assert.That(vm.Heap.ObjectTable.ContainsKey(objectId), Is.True);

        vm.Pop();
        var unrooted = vm.RunGcTick();
        Assert.That(unrooted.SweptObjects, Is.EqualTo(1));
        Assert.That(vm.Heap.ObjectTable.ContainsKey(objectId), Is.False);
    }


    [Test]
    public void VmGc_DebugModeWritesGcPhaseLog()
    {
        var module = new Module(
            constants: Array.Empty<AloeValue>(),
            code: new[] { new Instruction(EnumOpcode.Halt) },
            functions: new[] { new FunctionInfo("main", 0, 0, 0) },
            entryPointIndex: 0);
        var vm = new AloeVm(module);
        var logs = new List<string>();
        vm.TraceEnabled = true;
        vm.TraceWriter = logs.Add;
        // GC diagnostics are controlled by GC.debug, independently of instruction tracing.
        vm.SetGcDebug(true);

        var objectId = vm.Heap.Allocate(Fill(8, 0x55));
        vm.Heap.SetDestructionCandidate(objectId);

        vm.RunGcTick();

        Assert.That(logs.Any(x => x.StartsWith("[GC] Runtime roots refreshed:")), Is.True);
        Assert.That(logs.Any(x => x.Contains("Plan phase:")), Is.True);
        Assert.That(logs.Any(x => x.Contains("Sweep:")), Is.True);
        Assert.That(logs.Any(x => x.Contains("FreeBlock:")), Is.True);
        Assert.That(logs.Any(x => x.Contains("Tick end:")), Is.True);
    }


    [Test]
    public void VmTick_UpdatesReferenceStateButDoesNotRunPhysicalGc()
    {
        var module = new Module(
            Array.Empty<AloeValue>(),
            new[] { new Instruction(EnumOpcode.Return) },
            new[] { new FunctionInfo("main", 0, 0, 0) },
            0);
        var vm = new AloeVm(module);
        var objectId = vm.Heap.Allocate(Fill(8, 0x5A));

        vm.Tick();

        Assert.That(vm.Heap.ObjectTable.ContainsKey(objectId), Is.True);
        Assert.That(vm.Heap.GetEntry(objectId).IsDestructionCandidate, Is.True);

        vm.RequireGc();
        Assert.That(vm.Heap.ObjectTable.ContainsKey(objectId), Is.False);
    }

    [Test]
    public void ReferenceGraph_CycleFormsOneGroupAndBecomesCandidateWithoutExternalReferences()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);
        var a = heap.Allocate(Fill(8, 0x61));
        var b = heap.Allocate(Fill(8, 0x62));
        heap.AddReference(a, b);
        heap.AddReference(b, a);

        var graph = new AloeReferenceGraph(heap);
        var update = graph.Update();

        Assert.That(update.GroupCount, Is.EqualTo(1));
        Assert.That(heap.GetEntry(a).ReferenceGroupId, Is.EqualTo(heap.GetEntry(b).ReferenceGroupId));
        Assert.That(heap.GetEntry(a).GroupExternalReferenceCount, Is.EqualTo(0));
        Assert.That(heap.GetEntry(a).IsDestructionCandidate, Is.True);
        Assert.That(heap.GetEntry(b).IsDestructionCandidate, Is.True);
    }

    [Test]
    public void Gc_RootOnOneCycleMemberKeepsWholeReferenceGroupAlive()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);
        var a = heap.Allocate(Fill(8, 0x71));
        var b = heap.Allocate(Fill(8, 0x72));
        heap.AddReference(a, b);
        heap.AddReference(b, a);
        heap.SetRuntimeTemporaryRoots(new[] { a });

        var gc = new AloeGarbageCollector(heap);
        var rooted = gc.RunTick();

        Assert.That(rooted.SweptObjects, Is.EqualTo(0));
        Assert.That(heap.ObjectTable.ContainsKey(a), Is.True);
        Assert.That(heap.ObjectTable.ContainsKey(b), Is.True);
        Assert.That(heap.GetEntry(b).GroupRuntimeTemporaryReferenceCount, Is.EqualTo(1));

        heap.SetRuntimeTemporaryRoots(Array.Empty<long>());
        var unrooted = gc.RunTick();

        Assert.That(unrooted.SweptObjects, Is.EqualTo(2));
        Assert.That(heap.ObjectTable.ContainsKey(a), Is.False);
        Assert.That(heap.ObjectTable.ContainsKey(b), Is.False);
    }

    [Test]
    public void ReferenceGraph_ExternalEdgeKeepsTargetGroupAliveUntilRemoved()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);
        var owner = heap.Allocate(Fill(8, 0x81));
        var a = heap.Allocate(Fill(8, 0x82));
        var b = heap.Allocate(Fill(8, 0x83));

        heap.SetExternalReferenceCount(owner, 1);
        heap.AddReference(owner, a);
        heap.AddReference(a, b);
        heap.AddReference(b, a);

        var graph = new AloeReferenceGraph(heap);
        graph.Update();

        Assert.That(heap.GetEntry(a).GroupExternalReferenceCount, Is.EqualTo(1));
        Assert.That(heap.GetEntry(a).IsDestructionCandidate, Is.False);
        Assert.That(heap.GetEntry(b).IsDestructionCandidate, Is.False);

        heap.RemoveReference(owner, a);
        graph.Update();

        Assert.That(heap.GetEntry(a).GroupExternalReferenceCount, Is.EqualTo(0));
        Assert.That(heap.GetEntry(a).IsDestructionCandidate, Is.True);
        Assert.That(heap.GetEntry(b).IsDestructionCandidate, Is.True);
        Assert.That(heap.GetEntry(owner).IsDestructionCandidate, Is.False);
    }

    [Test]
    public void ReferenceGraph_RemovingCycleEdgeSplitsReferenceGroup()
    {
        var heap = new AloeHeap(bankSlotCapacity: 8);
        var a = heap.Allocate(Fill(8, 0x91));
        var b = heap.Allocate(Fill(8, 0x92));
        heap.SetExternalReferenceCount(a, 1);
        heap.AddReference(a, b);
        heap.AddReference(b, a);

        var graph = new AloeReferenceGraph(heap);
        graph.Update();
        Assert.That(heap.GetEntry(a).ReferenceGroupId, Is.EqualTo(heap.GetEntry(b).ReferenceGroupId));

        heap.RemoveReference(b, a);
        var update = graph.Update();

        Assert.That(update.GroupsRebuilt, Is.True);
        Assert.That(heap.GetEntry(a).ReferenceGroupId, Is.Not.EqualTo(heap.GetEntry(b).ReferenceGroupId));
        Assert.That(heap.GetEntry(b).GroupExternalReferenceCount, Is.EqualTo(1));
    }


    [Test]
    public void ReferenceGraph_DirtyDisconnectedComponentUsesIncrementalRebuild()
    {
        var heap = new AloeHeap(bankSlotCapacity: 16);
        var a = heap.Allocate(Fill(8, 0xA1));
        var b = heap.Allocate(Fill(8, 0xA2));
        var c = heap.Allocate(Fill(8, 0xA3));
        var d = heap.Allocate(Fill(8, 0xA4));
        heap.AddReference(a, b);
        heap.AddReference(b, a);
        heap.AddReference(c, d);
        heap.AddReference(d, c);

        var graph = new AloeReferenceGraph(heap)
        {
            MaxIncrementalFraction = 1.0
        };
        var initial = graph.Update();
        Assert.That(initial.FullRebuild, Is.True);
        Assert.That(initial.AnalyzedObjectCount, Is.EqualTo(4));

        heap.RemoveReference(b, a);
        var update = graph.Update();

        Assert.That(update.GroupsRebuilt, Is.True);
        Assert.That(update.FullRebuild, Is.False);
        Assert.That(update.AnalyzedObjectCount, Is.EqualTo(2));
        Assert.That(heap.GetEntry(a).ReferenceGroupId, Is.Not.EqualTo(heap.GetEntry(b).ReferenceGroupId));
        Assert.That(heap.GetEntry(c).ReferenceGroupId, Is.EqualTo(heap.GetEntry(d).ReferenceGroupId));
    }

    [Test]
    public void ReferenceGraph_LargeDirtyRegionFallsBackToFullRebuild()
    {
        var heap = new AloeHeap(bankSlotCapacity: 16);
        var a = heap.Allocate(Fill(8, 0xB1));
        var b = heap.Allocate(Fill(8, 0xB2));
        var c = heap.Allocate(Fill(8, 0xB3));
        var d = heap.Allocate(Fill(8, 0xB4));
        heap.AddReference(a, b);
        heap.AddReference(b, a);
        heap.AddReference(c, d);
        heap.AddReference(d, c);

        var graph = new AloeReferenceGraph(heap)
        {
            MaxIncrementalFraction = 0.25
        };
        graph.Update();

        heap.RemoveReference(b, a);
        var update = graph.Update();

        Assert.That(update.FullRebuild, Is.True);
        Assert.That(update.AnalyzedObjectCount, Is.EqualTo(4));
    }



    [Test]
    public void VmGc_FinishWithoutActivePlanIsNoOp()
    {
        // The bytecode verifier requires every function entry IP to be inside the code.
        var module = new Module(
            Array.Empty<AloeValue>(),
            new[] { new Instruction(EnumOpcode.Return) },
            new[] { new FunctionInfo("main", 0, 0, 0) },
            0);
        var vm = new AloeVm(module);
        var dead = vm.Heap.Allocate(Fill(8, 0xC1));
        vm.Heap.SetDestructionCandidate(dead);

        var result = vm.FinishGc();

        Assert.That(result.PlanCompleted, Is.True);
        Assert.That(result.SweptObjects, Is.EqualTo(0));
        Assert.That(vm.Heap.ObjectTable.ContainsKey(dead), Is.True);
        Assert.That(vm.GarbageCollector.HasActivePlan, Is.False);
    }

    [Test]
    public void AloeNewObject_BecomesSweepCandidateAfterFunctionReturns()
    {
        const string source = """
class Node {
    construct() {
    }
}

function makeGarbage(): void {
    var node = new Node();
}

function main(args: string[]): int {
    GC.debug = true;
    makeGarbage();
    tick();
    GC.require();
    GC.finish();
    return 0;
}
""";

        var module = new Aloe.CompilerLib.AloeCompiler().Compile(source);
        var logs = new List<string>();
        var vm = new AloeVm(module)
        {
            TraceEnabled = false,
            TraceWriter = logs.Add
        };

        vm.RunFromEntryPoint();

        Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
        Assert.That(logs.Any(x => x.Contains("Plan phase result: sweeps=1")), Is.True);
        Assert.That(logs.Any(x => x.Contains("Tick end: swept=1")), Is.True);
        Assert.That(logs.Any(x => x == "[GC] Finish: no active plan; no-op."), Is.True);
    }



    [Test]
    public void AloeGcDebugIntrinsic_EmitsGcLogWithoutVmInstructionTrace()
    {
        const string source = """
function main(args: string[]): int {
    GC.debug = true;
    GC.require();
    return 0;
}
""";

        var module = new Aloe.CompilerLib.AloeCompiler().Compile(source);
        var logs = new List<string>();
        var vm = new AloeVm(module)
        {
            TraceEnabled = false,
            TraceWriter = logs.Add
        };

        vm.RunFromEntryPoint();

        Assert.That(logs.Any(x => x == "[GC] Debug enabled."), Is.True);
        Assert.That(logs.Any(x => x.StartsWith("[GC] Reference Graph:")), Is.True);
        Assert.That(logs.Any(x => x.StartsWith("IP=")), Is.False);
    }


    [Test]
    public void AloeGcDebug_EmitsTimingAndMemoryMetrics()
    {
        const string source = """
class Node {
    construct() {
    }
}

function makeGarbage(): void {
    var node = new Node();
}

function main(args: string[]): int {
    GC.debug = true;
    makeGarbage();
    tick();
    GC.require();
    GC.debug = false;
    return 0;
}
""";

        var module = new Aloe.CompilerLib.AloeCompiler().Compile(source);
        var logs = new List<string>();
        var vm = new AloeVm(module)
        {
            TraceEnabled = false,
            TraceWriter = logs.Add
        };

        vm.RunFromEntryPoint();

        Assert.That(logs.Any(x => x.StartsWith("[GC] Timing:") && x.Contains("referenceGraphMs=") && x.Contains("planMs=") && x.Contains("sweepMs=") && x.Contains("moveMs=") && x.Contains("freeBlockMs=") && x.Contains("totalMs=")), Is.True);
        Assert.That(logs.Any(x => x.StartsWith("[GC] Tick end:") && x.Contains("elapsedMs=") && x.Contains("heapUsedBytes=") && x.Contains("heapReservedBytes=") && x.Contains("managedBytes=")), Is.True);
        Assert.That(logs.Any(x => x.StartsWith("[PERF] Program end:") && x.Contains("elapsedMs=") && x.Contains("gcElapsedMs=") && x.Contains("heapUsedBytes=") && x.Contains("heapReservedBytes=") && x.Contains("managedBytes=") && x.Contains("managedStartBytes=") && x.Contains("managedDeltaBytes=") && x.Contains("allocatedDeltaBytes=") && x.Contains("hostGcGen0=") && x.Contains("hostGcGen1=") && x.Contains("hostGcGen2=")), Is.True);
    }

    [Test]
    public void ClassReferenceField_IsTrackedAndCollectedWithOwner()
    {
        const string source = """
class Node {
    construct() {
    }
}

class Holder {
    field child: Node;
    construct(child: Node) {
        this.child = child;
    }
}

function makeGarbage(): void {
    var holder = new Holder(new Node());
}

function main(args: string[]): int {
    makeGarbage();
    tick();
    GC.require();
    GC.require();
    return 0;
}
""";

        var module = new Aloe.CompilerLib.AloeCompiler().Compile(source);
        var vm = new AloeVm(module);
        vm.RunFromEntryPoint();

        Assert.That(vm.Heap.ObjectTable.Count, Is.EqualTo(0));
    }


    [Test]
    public void ObjectFieldReference_UpdatesHeapReferenceGraphEdges()
    {
        var heap = new AloeHeap();
        var owner = heap.Allocate(ReadOnlySpan<byte>.Empty, fieldCount: 1);
        var child = heap.Allocate(ReadOnlySpan<byte>.Empty);

        heap.SetField(owner, 0, AloeValue.FromObject(child));
        Assert.That(heap.GetEntry(owner).OutgoingReferences.ContainsKey(child), Is.True);

        heap.SetField(owner, 0, AloeValue.FromInt(7));
        Assert.That(heap.GetEntry(owner).OutgoingReferences.ContainsKey(child), Is.False);
        Assert.That(heap.GetField(owner, 0).AsInt, Is.EqualTo(7));
    }


    private static byte[] Fill(int length, byte value)
        => Enumerable.Repeat(value, length).ToArray();
}
