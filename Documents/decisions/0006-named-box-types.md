# Named box types and extraction APIs

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

The boxing prototype already creates immutable-source boxes with reserved runtime names box:<primitive>. The language names the corresponding types Integer, Byte, Char, Float, Decimal, String and Boolean and defines ToXxx extraction. Those names and APIs were previously unavailable to source programs.

## Decision

Recognize all seven names as distinct builtin reference types. Constructors accept exactly one argument of the corresponding primitive, including the existing Byte constructor literal context and nonconsuming extraction from reference arguments in an expected primitive context. Validate the exact primitive domain then allocate a fresh box using Syscall29. Numeric conversions/parsing are not added.

Matching primitive values box when assigned/passed/returned to a named box type. Reference assignment and a successful reference cast preserve ObjectId. Generic object-to-named-box assignment requires an explicit as cast. Explicit primitive-to-named-box casts box the original primitive then perform a runtime reference check: incompatible boxed kinds throw InvalidCastException or use lazy ifnot fallback.

Expose only the corresponding zero-argument ToInt/ToByte/ToChar/ToFloat/ToDecimal/ToString/ToBool member. Validate the live named-box descriptor with ObjectCast before extracting the original primitive via PrimitiveCast. This preserves domain/shape checks and nonconsuming semantics. Private payload properties, setters and other conversion members are unavailable.

Named box arrays are invariant reference arrays with runtime descriptors box:int[], box:string[] etc; nesting appends []. Supported literals, empty contexts, indexes/writes/Length/foreach and source boundaries reuse existing array behavior and counted strong references. Each leaf must be a live one-field matching box with a valid exact-kind payload. Primitive element contexts box only the corresponding primitive. Byte values should be typed byte or explicitly constructed with new Byte(255); a Byte-typed reference context does not reinterpret an int literal. Array casts require exact descriptors and rank.

Use the existing Syscalls25/26/27/28/29, Object representation, heap fields and AloeBC layout. Factor shared descriptor/kind/payload validation rather than adding synthetic user-class methods or independent type metadata.

## Alternatives

- User classes named Integer/String/etc would conflict with the language's builtin type contract. Reserve all seven names for class/enum/struct declarations; existing user types with these names must be renamed. Builtin boxes cannot be inherited.
- Exposing private payload fields or general ToXxx conversion overloads would weaken exact-kind boxing semantics, so only the corresponding extraction method is supported.
- Generating ordinary class signatures/method bodies would add constructor/callgraph bookkeeping for a fixed intrinsic mapping. Dedicated frontend recognition reuses the existing runtime contracts without new ABI IDs.

## Impact and conditions

Runtime box names/payload layout remain unchanged from the earlier boxing prototype; raw host-created objects with names such as Integer do not become authentic boxes. Descriptor/shape/domain checks are prototype validation, not authentication against arbitrary raw hosts. Getter extraction reads Committed payloads and caller extraction reads current payloads; new immutable-source boxes are initialized immediately. Existing GC roots/edges retain typed references and queued arguments. No extra network/filesystem or host capability is introduced.

Consuming unbox/delete, box-target with blocks, broader implicit unboxing in operators/conditions and direct WASM remain unsupported. Source reference fields in structs remain disallowed. Tests execute after AloeBC round-trip and cover exact documentation examples, all seven constructors/methods, typed boundaries and fresh/alias identity, cast/fallback behavior, array rank/type invariance, GC/Tick/query behavior, reserved-name/count/member diagnostics and raw corrupt descriptor/shape/payload checks. Bilingual specs, roadmap and progress distinguish supported names/APIs from the remaining features.

The subsequent [local-validity decision](0007-local-reference-validity.md) adds local/parameter delete and consuming unbox with invalid-reference checks; field/element targets remain pending.

The subsequent [named-box with decision](0009-named-box-with.md) adds captured named-box expression targets and matching .ToXxx() extraction.
