# 0049: Typed catch bindings with exception information snapshots

Status: Accepted by the user (2026-11-10); implemented and validation confirmed on 2026-11-13.

## Context
Before this decision, typed catch clauses and bare rethrow worked, but catch variables and source exception values were unimplemented. The VM keeps the original exception context for rethrow. Strings and struct value snapshots expose basic information without exporting a CLR object or retaining a host exception handle in the heap.

## Decision
- Add `catch (error: Exception) { ... }`, also accepting the other existing supported exception type names after the colon. Existing `catch { ... }` and `catch (Type) { ... }` keep their behavior and source-order type matching. Untyped `catch (error)` is not added.
- The binding is local to that handler and has the built-in struct type `ExceptionInfo`, containing `Message: string` and `TypeName: string` in that order. TypeName is the original CLR exception type's simple name. Message is its message text; it is not a stack trace or formatted inner-exception report.
- It is an ordinary struct value snapshot. It may be copied, passed or returned using existing struct rules, and its local copy may be reassigned or edited. Such edits do not change the original exception. Bare `throw;` still rethrows that original exception, including after edits to the snapshot or nested catches.
- Reserve `ExceptionInfo` as a built-in type name. Normal two-field struct construction/value behavior is permitted; constructing an information value does not create or throw an exception.
- Initially support the existing synchronous free functions, class constructors and synchronous class methods/getters/setters. Do not add queued-async exception propagation, rollback or async-finally semantics.
- `throw expression;`, new exception constructors, custom exception hierarchies, stack traces, inner exceptions, CLR object access and direct WASM remain outside this first contract.

```aloe
function main(args: string[]): int {
 try { var fail=1/0; }
 catch (error: ZeroDivisionException) {
  print(error.TypeName);
  print(error.Message);
 }
 return 0;
}
```

## Implementation and compatibility
Add one built-in VM syscall that copies the active frame's caught exception information into an ExceptionInfo struct, appending its ID without renumbering existing syscalls. Calling it without an active caught exception is a VM error. Keep the existing opcode set and AloeBC container format. Programs using this feature require the updated VM; no promise is made that an older VM understands the new syscall. Existing bytecode and bare rethrow behavior must remain unchanged. Keep the compiler and VM libraries together in the SDK.

Mask catch-variable shadows in speculative interface/exception-flow scans; a handler binding must never borrow an outer variable's type facts. Validate nesting, scope exit, shadowing, value copies, catch ordering, rethrow, finally and rejected forms. Update bilingual specification and a public ADR after adoption. This is a bounded part of L30; arbitrary throw expressions remain open.

## Alternatives, security and operations
A CLR exception reference is deferred because it adds host-object lifetime, serialization and capability questions. A message-only string binding is smaller but loses type information and creates a different binding contract. The snapshot approach uses existing value semantics. Exception messages may contain text provided by the host; this adds no automatic logging and does not turn the trusted prototype VM into a sandbox. Do not expose stack traces or change host capabilities. Local validation and artifacts do not authorize a release or MSI lifecycle repeat.

## Validation boundary
Bound handlers remain unknown to speculative catch-body proofs, so they may conservatively reject programs that require additional interface narrowing evidence. Direct parsing and handler scope still retain actual local types. Acceptance is recorded separately in `.local/catch-bindings-20261110/acceptance.json`; no general exception-flow soundness proof is claimed.

The 2026-11-10 validation passed all 4541 CompilerLib tests on each net8.0 and net10.0 target, including 26 cases in `Test_CompilerLib/CatchBindingTests.cs`, and all 13 Release projects with zero warnings/errors. Source checksums embedded in both targets' portable PDBs were matched against the current 217 compiler/runtime/common/test source files per target on 2026-11-13. Existing 51 syscall IDs and opcode definitions are preserved; CaughtExceptionInfo is appended as syscall 52. Language specification version 224 and VM specification version 63 describe the contract. SDK/Browser artifacts and hosted CI require their own evidence and are not implied by these local results.
