# 0048: Readonly class fields for existing value and heap-reference types

Status: Adopted by user (2026-11-10); implemented; integrated validation recorded separately

## Context
Decision0042 implements readonly fields for seven scalar types. Existing class fields also support enum, struct and heap-reference types. The remaining type restriction leaves their readonly contract unfinished. Scalar setters0047 are accepted and remain ordinary methods for readonly checks.

## Decision
- Extend readonly instance class fields to every currently supported enum, struct and non-pipe heap-reference field type: class/interface/object, named boxes, and supported arrays/sets/lists/maps. Do not introduce a new field kind, initializer syntax or static state.
- Only `this.field` in the declaring class's constructor may assign or invalidate the readonly field binding. Multiple constructor assignments retain0042 behavior. Methods, setters, getters, derived constructors and with blocks cannot replace, delete or consume-unbox that binding.
- Readonly applies to the field value/reference binding. A readonly struct cannot be updated in place through a nested field path because this replaces the stored struct value. An object or collection reached through a readonly reference may still be mutated through already supported operations. This does not create deeply immutable objects or collections.
- Reading/copying the field reference creates an ordinary alias. Deleting or consuming that local alias does not change the readonly field slot; existing reference and GC rules apply. No new initialization, non-null, lifetime or thread-safety guarantee.
- Pipe fields, readonly declarations on structs, static fields, field initializers and direct WASM remain outside this contract.

## Alternatives
Keep seven scalar types only: smallest implementation, but leaves known field types inconsistent. Deep immutability: deferred because it changes alias, collection and object behavior far beyond a field-assignment modifier. Constructor-once assignment: rejected here because0042 already permits repeated assignments.

## Implementation and acceptance
Reuse field metadata and declaring-constructor checks. Apply them to every supported write/invalidation path, including nested struct writes and delete/unbox; do not remove runtime guards. Validate nominal typing, retained aliases, collection mutation versus replacement, base/derived constructors, setter writes, with blocks and existing scalar behavior. Publish an ADR and bilingual specification after adoption; require dual-target tests and all13 Release projects with zero warnings/errors. This is part of L39; remaining static/member contracts stay separate.

## Security and operations
No new host, filesystem or network capability. Existing VM trust boundary, GC and runtime/opcode contracts remain. No MSI lifecycle repeat or release is implied.

## Evidence
Implementation and dual-target validation: `.local/readonly-types-20261110/acceptance.json`. Existing readonly scalar and invalidation tests remain in the full suite. Existing grammar for class-held nested struct assignment is not expanded; mutating a copied struct keeps the stored snapshot unchanged.
