# Primitive boxing and nonconsuming unboxing

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

Primitive identity casts cannot yet extract values from object references. The language specifies a corresponding box object for each primitive, identity-preserving reference assignment, exact-kind nonconsuming unboxing and a separate consuming unbox operation. Existing Object handles, heap fields and reference graph can represent the first subset without a new value kind or codec format.

## Decision

Allocate a fresh heap box when a primitive crosses an expected generic object boundary, or is explicitly cast `as object`. Support int, byte, char, float, decimal, string and bool/boolean. Typed object array elements/writes, object locals/assignments, class fields, function/constructor/async arguments and return values share that conversion. Inferred primitive arrays retain their primitive type. Enum/struct/pipe values do not box.

Syscall 29 PrimitiveBox pops a valid primitive and pushes a new Object handle. Each box has one initialized Committed field and a reserved runtime TypeName box:<canonical-primitive-name>. Exact kind, signed-int32 and finite-binary32 domains are checked before allocation; invalid raw inputs raise VmException. Initialization and rooting complete under the existing heap lock. New boxes are immediately readable, including when allocated in Tick/getter execution, because their immutable payload has no Volatile mutation path in source.

Syscalls 27/28 additionally recognize a live one-field matching box descriptor, then validate its primitive payload. Success returns the original contained AloeValue without consuming/mutating the reference. Wrong/dead/uninitialized/malformed boxes fail with InvalidCastException (or Null/false for TryCast). Existing descriptor-format errors remain VmException. Getter queries read Committed payloads and callers read current payloads, preserving raw host Tick behavior. Source primitive assignment/argument/return/array-element/index contexts use the same nonconsuming runtime check when given heap references. Unboxing never performs numeric conversion or parsing.

Boxing occurs once at the boundary. Every boxing creates a distinct identity; copying existing Object references preserves the handle. Box payloads contain no heap references, including the existing managed String representation. Parent arrays/fields/queues use ordinary counted strong edges and roots; boxes add no separate GC implementation.

## Alternatives

- Storing primitive values directly in object[] would violate its heap-reference invariant and identity semantics; boxes preserve the existing array runtime checks.
- Using source class names such as Integer for private runtime descriptors would collide with current user-defined classes. Reserved box:int-style descriptors keep the prototype distinct until named builtin box types and APIs are implemented.
- New value kinds/opcodes or an independent runtime type table require a wider ABI migration. Existing Object fields and Syscall opcodes suffice for this subset.
- Consuming unbox requires assignable-reference invalidation and InvalidReferenceException semantics, so it remains a separate increment.

## Impact and conditions

Boxes are immutable through source APIs; raw heap callers can modify fields and must still supply valid descriptor/shape/payload values. Descriptor checks are not authenticity protection against arbitrary host/bytecode callers. Runtime Int values carry no enum tag; compiler nominal checks prevent enum boxing/unboxing. No network/filesystem or extra host capability is introduced.

Named box source types/constructors/ToXxx, broader implicit unboxing in operators/conditions, consuming unbox/delete and direct WASM remain unsupported. Compiler tests round-trip AloeBC and cover all seven kinds, implicit/explicit boundaries, exact/lazy failure, identity/aliasing, nested arrays/foreach/index contexts, class/getter/async/Tick behavior, GC roots and raw box domain/shape/corruption. Bilingual specifications, roadmap and progress link this decision and clearly separate the subset from full box-object APIs.

The subsequent [named-box decision](0006-named-box-types.md) adds source type names/constructors/ToXxx and invariant named-box arrays while preserving this runtime layout.
