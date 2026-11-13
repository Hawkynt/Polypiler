# O0455 — Recognize a loop that appends to one string accumulator and grow a code-unit buffer in place

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0455`.

## What it does

`out = out + part + ","` in a loop is the most common way text is built, and with immutable strings it is
quadratic: every iteration allocates a new result and copies everything accumulated so far. Languages with
a builder type expect the programmer to use it; translated code and code from languages without one do
not. The accumulator is a loop-carried value that nothing else reads, so it can live in a growable buffer
of code units — [#379](https://github.com/Hawkynt/Polypiler/issues/379) names `list` of code units as the builder — and become a string once, after
the loop. PRD §4.8, §8 P4.

## Example

### Before

```
head:
  %acc  = Phi.string<utf8> [pre: %init], [body: %next]
  %i    = Phi.uptr [pre: 0], [body: %i1]
  %c    = ICmp.ult.uptr %i, %n
  Br %c, body, exit
body:
  %x    = Call.string<utf8> @part(%i)
  %t    = StrConcat %acc, %x
  %next = StrConcat %t, string<utf8>(",")
  %i1   = Add.uptr %i, 1
  Br head
exit:
  Ret.string<utf8> %acc
```

### After

```
pre:
  %b    = ListNew.list<u8>[capacity=0]
  AppendAll %b, (SeqOf (StrUnits.roslice<u8> %init))
head:
  %i    = Phi.uptr [pre: 0], [body: %i1]
  %c    = ICmp.ult.uptr %i, %n
  Br %c, body, exit
body:
  %x    = Call.string<utf8> @part(%i)
  AppendAll %b, (SeqOf (StrUnits.roslice<u8> %x))
  AppendAll %b, (SeqOf (StrUnits.roslice<u8> string<utf8>(",")))
  %i1   = Add.uptr %i, 1
  Br head
exit:
  %out  = StrFromUnits.string<utf8>[validate trap InvalidOperation] (ListAsSlice.roslice<u8> %b)
  Release %b
  Ret.string<utf8> %out
```

## When it applies

**String model:** `string<enc>` of [#379](https://github.com/Hawkynt/Polypiler/issues/379) — an immutable sequence of code units with value semantics, no identity and no capacity; two references to one string cannot be told apart, and nothing can mutate a string another name still reads. The accumulator is such a value, so replacing its storage is invisible provided nobody looks at it in between:
(1) the loop is natural (the header dominates the latch) and has one exit block. (2) `%acc` is a header
phi of a validated string type whose back-edge value is `StrConcat(…StrConcat(%acc, s1)…, sk)` or
`StrConcatN %acc, s1, …, sk` with `k ≥ 1`; every intermediate of the chain has exactly one user. (3) Inside
the loop `%acc` and the chain have **no other user** — no call argument, no `StrLen`, no comparison, no
store, no capture by a closure ([#128](https://github.com/Hawkynt/Polypiler/issues/128) use lists). (4) No suffix depends on `%acc`
(`out = out + out` would append a view of the buffer to itself, PLY0144 of [#202](https://github.com/Hawkynt/Polypiler/issues/202)). (5) `%acc` is not
live into an exception handler or along an unwind edge of the loop ([#115](https://github.com/Hawkynt/Polypiler/issues/115)): if the body can throw
and a handler reads the accumulator, the string at the moment of the throw would have to exist. If the
body can unwind out of the loop at all, the buffer is released on that edge in a cleanup block; where the
function cannot host one the rule does not fire.
(6) After the loop `%acc` is replaced by one `StrFromUnits` of the buffer at the exit; with trip count 0
it yields the units of `%init`. The `validate` mode cannot fail, because a concatenation of well-formed
strings is well formed ([#379](https://github.com/Hawkynt/Polypiler/issues/379)). (7) The buffer is created, appended to and released in this
function only ([#376](https://github.com/Hawkynt/Polypiler/issues/376): owned, never borrowed across the rewrite). (8) Suffix values are
evaluated at the same points as before; only the copying moves. (9) Allocation failure: the number and
sizes of allocations change, so the rewrite needs a profile that declares allocation failure
unobservable, or a loop body without observable effects ([#114](https://github.com/Hawkynt/Polypiler/issues/114)). (10) [#155](https://github.com/Hawkynt/Polypiler/issues/155): `LoopCost`
with a per-iteration allocation and a copy of the growing prefix against amortized appends plus one final
copy.

## How it is proven

`structural+effect-facts`: with no reader of the intermediate accumulator values, the only observable is the final sequence of code units, which is `init ++ s1 ++ … ++ sk` per iteration in both forms. No solver, no timeout path.

## When it must not fire

- `out = out + p; Call @log(out)` in the body: the log must receive the string as it is after each step.
- `if (StrLen(out) > 80) break` — the accumulator's length is read in the loop.
- `out = out + out`: the suffix is the accumulator itself; appending a list to itself through a view is a stale-view error.
- A `try` around the loop whose handler returns `out`: after a throw in iteration 3 the handler needs the text of iterations 0–2.
- `string<utf16le>[unvalidated]` with a part that ends in a lone high surrogate: `StrFromUnits[lossy]` would turn it into U+FFFD, `validate` would trap.
- A closure created in the loop that captures `out`: each closure must see its own iteration's value.
- Trip count 0: the result must be `init`, not the empty string.

## Switching it off

`--disable-opt O0455`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Targets

targets with immutable strings and a builder idiom — C# (`StringBuilder`), Java, Go (`strings.Builder`), Python (list and `join`), Rust and C++ (`String`/`std::string` appended in place), C (a grown buffer) — and assembly and machine-code backends through the list runtime. JavaScript engines represent `+=` as ropes; their profile prices the loop no higher than the builder, the rule does not fire and the source keeps `+=`. The emitted builder is the idiom a programmer would write, so readability does not suffer where the rule fires.

## Interactions with other passes

runs in phase `optimize` **before** `OPT-STR-CONCATN` (a loop-carried chain collapsed into one n-ary concatenation would still copy the accumulator every iteration) and after `OPT-STR-EMPTYOPERAND`; [#508](https://github.com/Hawkynt/Polypiler/issues/508) may hoist invariant suffix computations; [#507](https://github.com/Hawkynt/Polypiler/issues/507) may fuse the `AppendAll` sources.
