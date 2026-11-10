# Synchronous static scalar methods

Status: Adopted by user (2026-11-05); implemented; integrated validation recorded separately

## Context
The existing compiler accepts only public static async methods. Decision0044 clarified that ordinary synchronous static methods are a separate feature. The current static context already has no receiver, and ordinary synchronous calls use existing Call/Return instructions.

## Proposed first subset
- Accept `public static method name(...): T { ... }` in classes, with seven scalar argument/result types (int, bool, byte, char, float, decimal, string), plus void results.
- Invoke explicitly as `ClassName.name(args)`: scalar results in expressions and void calls as statements. Reject discarded scalar results and void expressions, retaining ordinary arity/type checks and argument evaluation order.
- Execute immediately on the current synchronous stack, allowing normal function calls, recursion and the adopted synchronous try/catch/finally contract. Do not enqueue a Tick callback or add an instance receiver.
- Keep this/instance fields/methods inaccessible from static code. Existing static async behavior remains unchanged. Private/protected static methods, virtual/override/inheritance lookup, overloads, static state, type-based with shorthand, reference/enum/struct signatures and direct WASM support remain deferred.
- Reuse existing bytecode/runtime/codec, with positive and negative tests including context isolation, recursion, scalar payload preservation and finally/callee failure propagation.

## Effects and alternatives
This adds previously rejected syntax without changing existing accepted programs or runtime operations. General static member support is a larger alternative and is deferred to keep the initial contract reviewable. No external service, persistence, privileges or release operation is introduced. L39 remains open until its remaining readonly/setter/static contracts are separately accepted.

## Implementation and evidence
Class declarations retain synchronous static methods separately from queued static async methods. Explicit type-qualified value/void calls share ordinary function argument checks and Call/Return lowering. Static context cannot borrow local slot0 as an implicit instance receiver; local names shadow type qualifiers. No runtime/opcode/codec changes. Accepted evidence: `.local/static-scalar-20261105/acceptance.json`; regression fixture: `Test_CompilerLib/StaticScalarMethodTests.cs`. Existing static async, instance, this and finally tests are retained.
