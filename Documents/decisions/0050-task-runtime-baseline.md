# 0050: Task runtime baseline and separate VM optimizations

Status: Accepted by the user on 2026-11-10; not implemented.

## Context
The user adopted all recommendations in the Task compatibility review and asked that unresolved details be tracked in TODO. Baseline adoption does not resolve the open details or certify an implementation. The compiler resolves types/members while emitting bytecode, without a separate AST/semantic-analysis pipeline. The existing runtime has ObjectIds, Bank allocation and reference groups, but no Task-specific type pools. Object delete clears a reference slot rather than all aliases. Tick drains CallBuffers, commits Volatile fields and updates roots/reference groups; queued unhandled exceptions currently fault Tick, discard queues and skip commit.

## Decision
- Add task declarations and a dedicated Task reference type. Construct synchronously with new, initially Stopped. Ordinary Objects may be constructor arguments. Tasks cannot be method/constructor arguments; type erasure must not bypass this restriction.
- External Task method calls are implicitly asynchronous; self calls are synchronous. Scheduler-only run(): void has no arguments and executes at most once per Tick. Stopped suppresses scheduled run calls but still allows ordinary notifications to be processed.
- Use Stopped, Running and Deleted states. Start/stop affect run eligibility from the next Tick. Delete immediately removes pending execution, including a captured target that has not begun running. Self-deletion and active-frame cleanup remain open.
- Initially execute Tasks sequentially on the coordinator thread, without an OS thread per Task or changing the existing Object worker policy.
- Tick phases are: capture stable Task identities, Task processing, existing Object CallBuffer quiescence, Volatile commit, runtime-root/reference-graph update. Nested Tick is forbidden. Ordering between Task notifications and run remains open.
- Scan types in source declaration order, then slots in ascending index order. Use per-type chunked pools with pooled field storage, LIFO free-slot stacks, no compaction on deletion and reference stability across growth.
- Separate non-reused Task identities from reusable pool indexes. Check Task-wide liveness so all aliases become unusable after deletion; preserve ordinary Object delete semantics. Do not add Task-specific generation counters by default. Identity allocation/exhaustion and active-frame reclamation remain open.
- Preserve Object ownership, CallBuffer ordering, Volatile rules, commit boundaries and Reference Group semantics. Integrate Task-held Object references into root/graph management; exact lifetime rules remain open.
- Initially restrict asynchronous calls inside Task try/catch/finally after resolving the invocation target. Do not impose this new restriction globally on existing Object programs. Direct versus transitive effects remain open.
- Keep the existing Object queued-failure policy initially. Do not introduce automatic rollback. Task unhandled-failure reporting, continuation and state transitions remain open.
- Implement basic Tasks first. Object pooling and CallBuffer batching are separate later optimization phases. Different receiver identities or equal method names alone do not prove independence.

## Initial-subset details and remaining work
The user adopted [decision0051](0051-initial-task-execution-contract.md) on 2026-11-13. It settles the first-subset syntax, local-only Task handles, notification snapshots, self-deletion/unwind, explicit lifetime, pool identity/layout defaults and initial Tick-fault/direct-invocation policies. The earlier open-detail statements above are historical motivations; decision0051 is the normative refinement for that initial subset. The canonical checklist remains `.local/TODO.md`, TASK-01 through TASK-10: first-subset design items01..09 are resolved, while implementation, tests/measurements and broader follow-ups remain. Neither ADR claims Task implementation acceptance. Adopted decisions need not be requested again.

## Alternatives
Per-Task OS threads and ordinary Object allocations are not adopted because the initial design requires sequential scheduling and dedicated type pools. Reference-slot clearing alone cannot satisfy Task-wide invalidation. Reusing slot indexes as public identities could redirect stale aliases to replacements. Global Object exception changes and automatic cross-Object method batching are deferred to preserve behavior and avoid unproven independence.

## Implementation and validation
Resolve gated details and update bilingual language/VM specifications; implement parsing/type restrictions, identities/pools/lifecycle, scheduling and existing runtime integration, then confirmed exception reporting. Test Object constructor arguments, forbidden Task transfers, start/stop, one run per Tick, creation/deletion/reuse during Tick, stale aliases, LIFO order, pool growth, GC roots/reference groups, Object async calls, Volatile and exception boundaries. Preserve existing Object tests. Measure large-scale Task creation/deletion and Tick throughput, allocations, memory and latency with a recorded environment before making performance claims. Evaluate Object pooling separately and then dependency-safe batching with enabled/disabled comparisons and a measured profitability threshold.

## Security and operations
No hostile-code sandbox, new host capability, release or deployment is authorized by this ADR. Cleanup must release Task-owned roots without destroying externally shared Objects; avoid arbitrary cleanup under allocator locks. Finalize exception-report privacy rules. This records design acceptance only, not implementation, benchmark or test acceptance, and adds no progress-criterion completion. Preserve the existing catch-binding candidate and unrelated WIP.
