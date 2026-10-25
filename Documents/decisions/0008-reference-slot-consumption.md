# Reference field and array-slot consumption

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

Local delete/unbox cannot invalidate heap-owned reference variables. Existing class fields and reference-array elements already use counted strong edges and Tick Volatile/Committed state. Clearing these slots needs a marker-aware read path and existing query-write protection, without destroying the shared object.

## Decision

Extend lvalues to accessible reference fields (this/private with fields in the declaring class) and indexed reference-array elements, including nested array references and array-valued function/property/literal receivers. Capture receiver and index once in implicit rooted locals. Array/property contents may be changed through a returned array reference; the property itself is not an assignable target. Plain parenthesized locals/elements preserve their lvalue identity. Temporary object construction/call results, primitive/struct slots, this and pipes remain excluded.

On unbox, read the captured slot, call existing PrimitiveUnbox for live/exact-kind/domain validation and clear the same captured slot only after success. Failed extraction retains the stored reference. Delete clears the captured slot without first reading/dereferencing its element, allowing repeated deletion of invalid slots. An invalid owner or out-of-range index still fails. There is no atomic read/extract/clear guarantee across concurrent writers; a successful clear applies to the captured slot's then-current state.

Syscall33 ReferenceArrayInvalidate and34 ReferenceFieldInvalidate pop index and owner, require a live owner/in-range reference slot, reject Committed Query writes and store the existing Null marker with SetVolatileField during Tick or SetField otherwise. Existing edge accounting keeps old Committed references until commit and removes cleared current references. Syscall35 ReferenceFieldGet reads current/Committed Object-or-Null fields with marker-aware access. The source compiler selects it only for reference fields, preserving primitive/pipe raw ObjectFieldGet contracts. Raw field helpers rely on source field types and supplied shape/value consistency rather than an independent field type table.

Reference-array ArrayGet now returns Null for an invalid reference element, permitting validity checks/aliases; subsequent dereference/consuming unbox raises InvalidReferenceException through existing guards. Primitive arrays keep their prior validation. Raw ArrayCreate/ArraySet still reject Null/scalar reference elements: explicit invalidation uses dedicated syscalls, not an assignable source invalid literal. Raw hosts may directly create marker fields under the existing trusted heap interface.

Direct getter heap consumption is a compile error; transitive helper consumption in a query fails at runtime before any write. Local getter consumption remains supported. Access controls are unchanged: public getters are read-only destinations, private fields remain restricted to the declaring class, and source has no named-box payload field API.

## Alternatives

- Re-evaluating receiver/index after extraction would repeat effects or clear a different slot, so both are captured/rooted once.
- Clearing via ordinary ArraySet would need general Null assignment permission and weaken reference-array creation/write guards; dedicated invalidation preserves that boundary.
- Changing all raw ObjectFieldGet Null behavior would affect primitive uninitialized fields; the reference-only helper isolates the new source behavior.
- Destroying the referenced object or introducing a new invalid value kind/codec layout would break aliases or broaden the ABI, so the existing marker/GC model is reused.

## Impact and conditions

The change updates existing heap fields/edges without new host/network/filesystem capability or a new GC algorithm. Array/field aliases observe invalidation, while separate references to the original object remain valid. Captured owner locals can extend owner lifetime until function return. Source bytecode requires new Syscalls33/34/35; older VMs cannot execute them even though opcode/codec layout is unchanged.

Consuming private fields of unrelated classes, property setters, this/pipes/temporary reference targets, box-target with, broader implicit operator/condition unboxing and direct WASM remain unsupported. Tests round-trip AloeBC and cover documented example, receiver/index effects and GC, nested/class/object/box slots, restore/aliases, failure retention, private this/with fields, Tick committed/current views and reclamation, query rejection, unsupported destinations and raw bounds/kind/marker/stack behavior. Bilingual notes, README and progress describe the updated non-null-initialization/invalid-slot distinction.
