# Stable Struct Byte-Key Sort Subset

Status: Accepted by the user, 2026-10-28. Implementation and local validation passed; final-candidate MSI validation remains pending.

## Context

Decisions 0025 and 0030 support stable struct sorting by direct int and string fields. Decision 0033 supports argument-free `pipe<byte>` sort. The compiler originally rejected a declared byte field as a keyed sort with a diagnostic requiring int or string; a local CLI baseline confirmed this behavior.

## Proposed decision

Extend `sort: .Field` on a `pipe<Struct>` to a declared direct `byte` field. Sort its unsigned values from 0 to 255 in stable ascending order, preserving each original record, byte value, count, duplicates, and input order for equal keys. Buffer input until closure and respect output backpressure. Empty input closes output without values.

Field names remain exact and case-sensitive. Reject unknown fields, nested paths, arguments beyond one field, incompatible input/output pipes, and malformed bytecode schemas or runtime key values. Preserve existing int/string keyed and argument-free sort behavior. No descending/custom comparator, implicit conversion, new WASM pipeline backend, host capability, or public release is added.

## Acceptance plan

Cover zero and 255 boundaries, reverse order, duplicates, stable ties with distinct payloads, empty input, capacity-one pipelines, original and serialized bytecode, invalid fields and types, malformed schema/key values, and CLI behavior. Retain int/string regressions, run both framework test targets and the Release solution build, and update the bilingual filter specification. Passing local validation does not itself establish a release or VM lifecycle acceptance.

## Alternatives and impact

This bounded subset reuses the existing keyed-sort model and byte's existing unsigned semantics. Deferring it preserves the current int/string key contract. Nested keys, other field types and comparator customization need separate decisions. Memory use grows with input count; no filesystem, network, or permission capability is introduced.
