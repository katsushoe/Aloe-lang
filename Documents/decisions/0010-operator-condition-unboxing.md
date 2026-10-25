# Nonconsuming operator and condition unboxing

Status: Accepted, 2026-10-14.

## Context

Expected primitive assignment/argument/return boundaries already extract matching boxes without consuming references. Operators and boolean conditions must use the same no-conversion contract while preserving established reference equality, operand order and short-circuit behavior.

## Decision

Arithmetic, ordering and scalar/reference comparisons determine an extraction target from the named box type, the other primitive/named-box operand, or a fixed numeric result context. Two statically opaque operands in numeric expressions require that context or explicit casts. Integer-only operators supply an int context for opaque operands; named boxes retain their own primitive kinds and must satisfy existing operator typing. Byte/int/float/decimal promotion remains an operation on extracted primitive values, never a conversion during extraction. Decimal/float mixing and unsupported native operators remain rejected.

Boolean conditions, not, and/or use a bool extraction context. Check the left operand before evaluating the right; evaluate each expression once and keep right-side checks inside the executed short-circuit branch. Compiler lowering can insert the left cast ahead of the already parsed right expression using structured control flow. No VM opcode or codec layout changes are needed.

Two reference operands of ==/!= retain ObjectId equality, including named boxes. A primitive/reference pair extracts the reference and follows existing scalar comparison rules. Validity checks against invalid retain their existing reference semantics. Extraction never clears variables, fields, elements or aliases. Invalid/wrong/corrupt boxes use the existing PrimitiveCast InvalidCastException contract, as specified for implicit E as P.

## Alternatives

- Unbox both references for equality: rejected because it changes established identity comparison.
- Guess the runtime primitive kind of two opaque numeric operands: rejected because the compiler would lack a stable result type.
- Convert box payloads to the peer type: rejected because extraction explicitly forbids numeric conversion/parsing.
- Validate the left operand after evaluating the right: rejected because failed left extraction must prevent right-side effects.

## Impact and conditions

Compiler-only source extension with existing primitive arithmetic, range checks, GC roots and Tick/query reads. No host permissions, external configuration, heap mutation or deployment changes. Operators on strings/chars/bools remain limited to their existing supported native subset; user conversions, dynamic numeric dispatch and direct WASM boxes remain unsupported.

OperatorUnboxingTests execute after AloeBC round-trip and cover the exact documented source, numeric kinds, expected/peer contexts, boolean short circuit, integer masks/shifts, scalar/reference versus identity comparisons, one-time effects and left failure, arrays/GC/Tick/getters and compile/runtime failures. Bilingual language specifications, roadmap and progress describe the supported contracts and limitations.
