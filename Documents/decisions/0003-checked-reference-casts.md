# Checked reference casts and lazy fallback

Status: Accepted for the C# compiler/VM prototype, 2026-10-13.

## Context

Object arrays erase the source class/array type. Section 15.1 specifies `E as T` with InvalidCastException on failure and lazy `ifnot` fallback. Existing heap TypeNames and compiler class allowlists already describe the supported reference subset.

## Decision

Support casts between Object expressions and class, object or supported array targets. Evaluate E once and retain the same ObjectId on success. Require a live handle; class targets check declared/known-derived names, array targets require exact canonical descriptors, and object accepts any live heap Object. Preserve invariant array types and rank.

Add Syscall 25 ObjectCast and 26 ObjectTryCast, without a new opcode or codec format. Both pop a String descriptor then the value. ObjectCast returns the reference or throws System.InvalidCastException. ObjectTryCast returns reference/Null and then a Bool flag. Malformed target descriptors raise VmException. The compiler branches on the flag, evaluates fallback only on failure, and validates that fallback using ObjectCast. Omitted ifnot and ifnot throw use ObjectCast directly.

Descriptors are object, class:Base|Derived and array:<canonical-array-name>. The class list is compiler-generated, base first with remaining assignable names ordinal sorted. The VM checks unique identifier names and exact TypeName membership; it does not independently reconstruct inheritance or authenticate caller-supplied bytecode descriptors.

## Alternatives

- Silent Null on a default failed cast conflicts with the specified exception contract; Null is only the internal failed-try result.
- Eager fallback evaluation would execute effects on successful casts, so the existing control-flow instructions implement lazy evaluation.
- New runtime type IDs and inheritance tables would broaden the ABI beyond the existing descriptor prototype; independent metadata remains future work.

## Impact and conditions

Checks allocate/copy/mutate nothing and preserve reference identity. Existing stack/local roots retain source/fallback values through host-triggered GC. Getter-query checks are read-only and later accesses retain existing Tick/Committed rules. Unlike pure ObjectId equality, casting requires a live reference.

Unary operators bind above casts, which bind above multiplication. Fallback consumes a full expression; parenthesize the fallback cast before a larger operation. Class getters and array postfix reads work on cast results; async calls use existing locals/with.

Primitive/scalar String/struct/enum casts, numeric conversion, boxing/unboxing, nullable casts and direct WASM remain unsupported. Nine cast tests cover source/bytecode round trips, lazy fallback and evaluation order, class inheritance, array invariance/rank/nominal descriptors, failure kinds, Tick queries and GC roots. CLI/REPL generic exception boundaries report failed casts through their existing error paths.

The subsequent [boxing decision](0005-primitive-boxing.md) extends this original subset with primitive boxing and nonconsuming matching-box extraction; named APIs and consuming unbox remain pending.
