# Stable Struct Integer-Key Sort Subset

Status: Accepted, 2026-10-21. Implemented.

## Context

The standard-filter catalog sketches `sort(key)` but does not define supported element/key types, ordering, ties, or argument syntax. The VM currently implements argument-free signed-int32 ascending sort for `pipe<int>`. Accepted decision 0024 adds immutable structs with primitive fields, including `int`.

## Decision

- Preserve argument-free `sort : pipe<int> -> pipe<int>` exactly as currently implemented.
- Add `sort: .Field` for `pipe<T>` only when `T` is a declared Aloe struct and `Field` names a declared `int` field of that struct.
- Require an exact, case-sensitive direct field name. Reject unknown fields, nested paths, computed expressions, non-struct elements, and non-`int` fields at compile time. Validate the same schema when binding raw AloeBC.
- Sort by signed int32 field value in ascending order. Use a stable sort so records with equal keys retain their input order. Return the original records unchanged, preserving every record and duplicate.
- Consume input to closure before emitting results. Empty input emits no records and closes the output. Respect bounded output-pipe backpressure while emitting; the full input buffer uses memory proportional to item count.
- Do not add descending order, multiple keys, string/float/other key types, nested fields, or a key selector expression in this increment.

## Alternatives

- Support every primitive key type immediately: deferred because string collation and float edge ordering require separate contracts.
- Permit arbitrary key expressions: deferred because evaluation count, side effects, failure timing, and type restrictions need a separate contract.
- Leave tie order unspecified: not selected because a stable result is deterministic and preserves source order among equal-key records.

## Implementation and acceptance

Implemented source registration/type checking, raw-bytecode binding validation, and a cooperative buffered VM filter. Coverage includes int32 key boundaries, ascending order, stable ties, duplicate records, bounded input/output, invalid key names/types and nested paths, raw AloeBC schema validation, and source/AloeBC execution. Both standard-filter catalogs and README summaries describe the supported subset.
