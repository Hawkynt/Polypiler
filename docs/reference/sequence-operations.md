# Sequence operations

LINQ to Objects and JavaScript's array callbacks look alike and are not. LINQ is **deferred**: nothing
runs until the sequence is enumerated, the elements flow through the whole chain one at a time, and
every enumeration runs the lambdas again. JavaScript's `map`, `filter` and `reduce` are **eager**:
each method runs over the whole array before the next one starts. A translator that wrote `Select` as
`map` would change which lambdas run, how often and in which order; a lambda with an effect or a trap
makes that observable. The IR keeps both families apart and translates each with its own order
(#1329). Related: [sequences](sequences.md) · [functions and closures](functions-and-closures.md) ·
[collections](collections.md) · [exceptions](exceptions.md).

## LINQ in the IR

`IrSequence(Op, Type, Arguments)` is one operation on a lazy sequence (`IrSequenceOp`);
`IrSequenceOps.Signature` is the typing rule the verifier checks and the frontends build with.

| Operation | Shape | Laziness and the calls of its functions | Traps |
| --- | --- | --- | --- |
| `FromArray` | `T[] → seq<T>` | every element read when it is reached | |
| `FromList` | `list<T> → seq<T>` | as .NET enumerates a `List<T>` | `InvalidOperationException` at the next step after a change of the list |
| `Range` | `(i32, i32) → seq<i32>` | | `ArgumentOutOfRangeException` for a negative count or a last number above `int.MaxValue`, **when it is evaluated** |
| `Select`, `SelectIndexed` | `(seq<T>, fn(T[, i32]) → U)` | lazy, one call per element pulled; the index counts the elements this operation pulled | |
| `Where`, `WhereIndexed` | `(seq<T>, fn(T[, i32]) → bool)` | lazy | |
| `SelectMany`, `SelectManyResult` | `(seq<T>, fn(T) → seq<U>[, fn(T, U) → V])` | lazy; the inner sequence is enumerated completely before the next outer element | |
| `Take`, `Skip` | `(seq<T>, i32)` | lazy; `Take` pulls nothing after the n-th element, and nothing at all for n ≤ 0; a negative `Skip` acts as 0 | |
| `Zip` | `(seq<T>, array or list of U, fn(T, U) → V)` | lazy; pulls the first, then the second; stops at the shorter | |
| `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` | `(seq<T>, fn(T) → K)` | deferred; on enumeration every element is pulled, then the keys of the first level are computed once per element in the order pulled, then those of the next level; the sort is **stable**; keys compare as .NET's default comparer (NaN below every number, NaN = NaN, −0 = +0), a string key by code unit | |
| `ToArray`, `ToList`, `Count`, `CountWhere`, `Any`, `AnyWhere`, `All`, `Contains`, `IndexOf`, `FindIndex` | `seq<T> → …` | eager; `Any`, `All`, `Contains` and the searches stop at the element that decides them | `Count` traps with `OverflowException` beyond `int.MaxValue` |
| `First`, `FirstWhere`, `FirstOrDefault`, `FirstOrDefaultWhere` | `seq<T> → T` | eager, stops at the first match | `First` of nothing: `InvalidOperationException` |
| `Sum` | `seq<i32 / i64 / f32 / f64> → T` | eager | integers: a **checked** left fold from 0, `OverflowException` at the first partial sum outside the type; `f32` accumulated in `f64` and rounded once; `f64` a left fold from +0.0 |
| `Min`, `Max` | `seq<number> → T` | eager; of floating-point numbers a NaN is the smallest - `Min` returns the first NaN at once, `Max` returns NaN only when every element is one | of nothing: `InvalidOperationException` |
| `Aggregate`, `AggregateSeed` | `(seq<T>[, A], fn(A, T) → A)` | eager left fold | `Aggregate` of nothing: `InvalidOperationException` |

`Contains` and `IndexOf` compare by `Equals`: a NaN is found.

The interpreter computes every operation without `System.Linq`, so that it stays an independent oracle
of the generated code: each lazy operation is an iterator of its own over the cursors of the
[sequences](sequences.md), which closes its sources however it ends.

## One loop per chain

`SequenceFusion.Run` lowers every operation before a backend writes the module - both emitters run it
in `Legalize`, `Check` and `Build`. A chain - a source, its lazy operations and a terminal or nothing -
becomes the call of **one function** the pass adds to the module (`<function>$<operation>`; C# writes
the `$` as `_`). Its arguments are the values the chain was given, in the order the chain evaluates
them; its body is one loop that pulls an element and takes it through every operation in turn, calling
the lambdas in exactly the order the lazy enumeration calls them:

```csharp
// Deferred(): a.Select(f).Where(p) stored in q, then q.First()
var q = Deferred_Where(a, f, p);   // an iterator: nothing runs here
var first = Deferred_First(q);     // one loop over q

private static IEnumerable<int> Deferred_Where(int[] source, Func<int, int> select, Func<int, bool> where) {
  int i = 0;
  while (i < source.Length) {
    var x = source[i];
    i = i + 1;
    var y = select(x);
    if (where(y)) {
      yield return y;
    }
  }
}
```

- A chain that ends in a **terminal** is a function returning its value; `First`, `Any` and the
  searches return from inside the loop, so nothing after the deciding element is pulled.
- A chain that ends in a **sequence** - stored, passed or returned - is an iterator function: every
  enumeration runs the loop again, and a captured variable is read then.
- `Take` sets a flag after the element has passed every later operation; the flag is tested at the end
  of the pass, before the next element is pulled, in every loop of the segment - so an inner sequence of
  `SelectMany` stops too. A `Take` of nothing anywhere in the chain pulls nothing and calls nothing.
- An **ordering** splits the loop: the elements are pulled into a list, the keys computed level by
  level, the indices sorted by `$stableSort`, and the operations after the ordering loop over the sorted
  list.
- `Range` is checked where it is evaluated by `$rangeCount`, which returns the count or throws; a start
  that is no constant is kept in a temporary the check reads.

`$stableSort(int[] items, fn(i32, i32) → i32)` and `$rangeCount` are functions of the IR in
`SequenceLibrary`, added to a module once; the backends write them as any function, no helper source of
a backend changed. `$stableSort` is the bottom-up merge sort of Sedgewick and Wayne, *Algorithms*, 4th
ed., §2.2 (see `Polypiler.Core.IR/Sequences/THIRD-PARTY-NOTICE.MergeSort.txt`).

## C# frontend

`CSharpSequenceApi` is the table the tests check. The compilation references `System.Linq`; the members
are called as extension methods or as static methods on arrays, `List<T>` and sequences.

| Construct | IR |
| --- | --- |
| `Select`, `Where` (with and without the index), `SelectMany` (with and without a result selector), `Take(int)`, `Skip(int)`, `Range` | the operation of the same name |
| `Zip(second, (x, y) => …)` with an array or a `List<T>` as the second sequence | `Zip` |
| `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` of a number or truth value key, or of a string key with `StringComparer.Ordinal` | the ordering |
| `ToArray`, `ToList`, `Count`, `Any`, `All`, `First`, `FirstOrDefault`, `Sum`, `Min`, `Max`, `Aggregate`, `Contains` | the terminal; a predicate overload is the `…Where` form; a selector of `Sum`, `Min` and `Max` a `Select` before them; the result selector of `Aggregate` a call of it on the fold |
| `array.Contains(v)`, which C# 14 binds to `MemoryExtensions` | `Contains` |
| query syntax: `from`, `where`, `orderby [descending]`, `select`, `let`, a second `from` | the operations as ECMA-334 §12.20.3 defines them; `let` and a second `from` are a `SelectMany` of the rest of the query with both range variables in scope - a `let` over the one-element array of its value - which calls the lambdas in the order the query does |
| `Array.IndexOf`, `Array.Find`, `Array.FindIndex`, `Array.Exists`, `Array.TrueForAll` | `IndexOf`, `FirstOrDefaultWhere`, `FindIndex`, `AnyWhere`, `All` on the elements of the array |
| `OrderBy` of a string key without `StringComparer.Ordinal` | `PLY1002` "culture-sensitive comparison" |
| `ThenBy` of an ordered sequence held in a variable, `Zip` with any other second sequence | `PLY1002` |
| `join`, `group`, `into`, an `orderby` after a `let` or a second `from`, a typed range variable | `PLY1002` |
| `AsParallel`, `AsQueryable` and the members of `ParallelEnumerable` and `Queryable` | `PLY1002` naming the member |
| `GroupBy`, `Join`, `Distinct`, `ToDictionary`, `Last`, `Single`, `Average` and every other member | `PLY1002` naming the member or the type it yields |

A `List<T>` passed where an `IEnumerable<T>` is expected is the sequence of its elements, and a lambda
may return a sequence.

## JavaScript frontend

`JavaScriptArrayApi` is the table the tests check. A call of one of these methods on an array is the
call of a function of the module that runs the method as the loop of ECMA-262 §23.1.3 - eager, over the
whole array:

| Method | Loop |
| --- | --- |
| `map`, `filter`, `forEach`, `flatMap` | the length read once before the first call; every element read when it is reached; an index a list no longer has skipped, as `HasProperty` skips it |
| `reduce`, `reduceRight` | from the initial value, or from the first element present; of an empty array without one a `TypeError` |
| `some`, `every`, `find`, `findIndex` | stop at the element that decides them |
| `sort(comparator)` | the elements copied, their indices sorted by `$stableSort` by the sign of the comparator - a NaN counts as 0 - and written back; stable, as ECMAScript 2019 and later require |
| `indexOf`, `includes` | `ListIndexOfStrict` (`[NaN].indexOf(NaN)` is −1) and `ListIncludesSameValueZero` (`[NaN].includes(NaN)` is true), of [collections](collections.md) |

The callback is an arrow, a function expression or a function of the file; it takes `(element, index,
array)` - the accumulator first for `reduce` - as far as its parameters reach, and its result counts by
`ToBoolean` where the method tests it. The inference gives the callback what its method passes and the
call the type it yields; the new array of `map`, `filter` and `flatMap` is a list where the variable it
goes to grows.

Refused with `PLY1102`: `sort()` without a comparator (it orders by the text of the elements), a
comparator with effects - one that writes anything but its own locals, or calls a function, whose call
order ECMA-262 leaves to the engine - and one that is no function expression, `findLast`,
`findLastIndex`, `copyWithin`, a library function as the callback (`["1", "2"].map(parseInt)`), a
callback with more parameters than its method passes, a `thisArg`, `map` and `flatMap` of a typed array
of anything but doubles, and `find` of an array of strings. A comparator that returns a double is
announced with the caveat **`js-sort-order`**: for inputs that make it inconsistent, such as a NaN,
ECMA-262 fixes no order, the translation fixes that of its merge sort.

## Backends

Both backends write the functions `SequenceFusion` and the JavaScript frontend add as any function of
the module, in every profile: a fused pipeline is a loop everywhere; an escaping sequence is an iterator
- `yield return` in C# from `net2.0` on, `function*` from ES2015 and the state machine of
[sequences](sequences.md) in ES5; the comparison of a sort is `$stableSort` in every profile, since
`Array.Sort` and `List.Sort` are unstable and ECMA-262 2015 and 2017 do not require a stable `sort`.
No operation needs a row of a capability table, and no profile refuses one (`PLY1053`, `PLY1152`).
Where a target library has the same operation natively - `Enumerable.*` from .NET 3.5, `Enumerable.Zip`
from .NET 4.0, a stable `Array.prototype.sort` from ES2019 - the IR helper library of #1353 is where a
native replacement belongs.

## Known differences

- .NET 10 has fast paths for an `IList<T>` source that skip calls a plain lazy enumeration makes:
  `Select(f).Skip(n)` of an array does not call `f` for the skipped elements, `OrderBy(k).Count()` calls
  no key function, and `OrderBy(k).First()` computes the key while it pulls each element. The
  translation makes the calls of the plain enumeration in all of them; the fixtures stay off these paths.
- A `map` over a list that a callback shrinks leaves the default value where JavaScript leaves a hole.
