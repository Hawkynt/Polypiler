# Intrinsic contracts and the registry

An intrinsic is an operation of the IR that stays recognisable until a backend writes it: an
`IrIntrinsic` node of an `IrIntrinsicOp` (ADR 0009). Each has one contract document under
`spec/intrinsics/<group>/<name>.yaml`, one evaluator and one fallback. The registry binds the three
and checks them against each other, so that the verifier, the interpreter, constant folding and
every backend agree on what `PopCount(0)` is.
Related: [integer and bit operations](integer-operations.md) · [floating-point operations](floating-point-operations.md) · [IR and analysis](../architecture/IR.md).

## The parts

| Part | Where | What it is |
| --- | --- | --- |
| Contract | `spec/intrinsics/<group>/<name>.yaml`, schema `spec/schemas/intrinsics.schema.json` | signature, attributes, legality, effects, exactness, algebra, `lowersTo`, facts, reference algorithm and vectors |
| `IntrinsicContract` | `Polypiler.Core.IR/Intrinsics/` | the document in memory; `ContractLoader.Load` reads it, `Check(node)` checks a node against it |
| `IIntrinsicEvaluator` | `Polypiler.Core.IR/Intrinsics/` | the meaning in code; `InterpretedEvaluator` is the interpreter's (`IntegerOperations`, the float table) |
| `IIntrinsicDecomposition` | `Polypiler.Core.IR/Intrinsics/` | the fallback: a tree of core operations, of the intrinsics `lowersTo` lists and of library calls |
| `IIntrinsicRegistry` | `IntrinsicRegistryBuilder.Add(contract, evaluator, decomposition).Build()` | the three per intrinsic, `All` ordered by name |
| `IntrinsicVerifier` | `Polypiler.Core.IR/Intrinsics/` | checks every intrinsic node of a module against its contract |
| `ExpandIntrinsics` | `Polypiler.Core.Legalize/` | replaces selected nodes by their decomposition, copying in the library functions it calls |
| `IntrinsicCatalog` | `Polypiler.Core.Legalize/` | binds the contracts of the repository: the operation of the name, `InterpretedEvaluator`, and `IntegerLibrary` or the portable float implementation as decomposition |

The evaluator is the only statement of an intrinsic's meaning in code. A decomposition is written
independently of it - it never calls the evaluator - so that each checks the other.

## Adding an intrinsic

1. Write the contract: name and group (the file is `<group in lower case>/<name in lower case>.yaml`),
   type parameters with their constraint (`integer`, `unsigned`, `signed`, `float`, `any`), parameters,
   one to four returns, `effects`, `exactness` and at least three vectors whose values come from an
   independent oracle - the .NET runtime, a standard's table -, never from Polypiler. A hexadecimal
   string is a bit pattern, a decimal number a value, a list an array or the code units of a string.
2. Add the operation to `IrIntrinsicOp` with its signatures and evaluator (an integer family in
   `Polypiler.Core.IR/Integer/`, a floating-point one in the float table).
3. Give it a fallback: a helper in `Polypiler.Core.IR/Helpers/Operations/` bound in `IntegerLibrary`, a
   portable float implementation, or - where every target has it in its runtime and no decomposition
   exists - `requiresRuntime: true` in the contract.
4. Write it in every backend, natively where the profile's capability table has a form, else through
   the fallback.

`ContractConformanceTests` then checks the new contract without further code: the document is read,
it names an operation, the registry of all contracts builds, every vector over numbers is what the
evaluator gives and what the expanded node gives, and every signature the IR takes for an integer
operation is one the contract admits.

## The optional properties

| Property | Meaning |
| --- | --- |
| `attributes` | compile-time immediates after the parameters: each node operand there is a constant of the attribute's type within `[low, high]` |
| `algebra` | `commutative`, `associative`, `idempotent`, `involution`, `identity-element`, `absorbing-element` |
| `lowersTo` | the `INTRINSICS.` and `IR.` documents a decomposition may use, in lowering order; the registry refuses a cycle among the `INTRINSICS.` ones |
| `facts` | `resultRange` `[low, high]`, `knownZeroBits`, `knownOneBits` |
| `laneLiftable` | the operation applies lane by lane to vectors |
| `requiresRuntime` | no decomposition: the targets provide the operation from their runtimes, and `ExpandIntrinsics` leaves it |
| `spelling` | the IR spelling when it differs from the name, dotted PascalCase such as `Memory.Copy`; `TryResolve` finds a contract by either |

## Several results

A contract with more than one return describes one tuple value; `ResultNames` gives the names of its
projections in the order of `returns` (`first`, `second` are `First`, `Second`). The tuple values and
projection nodes themselves belong to the IR core (#120); until they exist an operation of the IR has
one result, and `Check` leaves the type of a node with several results unchecked.

## Diagnostics

| Code | When |
| --- | --- |
| `PLY0250` | a node's operation has no contract in the registry, or a contract names no operation (`IntrinsicCatalog`) |
| `PLY0251` | a contract document has a property outside the schema; the message names the file and the property |
| `PLY0252` | a node has an operand, attribute or result of a type its contract does not admit, or the wrong number of operands |
| `PLY0253` | two contracts of one name |
| `PLY0254` | a contract with neither a decomposition nor `requiresRuntime: true` |
| `PLY0255` | an attribute operand outside its range |
| `PLY0256` | the `lowersTo` lists form a cycle; the message gives the path |

`PLY0250`, `PLY0252` and `PLY0255` say that IR is invalid; the others stop a registry from being built.
All are defects of Polypiler, not of the program translated.

## Not yet

Types the IR does not have - the widths 24 and 128 of several contracts' issues, `u65` - wait for the
arbitrary-width integers of #118; a contract cannot name them yet.
