# Object identity equality prototype

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

The source compiler already accepts `==` and `!=` between Object expressions, but VM equality previously rejected Object values. Class and array aliases use stable Aloe heap ObjectIds, including when GC relocates their banks. Reference arrays make this frontend/runtime mismatch visible in ordinary source programs.

## Decision

Compare two Object values by their ObjectId payloads. Equal IDs yield true for CmpEq and false for CmpNe; different IDs yield the opposite. Class fields, array contents and runtime class names do not participate. This applies to class instances, all heap-backed arrays, base/derived aliases and opaque object references.

The operation does not dereference the heap or require a live handle. Raw runtime/bytecode callers compare supplied ID payloads, including stale or unallocated IDs; dereference/array operations retain their existing live-handle guards. Existing different-kind raw comparisons return false and Null equals Null; source type checking continues to reject Object/scalar mixes. Struct equality and Object ordering remain unsupported.

Use the existing CmpEq/CmpNe opcodes and AloeBC format, with a local branch in ComparisonSupport.Equal. No casts, equality override protocol, heap allocation, reference-graph updates or direct WASM object support are introduced.

## Alternatives

- Comparing physical heap positions would become incorrect after GC moves objects, so stable IDs are used.
- Comparing class fields or array contents would change reference semantics and require recursion/cycle handling and user equality contracts, so structural equality is deferred.
- Checking liveness while comparing is unnecessary for identity and would make a pure value operation depend on heap state; actual dereference operations already validate their inputs.

## Impact and conditions

The change is a pure comparison of existing values with no network/filesystem or host capability effects. It adds no object roots and does not alter ownership, GC or Tick rules. Getter expressions still read Committed references and caller expressions read their existing current references before equality is applied.

Source tests execute after AloeBC round-trip and cover aliases, equal-content/different objects, inheritance and generic references, nested arrays/self-cycles, evaluation order/short circuit and Tick queries. Runtime tests cover raw ID/kind/Null behavior, continued Struct rejection and actual GC bank evacuation. Bilingual language/VM notes and the roadmap/progress record state the prototype contract and unsupported scope.

The subsequent [operator-unboxing decision](0010-operator-condition-unboxing.md) permits primitive/reference scalar comparison via exact extraction while preserving two-reference identity comparison.
