# Language semantics and high-level lowering

**Status:** planned semantic model. See [PRD §§25, 30–32](../PRD.md).

Polypiler represents what a construct **does**, then chooses a native construct, structural lowering, minimal runtime helper or an unsupported-capability diagnostic. No assumption that the destination language implements the same syntax.

## Feature families

| Semantic construct | Recovery and canonical form | Example constrained-target lowering |
|---|---|---|
| Generics + constraints | Known instantiations, type relations | Monomorphized procedures/UDTs |
| Overloads/operators | Language-specific resolution + selected signature | Mangled specialized functions |
| Variadics/rest | Fixed and variable arguments, promotions, context | Specialized fixed-arity calls or explicit argument descriptors |
| Classes/interfaces/abstracts | Identity, dispatch, construction, type graph | UDT + generated methods/vtables |
| Multiple/diamond inheritance | Shared vs duplicated base subobjects, pointer adjustment | Explicit base storage and dispatch thunks |
| Anonymous/structural types | Property shape, equality and identity | Generated named record or scalar SSA fields |
| Enums/ADTs/type switch | Underlying values, tags, pattern guards/exhaustiveness | Constants, tagged unions and decision trees |
| Properties/indexers/closures | Accessor side effects, environments | Procedures + captured state |
| Iterators/yield/coroutines | Suspend/resume value and lifetime state | Generated state machine |
| Async/await/promises | Scheduling, errors, cancellation and completion | State machine **plus** compatible scheduler |
| try/catch/finally | Handler selection, nonlocal flow and cleanup | Explicit result/handler regions + cleanup edges |
| `using`/RAII/defer | Acquisition and deterministic cleanup | ResourceScope or explicit cleanup paths |
| VB `On Error Resume Next` | Statement-level recovery and `Err` state | Recovery edge after each relevant source statement |
| Static/TLS/lazy fields | Initialization ordering and storage duration | Module storage, init guards or runtime support |
| Reflection/dynamic calls | Type identity, metadata, code loading | Minimal metadata and dispatch or diagnostic |

C varargs, C# `params`, Java varargs, Python `*args`/`**kwargs`, JS rest and Perl `@_` are not one interchangeable representation. Source-specific argument promotions, evaluation and ABI must be explicit.

C++ virtual versus nonvirtual diamond inheritance and Python's C3 MRO are likewise **different semantic contracts** even if both are called multiple inheritance.

## Recover deterministic disposal

A C# `using` can be lowered to a `try/finally` with disposal. Reverse recovery from existing cleanup blocks is allowed only if acquisition, all exits, disposal order, null handling and exception precedence match.

    Acquire resource ──► body ──► normal exit
             │             │
             └─────► cleanup region ◄──── exception / return / break
                            │
                      completion / rethrow

Do not merge cleanup edges that change visible error behavior. When a target lacks structured exceptions, explicitly synthesize propagation/cleanup if compatible; otherwise report unsupported semantics.

## Coroutines and async are distinct

A coroutine is resumable computation and may have no concurrency. An async function adds an execution/completion contract that may involve scheduler, cancellation and exception propagation. A target without `async` syntax can still run a state machine, but not without the relevant runtime capabilities if asynchronous operations remain.

Recovery of a compiler-generated state machine into a high-level construct requires proof about live variables, suspension points, control-flow transitions and cleanup. A recognizable state machine shape alone is not enough.

## Object lifetime and initialization

A C# object, Rust value, C++ base subobject and dynamic Python object do not have identical ownership or type identities. Lowering must preserve:

- construction/destruction order, finalization and observable identity;
- dynamic dispatch resolution, casts, method overrides and interface tables;
- static initialization order, thread-safety, re-entrancy and failure behavior;
- heap/stack movement, escaping references and retained foreign pointers.

Optimize away an abstraction only after proving it is not observable (e.g. one reachable interface implementation, one generic instantiation, no reflective lookup).

## Runtime closure

Only include reachable helpers for supported behaviors. Importing a checksum should not pull an entire .NET or JVM runtime into DOS or Perl. Reflection, dynamic assembly loading or thread scheduling may require services the target does not have; reject with a specific capability diagnostic rather than fabricating behavior.

Related: [IR](IR.md) · [pipeline](PIPELINE.md) · [targets](TARGETS.md).
