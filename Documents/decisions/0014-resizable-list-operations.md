# Resizable List Operations

Status: Accepted, 2026-10-14.

## Context

The fixed-length List prototype shares the VM's heap-backed array storage. List values have stable ObjectIds, aliases share the same object, and collection writes follow Committed/Volatile Tick semantics. The heap currently stores a fixed number of committed and volatile fields per object, so resizing must update storage and reference-graph edges without changing object identity.

## Decision

Add `List.Add(value)` and `List.RemoveAt(index)` for source List locals. Add appends one exactly typed element; RemoveAt removes one existing element and shifts later values left. Both mutate the existing List object, return void, preserve its runtime descriptor and ObjectId, and remain visible through aliases. Length remains read-only. Empty lists can grow with Add; RemoveAt on an empty or out-of-range index fails at runtime.

Resizing is a VM operation restricted to the `list:` descriptor family. It preserves Committed and Volatile snapshots during Tick, exposes the Volatile length and elements in normal execution, exposes Committed values in property queries, and commits the complete resized snapshot at the normal Tick barrier. Shrinking or removal retains old Committed references until commit and removes dropped references after commit. Runtime element validation remains identical to ArrayCreate/Set. Add and RemoveAt are rejected inside Committed property queries.

## Alternatives

- Reallocate a new List object and replace the receiver: rejected because aliases and reference identity would break.
- Resize fields only outside Tick: rejected because List mutators must obey the same execution model as existing element writes.
- Make Length assignable or introduce a generic Resize operation: deferred because growth requires a default-value contract for every supported element kind.
- Add/remove via host APIs only: rejected because ordinary source code must mutate Lists without host capabilities.

## Impact and conditions

The heap entry's field snapshots and physical slot allocation become resizable while ObjectId remains stable. GC outgoing-edge accounting must include both Committed and Volatile snapshots until Tick commit. No opcode or AloeBC layout change is required; new Syscall IDs are required and older VMs cannot execute modules using the new operations. This adds no host permissions or external configuration. Tests must cover aliases, growth/shrink, bounds, exact element kinds, reference edges, and Committed/Volatile Tick behavior. Source documentation and VM syscall specifications must describe the exact contract.
