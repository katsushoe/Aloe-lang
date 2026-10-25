# Basic fixed-length List subset

Status: Accepted, 2026-10-14.

## Context

The source draft defines T() types and parenthesized comma-separated List literals. Arrays already provide indexed heap fields, reference sharing, GC edges and Tick/query rules. A first List subset can reuse these rules while retaining a separate nominal collection kind.

## Decision

Support a single-level fixed-length T() for the existing seven primitive kinds, nominal enums, reference-free structs, classes, object and named boxes. Use (a,b), (single,) and typed empty (); (single) remains a grouping expression. List inference uses the first element. Typed contexts propagate empty/byte/boxing expectations through existing source boundaries.

Index reads/writes, readonly Length, captured foreach, struct element field rebuilds, reference identity and exact reference casts use existing collection lowering. Shared aliases see element updates. Reference elements support existing invalidation/consuming unbox; private field/getter/Query restrictions remain unchanged. Class element assignment accepts subclasses, with invariant collection types.

Reserve list:<array-leaf-descriptor>, such as list:int[], list:box:int[] or list:class:Base|Derived[]. The list prefix distinguishes it from an array, while leaf validation reuses existing exact kinds/ranges/names. No new Syscall IDs, opcodes or AloeBC layout. Older VMs reject the new descriptor; consumers need the matching runtime update.

Nested typed Lists, mixed List/array nesting, resizing/insertion/removal, methods such as Add, Sets/Maps and direct WASM remain unsupported. Opaque object elements may retain arbitrary live heap handles; this does not add nested static collection types.

## Alternatives

- Alias T() to T[]: rejected because it loses type/identity-of-kind checks and exact cast distinctions.
- Treat every (E) as a singleton List: rejected because it breaks grouping; use (E,).
- Add resizing now: deferred because FieldCount, length storage and Tick committed/volatile state need a separate contract.

## Impact and conditions

Existing initialization, counted strong edges, GC roots, struct snapshots and Query-write guards apply. Getter-internal reads use Committed fields; reading a returned collection after the getter completes uses the caller's current context. Descriptor names are a validation contract, not authenticity protection. No host permissions, deployment or external configuration changes.

ListTests cover the documented source after AloeBC round-trip, empty/singleton/grouping and function contexts, primitive/enum/struct values, reference roots and consumption, expression evaluation/foreach capture, Tick/query guards, exact casts, invalid syntax/access and raw descriptors. Bilingual language/VM specs and roadmap/progress describe the supported fixed-length subset and remaining operations.
