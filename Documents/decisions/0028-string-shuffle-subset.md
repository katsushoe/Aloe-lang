# String Shuffle Subset

Status: Accepted and implemented (2026-10-22). User accepted the proposal as written.

## Context

Decision 0023 defines the int-only shuffle contract. String sorting is implemented under decision 0026. A string shuffle can use the existing cooperative permutation algorithm without defining comparison, key extraction, normalization or string conversion.

## Decision

Extend argument-free `shuffle` to `pipe<string>`. Buffer all input until closure, then apply the same Fisher-Yates permutation as the int variant, one swap per scheduler quantum. Emit the original strings with exact contents, item count and duplicate multiplicities preserved. Empty input emits no values. Input and output may be capacity bounded; output respects backpressure.

Use noncryptographic randomness. Unchanged order is a valid result. There is no seed/replay contract, key argument, conversion or normalization. Struct and other element types remain outside this increment. Existing int behavior is preserved.

## Alternatives

- Seeded deterministic permutation: deferred because the public seed type and cross-version replay contract are undefined.
- Cryptographic shuffling: deferred because the filter does not claim a security-sensitive random source.
- General object/struct shuffling: deferred to keep pipe typing and ownership rules explicit.

## Impact and conditions

The compiler selects the string variant from matching pipe types and rejects arguments or mismatched destinations. Runtime binding/value validation also rejects malformed AloeBC inputs. Full-stream memory usage grows with input size. No package, filesystem, network or host capability is introduced.

## Verification and documentation plan

After acceptance, cover empty/singleton input, duplicates and Unicode strings, capacity-one chained pipelines, AloeBC round trips, closed output and malformed binding types. Test permutation membership/count rather than requiring changed order or asserting statistical randomness. Update bilingual filter catalogs, READMEs and local progress records.
