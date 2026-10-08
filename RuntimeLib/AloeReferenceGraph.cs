using System;
using System.Collections.Generic;
using System.Linq;

namespace Aloe.RuntimeLib
{
    /// <summary>
    /// Maintains Object Reference Graph metadata and Reference Groups (SCCs).
    /// The first analysis is global. Later graph changes prefer an incremental SCC
    /// rebuild limited to dirty weakly-connected regions, with a bounded fallback to
    /// a full rebuild when the affected region is too large.
    /// </summary>
    public sealed class AloeReferenceGraph
    {
        private readonly AloeHeap _heap;
        private readonly Dictionary<long, AloeReferenceGroup> _groups = new();
        private long _analyzedGraphVersion = -1;

        public AloeReferenceGraph(AloeHeap heap)
        {
            _heap = heap ?? throw new ArgumentNullException(nameof(heap));
        }

        public IReadOnlyDictionary<long, AloeReferenceGroup> Groups => _groups;
        public long AnalyzedGraphVersion => _analyzedGraphVersion;

        /// <summary>Maximum affected objects for an incremental SCC rebuild.</summary>
        public int MaxIncrementalObjects { get; set; } = 4096;

        /// <summary>
        /// If the dirty weak component reaches this fraction of the live heap, a full
        /// SCC rebuild is used instead. Must be in (0, 1].
        /// </summary>
        public double MaxIncrementalFraction { get; set; } = 0.50;

        public AloeReferenceGraphUpdateResult Update()
        {
            ValidateSettings();

            var graphChanged = _analyzedGraphVersion != _heap.ReferenceGraphVersion ||
                               _heap.ObjectTable.Values.Any(x => x.ReferenceGroupId == 0) ||
                               HasStaleGroups();

            var groupsRebuilt = false;
            var fullRebuild = false;
            var analyzedObjectCount = 0;

            if (graphChanged)
            {
                if (_analyzedGraphVersion < 0 || _groups.Count == 0)
                {
                    analyzedObjectCount = RebuildAllGroups();
                    groupsRebuilt = true;
                    fullRebuild = true;
                }
                else
                {
                    var affected = CollectAffectedObjects();
                    var liveCount = _heap.ObjectTable.Count;
                    var fractionLimit = Math.Max(1, (int)Math.Ceiling(liveCount * MaxIncrementalFraction));
                    var incrementalLimit = Math.Min(MaxIncrementalObjects, fractionLimit);

                    if (affected.Count == 0 || affected.Count > incrementalLimit)
                    {
                        analyzedObjectCount = RebuildAllGroups();
                        groupsRebuilt = true;
                        fullRebuild = true;
                    }
                    else
                    {
                        RebuildAffectedGroups(affected);
                        analyzedObjectCount = affected.Count;
                        groupsRebuilt = true;
                    }
                }
            }

            RefreshLifetimeMetadata();
            return new AloeReferenceGraphUpdateResult(
                groupsRebuilt,
                fullRebuild,
                analyzedObjectCount,
                _groups.Count,
                _analyzedGraphVersion);
        }

        private bool HasStaleGroups()
            => _groups.Values.Any(g => g.MemberObjectIds.Any(id => !_heap.ObjectTable.ContainsKey(id)));

        private int RebuildAllGroups()
        {
            _groups.Clear();
            var all = _heap.ObjectTable.Keys.ToHashSet();
            RunTarjan(all);
            MarkAnalysisComplete();
            return all.Count;
        }

        /// <summary>
        /// Build the smallest safe incremental region: dirty/new objects, surviving
        /// members of groups invalidated by deletion, then the complete current weakly
        /// connected component of those seeds. Ignoring edges outside this set is safe
        /// because no graph edge crosses a complete weak component boundary.
        /// </summary>
        private HashSet<long> CollectAffectedObjects()
        {
            var seeds = _heap.ObjectTable.Values
                .Where(x => x.ReferenceDirty || x.ReferenceGroupId == 0)
                .Select(x => x.ObjectId)
                .ToHashSet();

            foreach (var group in _groups.Values)
            {
                if (group.MemberObjectIds.Any(id => !_heap.ObjectTable.ContainsKey(id)))
                {
                    foreach (var memberId in group.MemberObjectIds)
                    {
                        if (_heap.ObjectTable.ContainsKey(memberId))
                            seeds.Add(memberId);
                    }
                }
            }

            if (seeds.Count == 0)
                return seeds;

            var incoming = new Dictionary<long, List<long>>();
            foreach (var id in _heap.ObjectTable.Keys)
                incoming[id] = new List<long>();

            foreach (var source in _heap.ObjectTable.Values)
            {
                foreach (var targetId in source.OutgoingReferences.Keys)
                {
                    if (incoming.TryGetValue(targetId, out var sources))
                        sources.Add(source.ObjectId);
                }
            }

            var affected = new HashSet<long>(seeds);
            var queue = new Queue<long>(seeds);
            while (queue.Count > 0)
            {
                var objectId = queue.Dequeue();
                var entry = _heap.GetEntry(objectId);

                foreach (var targetId in entry.OutgoingReferences.Keys)
                {
                    if (_heap.ObjectTable.ContainsKey(targetId) && affected.Add(targetId))
                        queue.Enqueue(targetId);
                }

                foreach (var sourceId in incoming[objectId])
                {
                    if (affected.Add(sourceId))
                        queue.Enqueue(sourceId);
                }
            }

            return affected;
        }

        private void RebuildAffectedGroups(HashSet<long> affected)
        {
            var groupIdsToRemove = _groups.Values
                .Where(g => g.MemberObjectIds.Any(id => affected.Contains(id) || !_heap.ObjectTable.ContainsKey(id)))
                .Select(g => g.GroupId)
                .ToArray();

            foreach (var groupId in groupIdsToRemove)
                _groups.Remove(groupId);

            foreach (var objectId in affected)
                _heap.GetEntry(objectId).ReferenceGroupId = 0;

            RunTarjan(affected);
            MarkAnalysisComplete();
        }

        private void RunTarjan(HashSet<long> allowed)
        {
            var index = 0;
            var stack = new Stack<long>();
            var onStack = new HashSet<long>();
            var indices = new Dictionary<long, int>();
            var lowLinks = new Dictionary<long, int>();

            foreach (var objectId in allowed.OrderBy(x => x))
            {
                if (!indices.ContainsKey(objectId))
                    StrongConnect(objectId, allowed, ref index, stack, onStack, indices, lowLinks);
            }
        }

        private void StrongConnect(
            long objectId,
            HashSet<long> allowed,
            ref int index,
            Stack<long> stack,
            HashSet<long> onStack,
            Dictionary<long, int> indices,
            Dictionary<long, int> lowLinks)
        {
            indices[objectId] = index;
            lowLinks[objectId] = index;
            index++;
            stack.Push(objectId);
            onStack.Add(objectId);

            var entry = _heap.GetEntry(objectId);
            foreach (var targetId in entry.OutgoingReferences.Keys.OrderBy(x => x))
            {
                if (!allowed.Contains(targetId))
                    continue;

                if (!indices.ContainsKey(targetId))
                {
                    StrongConnect(targetId, allowed, ref index, stack, onStack, indices, lowLinks);
                    lowLinks[objectId] = Math.Min(lowLinks[objectId], lowLinks[targetId]);
                }
                else if (onStack.Contains(targetId))
                {
                    lowLinks[objectId] = Math.Min(lowLinks[objectId], indices[targetId]);
                }
            }

            if (lowLinks[objectId] != indices[objectId])
                return;

            var members = new List<long>();
            while (stack.Count > 0)
            {
                var member = stack.Pop();
                onStack.Remove(member);
                members.Add(member);
                if (member == objectId)
                    break;
            }

            members.Sort();
            var groupId = members[0];
            var group = new AloeReferenceGroup(groupId, members);
            _groups.Add(groupId, group);

            foreach (var member in members)
                _heap.GetEntry(member).ReferenceGroupId = groupId;
        }

        private void MarkAnalysisComplete()
        {
            foreach (var entry in _heap.ObjectTable.Values)
                entry.ReferenceDirty = false;
            _analyzedGraphVersion = _heap.ReferenceGraphVersion;
        }

        private void RefreshLifetimeMetadata()
        {
            foreach (var entry in _heap.ObjectTable.Values)
            {
                entry.ReferenceCount = entry.ExternalReferenceCount;
                entry.GroupExternalReferenceCount = 0;
                entry.GroupRuntimeTemporaryReferenceCount = 0;
                entry.GroupCallBufferStrongReferenceCount = 0;
            }

            var externalByGroup = _groups.Values.ToDictionary(g => g.GroupId, _ => 0);
            var temporaryByGroup = _groups.Values.ToDictionary(g => g.GroupId, _ => 0);
            var callBufferByGroup = _groups.Values.ToDictionary(g => g.GroupId, _ => 0);

            foreach (var entry in _heap.ObjectTable.Values)
            {
                externalByGroup[entry.ReferenceGroupId] += entry.ExternalReferenceCount;
                temporaryByGroup[entry.ReferenceGroupId] += entry.RuntimeTemporaryReferenceCount;
                callBufferByGroup[entry.ReferenceGroupId] += entry.CallBufferStrongReferenceCount;
            }

            foreach (var source in _heap.ObjectTable.Values)
            {
                foreach (var pair in source.OutgoingReferences)
                {
                    if (!_heap.ObjectTable.TryGetValue(pair.Key, out var target))
                        continue;

                    target.ReferenceCount += pair.Value;
                    if (source.ReferenceGroupId != target.ReferenceGroupId)
                        externalByGroup[target.ReferenceGroupId] += pair.Value;
                }
            }

            foreach (var group in _groups.Values)
            {
                var external = externalByGroup[group.GroupId];
                var temporary = temporaryByGroup[group.GroupId];
                var callBuffer = callBufferByGroup[group.GroupId];

                group.ExternalReferenceCount = external;
                group.RuntimeTemporaryReferenceCount = temporary;
                group.CallBufferStrongReferenceCount = callBuffer;
                group.IsDestructionCandidate = external == 0;

                foreach (var memberId in group.MemberObjectIds)
                {
                    if (!_heap.ObjectTable.TryGetValue(memberId, out var member))
                        continue;
                    member.GroupExternalReferenceCount = external;
                    member.GroupRuntimeTemporaryReferenceCount = temporary;
                    member.GroupCallBufferStrongReferenceCount = callBuffer;
                    member.IsDestructionCandidate = group.IsDestructionCandidate;
                }
            }
        }

        private void ValidateSettings()
        {
            if (MaxIncrementalObjects <= 0)
                throw new InvalidOperationException("MaxIncrementalObjects must be positive.");
            if (MaxIncrementalFraction <= 0 || MaxIncrementalFraction > 1)
                throw new InvalidOperationException("MaxIncrementalFraction must be in (0, 1].");
        }
    }

    public sealed class AloeReferenceGroup
    {
        internal AloeReferenceGroup(long groupId, IReadOnlyList<long> memberObjectIds)
        {
            GroupId = groupId;
            MemberObjectIds = memberObjectIds;
        }

        public long GroupId { get; }
        public IReadOnlyList<long> MemberObjectIds { get; }
        public int ExternalReferenceCount { get; internal set; }
        public int RuntimeTemporaryReferenceCount { get; internal set; }
        public int CallBufferStrongReferenceCount { get; internal set; }
        public bool IsDestructionCandidate { get; internal set; }
    }

    public readonly record struct AloeReferenceGraphUpdateResult(
        bool GroupsRebuilt,
        bool FullRebuild,
        int AnalyzedObjectCount,
        int GroupCount,
        long GraphVersion);
}
