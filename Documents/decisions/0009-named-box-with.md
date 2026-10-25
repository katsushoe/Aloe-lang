# Named-box with targets

Status: Accepted, 2026-10-13.

## Context

Named boxes already expose one nonconsuming matching ToXxx() method. Existing with blocks capture class instances once and resolve abbreviated members across mixed, nested scopes. Boxes should follow those reference and lookup rules without exposing their private payloads.

## Decision

Allow statically named box expressions as instance targets. Capture each expression once from left to right in a rooted local, and resolve only its matching dot-prefixed .ToXxx() call in value expressions. Reuse named-box live-reference, exact descriptor and primitive extraction checks. The reference is not consumed. Original-variable reassignment/delete does not retarget the block.

Box targets participate in existing same-block ambiguity checks, mixed class/enum/struct scopes and innermost-first lookup. Bare box type targets, opaque object targets, payload access/writes, wrong methods/arguments and discarded extraction results are rejected. Existing APIs require an explicit named cast for opaque objects. An invalid target is checked on member use, as with existing instance targets.

## Alternatives

- Expose raw box payload fields: rejected because payloads remain private and immutable.
- Re-evaluate receivers per member: rejected because it changes side effects, identity and rooting relative to existing with behavior.
- Add operator/condition implicit extraction simultaneously: deferred as a separate type-inference and evaluation contract.

## Impact and conditions

Compiler-only extension; existing Syscalls and AloeBC format are unchanged. Source result kinds, invalid-reference/cast errors, GC roots and Tick/query payload reads reuse existing contracts. No host permissions, external configuration or deployment changes are introduced. Direct WASM remains unsupported for named boxes. No new concurrency guarantee is added.

BoxWithTests execute after AloeBC round-trip and cover the documented captured-target example, all seven types, one-time ordered evaluation, GC, alias/delete retention, mixed/nested scopes, Tick/getters and rejection diagnostics. Bilingual language specifications and the roadmap describe this subset; broader implicit unboxing remains pending.

The subsequent [operator-unboxing decision](0010-operator-condition-unboxing.md) adds exact nonconsuming extraction in supported operators and boolean conditions.
