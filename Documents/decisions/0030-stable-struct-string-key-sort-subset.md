# Stable Struct String-Key Sort Subset

Status: Implemented (2026-10-22). The user adopted the proposal as written. Focused sort/shuffle checks pass 64/64 and complete CompilerLib Release tests pass 1665/1665.

## Context

Decision0025 defines stable ascending sort by a declared direct int field, and decision0026 defines ordinal UTF-16 string sorting. Struct string fields already exist. Extending the existing keyed syntax can combine those contracts without adding key expressions or culture settings.

## Decision

Allow sort: .Field for pipe<T> when T is a declared Aloe struct and Field is a declared direct string field. Match field names exactly and case sensitively. Sort ascending with StringComparer.Ordinal semantics over UTF-16 code units, preserving original records, exact string contents, duplicate records and original input order among equal keys. Empty strings sort before nonempty strings. Do not normalize, fold case, convert values or infer culture.

Consume all input until closure, then emit under output backpressure. Empty input emits no values and closes the destination. Buffer memory grows with item count. Keep existing int-key sort and argument-free int/string sorts unchanged.

Nested paths, computed selectors, multiple keys, descending order, other element/key types and locale options remain outside this increment. Compiler and raw AloeBC schema/value validation must reject mismatched kinds and invalid schemas. Preserve compatibility with existing int-key AloeBC schemas; use explicit metadata for the string-key variant so malformed values cannot silently select another ordering.

## Alternatives

- Culture-sensitive collation: not selected because results would depend on host culture/settings.
- General key expressions: deferred until side effects, evaluation count and failure timing are defined.
- All primitive key types: deferred because float and nullable ordering need separate contracts.

## Impact, security and operation

Reuse the existing pipeline scheduler and immutable struct records. No host callback, filesystem/network access, dependency, package database or AloeBC opcode is added. This change expands accepted source programs only after user adoption.

## Verification and documentation plan

Compare VM and AloeBC round trips for ordinal-vs-culture cases, empty strings, composed/decomposed Unicode, supplementary UTF-16 code units, stable ties, duplicate records, empty streams, capacity-one pipelines, closed output, malformed schemas and mismatched values. Retain int-key regressions and update bilingual catalogs, README and local progress records.
