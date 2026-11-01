# Bool Shuffle Filter Subset

Status: Accepted by the user, 2026-10-30. Implemented and locally validated, 2026-10-31: focused18, full2467 per net8/net10 target, all13 Release and bilingual filter specification49.

## Context

The accepted int, string and byte shuffle subsets use the same full-stream Fisher–Yates algorithm. The VM already supports pipe<bool>, and decision0036 adds its argument-free sort. Boolean shuffle can reuse the existing permutation and cooperative scheduling model without adding a pipe type.

## Decision

- Accept argument-free shuffle with matching pipe<bool> input and output in both existing pipeline forms: `source |: shuffle | result` and `source | filter(shuffle) | result`.
- Buffer through input closure, then apply the existing Fisher–Yates algorithm with noncryptographic System.Random.Shared and emit under bounded output backpressure. Close output after draining; empty input emits no values.
- Preserve exact boolean kinds, values, counts and duplicates. False/true counts must remain unchanged. Empty, singleton and all-equal streams are valid; output identical to input is also valid.
- Reject arguments, incompatible pipe bindings and nonboolean malformed-bytecode payloads. Do not coerce int or byte values into booleans.
- No seed, deterministic replay, persisted random state or cryptographic unpredictability. Other new types, custom random sources and the direct-WASM pipeline backend are outside this subset.

## Implementation and acceptance

Add a distinct validated built-in binding and use the existing shuffle buffer, swap-yield and drain path. Older VMs must reject an unsupported binding instead of interpreting it as another filter.

Verify both forms, empty/singleton/all-equal/mixed streams, large capacity-one chained pipes, exact kinds and multisets, serialized modules and invalid arguments/bindings/payloads/closed output. Avoid probabilistic assertions requiring output to differ from input. Preserve existing shuffle and sort coverage, both framework full suites and all13 Release projects. Update the English and Japanese standard-filter documents in the same implementation.

## Security and operational conditions

Memory grows with input count. Bounded pipes do not bound the aggregate buffer. The existing random source is unsuitable for secrets. This change adds no filesystem, network, registry, permission or host-syscall capability.

The frozen9954e35 MSI candidate remains separate. No per-feature MSI/ISO rebuild, release publication or external delivery is requested.

## Alternatives

- Adopt this bounded boolean subset, reusing the already accepted shuffle contract.
- Defer boolean shuffle and retain only the existing int/string/byte subsets.

General random-source policy, resource limits and additional element types require separate contracts.
