# Readonly scalar class fields

Status: Accepted by user (2026-11-04); implementation acceptance recorded separately.

## Context
Chapter6 specifies that readonly fields can be assigned inside a constructor and are read-only afterward. The current class modifier collector accepts but drops readonly, so source intent is not enforced. Struct fields, reference invalidation and deep mutation need separate policies.

## Decision
- Support `readonly field name: T;` on instance class fields of int, bool/boolean, byte, char, float, decimal or string. Ordinary class field visibility and no-initializer rules remain.
- Assign only through `this.name = expression;` in the constructor of the class declaring that field. Multiple constructor assignments are permitted; readonly does not mean assign-once.
- Reject direct assignment outside that constructor, including private/protected/public async methods and derived constructors writing inherited readonly fields. Reject readonly scalar field mutation through instance-with syntax as well.
- Read through ordinary getters/member access allowed by existing visibility rules. Other fields and locals remain mutable. No deep immutability, compile-time constant or new runtime/opcode behavior.
- Reject readonly reference/collection/pipe/enum/struct fields for now, and reject readonly applied to methods/properties/constructors or static fields. Struct readonly fields remain unsupported.
- Do not add constructor definite assignment for class fields in this increment; existing default initialization and constructor rules remain. Constructor helpers cannot write readonly fields.
- Verify seven scalar kinds, exact values, permitted repeated constructor writes, ordinary mutable fields, method/derived/with write denial, unsupported target types/modifiers and VM bytecode round-trip. Direct WASM class restriction is unchanged.

Adoption defines this bounded field milestone only; setter, extended static, readonly references and constructor definite-assignment contracts remain later work. L39 as a whole stays open.


## Alternatives and impact
Reference/struct readonly fields are deferred because invalidation and deep mutation need additional contracts. Runtime freezing is unnecessary for this source-level scalar assignment rule. A field retains its declaring-class identity; inherited readonly fields cannot be initialized by a derived constructor. Existing mutable fields retain their behavior. No runtime, dependency, syscall, opcode or bytecode layout change.

## Security and operating conditions
This is a compiler restriction, not heap isolation against host APIs. Constructor helpers receive no extra privilege. Existing getter/Tick/visibility and class initialization rules remain. All unsupported forms are rejected at compilation, including readonly modifiers on methods/properties/constructors and static fields.

## Implementation, tests and documentation
FieldSignature retains readonly and declaring class. RequireWritableField guards both direct this assignments and with assignments. ReadonlyFieldTests cover scalar values, constructors, helper/method/derived/with rejection, mutable controls and unsupported forms. Bilingual chapter6 documents the subset. L39 also includes setter and static contracts and remains open.

2026-11-10: user-adopted decision0048 extends the initial seven scalar types to existing enum/struct/non-pipe reference fields and applies the same binding checks to delete/unbox. See [decision0048](0048-readonly-class-field-types.md); the original constructor-only assignment contract is retained.
