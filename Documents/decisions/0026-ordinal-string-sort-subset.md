# Ordinal String Sort Subset

Status: Accepted by the user and implemented (2026-10-22). Validation: focused sort/interface 195/195, complete CompilerLib 1594/1594, all 13 Release projects build with zero warnings/errors.

## Context

The standard sort filter currently supports signed int32 values and stable sorting of declared structs by a direct int field (decision 0025). String sorting needs an explicit comparison contract so results do not depend on the host's culture or normalization settings.

## Decision

Extend the no-argument `sort` filter to `pipe<string>`. Buffer until input closure and emit the original strings in ascending ordinal UTF-16 code-unit order, using the same comparison semantics as `StringComparer.Ordinal`. Preserve duplicates and exact string contents. Empty input emits no values; an empty string sorts before any nonempty string. Preserve stable input order for equal keys. Emission obeys bounded output backpressure.

This subset has no culture, case-folding, normalization, descending-order or key argument. Struct string keys and other element types remain separate decisions. The existing int and struct-int variants retain their contracts.

## Alternatives

- Current-culture comparison: deferred because results would depend on host configuration.
- Ordinal case-insensitive comparison: deferred because it merges distinct comparison keys and requires a public option contract.
- Wait for a general comparison callback: deferred because callback purity, scheduling and error handling are not defined.

## Impact and security conditions

The compiler selects the string variant from the declared pipe element type and rejects unsupported arguments. The runtime validates values for malformed AloeBC rather than coercing them. Full-stream buffering has the same input-size-dependent memory cost as existing sort variants and does not introduce filesystem or network access.

## Implementation and verification plan

After acceptance, extend compiler registration and cooperative VM sort state, document the subset in both filter catalogs, and cover empty input, empty strings, duplicate values, case distinctions, combining characters, supplementary characters, capacity-one output and AloeBC round trips. Existing int and struct-key tests must continue to pass. No new package dependency or WASM host import is required.
