# Int Shuffle Filter Subset

Status: Accepted, 2026-10-20. The user accepted the proposed subset in full.

## Context

The standard-filter draft specifies `shuffle : pipe<T> -> pipe<T>` as a full-input random permutation. It does not yet define supported element types, random-source guarantees, or seed/replay controls. The implemented argument-free `sort` already establishes a buffered `pipe<int>` subset with bounded output.

## Decision

- Initially support argument-free `shuffle : pipe<int> -> pipe<int>` only.
- Consume input until closure, then apply an in-place Fisher-Yates permutation and emit all values under output backpressure. Preserve the count and every duplicate; empty input closes the output without values.
- Use the runtime's noncryptographic random source (`System.Random.Shared`). Every valid permutation is an allowed result, including unchanged order. Do not promise a different order on each call or deterministic replay.
- Do not expose a seed argument or random-state persistence in this increment. Reject arguments and other element types at compile time, and validate pipe kinds when binding raw bytecode.
- Retain the complete input while buffering and emitting; memory grows with input count. Release that buffer when the filter completes.

## Alternatives

- Add generic element types and seeds immediately: deferred until their public type and replay contracts are specified.
- Use a cryptographic random source: not selected for this nonsecurity permutation filter; cryptographic unpredictability is outside this subset's contract.
- Require a changed order: not selected because it would exclude legitimate permutations and cannot hold for empty, singleton, or equal-valued input.

## Impact, security, and operation

This is a bounded-output filter, not bounded-memory aggregation. It must not be used to generate secrets, security tokens, or cryptographic randomness. Random-source internals and a particular shuffled sequence are not a compatibility contract. Separate VMs may consume the process-wide random source independently of their source programs; reproducibility is deliberately unspecified.

## Implementation and acceptance

After acceptance, add compiler registration, VM binding/type checks, and a cooperative buffered filter using the existing pipe wait/wake machinery. Cover source execution and AloeBC round trips, multiset/count preservation including int32 boundaries and duplicates, empty and singleton inputs, chained capacity-one pipes, unsupported types/arguments, invalid bytecode bindings, and closed output. Tests must not require a fixed random sequence or require the output to differ from input. Update both filter specifications, README summaries, and progress records. Direct WASM pipes remain unsupported.
