# JSON Decode Filter Subset

Status: Accepted by the user and implemented, 2026-10-21. Focused JSON filter tests pass 24/24; latest CompilerLib validation passes 1556/1556.

## Context

The standard-filter draft sketches `json<T> : pipe<string> -> pipe<T>` but does not specify how JSON object members map to Aloe members, which type shapes are supported, or how invalid input behaves. Aloe instance properties are getter-only, so a first implementation should not imply that arbitrary class DTOs can be populated.

## Proposed decision

- Initially support `json<T>` only when `T` is a `struct` whose fields are all `int`, `bool`, or `string`.
- Each input string is one complete JSON document. Require the root value to be an object. Do not accept arrays, nested objects, null, enum values, classes, or other field types in this increment.
- Match JSON property names to struct field names using ordinal, case-sensitive comparison. Require every declared field exactly once. Reject unknown names, duplicate names, and missing fields.
- Require JSON token types to match the Aloe field type. `int` accepts only a lexical JSON integer whose value fits signed int32; `bool` accepts `true` or `false`; `string` accepts a JSON string. Do not coerce strings, fractional or exponent-form numbers, or null.
- Emit one fully initialized struct for each valid input item and retain input order. On malformed JSON or a shape/type/member mismatch, fail the filter with an error identifying the input item and, when available, the member name. Never silently drop or partially emit an item.
- Do not add options for case folding, unknown-member skipping, defaults, nullable fields, or numeric coercion. Add those only through a later explicit decision.

## Alternatives

- Populate arbitrary classes: deferred because instance property setters are not part of the current language subset and constructor selection/side effects need a contract.
- Support nested objects and arrays immediately: deferred until recursive shape mapping and collection allocation/ownership semantics are specified.
- Ignore unknown members or supply default values: not selected because permissive mapping can conceal schema drift and risks silently returning incomplete records.
- Drop malformed records and continue: not selected because it would hide data loss; a future error-handling filter can define recovery explicitly.

## Impact and security

The proposed subset is deliberately narrower than common JSON object mappers. Parsing remains bounded by each input string's size, while the existing pipe backpressure governs emitted structs. JSON is data, not executable Aloe source. The decoder must not evaluate member names or contents as code.

## Implementation and acceptance

Implementation adds compiler registration and a cooperative runtime decoder. Immutable struct values and compiler struct field validation support string fields. Acceptance covers valid int/bool/string records, input order, empty input, malformed and mismatched cases, duplicate/unknown/missing fields, int32 boundaries, bounded-pipe backpressure, raw AloeBC schema validation (including duplicate keys after JSON escape decoding and unknown schema properties), and AloeBC round trips. Both language catalogs and README summaries were updated in the same implementation. Focused JSON filter tests pass 24/24; the latest full CompilerLib Release suite passes 1556/1556.
