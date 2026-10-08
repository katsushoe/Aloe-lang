using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Aloe.RuntimeLib
{
    public sealed class AloeGcSettings
    {
        public int MaxMovedObjectsPerTick { get; set; } = 256;
        public long MaxMovedBytesPerTick { get; set; } = 1024 * 1024;
        public double MaxEvacuationOccupancy { get; set; } = 0.50;
    }

    public sealed class AloeGcTickResult
    {
        internal AloeGcTickResult(int sweptObjects, int movedObjects, long movedBytes, int freedBanks, bool planCompleted)
        {
            SweptObjects = sweptObjects;
            MovedObjects = movedObjects;
            MovedBytes = movedBytes;
            FreedBanks = freedBanks;
            PlanCompleted = planCompleted;
        }

        public int SweptObjects { get; }
        public int MovedObjects { get; }
        public long MovedBytes { get; }
        public int FreedBanks { get; }
        public bool PlanCompleted { get; }
    }

    /// <summary>
    /// Three phase GC: Plan -> Sweep/Move -> FreeBlock.
    /// A plan may remain active across multiple ticks when movement budgets are exhausted.
    /// </summary>
    public sealed class AloeGarbageCollector
    {
        private readonly AloeHeap _heap;
        private AloeGcPlan? _activePlan;
        private readonly AloeReferenceGraph _referenceGraph;

        public AloeGarbageCollector(AloeHeap heap, AloeGcSettings? settings = null)
        {
            _heap = heap ?? throw new ArgumentNullException(nameof(heap));
            Settings = settings ?? new AloeGcSettings();
            _referenceGraph = new AloeReferenceGraph(_heap);
        }

        public AloeGcSettings Settings { get; }
        public bool HasActivePlan => _activePlan != null;
        public AloeReferenceGraph ReferenceGraph => _referenceGraph;
        public double TotalElapsedMilliseconds { get; private set; }

        public void ResetMetrics()
        {
            TotalElapsedMilliseconds = 0;
        }

        /// <summary>Emit GC phase/debug information when enabled.</summary>
        public bool DebugEnabled { get; set; }

        /// <summary>GC debug output destination. AloeVm wires this to TraceWriter.</summary>
        public Action<string>? LogWriter { get; set; } = Console.WriteLine;

        public AloeGcTickResult RunTick()
        {
            var started = Stopwatch.GetTimestamp();
            ValidateSettings();

            var referenceGraphStarted = Stopwatch.GetTimestamp();
            var graph = _referenceGraph.Update();
            var referenceGraphMs = Stopwatch.GetElapsedTime(referenceGraphStarted).TotalMilliseconds;
            Log($"Reference Graph: groups={graph.GroupCount}, rebuilt={graph.GroupsRebuilt}, fullRebuild={graph.FullRebuild}, analyzedObjects={graph.AnalyzedObjectCount}, graphVersion={graph.GraphVersion}.");
            Log($"Tick begin: objects={_heap.ObjectTable.Count}, banks={_heap.Banks.Count}, activePlan={_activePlan != null}.");

            var planStarted = Stopwatch.GetTimestamp();

            if (_activePlan == null)
            {
                Log("Plan phase: building new plan.");
                _activePlan = BuildPlan();
            }
            else if (_activePlan.AllocationVersion != _heap.AllocationVersion ||
                     _activePlan.LifetimeVersion != _heap.LifetimeVersion)
            {
                Log($"Plan phase: heap lifetime changed; rebuilding plan (allocation {_activePlan.AllocationVersion}->{_heap.AllocationVersion}, lifetime {_activePlan.LifetimeVersion}->{_heap.LifetimeVersion}).");
                _activePlan = BuildPlan();
            }
            else
            {
                Log($"Plan phase: continuing plan (sweep {_activePlan.SweepIndex}/{_activePlan.Sweeps.Count}, move {_activePlan.MoveIndex}/{_activePlan.Moves.Count}).");
            }

            var planMs = Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
            var plan = _activePlan;
            var swept = 0;
            var moved = 0;
            long movedBytes = 0;

            // Phase 2a: sweep is performed once for the plan.
            var sweepStarted = Stopwatch.GetTimestamp();
            while (plan.SweepIndex < plan.Sweeps.Count)
            {
                var action = plan.Sweeps[plan.SweepIndex++];
                if (_heap.ObjectTable.ContainsKey(action.ObjectId))
                {
                    var entry = _heap.GetEntry(action.ObjectId);
                    Log($"Sweep: object={action.ObjectId}, bank={entry.BankId}, slot={entry.StartSlot}, slots={entry.SlotCount}.");
                    _heap.Sweep(action.ObjectId);
                    swept++;
                }
            }

            var sweepMs = Stopwatch.GetElapsedTime(sweepStarted).TotalMilliseconds;

            // Phase 2b: moving is tick-budgeted and may continue next Tick.
            var moveStarted = Stopwatch.GetTimestamp();
            while (plan.MoveIndex < plan.Moves.Count)
            {
                var action = plan.Moves[plan.MoveIndex];
                if (!_heap.ObjectTable.TryGetValue(action.ObjectId, out var entry))
                {
                    plan.MoveIndex++;
                    continue;
                }

                var bytes = entry.AllocatedBytes;
                if (moved >= Settings.MaxMovedObjectsPerTick || movedBytes + bytes > Settings.MaxMovedBytesPerTick)
                {
                    Log($"Sweep/Move: budget reached after {moved} object(s), {movedBytes} byte(s); remainingMoves={plan.Moves.Count - plan.MoveIndex}.");
                    break;
                }

                var sourceBankId = entry.BankId;
                var sourceStartSlot = entry.StartSlot;
                Log($"Move: object={action.ObjectId}, {sourceBankId}:{sourceStartSlot} -> {action.TargetBankId}:{action.TargetStartSlot}, bytes={bytes}.");
                _heap.Move(action.ObjectId, action.TargetBankId, action.TargetStartSlot);
                plan.MoveIndex++;
                moved++;
                movedBytes += bytes;
            }

            var moveMs = Stopwatch.GetElapsedTime(moveStarted).TotalMilliseconds;

            // Phase 3: only physically empty Banks are released.
            var freeBlockStarted = Stopwatch.GetTimestamp();
            var freedBanks = 0;
            foreach (var bankId in plan.FreeBankCandidates.ToArray())
            {
                if (_heap.Banks.Any(x => x.BankId == bankId) && _heap.FreeBankIfEmpty(bankId))
                {
                    plan.FreeBankCandidates.Remove(bankId);
                    freedBanks++;
                    Log($"FreeBlock: bank={bankId} released.");
                }
            }

            var freeBlockMs = Stopwatch.GetElapsedTime(freeBlockStarted).TotalMilliseconds;

            var completed = plan.SweepIndex >= plan.Sweeps.Count && plan.MoveIndex >= plan.Moves.Count;
            if (completed)
            {
                Log("Plan completed.");
                _activePlan = null;
            }

            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            TotalElapsedMilliseconds += elapsedMs;
            Log($"Timing: referenceGraphMs={referenceGraphMs:F3}, planMs={planMs:F3}, sweepMs={sweepMs:F3}, moveMs={moveMs:F3}, freeBlockMs={freeBlockMs:F3}, totalMs={elapsedMs:F3}.");
            Log($"Tick end: swept={swept}, moved={moved}, movedBytes={movedBytes}, freedBanks={freedBanks}, planCompleted={completed}, elapsedMs={elapsedMs:F3}, heapUsedBytes={_heap.UsedBytes}, heapReservedBytes={_heap.ReservedBytes}, managedBytes={System.GC.GetTotalMemory(false)}.");
            return new AloeGcTickResult(swept, moved, movedBytes, freedBanks, completed);
        }

        public void InvalidatePlan()
        {
            _activePlan = null;
        }

        private AloeGcPlan BuildPlan()
        {
            var plan = new AloeGcPlan { AllocationVersion = _heap.AllocationVersion, LifetimeVersion = _heap.LifetimeVersion };
            var entries = _heap.ObjectTable.Values.ToArray();

            foreach (var entry in entries.Where(x => x.IsReclaimable))
                plan.Sweeps.Add(new SweepAction(entry.ObjectId));

            // Virtual occupancy after planned sweep. Plan phase itself does not move objects.
            var occupied = _heap.Banks.ToDictionary(
                b => b.BankId,
                b => BuildVirtualSlots(b, entries, plan.Sweeps));

            var bankEntries = entries
                .Where(x => !x.IsReclaimable)
                .GroupBy(x => x.BankId)
                .ToDictionary(x => x.Key, x => x.ToList());

            foreach (var bank in _heap.Banks)
            {
                if (!bankEntries.ContainsKey(bank.BankId))
                    plan.FreeBankCandidates.Add(bank.BankId);
            }

            var sources = _heap.Banks
                .Select(bank => new
                {
                    Bank = bank,
                    Live = bankEntries.TryGetValue(bank.BankId, out var live) ? live : new List<AloeObjectTableEntry>()
                })
                .Where(x => x.Live.Count > 0)
                .Select(x => new
                {
                    x.Bank,
                    x.Live,
                    LiveSlots = x.Live.Sum(e => e.SlotCount),
                    Occupancy = (double)x.Live.Sum(e => e.SlotCount) / x.Bank.SlotCapacity
                })
                .Where(x => x.Occupancy < Settings.MaxEvacuationOccupancy)
                .OrderBy(x => x.LiveSlots)
                .ThenBy(x => x.Bank.BankId)
                .ToArray();

            var sourceIds = sources.Select(x => x.Bank.BankId).ToHashSet();

            foreach (var source in sources)
            {
                var tentative = new List<MoveAction>();
                var reservationChanges = new List<(int BankId, int Start, int Count)>();
                var canEvacuate = true;

                foreach (var entry in source.Live.OrderByDescending(x => x.SlotCount))
                {
                    if (!TryReserveDestination(
                            entry.SlotCount,
                            source.Bank.BankId,
                            sourceIds,
                            plan.FreeBankCandidates,
                            occupied,
                            out var targetBankId,
                            out var targetStartSlot))
                    {
                        canEvacuate = false;
                        break;
                    }

                    MarkVirtual(occupied[targetBankId], targetStartSlot, entry.SlotCount, true);
                    reservationChanges.Add((targetBankId, targetStartSlot, entry.SlotCount));
                    tentative.Add(new MoveAction(entry.ObjectId, targetBankId, targetStartSlot));
                }

                if (!canEvacuate)
                {
                    foreach (var change in reservationChanges)
                        MarkVirtual(occupied[change.BankId], change.Start, change.Count, false);
                    continue;
                }

                plan.Moves.AddRange(tentative);
                plan.FreeBankCandidates.Add(source.Bank.BankId);
            }

            Log($"Plan phase result: sweeps={plan.Sweeps.Count}, moves={plan.Moves.Count}, freeBlockCandidates={plan.FreeBankCandidates.Count}.");
            return plan;
        }

        private void Log(string message)
        {
            if (DebugEnabled)
                LogWriter?.Invoke($"[GC] {message}");
        }

        private static bool[] BuildVirtualSlots(
            AloeHeapBank bank,
            IReadOnlyCollection<AloeObjectTableEntry> entries,
            IReadOnlyCollection<SweepAction> sweeps)
        {
            var result = new bool[bank.SlotCapacity];
            var sweptIds = sweeps.Select(x => x.ObjectId).ToHashSet();
            foreach (var entry in entries.Where(x => x.BankId == bank.BankId && !sweptIds.Contains(x.ObjectId)))
                MarkVirtual(result, entry.StartSlot, entry.SlotCount, true);
            return result;
        }

        private static bool TryReserveDestination(
            int slotCount,
            int sourceBankId,
            HashSet<int> sourceBankIds,
            HashSet<int> freeBankCandidates,
            Dictionary<int, bool[]> occupied,
            out int bankId,
            out int startSlot)
        {
            foreach (var pair in occupied.OrderByDescending(x => CountFree(x.Value)))
            {
                if (pair.Key == sourceBankId || sourceBankIds.Contains(pair.Key) || freeBankCandidates.Contains(pair.Key))
                    continue;

                if (TryFindVirtualRange(pair.Value, slotCount, out startSlot))
                {
                    bankId = pair.Key;
                    return true;
                }
            }

            bankId = -1;
            startSlot = -1;
            return false;
        }

        private static int CountFree(bool[] occupied) => occupied.Count(x => !x);

        private static bool TryFindVirtualRange(bool[] occupied, int slotCount, out int startSlot)
        {
            var run = 0;
            for (var i = 0; i < occupied.Length; i++)
            {
                if (!occupied[i])
                {
                    run++;
                    if (run == slotCount)
                    {
                        startSlot = i - slotCount + 1;
                        return true;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            startSlot = -1;
            return false;
        }

        private static void MarkVirtual(bool[] occupied, int start, int count, bool value)
        {
            for (var i = start; i < start + count; i++)
                occupied[i] = value;
        }

        private void ValidateSettings()
        {
            if (Settings.MaxMovedObjectsPerTick <= 0)
                throw new InvalidOperationException("MaxMovedObjectsPerTick must be positive.");
            if (Settings.MaxMovedBytesPerTick <= 0)
                throw new InvalidOperationException("MaxMovedBytesPerTick must be positive.");
            if (Settings.MaxEvacuationOccupancy < 0 || Settings.MaxEvacuationOccupancy > 1)
                throw new InvalidOperationException("MaxEvacuationOccupancy must be in [0, 1].");
        }

        private sealed class AloeGcPlan
        {
            public long AllocationVersion { get; init; }
            public long LifetimeVersion { get; init; }
            public List<SweepAction> Sweeps { get; } = new();
            public List<MoveAction> Moves { get; } = new();
            public HashSet<int> FreeBankCandidates { get; } = new();
            public int SweepIndex { get; set; }
            public int MoveIndex { get; set; }
        }

        private readonly record struct SweepAction(long ObjectId);
        private readonly record struct MoveAction(long ObjectId, int TargetBankId, int TargetStartSlot);
    }
}
