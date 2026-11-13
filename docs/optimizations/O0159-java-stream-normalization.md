# O0159 — Normalize Java stream pipelines into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#783](https://github.com/Hawkynt/Polypiler/issues/783). Once implemented it is switched off with `--disable-opt O0159`.

## What it does

Java expresses element pipelines as streams: `Arrays.stream(xs).filter(x -> x > 6).map(x -> x *
2).sum()`. In bytecode and in source that is a chain of interface calls, lambdas as `invokedynamic` call
sites and a spliterator; translated literally it drags a stream runtime into every target. Normalized,
it is `Filter`/`Map`/`Reduce`. Java's contract has its own edges: streams are lazy and single-use,
`IntStream.sum()` wraps silently (where C#'s `Sum` throws), terminal operations on empty streams return
`Optional`s, and a `parallel()` stream gives the runtime licence to reorder that sequential code does
not have. PRD §4.5, §8 P4 (Java stream normalization), §17.3 (Java streams → sequence operations after
effect and laziness checks). Calls are matched as Source Semantic IR `SourceCall` nodes carrying the
resolved library identity and signature — never by method name text. The result consists of the sequence
operations of [#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`, `Filter.seq`, `Map.seq`, `Reduce[order: left | assoc]`, `Zip`,
`Materialize`, `SeqLen`), written as that issue defines them.

## Example

### Before

```
%st = SourceCall "java.util.Arrays.stream(int[])" %xs
%q1 = SourceCall "java.util.stream.IntStream.filter(IntPredicate)" %st, λ(%x): ICmp.sgt.i32 %x, 6
%q2 = SourceCall "java.util.stream.IntStream.map(IntUnaryOperator)" %q1, λ(%x): Mul.i32 %x, 2
%s  = SourceCall "java.util.stream.IntStream.sum()" %q2
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i32> %s0, λ(%x): ICmp.sgt.i32 %x, 6
%m = Map.seq<i32> %f, λ(%x): Mul.i32 %x, 2
%s = Reduce.i32[order: assoc] %m, λ(%a, %x): Add.i32 %a, %x, 0
```

## When it applies

(1) every call resolves to the `java.util.stream`/`java.util.Arrays`
member of the mapping table; (2) the stream is sequential — no `parallel()` in the chain and the
source is not a parallel stream; (3) each stream value has exactly one terminal operation and is not
stored for later use (a second use throws `IllegalStateException` in Java); the terminal operation
runs where the pipeline is built, or nothing between construction and terminal operation can modify
the source array or observe an effect (non-interference); (4) lambdas are stateless and pure
([#80](https://github.com/Hawkynt/Polypiler/issues/80)); (5) arithmetic is Java's: `int`/`long` wrap, so `sum` is `Reduce[order: assoc]` of
wrapping `Add`; `double` sums are sequential `FAdd` — the API permits a different summation order, but
the normalization keeps encounter order so results match the reference runtime bit for bit; (6)
`min`/`max` on an empty stream yield an empty `Optional`, and `getAsInt` on it throws
`NoSuchElementException`; (7) the source array is non-null or the `NullPointerException` check is
kept.

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Wrapping `sum`: `IntStream.of(Integer.MAX_VALUE, 1).sum()` is `−2147483648`; emitting a checked
  reduction (as the LINQ normalization must) would throw.
- Parallel non-associative `reduce`: `Arrays.stream(xs).parallel().reduce(0, (a, b) -> a - b)` has no
  specified result; over `1 2 3` sequential code gives −6, a split evaluation may give another value.
  No single normalization is correct.
- Reuse: `IntStream s = …; s.sum(); s.count();` throws `IllegalStateException` at the second call.
- Stateful lambda: `map(x -> { counter[0]++; return x; })` — call count and order are observable.
- Empty `max`: `IntStream.empty().max().getAsInt()` throws `NoSuchElementException`; a `Reduce` with
  an identity returns `Integer.MIN_VALUE`.

## Switching it off

`--disable-opt O0159`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR (from Java source or from lifted
class files, where lambdas arrive as resolved `invokedynamic` targets); produces IR equal, under the
correspondence that [#195](https://github.com/Hawkynt/Polypiler/issues/195), [#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their
span forms and the sequence forms of [#201](https://github.com/Hawkynt/Polypiler/issues/201), to what the index-loop rules produce for the
same computation.
