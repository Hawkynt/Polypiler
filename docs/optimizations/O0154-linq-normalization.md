# O0154 — Normalize C# LINQ query operators into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#577](https://github.com/Hawkynt/Polypiler/issues/577). Once implemented it is switched off with `--disable-opt O0154`.

## What it does

C# expresses element pipelines as LINQ: CompressionWorkbench's LZH encoder computes a Kraft sum as
`lengths.Where(code => code > 0).Sum(code => kraftMax >> code)`. Translated call by call, a target
without LINQ would need an emulation of `IEnumerable`, deferred execution and delegates. Normalized to
`Filter`, `Map` and `Reduce`, the same pipeline becomes a loop in C, an iterator chain in Rust or a
comprehension in Python. LINQ's semantics are not those of a plain loop: queries are deferred, `Sum`
over `int` is overflow-checked, and several operators throw on empty input. PRD §4.5, §17.3 (LINQ →
sequence operations "after effect and laziness checks"), §19.2 (source-specific level). Calls are
matched as Source Semantic IR `SourceCall` nodes carrying the resolved library identity and signature —
never by method name text. The result consists of the sequence operations of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`,
`Filter.seq`, `Map.seq`, `Reduce[order: left | assoc]`, `Zip`, `Materialize`, `SeqLen`), written as that
issue defines them.

## Example

### Before

```
%q1 = SourceCall "System.Linq.Enumerable.Where<int>(IEnumerable<int>, Func<int,bool>)" %xs, λ(%x): ICmp.sgt.i32 %x, 6
%q2 = SourceCall "System.Linq.Enumerable.Select<int,int>(IEnumerable<int>, Func<int,int>)" %q1, λ(%x): Mul.i32 %x, 2
%s  = SourceCall "System.Linq.Enumerable.Sum(IEnumerable<int>)" %q2
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i32> %s0, λ(%x): ICmp.sgt.i32 %x, 6
%m = Map.seq<i32> %f, λ(%x): Mul.i32 %x, 2
%s = Reduce.i32[order: left] %m, λ(%a, %x): Add.i32[checked] %a, %x, 0
```

## When it applies

(1) every call resolves to the `System.Linq.Enumerable` member of the
mapping table, with the exact overload; (2) every lambda is pure ([#80](https://github.com/Hawkynt/Polypiler/issues/80)): no captured
variable is written, no effectful call; (3) *single immediate enumeration*: each deferred query value
has exactly one consumer, an eager operator or a `foreach`, and between the query's construction and
its enumeration there is no write that may modify the source and no effect that the lambdas could
observe — otherwise deferred execution would be visible; (4) the source is an array, `List<T>` not
modified during enumeration, or a range, so enumeration order is index order and `MoveNext` has no
effects; (5) exceptions are preserved as explicit IR: null source, empty `Min`/`Max`/seedless
`Aggregate`, checked overflow in integer `Sum` (the arithmetic of `Sum` is checked regardless of the
caller's `checked` context); (6) wrapping lambdas keep their own overflow context (`x * 2` in an
unchecked context wraps).

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Checked `Sum`: `new[] { int.MaxValue, 1 }.Sum()` throws `OverflowException`; a wrapping `Reduce`
  returns `−2147483648`.
- Deferred execution: `var q = xs.Where(x => x > 6); xs[0] = 100; q.Sum()` sees the modified element;
  a normalization at the construction site reads the old one.
- Lambda with an effect enumerated twice: `var q = xs.Select(x => { counter++; return x; });
  q.Count(); q.Count();` increments `counter` `2n` times.
- Empty input: `Array.Empty<int>().Min()` throws `InvalidOperationException`; a `Reduce` with an
  identity element returns `int.MaxValue`.
- `double` sum order: `Sum` adds left to right; the result is `Reduce[order: left]`.

## Switching it off

`--disable-opt O0154`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR before lowering of delegates and
enumerators; produces IR equal, under the correspondence that [#195](https://github.com/Hawkynt/Polypiler/issues/195), [#194](https://github.com/Hawkynt/Polypiler/issues/194) and
[#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their span forms and the sequence forms of [#201](https://github.com/Hawkynt/Polypiler/issues/201), to what
the index-loop rules produce for the same computation; enables fusion of the three operations into one
loop.
