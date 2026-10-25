# Direct WASM GC Debug State

Status: Implemented (2026-10-22), extending the existing GC.debug property contract to the scalar direct-WASM subset.

## Context

The VM exposes the main-only bool property `GC.debug`. The direct-WASM backend already lowers the heap-free `tick()`, `GC.require()` and `GC.finish()` controls as no-ops, but previously rejected reading or writing the debug flag. The scalar subset has no Aloe heap, asynchronous call buffers, diagnostic output import or host settings parameter.

## Decision

Lower existing GCSetDebug/GCGetDebug syscalls to one private mutable i32 global, emitted only when the module uses these syscalls. Initialize it to false, matching the reference VM's default settings. Share it among calls within one WebAssembly instance and keep different instances independent. Existing source restrictions still require main access and bool assignments; the backend also rejects malformed raw bytecode that lacks a bool assignment operand.

The flag is runtime state, so writes completed before a later trap persist for the next call, as in the reference VM. No import, export, host capability, bytecode opcode, or AloeBC format change is required. The global is not exported and the compiler's bool-domain guards prevent externally supplied noncanonical bool parameters from populating it.

This increment supports the observable bool state. It does not provide GC phase logs, timing/performance output, custom host-debug initialization, heap collection or a diagnostic output import. These remain explicit WASM limitations; setting the flag does not claim to emit the reference VM's log stream.

## Alternatives

- A function-local flag: rejected because state would reset between calls.
- A module-static C# field: rejected because unrelated WASM instances would share state.
- Host-imported debug state/logging: deferred until the broader runtime-import contract is defined.

## Impact, security and operation

Only modules that use the existing GC debug syscalls gain a private global section. Modules without them retain their existing section layout. No filesystem/network access or host-visible global is introduced. Source main/bool diagnostics remain unchanged.

## Verification and documentation

Compare the reference VM, AloeBC round trip and Node/WebAssembly execution for initial false, true/false assignment, barrier preservation, repeated calls, separate instances and state retained after failure. Reject empty-stack and non-bool raw assignments. Keep the remaining WASM tests passing and update the bilingual runtime specification, READMEs and local progress records.
