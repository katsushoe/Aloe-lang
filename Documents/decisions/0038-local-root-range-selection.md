# 0038: Local root numeric-version interval selection

Status: Accepted by the user, 2026-11-03. Implemented; validation receipts are maintained in .local/package-root-range-20261103.
Scope: Aloe-lang only. Existing decision0031 exact resolution remains unchanged.

## Context

The current read-only resolver follows exact three-part numeric dependency versions in an explicit local JSON index. It does not select a root version from a range. Transitive range solving, registries, acquisition, cache/install and trust policies remain undecided.

## Decision

Add a separate root-selection entry point:
`aloec package-db resolve --name NAME --version-range ">=1.2.0 <2.0.0" [--package-db PATH]`.

- `--version` keeps its existing exact behavior and formatVersion1 output. The two options are mutually exclusive.
- The first range syntax is exactly `>=M.m.p <N.n.p`: inclusive lower and exclusive upper bound, one ASCII space, no surrounding whitespace, with both bounds required and lower strictly below upper.
- Each version component follows decision0031: ASCII nonnegative decimal digits, no leading zero except zero itself. No prerelease/build suffix, caret, tilde, wildcard, alternate operators or compound ranges.
- Compare normalized components numerically without imposing a new Int32/Int64 component limit.
- Select the highest eligible indexed version for the exact ordinal, case-sensitive root name. Nonconforming legacy version strings are not candidates and are listed as excluded versions in the result.
- Select only once. A selected root's missing file, stale metadata, dependency conflict/cycle or other exact-plan error fails the operation. Do not fall back to a lower root, even if that lower root could resolve.
- Read the index as one byte snapshot for selection and exact closure. Reuse the exact resolver on that snapshot; selected metadata is still read afresh and checked against its identity.
- Transitive dependencies remain exact-only. No constraint solver, backtracking, package copying, download, index mutation, code execution or permanent lock file.
- Existing exact plans and their hash meaning/order remain unchanged.

## Output and errors

Range selection returns a distinct formatVersion2 wrapper containing the requested name/range, selected exact root, index SHA256, ordinal-sorted excluded legacy version strings and the existing formatVersion1 exact plan.
The library exposes the equivalent typed result rather than changing the existing exact result type.
No timestamp is added. Invalid syntax, no eligible root and all existing exact-plan failures are diagnostics/nonzero status with no partial successful plan.

## Alternatives

1. Keep exact-only resolution: lowest implementation risk, but root selection remains manual.
2. Full transitive ranges/backtracking: deferred until reconciliation and lock policy are agreed.

## Impact and security

Only a local read-only selection operation is added. Hashes identify bytes, not publisher identity or permission to execute. Existing explicit indexed paths and caller filesystem access remain the boundary. No remote trust or installation behavior is implied.
The result remains a snapshot; later build/install behavior must define its own revalidation. New highest versions in the index can change future selection.

## Implementation, tests and documentation

After adoption, record the accepted decision in Documents/decisions before implementation.
Share index parsing and exact-resolution internals without changing exact CLI/API behavior.
Verify bound inclusivity, numeric ordering (including large components), case sensitivity, unsupported legacy versions, duplicate/missing entries, deterministic formatVersion2, selected-root failure without fallback, exact transitive constraints and success/failure immutability.
Keep API/CLI parity, existing exact tests, Release builds and both CompilerLib TFMs passing. Update bilingual package specifications and CLI help only when implemented.

## Operating conditions

No release, publication, registry registration or VM/MSI lifecycle run is part of this proposal.

