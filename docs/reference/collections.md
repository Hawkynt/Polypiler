# Collections

Lists, maps and sets: C#'s `List<T>`, `Dictionary<TKey, TValue>` and `HashSet<T>`, and JavaScript's
arrays that change their length, `Map` and `Set`. They look interchangeable and are not. Three
differences change results: the order a map enumerates in, which keys are equal and which key a
map keeps, and what a change does to a loop that walks the collection. The IR states all three, so
a translation is never "correct up to ordering": the generated program prints what the source
program prints.
Related: [repository README](../../README.md) · [optional values](optional-values.md) ·
[exceptions](exceptions.md) · [classes](classes.md) · [value records](records.md).

## The model

- **Types.** `IrType.List(T)` (`list<T>`), `IrType.Map(K, V, order)` (`map<K, V, order>`) and
  `IrType.Set(T, order)` (`set<T, order>`): references with an identity, never `null`. An element
  or a value is a scalar, a string, a class reference, a struct or a collection; a key or an element
  of a set is no struct. An optional value is no element yet.
- **Order** (`MapOrder`). `Insertion` is JavaScript's: the order keys were first inserted, a removed
  key gone, inserted again last (ECMA-262 §24.1, §24.2). `SlotReuse` is .NET's: entries in the order
  of their slots; a removal frees its slot, the next insertion takes the slot freed last, or else
  the next unused one; `Clear` frees every slot and forgets the freed ones.
- **Key equality**, one rule for both orders: integers and truth values by value, strings by code
  unit, class references by identity, floating-point numbers by `Equals` - every NaN equals every
  NaN, `+0` equals `-0`. Inserting a key equal to a present one keeps the **present key** and
  replaces the value. The JavaScript frontend inserts a floating-point key as `k + 0`, which is how
  a `Map` stores `-0` as `+0`.
- **Enumeration** (`IrForEachCollection(collection, mode, element, value, body)`; the element of a
  map is its key). `Checked` is .NET's: a list traps with `CollectionModified` at the next step
  after any change - also after the last pass; a map or a set after the insertion of a new key, not
  after a removal, a clear or the replacement of a value (.NET Core 3.0 and later). `Live` is
  JavaScript's: a list is walked by index up to its current length; a map or a set visits what is
  inserted during the walk and skips what is removed before it is reached.

The example of the order: insert 1, 2, 3, remove 1 and 3, insert 4, 5, 6.

| Step | .NET slots | enumerated | JavaScript | enumerated |
| --- | --- | --- | --- | --- |
| insert 1, 2, 3 | `[1, 2, 3]` | 123 | `1, 2, 3` | 123 |
| remove 1, 3 | `[_, 2, _]`, freed 2 then 0 | 2 | `2` | 2 |
| insert 4, 5, 6 | `[5, 2, 4, 6]` | **5246** | `2, 4, 5, 6` | **2456** |

The slot order is the behaviour measured on .NET 10, and the free list of the .NET Framework 4.8
reference source (`Dictionary.Remove` links the freed entry as the new head of the free list).

## Operations

`IrCollection(op, type, arguments)`, the collection first; `IrCollectionOp`:

| Kind | Operation | Value | Traps |
| --- | --- | --- | --- |
| list | `ListNew`, `ListFromArray`, `ListOf` (a list of the operands, for elements no array holds: objects, arrays; #1365), `ListCount`, `ListCopyToArray` | the list, its count, a new array | - |
| list | `ListGet`, `ListSet`, `ListRemoveAt`; `ListInsert` (0 … count) | the element; nothing | `ArgumentOutOfRange` outside |
| list | `ListAdd`, `ListClear` | nothing | - |
| list | `ListRemoveLast` (`pop`) | an optional element, `null` for references, none when empty | - |
| list | `ListContains`, `ListIndexOfEquals` | by `Equals`: a NaN is found | - |
| list | `ListIndexOfStrict` | by `==`: no NaN, `-0` found as `0` | - |
| list | `ListIncludesSameValueZero` | SameValueZero: a NaN is found, references by identity | - |
| map | `MapNew`, `MapCount`, `MapContainsKey`, `MapPut`, `MapRemove` (was present), `MapClear` | | - |
| map | `MapGet` | the value | `KeyNotFound` |
| map | `MapTryGet` | an optional value, `null` for references | - |
| map | `MapAdd` / `MapTryAdd` | nothing / whether it inserted | `DuplicateKey` / - |
| set | `SetNew`, `SetCount`, `SetAdd` (was absent), `SetRemove` (was present), `SetContains`, `SetClear` | | - |

`KeyNotFound` throws `KeyNotFoundException`, `DuplicateKey` an `ArgumentException`,
`CollectionModified` an `InvalidOperationException` ([exceptions](exceptions.md)).

## C# frontend

| Construct | IR | Status |
| --- | --- | --- |
| `List<T>`: `new()`, `new(array)`, initializers, collection expressions, `Add`, `Insert`, `RemoveAt`, `Clear`, `Count`, the indexer (also `l[i] op= v`), `Contains`, `IndexOf`, `ToArray` | `List` | supported |
| `Dictionary<K, V>`: `new()`, `{ k, v }` (`Add`) and `[k] = v` initializers, the indexer (`Get`, `Put`, `d[k] op= v`), `Add`, `TryAdd`, `ContainsKey`, `Remove`, `Clear`, `Count` | `Map(SlotReuse)` | supported |
| `if (d.TryGetValue(k, out var v)) … else …`, also negated | `ContainsKey`, `Get`; the default value where the key is missing | supported |
| `HashSet<T>`: `new()`, initializers, `Add`, `Remove`, `Contains`, `Clear`, `Count` | `Set(SlotReuse)` | supported |
| `foreach` over a list, a set, a dictionary (`kv.Key`, `kv.Value`, `var (k, v)`), `d.Keys`, `d.Values` | `Checked` | supported |
| a capacity, a comparer, `SortedDictionary`, `SortedSet`, `Queue`, `Stack`, `LinkedList`, any other member, `TryGetValue` elsewhere | - | `PLY1002` naming it |

## JavaScript frontend

An `Array` that changes its length - `push` (of one or more values), `pop`, `splice(i, 1)`,
`splice(i, 0, x)`, `a.length = 0`, `a[a.length] = x` - or that is searched with `indexOf` or
`includes` is a list; one that never changes its length stays an array. One array is one list
wherever it is held: a variable assigned to such a list, an argument passed for one, a returned
variable and the result of a function of the file become lists as well. A list and a typed array
in one place are `PLY1105`.

| Construct | IR | Status |
| --- | --- | --- |
| `a[i]`, `a[i] = v`, `a[i] op= v`, `a[i]++`, `a.length` | `Get`, `Set`, `Count`; a read or write outside traps (caveat `js-oob-read`) | supported |
| `push`, `pop`, `splice(i, 1)`, `splice(i, 0, x)`, `a.length = 0`, `a[a.length] = x` | `Add`, `RemoveLast`, `RemoveAt`, `Insert`, `Clear`, `Add` | supported |
| `indexOf`, `includes`, `[]`, `new Array()`, `[...a]`, `for…of` | `IndexOfStrict`, `IncludesSameValueZero`, a new list, a copy, `Live` | supported |
| `new Map()`, `new Map([[k, v], …])`, `get` (an optional value), `set`, `has`, `delete`, `clear`, `size` | `Map(Insertion)` | supported |
| `new Set()`, `new Set([…])`, `new Set(array)`, `add`, `delete`, `has`, `clear`, `size` | `Set(Insertion)` | supported |
| `for (const [k, v] of m)`, `m.entries()`, `m.keys()`, `m.values()`, `for (const x of s)` | `Live` | supported |
| another length or splice, the value of `push`, `set`, `add` or `splice`, `pop` or `get` of strings as a value, `indexOf` of records, a `Map` keyed by object literals, `WeakMap`, callbacks (`map`, `filter`, `sort`, `forEach`) | - | `PLY1102` naming it |

## C# backend

| IR | net2.0 | net3.5 … netstandard2.0 | netstandard2.1 … net10.0 |
| --- | --- | --- | --- |
| `List`; `Checked` loop | `List<T>`; `foreach` | same | same |
| `List`; `Live` loop | `for` reading `Count` before every pass | same | same |
| `pop`, `indexOf` on floats, `includes` on references | helpers `PopValue`/`PopReference`, `IndexOfStrictDouble`/`IndexOfStrictSingle`, `ContainsReference` | same | same |
| `Map(SlotReuse)` | `Dictionary<K, V>`; the nested `KeyedSlots<K, V>` where a checked loop's body may change a map or a set (.NET Framework refuses a removal there) | same | `Dictionary<K, V>` |
| `Set(SlotReuse)` | `KeyedSlots<T, bool>` (no `HashSet<T>`) | `HashSet<T>`; `KeyedSlots` as for the map | `HashSet<T>` |
| `Map`/`Set(Insertion)` | `Dictionary`/`HashSet` where no removal is reachable and no live loop's body may change one - then the slots are in the order of insertion; otherwise `KeyedSlots` without reuse | same | same |
| floating-point keys | the nested comparer `FloatKeys`/`SingleKeys` (.NET Framework hashes NaNs and zeros apart) | same | `EqualityComparer<T>.Default` |
| `TryAdd`; `TryGet` | helper `TryAdd`; helpers `FindValue`, `FindReference` (`FindSlotValue`, `FindSlotReference` on the slots) | same | `Dictionary.TryAdd` |

A loop over `KeyedSlots` walks its slots by index, skips the empty ones and, when checked, compares
the version - the count of insertions of new keys - before every step. Rows: `List<T>` (net2.0),
`Dictionary<TKey,TValue>` (net2.0), `HashSet<T>` (net3.5), `Dictionary<TKey,TValue>.TryAdd`
(netstandard2.1), `Dictionary<TKey,TValue> enumeration of .NET Core 3.0` (netstandard2.1),
`EqualityComparer<double>.Default normalising NaN and -0` (netstandard2.1),
`OrderedDictionary<TKey,TValue>` (net10.0, recorded; the slots stand in for it).

## JavaScript backend

| IR | es5 | es2015 … es2022 |
| --- | --- | --- |
| `List` | `Array`: `push`, `pop`, `length`, `indexOf`; helpers `$lget`, `$lset`, `$linsert`, `$lremoveAt` that trap outside the list; `$lindexEq`, `$lindexBy` for `Equals` | same |
| `includes` | helper `$lincludes` | native from es2017 (ECMAScript 2016), helper at es2015 |
| `List` under a checked loop whose body may change a list | every change through `$lvadd`, `$lvset`, `$lvinsert`, `$lvremoveAt`, `$lvpop`, `$lvclear`, which count in `$v`; the loop compares with `$lvcheck` before every step | same |
| `Map`/`Set(Insertion)` | `$Keyed` without reuse | `Map`/`Set`, walked by their iterator |
| `Map`/`Set(SlotReuse)` | `$Keyed` with reuse | `Map`/`Set` where no removal is reachable, the keys are no floating-point numbers and no checked loop's body may change one; otherwise `$Keyed` |
| `Get`, `Add`, `TryAdd`, `SetAdd` | `$mget`, `$madd`, `$mtryadd`, `$sadd` | same |
| checked loop over `$Keyed` | by slot, `$mvcheck` before every step | same |

`$Keyed` has the methods of `Map` and `Set` and keeps its entries in slots - the key as inserted,
so `-0` stays the key where .NET keeps it - with the order of either language; a key is found by
its type and text, a pair of 64-bit halves by its halves, an object by an identifier it is given.
Rows: `Map`, `Set`, `.entries`, `.keys`, `.values`, `.next` (2015, `$Keyed`), `.get`, `.has`,
`.delete`, `.clear`, `.add`, `.push`, `.pop`, `.splice`, `.slice`, `.map` (5), `.includes of an
array` (2016, `$lincludes`).

## Windows Batch backend

For a closed Int32 kernel with a finite pool of string keys, the `cmd` target has a compact
`Dictionary<string, int>` lowering. It assigns numeric IDs to
non-null keys from a finite set of literals using ordinal UTF-16 equality. An empty key, differently
cased keys, and distinct Unicode code-unit sequences can each have their own ID. A conditional or
local can select a key from that set.

Map references carry a map ID, so an alias updates the same entries. A slot stores the integer value
and its presence follows the environment variable's existence, including for zero. A new map clears
its owned slots under `SETLOCAL` and initializes its count. The generated commands use numeric IDs
when addressing an entry, make no external calls, and restore the caller's environment on return.

The supported operations are allocation, assignment, `Add`, indexer reads, `ContainsKey`, and
`Count`. A missing read or duplicate `Add` exits with status 97 and a `PLY_BATCH_TRAP` marker on
standard error. The shared 512-slot budget counts array elements, one slot per potential key per map
allocation, and each map's count. The compact lowering reports `PLY2850` when this budget is
exceeded. Other supported dictionary shapes use the general CMD runtime.

## Known differences

- A JavaScript read or write outside a list traps where JavaScript reads `undefined` or leaves a
  hole (caveat `js-oob-read`).
- `m.get(k)` is always an optional value; a `has(k)` test before it proves nothing to the import yet.
- `pop` and `get` of strings are refused as values: the IR has no optional string yet.
- `ToArray`, `FromArray` and `[...a]` of a list of class references are refused: the IR has no array
  of class references yet.
- `OrderedDictionary<TKey,TValue>` is not written for net10.0 yet; the nested slots stand in for it.
