# Local scalar const

## Status
Accepted by the user on 2026-11-04 (proposal0041). Product validation is recorded separately.

## Context
Chapter5.3 requires annotated, non-reassignable const bindings. Global scope and compile-time evaluation are not defined by the current function-body compiler subset.

## Decision
Implement `const name: T = expression;` for local int, bool/boolean, byte, char, float, decimal and string bindings. Both annotation and initializer are required. Evaluate the initializer exactly once whenever execution reaches the declaration, with the ordinary typed-let conversion, range and exception behavior. This is runtime initialization, not compile-time constant evaluation. Reads, copies, arguments and returns preserve ordinary scalar semantics.

Reject direct reassignment and either operand of swap, including a same-slot swap. Check resolved slots rather than names; an inner lexical binding is distinct and an initializer reads the outer binding before the new binding exists. Local slots are not reused within a function and mutability state is cleared between functions. Existing var/let bindings and parameters remain writable.

Reject global const and nominal/reference/collection/pipe types in this subset. Direct WASM retains int/bool/byte/char/float; decimal/string retain ordinary target rejection. No deep immutability or constant folding is promised.

## Alternatives
- Full constant evaluation: deferred because permitted expressions and evaluation errors require a separate contract.
- Global/reference constants: deferred because lifetime, ownership and mutation policy require specification.
- Name-based immutability: rejected because lexical shadowing and per-function slots require binding identity.

## Impact and security conditions
Compiler-only local state and validation; no new runtime, opcode, syscall, bytecode layout, package or host capability. Both swap operands are checked before any store. Lexical/shadow and primitive exception-flow scans recognize const declarations conservatively, without assuming an arbitrary initializer is nonthrowing.

## Operating conditions
Existing local declaration contexts and scope rules apply. No installation, external access or environment mutation is involved.

## Implementation, tests and documentation
ParseLetDeclaration shares ordinary typed initialization. AloeCompiler.Const.cs owns immutable-slot validation, and assignment/swap call it. ConstTests and Aloe2WasmTests cover seven scalar kinds, annotation/initializer/type errors, forbidden writes, lexical/function boundaries, runtime side effects/exceptions and VM/Node WASM parity. Bilingual chapter5.3 documents the subset. Complete fixed criterion L37 only after integrated validation; global/reference/compile-time policies remain later work.
