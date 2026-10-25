# Local reference invalidation and consuming unbox

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

Named boxes support nonconsuming extraction but source variables cannot yet become invalid. Chapters8/15.3 specify per-variable delete, idempotent deletion, invalid-reference exceptions and expected-type consuming unbox that preserves the variable on failure. AloeBC currently rejects Null constants even though the VM has a Null value.

## Decision

Implement delete and consuming unbox for plain heap-reference locals/parameters (generic object, named box, class and array references). Exclude this, fields, array elements, temporary/call results and pipes. Delete stores a runtime invalid marker in that local and never inspects/destroys its object. Repeated deletion succeeds; aliases, caller parameter copies, captured with/foreach targets and heap fields/queued references remain independent roots.

Represent source invalid references with the existing runtime AloeValue.Null. Add Syscall32 ReferenceInvalid to push that marker without adding a Null constant or changing AloeBC layout. Existing PushConst/StoreLocal instructions are not extended. Typed var x:ReferenceType; declarations without initializers store the same marker; typed primitive/pipe uninitialized declarations remain unsupported. Existing initialized var inference and typed let declarations retain their syntax.

Consuming unbox requires an expected primitive target: typed declarations/assignments/returns, function/constructor/method/host arguments, typed array elements/writes/indexes, primitive cast fallbacks and explicit Bool condition contexts. Expected-type scopes are restored around nested expressions; untyped inferred var unbox expressions are rejected. Syscall30 PrimitiveUnbox takes primitive descriptor and reference, checks a live handle first, then exact matching one-field box kind/domain, and pushes its primitive payload. Invalid references raise InvalidReferenceException, incompatible boxes InvalidCastException, malformed descriptors VmException. Only after successful extraction does the compiler generate the invalid marker and clear the original local. Failures leave it unchanged. Runtime Syscall30 alone does not clear any VM local or mutate/destroy a box.

Syscall31 ReferenceRequireLive preserves a live heap handle or raises InvalidReferenceException. Source box/class getters, async receiver calls, array access/Length/foreach and with member reads/writes use guards; unchanged raw operations retain their existing contracts unless this helper is explicitly invoked. Getter-local deletion/unbox is permitted because it only changes a local slot, not committed heap state.

Permit ref == invalid, ref != invalid and reversed equality checks; lower them to existing comparisons of the runtime marker. invalid is not assignable and is rejected in numeric/ordering/ordinary expression contexts. Reassignment can restore a local to a valid reference. Explicit as casts retain their prior cast-failure contracts; consuming unbox specifically prioritizes invalid-reference failure. No physical object deletion or new GC algorithm is added.

## Alternatives

- Extending Null constant serialization would change the current codec's supported constant contract; a small runtime marker syscall preserves its layout and existing codec tests.
- Freeing the heap object on delete would invalidate aliases and contradict per-variable lifetime rules, so GC remains responsible for reclamation.
- Clearing before extraction would lose the reference on failed casts; the compiler stores invalid only after the guarded extraction returns.
- Field/element lvalues require separate captured-target, query-write and atomicity contracts, so those consuming targets remain a future increment.

## Impact and conditions

InvalidReferenceException is a CommonLib exception caught by existing generic application boundaries. No new host/network/filesystem capability or heap payload mutation is introduced. Dropping the last local root allows existing GC to reclaim the object; aliases and existing strong edges preserve it. Invalidity is distinct from boxing kind mismatch in consuming extraction. Existing ObjectId identity equality is unchanged; validity checks use the established raw Null comparison behavior.

Old VMs cannot execute new Syscalls30/31/32 even though the AloeBC format is unchanged. Source uninitialized fields, typed-var initializers, consuming field/element/pipe targets, box-target with, broader implicit operator/condition unboxing and direct WASM remain unsupported. Tests round-trip AloeBC and cover documented examples, seven primitive targets, idempotence/reassignment/uninitialized refs, failed-local retention, dereference/with guards, parameter copies/typed expression contexts, explicit Bool conditions, getter locals, GC retention/reclamation, raw stack/errors and invalid syntax/context diagnostics. Bilingual specs, roadmap and progress state the local subset and limitations.

Expected array types also propagate through typed argument/return scopes, extending literal contexts to empty [] in those positions; the expected-context test exercises both.

The subsequent [reference-slot decision](0008-reference-slot-consumption.md) adds accessible reference-field/element targets and marker-aware reads; properties/primitive slots/this/pipes/temporary references remain excluded.
