# Byte Shuffle Filter Subset

Status: Accepted by the user, 2026-10-28. The byte-shuffle implementation and local validation passed.

## Context

Decisions 0023 and 0028 implement argument-free shuffle for int and string streams. Decision 0033 adds byte sorting. A byte shuffle can use the existing permutation and scheduling behavior without introducing a conversion or comparison policy.

On the current 2321-test compiler, a `pipe<byte>` pipeline with `shuffle` is rejected because the registered input type is `pipe<int>`. This is a baseline observation, not a claim about byte shuffle support.

## Decision

- Accept argument-free `shuffle` with matching `pipe<byte>` input and output through both existing pipeline forms.
- Buffer input until closure, then apply the existing Fisher–Yates permutation, preserving each byte value, its kind, count and duplicate multiplicity. Empty input emits no values.
- Continue using noncryptographic randomness. Original order is a valid result; no seed or reproducibility contract is added. Output observes bounded backpressure.
- Reject arguments, mismatched pipes and invalid bytecode values without silently converting integers to bytes.
- Leave struct and other element types, seeded or cryptographic randomness, direct-WASM pipeline lowering, new host capabilities, and public release outside this increment.

## Acceptance plan

Check empty and singleton streams, duplicates, byte values 0 and 255, capacity-one pipelines and both forms. Verify original multiset and byte kinds rather than expecting a changed order or asserting statistical randomness. Cover serialized bytecode, invalid arguments and pipe/value types, CLI behavior, existing int/string shuffle regressions, both framework targets, and the Release solution build. Update the bilingual filter specification and progress records.

## Alternatives and impact

This bounded subset reuses the existing full-stream buffering and cooperative scheduling model; memory grows with input count. Deferring it keeps the current int/string contract. Cryptographic shuffle and seed/replay semantics need separate decisions. It introduces no new network, filesystem, permission or package-release behavior.
