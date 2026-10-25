# Primitive identity casts

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

Section 15.1 explicitly says `as` does not perform numeric conversions or parsing. For a primitive target P, an operand already of P succeeds unchanged; corresponding box-object unboxing is a separate contract not implemented by the current VM. Calling this feature numeric conversion would contradict the language specification.

## Decision

Support `as` targets int, byte, char, float, decimal, string and bool/boolean. Preserve the original AloeValue only when its runtime kind exactly matches the target and it satisfies the existing signed-int32/finite-binary32 domain. Different primitive kinds, Null and heap references fail with InvalidCastException. No conversions, parsing, heap allocation, copying, boxing or unboxing occur.

Syscall 27 PrimitiveCast and 28 PrimitiveTryCast pop a canonical String target name then the value. Cast pushes the original value or throws InvalidCastException; TryCast pushes value/Null then a Bool success flag. Unknown/non-String descriptors raise VmException. The compiler branches on that flag to evaluate `ifnot` only on failure, and validates the fallback using PrimitiveCast. Omitted ifnot equals ifnot throw. Reuse existing Syscall opcode and AloeBC layout.

Fallback expressions must have the exact source target type. The existing in-range byte literal context is allowed in fallback; it does not convert the cast operand. Enum/struct/pipe operands and enum/struct targets remain rejected, avoiding erasure of nominal enum types stored as runtime Int. Heap operands may target primitives but currently always fail because box objects are not implemented. Primitive operands may not target references because that would require boxing.

## Alternatives

- Numeric widening/truncation via as contradicts the existing specification, so it is not implemented.
- Accepting arithmetic assignment promotions for fallback would hide a conversion, so fallback types are exact, apart from the existing byte-literal context.
- Encoding new opcodes or box-object layouts would broaden the feature unnecessarily; existing syscall/control-flow instructions suffice and boxing is deferred.

## Impact and conditions

Primitive casts are pure value checks with no heap, host capability, network or filesystem changes. Getter queries retain existing Committed/current reads. Float negative zero, decimal representation and String contents are preserved. Invalid raw int/float values fail rather than entering the source primitive domain. Runtime-kind checks cannot independently recover enum identity from arbitrary bytecode; source checks enforce that boundary.

Direct WASM rejects cast syntax explicitly until these syscalls are lowered. Tests execute source after AloeBC round-trip and cover all seven kinds, failure without conversion/parsing, lazy fallback/order/chaining, byte contexts, nominal guards, raw descriptors/domain/stack shape and Tick query reads. Bilingual specs, roadmap and progress distinguish this subset from future box-object unboxing.

The subsequent [boxing decision](0005-primitive-boxing.md) extends this original subset with primitive boxing and nonconsuming matching-box extraction; named APIs and consuming unbox remain pending.
