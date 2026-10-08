# AloeRepl.Core


Shared C# REPL engine used by both the native console host and the browser/WASM host.


Current REPL/compiler subset:


- user-defined functions with typed parameters and `int` / `string` / `bool` / `void` returns
- function calls, forward references, recursion, and nested early `return`
- `var`, `let`, assignment
- `if / else if / else`
- `while`, `break`, `continue`
- `print`
- int / string / bool expressions
- `+ - * / %`
- `== != < <= > >=`
- `not`, `and`, `or` with short-circuit evaluation


Function and class declarations entered in the REPL are persisted as module-level declarations. Ordinary statements are still persisted inside the synthesized `main`. Expressions entered without a trailing semicolon are printed automatically.

A submission containing `function main(...)` is treated as a complete Aloe program and is compiled without wrapping it in a synthesized `main`. Module-level class/function declarations already entered in the session are prepended, so the submitted `main` can use them. Standalone execution does not replace or mutate the existing interactive session history.

The current compiler supports a focused class/runtime subset: `field name: T;`, one synchronous `construct(...)` constructor, `this.field` reads/writes in instance code, `new Type(args)`, getter-only `public property`, synchronous private/protected instance methods callable from the same instance, and `public async method ... : void` calls from a statically known class instance. External public-async calls are enqueued in the VM's prototype Instance CallBuffer and are drained synchronously by `tick()` in enqueue order; unqualified self calls remain synchronous. Tick-time member writes now use per-field Volatile State, self/internal field reads observe the latest Volatile value, public property queries observe Committed State, and `tick()` commits dirty Volatile fields only after the CallBuffer reaches quiescence. Reference-valued Volatile fields keep both old Committed and new Volatile references alive until commit. Object field storage and class type metadata live with the Aloe heap Object table entry, and allocation reserves one 8-byte Aloe heap slot per field. Public instance property setters remain forbidden. The compiler also rejects cycles in the currently statically resolved public-async CallGraph. The current CallBuffer milestone is single-threaded and does not yet implement dynamic-dispatch CallGraph expansion, VMThread-pool scheduling, inheritance, field initializers, or direct external field access.

The compiler still emits a complete `main`, so interactive snippets keep successful source history and deterministically replay it on each submission. Previous output is removed as a baseline so it is not printed twice.


This replay model is temporary. When Host APIs, input, random values, network/file access, Object/CallBuffer execution, or other nondeterministic effects are introduced, the REPL should move to persistent VM state.

## Host memory diagnostics

When an executed program enables `GC.debug`, the REPL appends a `[REPL-PERF]` line after the VM's `[PERF] Program end` summary. It separates compile, VM construction, and VM execution timing/allocation (`compileAllocatedBytes`, `vmCreateAllocatedBytes`, `runAllocatedBytes`, and `totalAllocatedBytes`) and reports managed-memory start/end values. Normal submissions that do not request GC diagnostics keep their existing output.

`:hostgc` is a diagnostic-only REPL command. It explicitly runs the host .NET GC (`Collect` / pending-finalizer wait / `Collect`) and reports `beforeBytes`, `afterBytes`, `reclaimedBytes`, and Gen0/1/2 collection deltas. Normal Aloe execution never forces host GC through this command path.
