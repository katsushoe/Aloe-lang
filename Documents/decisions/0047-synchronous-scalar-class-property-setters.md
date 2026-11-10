# 0047: Synchronous scalar class property setters

Status: Adopted by user (2026-11-05); implemented; integrated acceptance recorded separately

## Context
Class property getters and direct field assignments already work. Before this decision, the class scanner rejected any setter after a getter. Readonly scalar fields are writable only in their declaring constructor. Synchronous call/return and finally lowering are implemented, including public static scalar methods.

## Decision
- Extend a public instance class property with `set { ... }` after its existing getter. The property type must be int/bool/byte/char/float/decimal/string. A setter has one implicit local parameter named `value` with the exact property type and returns void.
- `local.Property = expression;` and `this.Property = expression;` evaluate the value once and invoke the setter immediately on that receiver. Setters are synchronous; they do not enqueue a Tick. The normal exact scalar type and allowed numeric conversion rules apply, matching ordinary scalar assignment.
- The setter body may read `this`, use fields/private methods, and use existing synchronous try/catch/finally. Existing readonly-field rules apply: a setter is a method, so calling it from a constructor does not permit it to write a readonly field.
- Getter-only writes are compile errors. Setter-only properties, repeated/reordered accessors, async/static/virtual/override properties, non-scalar setter signatures, property compound assignment, chained/executable/indexed assignment receivers, struct setters and direct WASM stay outside this first contract.
- Inherited properties retain existing getter lookup. An inherited setter may be invoked on a statically known derived-class local using its declaring-class implementation; property overriding is not introduced.
- A setter's exception propagates through the existing synchronous call stack. Writes completed before the exception retain existing class-state semantics; no new rollback promise. Setter execution remains an unknown/possibly throwing operation for interface CFG analysis.

```aloe
class Box {
 field stored: int;
 construct() { this.stored=0; }
 public property Value: int {
  get { return this.stored; }
  set { this.stored=value; }
 }
}
function main(args: string[]): int { var box=new Box(); box.Value=42; return box.Value; }
```

## Alternatives and effects
Keep getter-only properties: no implementation risk, but ordinary writable properties remain unavailable. Automatic backing fields are deferred because explicit getter/setter bodies already fit the language and require no new storage policy. General reference/struct setters and async behavior are deferred to avoid adding lifetime/queue/rollback decisions to this change.

## Implementation and acceptance
Add a setter function signature and shared ordinary Call/Return lowering; keep current bytecode/runtime unchanged if feasible. Validate single evaluation, receiver identity, seven scalar kinds, field/parameter shadowing, inheritance, readonly rejection, exact getter-only/type checks, exceptions and finally. Bilingual language specification and this ADR describe the adopted contract; integrated evidence is `.local/scalar-setter-20261105/acceptance.json`. This is a bounded part of L39; it does not close all readonly/static/setter contracts or independently add a progress criterion.

## Security and operations
This adds no filesystem, network or host capability. It retains the prototype VM trust boundary, existing runtime guards and local validation/publish policy. No MSI repeat, deployment or release is required for this compiler subset.

## Compatibility detail
Existing `this.Field` assignment keeps field precedence when a private field and a public property have the same name. An external `local.Property` assignment uses the public setter. Receiver validity is checked before evaluating the value. The compiler uses existing Call/Return and does not change VM/opcode contracts.
