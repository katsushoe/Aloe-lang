# Basic Set Subset

Status: Accepted, 2026-10-14.

## Context

Aloe has fixed arrays and resizable Lists backed by heap fields. The language roadmap calls for Sets and Maps, but no source syntax, equality contract, or runtime descriptor had been chosen. This decision introduces a small Set subset using the existing collection identity, snapshot, and heap-reference machinery.

## Decision

Use `T{}` as the Set type suffix and `{value, ...}` as a Set literal. An empty literal requires an expected Set type. Support primitive scalars, strings, normal/bitfield enums and statically named class references. Class references compare by ObjectId identity; structs, generic object references and other references are rejected by the source compiler. Duplicate values collapse, preserving the first insertion position. Iteration follows insertion order. `Count` is read-only; `Add(value)`, `Remove(value)`, and `Contains(value)` require the exact element type. Remove of a missing value is a no-op. `UnionWith(other)`, `IntersectWith(other)`, and `ExceptWith(other)` require the exact same Set descriptor. Union appends new source elements in source order; intersection and difference retain destination order. Self-operations are valid. Sets are not indexable and do not expose resize or direct WASM support.

Set values keep one heap ObjectId, so aliases observe mutations. Add, Remove and Clear preserve existing List snapshot behavior: normal execution sees Volatile Tick state, property queries see Committed state, and mutation is forbidden inside committed property queries. Clear removes every element and retains the empty Set handle for aliases to reuse. Runtime descriptors use `set:<array-element-descriptor>` and Syscalls 38-40 and 48-51; no opcode or AloeBC encoding changes.

## Alternatives

- Reuse List syntax and permit duplicates: rejected because it would not provide Set semantics.
- Sort values for canonical iteration: deferred; insertion order is stable and avoids reordering values on each mutation.
- Support struct/generic object references: deferred until Aloe defines complete equality and hashing for those types. Statically named classes already have defined ObjectId identity equality.
- Introduce opcode and bytecode collection layouts: rejected for this subset because the existing heap-backed collection path already supplies identity, GC edges, and Tick snapshots.

## Impact and conditions

The heap may resize `set:` objects while preserving ObjectId and the Committed/Volatile field snapshots. Runtime Set creation canonicalizes duplicates; the VM validates every element. Syscalls 38-40 and 48-51 are incompatible with older runtimes, while the AloeBC encoding is unchanged. No host permissions or external configuration are added. Tests cover duplicate literals, Add/Remove/Clear and set algebra, Count, alias identity, insertion order, invalid element types, and Tick/query behavior. Map remains a separate design and implementation task.

## Interface reference extension

The Set subset also supports interface references. The collection type remains invariant, while elements or keys/values may use conforming class instances. The descriptor and dispatch rules are defined in [the interface decision](0018-interface-async-dispatch-subset.md); existing identity, heap and Tick rules apply.
