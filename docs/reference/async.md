# Async functions

C#'s `async`/`await` and JavaScript's look identical and order effects differently. .NET continues
after an `await` **synchronously** when the awaited task has completed already, and inline inside
`TaskCompletionSource.SetResult`; JavaScript **always** suspends and resumes from its queue of jobs.
So "a `Task` is a `Promise`" is right for some programs and silently wrong for others. Polypiler
translates exactly the programs for which both runtimes produce the same trace, and refuses every
other one by name. Related: [repository README](../../README.md) · [exceptions](exceptions.md) ·
[sequences](sequences.md) · [functions and closures](functions-and-closures.md).

## Why a subset

Measured with .NET 10.0.112 and node v22.23.3; the acceptance tests run these six probes on both
runtimes every time, so the table stays measured (`tests/features/ir/ir-async.feature`).

| Program shape | .NET | node |
| --- | --- | --- |
| `log m1; t = F(); log m2; await t; log m3` with `F` = `log f1; await <completed>; log f2` | `m1 f1 f2 m2 m3` | `m1 f1 m2 f2 m3` |
| `log m1; await F(); log m3` (awaited where it is created) | `m1 f1 f2 m3` | `m1 f1 f2 m3` |
| `WhenAll(G("a"), G("bb"))` / `Promise.all`, `G` logging around two awaits | `a1 a2 a3 bb1 bb2 bb3` | `a1 bb1 a2 bb2 a3 bb3` |
| waiter awaits a `TaskCompletionSource` / `new Promise`; `log before; set 7; log after` | `w1 before w2:7 after` | `w1 before after w2:7` |
| `WhenAll(A(), B())`, `A` throws after two awaits, `B` after one | throws `A` (argument order) | `Promise.all` rejects with `B` (time order) |
| async method throws before its first await; caller logs, then awaits | `t1 returned caught boom` | `t1 returned caught boom` |

The rows that agree are the ones where every task is **awaited where it is created**. That is the
contract of the IR: an async value is the operand of an `await` - or a call handed directly to an
awaited `WhenAll` - and nothing else. The caller then runs nothing between the start of the awaited
work and its end, which is where the two runtimes differ.

## The model

- **Type.** `IrType.Async(T)`, written `async<T>` (`async<void>` for none): C#'s `Task<T>`/`Task`,
  JavaScript's `Promise`. It is no value of its own - never a local, a parameter, a field or an
  element (`IsStorable` is false). A function declared as C#'s `ValueTask<T>` has the type
  `async<T, value task>`.
- **Async function.** An `IrFunction` whose result is an async type (`IsAsync`). Its returns give the
  result type; a call of it runs it, and a fault - also one before its first `await` - is kept in the
  value and thrown where it is awaited, as both runtimes do (#1323 maps the exception classes).
- **`IrAwait(Operand)`** gives the result of its operand or throws its fault.
- **`IrAsync(Op, Type, Arguments)`**: `Completed` (`Task.CompletedTask`), `FromResult(v)`
  (`Task.FromResult(v)`, `Promise.resolve(v)`), and `WhenAll(calls…)`, which completes with the results
  in argument order and faults with the **first fault in argument order**, as `await Task.WhenAll` does.

Verifier rules: an `IrAwait` stands in the body of an async function itself, never in a lambda; its
operand is the call of an async function of the module or an `IrAsync`; every async value is awaited
where it is created; the operands of `WhenAll` are calls of async functions of one result type; a
`WhenAll` of a function that writes more than its own locals - a global, an array element, a field of
an object, a collection, a method or a function value - is refused, because its interleaving would
show (`IrAsyncFunctions.Timed`).

The interpreter runs the subset in the one order both runtimes agree on: an awaited call runs to its
end before the caller goes on. It uses no task, thread or scheduler. At the boundary of
`IrInterpreter.Call` an async result is awaited: its value is returned, its fault thrown.

## Construct tables

C# (`CSharpAsyncApi`), refused with **PLY1002**:

| Construct | IR, or the words of the refusal |
| --- | --- |
| `async Task`, `async Task<T>`, `async ValueTask`, `async ValueTask<T>` methods of the class | an async function |
| `await F(…)` of an async method of the class | `Await` of the call |
| `await Task.CompletedTask`, `await Task.FromResult(v)` | `Await` of `Completed`, `FromResult` |
| `await Task.WhenAll(F(…), G(…), …)` of isolated async methods of the class | `Await` of `WhenAll` |
| `x.ConfigureAwait(false)`, `x.ConfigureAwait(true)` | dropped: the subset has no synchronization context |
| `try`/`catch`/`finally` around `await` | the exceptions of #1323 |
| a task stored, passed, returned, a parameter or a field | "a task that is not awaited where it is created; its order differs between .NET and JavaScript" |
| `TaskCompletionSource` | "a task completed by hand: .NET continues inside SetResult, JavaScript after it" |
| `.Result`, `.Wait()`, `GetAwaiter().GetResult()` | "blocking on a task" |
| `Task.Run`, `Task.Delay`, `Task.Yield`, `.ContinueWith`, `Task.WhenAny` | the member, `Task.Run` |
| `async void`, an async lambda or local function, `await foreach` | named |
| an array or a list of tasks, so `Task.WhenAll` over a collection | the type, `Task<int>[]` |
| `Task.WhenAll` of a method that writes more than its locals | "… whose order depends on timing" |

JavaScript (`JavaScriptAsyncApi`), refused with **PLY1102**:

| Construct | IR, or the words of the refusal |
| --- | --- |
| `async function f(…)` of the module | an async function of the type its returns are inferred to |
| `await f(…)` of an async function of the module | `Await` of the call |
| `await Promise.resolve()`, `await Promise.resolve(v)`, `await v` of a value that is no promise | `Await` of `Completed`, `FromResult` |
| `await Promise.all([f(…), g(…)])` of isolated async functions, at most one of which can fault | `Await` of `WhenAll` |
| a promise stored, passed or returned without `await` | "a task that is not awaited where it is created" |
| `new Promise(…)` | "a task completed by hand" |
| `o.then(…)`, `o.catch(…)`, `o.finally(…)` where `o` is an object of a class of the module that declares the method | a method call, as [classes](classes.md) import it |
| `.then`, `.catch`, `.finally` of a promise, or of any receiver when no class of the module declares the method, `setTimeout`, `setInterval`, `setImmediate`, `queueMicrotask`, `Promise.race`, `Promise.any`, `Promise.allSettled`, `Promise.reject` | the member |
| an object of a class with a `then` method returned from an async function or awaited: JavaScript calls its `then`, .NET does not | "a thenable object" |
| an async arrow, a nested async function, an async method, an async generator, `for await` | named |
| `Promise.all` of two calls that can fault | "Promise.all whose rejection depends on timing": `Promise.all` rejects with the first fault in time, `Task.WhenAll` with the first in argument order |

## The form per profile

| IR | C# net2.0, net3.5 | C# net4.6 … netcoreapp3.1 | C# net8.0, net10.0 | JS es5 | JS es2015 | JS es2017, es2020, es2022 |
| --- | --- | --- | --- | --- | --- | --- |
| async function | **PLY1053** | `async Task<T>` (`ValueTask<T>` when flagged, from netstandard2.1 on) | `async Task<T>`, `ValueTask<T>` when flagged | **PLY1152** | `function` returning `$async(function* () { … })` | `async function` |
| `IrAwait` | — | `await` | `await` | — | `yield` | `await` |
| `Completed` / `FromResult(v)` | — | `Task.CompletedTask` / `Task.FromResult<T>(v)` | same | — | `Promise.resolve()` / `Promise.resolve(v)` | `void 0` / `v` |
| `WhenAll` | — | `Task.WhenAll` | `Task.WhenAll` | — | `$whenAll` over `Promise.all` | `$whenAll` over `Promise.all` at es2017, `Promise.allSettled` from es2020 |

- **C#.** `async`/`await` needs C# 5 and the builders of .NET Framework 4.5. net2.0 and net3.5 are
  refused: a task type of the class's own would be no API a .NET caller recognises. A function
  declared as a `ValueTask` is written as `Task` below netstandard2.1, and the header of the file
  names it. An async function that awaits nothing awaits `Task.CompletedTask` first, which changes
  nothing and keeps C# from warning (CS1998).
- **JavaScript.** ECMAScript 5 has neither `Promise` nor a queue of jobs: a helper would have to
  schedule with `setTimeout`, a host API outside the profile, or synchronously, the order of .NET. No
  faithful form exists, so it is refused. `$async` (ECMA-262 §27.7.5, AsyncFunctionStart and Await)
  runs a generator over the native `Promise`. `$whenAll` rejects with the first rejection in argument
  order; `Promise.all` alone would choose another fault. A `WhenAll` of numbers becomes the typed array
  of its element type.

## Not in this slice

Stored tasks, `TaskCompletionSource`/`new Promise`, `.then`, timers, threads (an explicit scheduler,
ordering "A" of the issue), async iteration (`IAsyncEnumerable<T>`, `for await`), async lambdas and
local functions, cancellation, and lowering async to state machines for targets without it.
