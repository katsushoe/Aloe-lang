# Persistent VM state for the interactive REPL

Status: Accepted, 2026-10-14.

## Context

The compiler produces complete modules with a synthesized `main`. The earlier REPL recompiled and reran the successful statement history for every input, hiding repeated output with a baseline. That repeats input, sleep, queued calls and other effects, and rebuilds every prior object rather than retaining one VM.

## Decision

Compile the full candidate module for each submission so names and types remain compiler-checked, but execute only the new instruction suffix of synthesized `main`. Preserve the VM instance, heap, main-frame locals and pending async CallBuffers between successful submissions. Rebind the verified module only while the VM is quiescent. Completed scheduler contexts are discarded at the next entry; an unfinished pipe context blocks a new submission.

Standalone complete programs containing `main` continue to run in a separate VM and do not change the interactive session. Compile errors do not affect the session. A runtime error faults the interactive session and requires `:reset`. The session does not replay earlier input or other host effects to simulate rollback. This keeps the existing VM heap and host effects from being silently duplicated; the current implementation does not promise transactional rollback of a failing submission.

No opcode, AloeBC encoding or heap representation changes. Reset discards the session VM and locals. Source history remains available for `:history`, `:source` and diagnostics, but it is not executed on successful submissions.

## Alternatives

- Replay all prior source in a fresh VM: rejected because every submission repeats host effects and allocation.
- Add language-level global variables or a session-global opcode: deferred because the current source subset can resume its synthesized `main` using retained frame locals.
- Retry successful history after runtime failure: rejected because `readLine`, `sleep` and future Host APIs cannot be safely repeated.

## Verification and limits

`AloeReplSessionTests` verifies local and class-instance state, same-VM reuse, async calls across submissions followed by `tick()`, standalone-program isolation, and runtime-error fault/reset behavior. Browser and native hosts share `AloeRepl.Core`. Persistent unfinished pipe/scheduler contexts are not yet supported by interactive resumption.
