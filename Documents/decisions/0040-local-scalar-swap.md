# Local scalar swap

## Status
Accepted by the user on2026-11-04 (proposal0040). Implementation acceptance is recorded separately.

## Context
Language chapter14 specifies swap(a,b) with local int variables. Target forms, conversions and initial supported types require a bounded contract.

## Decision
Implement `swap(a,b);` for two identifier operands resolving to locals or function parameters with exactly the same declared type: int, bool, byte, char, float, decimal or string. Read both slots before writing either slot; preserve their exact values and lexical scope. A same-slot exchange is a no-op. Use declared slots rather than temporary narrowing. No implicit conversions, boxing or nominal erasure.

Fields, properties, indexed/called/compound operands, enum, struct, class/interface/object, collection and pipe references are rejected. Direct WASM retains its int/bool/byte/char/float subset; decimal/string remain ordinarily unsupported. Future immutable local bindings must treat swap as a write to both operands.

## Alternatives
- Full lvalue/reference swap: deferred because target evaluation, invalidation and struct ownership need a separate contract.
- Implicit numeric conversions: rejected because value/type preservation requires an exact type match.
- New swap opcode/syscall: unnecessary; existing LoadLocal/StoreLocal transfers implement the contract.

## Impact and security conditions
Compiler syntax and tests only; no runtime, bytecode schema, dependency or capability expansion. Validate both declared types before emitting stores. Interface write tracking records both writes; speculative scanners may remain conservative around swap.

## Operating conditions
Ordinary local scope and parameter rules apply in synchronous functions and existing method/constructor/getter contexts. No external operation or installation is involved.

## Implementation, tests and documentation
CompilerLib/AloeCompiler.Swap.cs and ParseStatement dispatch implement the syntax. SwapTests and Aloe2WasmTests verify seven VM scalar kinds, parameters, shadows, identical slots, bit preservation, loops/try, invalid targets/types and VM/Node WASM parity. Bilingual language chapter14 documents this subset. Mark the fixed swap milestone complete only after integrated product validation.
