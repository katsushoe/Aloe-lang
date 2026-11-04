# 0039: Stable struct bool-key sort subset

Status: Accepted by the user, 2026-11-03. Implemented; product integration validation passed.

## Decision

Extend `sort: .Field` on `pipe<Struct>` to a declared direct `bool` field. Order false before true, preserving original records, bool values, count, duplicates and source order within equal keys. Buffer input until closure; retain bounded-output backpressure and empty-input behavior.

Names are exact and case sensitive. Reject missing fields, nested paths, extra arguments, incompatible pipe types and malformed schema/runtime key values. Keep existing int/byte/string keyed sorts and argument-free sorts unchanged. No numeric coercion, descending order, arbitrary expression/comparator, external I/O, WASM pipeline implementation or release is added.

The optional keyed-sort schema `fieldType` accepts `bool` for the new form. Int retains its legacy schema; byte/string retain their existing tags. Old runtimes may reject the new tag; no automatic cross-version compatibility is promised.

## Validation and boundaries

Check reversed groups, both bool values, stable ties with distinct payloads, all-equal/empty input, capacity-one backpressure, original/AloeBC execution, invalid fields/types and malformed schemas/keys. Preserve existing keyed/element sorts. Validate both target frameworks, Release solution, CLI and bilingual filter documentation before product acceptance.

The user adopted this bounded scope on 2026-11-03. The isolated candidate passed before product integration. Installer lifecycle, publication and unrelated work remain outside this subset.

## Alternatives

Defer bool keys and retain manual ordering; other field types and custom ordering remain separate decisions.

Isolated candidate validation: full2969 per framework, focused100, CLI5 passed. Product integration is validated separately from the isolated candidate.
