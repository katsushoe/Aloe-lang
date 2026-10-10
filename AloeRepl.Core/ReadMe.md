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

The current compiler supports a focused class/runtime subset: `field name: T;`, one synchronous `construct(...)` constructor, `this.field` reads/writes in instance code, `new Type(args)`, getter-only `public property`, synchronous private/protected instance methods callable from the same instance, `public async method ... : void` calls from a statically known class instance, and `public static async method ... : void` calls by class name. Basic single inheritance resolves inherited fields, getter properties, and instance methods; derived instances can be assigned to a base-typed local. `public virtual async method ... : void` and matching `public override async method ... : void` declarations support runtime dispatch for async calls, including calls through base-typed locals and inherited overrides in further-derived classes. The compiler includes all known concrete derived targets in async CallGraph cycle checks. Base classes must be declared first. Parameterless base constructors are called automatically. Parameterized base-constructor calls, property overrides, member hiding, synchronous virtual methods, field initializers, and direct external field access remain unsupported or unspecified. Tick dispatches instance calls through a bounded VMThread pool, serializing calls for each target Object; per-type Static CallBuffers are FIFO and serialize calls of the same type while different types may run in parallel. Calls enqueued during a Tick run before it returns. Worker stacks are isolated, Heap operations and Host syscall callbacks are synchronized, and blocking pipe operations from workers fail. If a worker throws, Tick joins active workers, discards queued calls, skips Volatile commit, and rethrows the first exception; completed Host side effects cannot be rolled back. Tick-time member writes use per-field Volatile State; self/internal reads observe the latest Volatile value, public property queries observe Committed State, and `tick()` commits dirty Volatile fields after both call buffer kinds drain. Reference-valued Volatile fields keep old Committed and new Volatile references alive until commit. Object field storage and class type metadata live with the Aloe heap Object table entry, and allocation reserves one 8-byte Aloe heap slot per field. Public instance property setters remain forbidden. The compiler rejects cycles in the statically resolved async CallGraph, including known virtual targets.

The compiler currently supports the `byte` type (0–255) for typed locals, fields, parameters, returns, and AloeBC constants. An integer literal in that range can initialize a byte or be passed to a byte parameter; out-of-range literals are rejected. Byte arithmetic and mixed byte/int arithmetic produce `int`. The `char` subset supports single UTF-16 code unit literals, typed locals/parameters/returns, char-to-char equality and ordering comparisons, and AloeBC constants; numeric conversion and arithmetic are rejected. Other conversions and broader value types remain unsupported.

The normal `enum` subset supports implicit and explicit int32 member values, `EnumType.Member` literals, typed assignments and function pass-through, and equality comparisons within the same enum type. For example, `enum Code { Zero, First = 10, Next, Alias = 10, Negative = -2 }` assigns 0, 10, 11, 10 and -2. Explicit values accept decimal, hexadecimal and binary integer literals with one optional sign; constant expressions and references to other members remain unsupported. The first implicit value is 0 and each later implicit value is the preceding value plus one. An implicit value beyond int32 is a compile error; an explicit value can reset numbering after int32 maximum. Different member names may share a value and compare equal within the same enum. `with (EnumType) { ... }` adds short access through either `Member` or `.Member`; nested enum contexts resolve through the innermost type first. Mixing enum values with integers or another enum type is rejected even when their numeric values match. Bitfield enums remain unsupported.

Type-based `with` also accepts a class name: `with (Logger) { .emit(1); }` calls an existing public static async method, with the same argument checks, CallGraph validation and deferred execution until `tick()` as `Logger.emit(1);`. Type targets are resolved at compile time. Dot-prefixed members are searched from the innermost context outward, so class and enum contexts may be nested together. Instance members cannot be accessed through a type context, and a local value with the same name as the target type makes the target ambiguous.

Instance `with (expression) { ... }` accepts a statically known class instance, including `new`, a function result, a local, or `this` in instance code. The target is evaluated once and retained in a local slot; reassigning the original variable does not change that target. `.Property` reads a public getter and `.Method(...)` queues an existing public async instance method with the same argument checks, virtual dispatch, CallGraph validation and Tick behavior as an explicit instance call. Private field reads/writes through `.field` are restricted to code in the field's declaring class; getter setters remain forbidden.

Multiple targets may be mixed in one block, for example `with (box, Logger, Color) { print(.Value); .emit(.Green); }`. Instance expressions are evaluated once from left to right before entering the block; type targets require no runtime evaluation. Lookup searches the innermost block first. Within a block, exactly one target must contain the requested member name; duplicate candidates are rejected as ambiguous, including identical targets and names shared across getters, methods or enum members. Method arguments do not disambiguate target names in this subset. Bare enum aliases also reject duplicate enum candidates, while ordinary local variables retain precedence. Explicit target member access can resolve a collision. Nested blocks restore their outer targets when they end. Struct and container targets remain unsupported, as do abbreviated synchronous private/protected method calls.

The compiler still emits a complete `main`, so interactive snippets keep successful source history and deterministically replay it on each submission. Previous output is removed as a baseline so it is not printed twice.


This replay model is temporary. When Host APIs, input, random values, network/file access, Object/CallBuffer execution, or other nondeterministic effects are introduced, the REPL should move to persistent VM state.

## Host memory diagnostics

When an executed program enables `GC.debug`, the REPL appends a `[REPL-PERF]` line after the VM's `[PERF] Program end` summary. It separates compile, VM construction, and VM execution timing/allocation (`compileAllocatedBytes`, `vmCreateAllocatedBytes`, `runAllocatedBytes`, and `totalAllocatedBytes`) and reports managed-memory start/end values. Normal submissions that do not request GC diagnostics keep their existing output.

`:hostgc` is a diagnostic-only REPL command. It explicitly runs the host .NET GC (`Collect` / pending-finalizer wait / `Collect`) and reports `beforeBytes`, `afterBytes`, `reclaimedBytes`, and Gen0/1/2 collection deltas. Normal Aloe execution never forces host GC through this command path.
