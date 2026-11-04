# Explicit this instance method calls

## Status
Implementation refinement of existing method-call contracts (2026-11-04); product validation recorded separately. No new scheduling, visibility or runtime policy.

## Context and decision
Class instance code accepts `this.method(arguments)` in expressions and statements. Resolve the named instance method through the current declaring class and existing inheritance lookup; never fall back to a same-name global function. Global/static code has no this receiver, static methods cannot be called through this, and a missing method rejects.

Private/protected synchronous methods use existing typed argument/return lowering and one implicit receiver. Void calls are statements; non-void results must be used. Public async statements use the existing instance enqueue, virtual dispatch and async cycle tracking; they cannot be values. Arguments are evaluated once in order. Existing bare-call lookup, struct-this behavior, field access, readonly checks and getter/Tick rules remain.

## Alternatives
- Treat this as a general reference value: deferred; only this method syntax is added.
- Duplicate call lowering: rejected; the resolved synchronous argument/emission tail is shared.
- Synchronously invoke public async methods: rejected; the existing command queue contract is retained.

## Impact, security and operation
Compiler syntax/lowering only. No arbitrary other-instance private access, new runtime/syscall/opcode/bytecode format or dependency. Constructor helpers receive no readonly write privilege. Current inheritance/visibility rules still apply. Direct WASM class rejection remains.

## Implementation, tests and documentation
AloeCompiler.ThisCalls.cs resolves the explicit receiver and routes calls; ParseResolvedFunctionCall shares existing synchronous argument handling. ThisMethodCallTests cover scalar values, void constructor helpers, getter calls, global-name collisions, inheritance, argument evaluation, async timing/virtual dispatch/cycles and rejected forms. Bilingual specification note documents the syntax. Existing completed L13 gains coverage but no extra progress point.
