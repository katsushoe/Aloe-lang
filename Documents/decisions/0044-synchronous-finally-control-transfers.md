# Synchronous finally across protected exits

Status: Adopted 2026-11-04; scope clarified by user 2026-11-05. Implemented; integrated validation recorded separately.

## Context
Chapter16 drafts finally, while decision0017 currently rejects it. Existing catch/rethrow and structured branch cleanup permit a compiler-lowering candidate, but supported control-transfer behavior needs an explicit contract.

## Proposed first subset
- Support `try { ... } finally { ... }` and existing ordered typed/catch-all handlers followed by finally, in synchronous free functions and class instance methods and class constructors. Ordinary synchronous static methods are independently unsupported and deferred. Async functions, direct WASM and all struct contexts remain unsupported.
- Execute the finally body exactly once on normal completion, return, break, continue and a handled/unhandled exception leaving the protected region, including nested handlers/finally guards. A break/continue that stays inside the protected region does not run finally yet.
- Evaluate a return expression before finally, preserve its exact value, then run finally before returning. A new exception from finally replaces the pending return/branch/original exception and follows enclosing handler rules.
- A finally body may declare locals, call functions, branch, loop and use nested try/catch/finally. Outward return/break/continue from that body are rejected in this first subset; break/continue within its own nested loops are allowed.
- Bare throw in finally is allowed only inside a user-written catch nested in that finally. Rethrowing a lexically enclosing catch from finally is deferred. Existing bare throw in ordinary catches remains.
- Finally sees the lexical bindings outside the protected try/catches; inner body/catch declarations do not leak, and a shadow inside the protected body cannot change the meaning of a finally reference.
- Keep interface/reference flow conservative across finally until a dedicated proof exists. Do not claim unrelated field/index CFG completion. Existing class readonly rules remain; no constructor-helper privilege is introduced.
- Candidate lowering should use existing structured branches, catch tables and hidden local slots. A runtime/opcode/codec change is not authorized by this proposal and would require a separate decision.
- Verify normal/fault/callee unwinding, exact-once and nested order, typed catch ordering/rethrow, return-value capture (including float sign/reference/struct payloads), break/continue destinations, finally failures, lexical shadows/const, readonly guards, malformed/rejected syntax, codec round-trip and both library TFMs. Complete L31 only after integrated evidence satisfies this adopted subset.

## Deferred scope
Outward transfers originating in finally, exception values/construction, async failure/rollback and struct-constructor definite-assignment integration are separate policies. This is not a promise for host process termination or cancellation outside normal VM control flow.

Scope clarification (2026-11-05): the user accepted the first subset in currently supported synchronous free functions, class instance methods and class constructors. Synchronous static methods are a separate implementation. The original inclusion of that unsupported syntax was a proposal error.

## Lowering and verification
The compiler uses existing blocks, branches, catch tables and hidden locals. Protected exits capture their destination and return payload before a shared normal epilogue; a generated fault epilogue runs finally then rethrows. Finally failures follow enclosing handlers. No runtime, opcode or codec changes are required. Interface facts are conservatively widened across finally and loop transfers.
Fifty-two new regressions cover exact-once ordering, nested returns and loop transfers, catch/rethrow/callee faults, exception precedence, lexical shadows/const, readonly constructors, scalar bits and reference/struct payload capture, unsupported contexts and conservative interface facts. The previous exception regression suite remains applicable. Integrated evidence: `.local/finally-product-20261105/acceptance.json`.
