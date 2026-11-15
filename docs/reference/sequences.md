# Sequences

C#'s iterator methods and JavaScript's generators produce values on demand. They look alike and
differ in exactly the places a naive translation breaks: the result of a C# iterator method can be
enumerated **again** and runs its body afresh each time, a JavaScript generator object is
**single-pass** and yields nothing the second time; both run nothing until the first value is asked
for; both run their `finally` blocks when the consumer leaves early (`Dispose`, `return()`). The IR
states all of it, so a translation keeps the values, the order of the side effects and the cleanup.
Related: [repository README](../../README.md) · [collections](collections.md) ·
[exceptions](exceptions.md) · [functions and closures](functions-and-closures.md).

## The model

- **Type.** `IrType.Sequence(T, restartable)` (`seq<T>` or `seq<T, single-pass>`): a reference that is
  never `null`. An element is a scalar, a string, a struct, a class reference, a collection, an array
  or a function - no view, no optional value, no sequence.
- **Iterator function.** An `IrFunction` whose result is a sequence. Calling it evaluates the
  arguments and returns the sequence **without running the body**.
- **Restartable** (`Restartable = true`, C#'s `IEnumerable<T>` of an iterator method, an array as a
  sequence): every enumeration runs the body anew from the argument values of the call.
- **Single-pass** (`Restartable = false`, a JavaScript generator object): one cursor. A second
  enumeration continues where the first stopped; after a finished or abandoned enumeration it yields
  nothing.
- **Closing.** However a loop over a sequence ends - at the end, by `break`, by `return` or by an
  exception leaving its body - the cursor is closed before control goes on: the pending `finally`
  blocks of the iterator run, innermost first, exactly once. An exception thrown by the body reaches
  the consumer after the values already produced.

## Nodes and verifier rules

| Node | Meaning | Verifier |
| --- | --- | --- |
| `IrYield(Value)` | produce the next value and suspend | in the body of an iterator function, of its element type; not inside a catch clause, a finally block or a try that has a catch clause (C# has no form for them: CS1626, CS1625, CS1631); not inside a lambda |
| `IrYieldBreak` | end the sequence; `IrReturn` without a value means the same | in an iterator function |
| `IrYieldFrom(Sequence)` | produce every value of another sequence; closing the outer cursor closes the inner one first | a sequence of the element type |
| `IrForEachSequence(Element, Sequence, Body)` | a loop over a sequence, closed however it ends | the element a local of the function of the element type, in scope in the body only |
| `IrArraySequence(Array)` | the elements of an array as a restartable sequence, each read when it is reached | of an array |

An iterator function has no `IrReturn` with a value. The interpreter runs the body of every
enumeration as a coroutine on a thread of its own, which the consumer and the body hand control to
in turn; closing a cursor throws at the yield it waits at, which runs its pending finally blocks.

## Construct table

| Construct | IR | C# | JavaScript |
| --- | --- | --- | --- |
| iterator method returning `IEnumerable<T>` | iterator function, restartable | supported, also of a class and generic | - |
| `function*`, generator method of a class | iterator function, single-pass | - | supported |
| `yield return e` / `yield e` as a statement | `IrYield` | supported | supported |
| `yield break` / `return;` in a generator | `IrYieldBreak` / `IrReturn` | supported | supported |
| `foreach (var v in s) yield return v;` / `yield* s` | `IrYieldFrom` | the idiom is recognised | of a generator object or an array |
| `foreach` / `for…of` over a sequence | `IrForEachSequence` | supported, converting the element where the source does | over a generator object |
| array as a sequence | `IrArraySequence` | `T[]` converted to `IEnumerable<T>` | an array in `yield*` |
| `const x = yield 1`, `yield;`, `return v` in a generator, a nested or async generator | - | - | `PLY1102` naming it |
| an enumerator used by hand (`GetEnumerator`, `MoveNext`, `Current`), `.next()` outside a `for…of` | - | `PLY1002` | `PLY1102` |
| an iterator local function or lambda, a method returning `IEnumerable<T>` without being an iterator, a `List<T>` as `IEnumerable<T>`, the non-generic `IEnumerable` | - | `PLY1002` naming it | - |
| a variable holding a generator object and a number, a generator yielding values of two kinds | - | - | `PLY1105` |

## C# backend

| IR | every profile, net2.0 (C# 2) to net10.0 (C# 14) |
| --- | --- |
| iterator function, restartable | an iterator method returning `IEnumerable<T>` with `yield return` and `yield break` |
| iterator function, single-pass | the same iterator method; at the call the helper `SinglePass(f(…).GetEnumerator())`, an iterator over the one enumerator of the call that disposes it when the first enumeration ends, after which every enumeration yields nothing |
| `IrForEachSequence` | `foreach`; below C# 5 the element is copied into a variable of the pass where a closure captures it |
| `IrYieldFrom` | `foreach (T value in s) yield return value;` |
| `IrArraySequence` | the array cast to its `IEnumerable<T>`, the array itself in a `foreach` |

Rows: `iterator (yield)` (C# 2), `IEnumerable<T>` (net2.0), the helper of single-pass sequences
(net2.0). No profile lacks any of them, so `PLY1053` is unreachable for every construct of this page.

## JavaScript backend

| IR | es5 (with or without `--typed-arrays`) | es2015, es2017, es2020, es2022 |
| --- | --- | --- |
| iterator function, single-pass | a state machine: its variables in the closure of the function, a labelled loop over a `switch` of resume points, returning the cursor of `$generator` - `next()` gives `{ value, done }`, `"return"()` runs the pending finally blocks innermost first and ends it | `function*` |
| iterator function, restartable | `{ $iterator: function () { return $make(a, b); } }`, `$make` the state machine | `{ [Symbol.iterator]: () => $make(a, b) }`, `$make` the `function*` of the body; a method calls it with `.call(this, …)` |
| `IrForEachSequence` | `var $it = $iterate(s), $r = null; try { while (!($r = $it.next()).done) { var v = $r.value; … } } finally { if ($r && !$r.done) $it["return"](); }` | `for (const v of s)` |
| `IrYieldFrom` | a resume point that loops over the inner cursor, which a pending finally block closes | `yield*` |
| `IrArraySequence` | `$arrayIterator(a)` | the array |
| exported function returning a sequence | the cursor, or the object of `$iterator()` | the generator object or the iterable |

In the state machine a statement that suspends is cut into resume points: a yield sets the next one
and returns its value, a loop, a switch and a conditional become jumps between them - a loop over a
list walks it by an index the machine keeps, one over a map or a set the slots of its `$Keyed`,
comparing the count of changes before every step where it is checked - and a try's
finally block becomes a function the cursor keeps (`enter`) while the body is inside the try, which
leaving it (`leave`), closing the cursor and an exception run. Every other statement is written as
it is; a `break`, `continue` or `return` that leaves it becomes a jump of the machine
(`$s = n; continue $machine;`), through a resume point that leaves the finally blocks it crosses.
A closure made in a pass of a loop that suspends keeps the variables of that pass - a variable the
body declares, the element of a loop over a sequence or a collection, a variable of every pass of a
`for (let …)` - in the environment object the pass makes anew, as everywhere else at ES5. A checked
loop over a list, a map or a set whose body suspends counts the changes of the collection, since the
consumer may change it while the iterator waits.

Rows: `function*` (2015, "state machine"), `for-of` (2015, "next loop"), `Symbol` and
`Symbol.iterator` (2015, `$iterator`), `yield*` (2015, "nested resume point"); the helpers
`$generator`, `$closeAll`, `$iterate`, `$arrayIterator`.

## Known differences

- An exception a `finally` block of the iterator throws while the cursor is closed by an exception of
  the consumer replaces that exception, as `Dispose` does in .NET; JavaScript's `for…of` keeps the
  consumer's exception.
- An iterator method returning `IEnumerator<T>`, used by a class's `GetEnumerator`, is refused with
  the enumerator used by hand.
