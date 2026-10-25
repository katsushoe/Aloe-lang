# Browser REPL Host API capabilities

Status: Accepted, 2026-10-18.

## Context

The native and browser REPL share `AloeRepl.Core`. A default `AloeVm` grants the configured Host API capabilities, and the default `readLine()` and `sleep(ms)` handlers synchronously call `Console.ReadLine` and `Thread.Sleep`. The browser REPL has no synchronous browser-input adapter, and a blocking timer would block its UI thread.

## Decision

`AloeReplSession` may receive optional `AloeVmSettings`; when omitted, VM creation keeps the existing `AloeVm(module)` behavior and loads the normal VM settings. `AloeRepl.Wasm` loads the normal settings and overrides `AllowedHostCapabilities` to `None` before creating its session. Thus browser REPL submissions that call `readLine()` or `sleep(ms)` fail through the existing capability-denial error path. The native REPL continues to use configured VM capabilities.

This is a host restriction, not a language or bytecode change. A future browser input/timer adapter may grant a capability only after it provides non-blocking browser-safe behavior.

## Alternatives

- Keep all default capabilities in the browser: rejected because console input has no browser adapter and `Thread.Sleep` blocks browser execution.
- Implement synchronous browser input or timers in this change: deferred because the existing Blazor interaction flow and shared synchronous VM do not provide an asynchronous host-call protocol.

## Impact and security conditions

- Browser users cannot use the two current Host intrinsics in the REPL; other VM operations remain available.
- No host capability is newly granted, and the browser does not fall back to native console or blocking timer behavior.
- The capability setting is applied when a session creates its VM; runtime behavior remains guarded by `HostSyscallRegistry`.

## Operations and verification

- The native console host retains its existing configured capability behavior.
- The browser host sets capabilities to `None` at its composition root.
- `AloeReplSessionTests` verifies that both `readLine()` and `sleep(1)` return capability errors under disabled settings; browser/core documentation describes the platform limitation.
