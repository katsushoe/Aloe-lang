# Direct WASM Scalar Print Imports

Status: Implemented (2026-10-22). The user adopted the proposal as written. Focused WASM tests pass 76/76, complete CompilerLib Release passes 1645/1645, and all 13 Release projects build with zero warnings/errors. Distribution refresh is in progress.

## Context

The VM print syscall accepts scalar values, but the direct-WASM backend currently rejects output syscalls. Its scalar subset has no strings, linear-memory string encoding or host runtime ABI. A typed callback ABI can support existing scalar print statements without introducing a heap or formatting text inside WASM.

## Decision

Lower print of int, float, bool, byte and char to typed synchronous function imports in the module namespace aloe. Use print_int(i64), print_float(f32), print_bool(i32), print_byte(i32) and print_char(i32), each returning no value. Emit only the imports actually required by the module. Preserve existing export names; shift internal call/export function indices by the emitted import count. The host supplies the callbacks at instantiation.

Callbacks receive the original typed numeric value, once per print in source order. int uses JavaScript BigInt when hosted by JavaScript; bool uses canonical 0/1; char is one UTF-16 code unit. Each callback represents one output line. The host owns text formatting and output destination; this ABI does not promise identical decimal text across host languages. A throwing callback aborts that invocation and preserves prior completed instance-state writes. Modules without print retain their existing import-free ABI.

String/object/array printing, generic formatting, readLine, sleep, heap operations and GC diagnostic log output remain unsupported. Existing source typing and exported-parameter guards remain in force. Missing or wrongly typed imports fail normal WebAssembly instantiation; callbacks are explicit host capabilities.

## Alternatives

- Shared linear memory plus UTF-8 formatter: deferred because strings and memory ownership are not yet defined for this backend.
- One untyped numeric import: rejected because it loses scalar identity and the int/float calling convention.
- Implicit stdout or filesystem capability: rejected because a portable WASM module must receive output explicitly from its host.

## Impact, security and operation

No VM syscall ID or AloeBC format change is needed. Imports affect only modules using supported scalar print. The host decides callback behavior and must provide the declared functions. No additional filesystem, network, timer or process access is granted by the compiler.

## Verification and documentation plan

Compare VM and Node/WASM typed output for all five scalar kinds, boundaries, repeated output, calls between functions, repeated invocations and private GC state. Check import deduplication/order, call/export index adjustment, missing callbacks and throwing callbacks. Keep no-print modules import-free and existing tests passing. Update bilingual VM specs, README and local progress records. Generated modules require the documented host callbacks; no publication or deployment is included.
