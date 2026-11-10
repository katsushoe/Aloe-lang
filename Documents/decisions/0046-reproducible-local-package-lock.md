# Reproducible local package lock and verification

Status: Adopted by user (2026-11-05); implemented; integrated validation recorded separately

## Context
Exact dependency plans and highest-version root interval selection are implemented. Persistent replay must preserve the selected root and content instead of silently selecting a newer root later. Acquisition, installation and publisher trust are separate unfinished contracts.

## Proposed first contract
- `package-db lock --name NAME` with exactly one of `--version` or `--version-range`, plus `--out FILE` and optional `--package-db INDEX`, creates a new UTF-8 JSON lock. Existing output files are never overwritten; creation is atomic in the destination directory.
- Lock schema has kind `aloe-package-lock`, formatVersion1, an exact selected root, and all reachable packages in deterministic dependency order. Each node pins exact name/version, ABC SHA256, metadata SHA256 and sorted exact dependency identities. It contains no filesystem paths or registry URLs.
- `package-db verify-lock --lock FILE [--package-db INDEX]` strictly validates the lock, resolves its exact root against the current local index and compares the complete reachable graph, order and content hashes. On success it returns the verified current exact plan as JSON; on mismatch/missing content/malformed data it fails with a nonzero exit code and does not update the lock, index or packages.
- A newer version or unrelated index entry does not change the locked root. Relocating byte-identical packages via another index is allowed. Metadata byte changes fail verification even when semantically equivalent. Transitive dependencies remain exact; no backtracking or range reconciliation is added.
- Reject unknown/duplicate/missing JSON fields, invalid identities/hashes, duplicate package names, incomplete/unreachable/cyclic graphs and noncanonical dependency order. Treat lock files as untrusted data: file paths are obtained from the existing index resolver, never from lock contents.
- Library APIs expose creation, strict read/write and verification. The command-line and bilingual package specification describe offline, read-only verification. Hash pinning does not establish publisher authenticity or perform installation.

## Alternatives and effects
Re-running a floating root range on replay is rejected because it changes reproducible selection. Pinning the entire index byte hash is rejected because unrelated additions/relocation need not invalidate the selected content. Registry download, install/uninstall, signatures and transitive ranges remain independent decisions. This bounded local contract may complete P12 only after integrated API/CLI and rejection-boundary evidence passes.

## Implementation details
Hashes use lowercase64-character hexadecimal SHA256. Graph order is deterministic depth-first dependency order, matching the existing resolver. Lock JSON requires UTF-8, case-sensitive field names and exact properties. Writes require an existing parent directory and atomically move a flushed unique temporary file without replacing a destination. Verification compares observed bytes; it neither freezes package files nor applies a plan. Library APIs: CreateLock, CreateRangeLock, WriteLock, ReadLock, VerifyLock. Evidence: `.local/package-lock-20261105/acceptance.json`; API regressions: `Test_CompilerLib/PackageLockTests.cs`; CLI report: `.local/package-lock-20261105/validation/cli-report.json`.
