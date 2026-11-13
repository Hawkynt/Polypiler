# O0158 — Normalize Rust iterator adaptor chains into the shared iteration IR

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#787](https://github.com/Hawkynt/Polypiler/issues/787). Once implemented it is switched off with `--disable-opt O0158`.

## What it does

Idiomatic Rust has no index loops: `xs.iter().filter(|&&x| x > 6).map(|&x| x * 2).sum::<i32>()`. After
monomorphization that chain is a nest of generic `next` implementations; translated literally, a C or
PowerBASIC target would receive an iterator state machine. Normalized, it is the same
`Filter`/`Map`/`Reduce` every other frontend produces. Rust's contract differs in three places: adaptors
are lazy and do nothing unless consumed, integer `sum` panics on overflow when overflow checks are on,
and `into_iter` moves and drops elements. PRD §4.5 (iterators), §8 P4 (Rust iterator normalization),
§17.3. Calls are matched as Source Semantic IR `SourceCall` nodes carrying the resolved library identity
and signature — never by method name text. The result consists of the sequence operations of
[#201](https://github.com/Hawkynt/Polypiler/issues/201) (`SeqOf`, `Filter.seq`, `Map.seq`, `Reduce[order: left | assoc]`, `Zip`, `Materialize`,
`SeqLen`), written as that issue defines them.

## Example

### Before

```
%it = SourceCall "core::slice::<impl [i32]>::iter" %xs
%q1 = SourceCall "core::iter::Iterator::filter" %it, λ(%x): ICmp.sgt.i32 %x, 6
%q2 = SourceCall "core::iter::Iterator::map" %q1, λ(%x): Mul.i32[profile] %x, 2
%s  = SourceCall "core::iter::Iterator::sum::<i32>" %q2
```

### After

```
%s0 = SeqOf %xs
%f = Filter.seq<i32> %s0, λ(%x): ICmp.sgt.i32 %x, 6
%m = Map.seq<i32> %f, λ(%x): Mul.i32[profile] %x, 2
%s = Reduce.i32[order: left] %m, λ(%a, %x): Add.i32[profile] %a, %x, 0
```

## When it applies

(1) every call resolves to the `core`/`alloc` item of the mapping table
for a slice, array, `Vec` or range source; (2) element types are `Copy` scalars without drop glue; (3)
the chain ends in a consumer — an unconsumed adaptor chain performs no work and is left alone; (4)
closures are `Fn`, or `FnMut` only as the `fold` accumulator; captured data is not written during the
chain; (5) arithmetic keeps the crate profile's overflow contract, written `[profile]` above: trapping
(panic) with overflow checks on, wrapping with them off, and explicitly `wrapping_*`/`checked_*` calls
keep their own meaning; a `Reduce` with a trapping contract is `[order: left]`, and a `Map` kernel
that can trap keeps the sequence ordered and evaluated left to right; (6) `zip` iterates `min(len_a,
len_b)` elements and evaluates the first source's element before the second's; (7) the borrow rules
already guarantee that the source is not modified during the chain, which is why no extra alias proof
is needed for safe code — chains inside `unsafe` blocks with raw-pointer sources are refused.

## How it is proven

`validated-rule+differential`: each mapping is a registry entry in the sense of PRD
§17.3 (library identity, version range, signature, semantics, throws, effects) with checked
preconditions; purity and single-enumeration are `EffectFact` obligations; equivalence is established
by differential execution against the real runtime on the fixtures and on generated inputs. No SMT
claim is made. A failed, unknown or timed-out obligation (budget 5 s each) leaves the IR unchanged and
reports `not applied: proof timeout` or the failing precondition.

## When it must not fire

- Profile-dependent overflow: `[i32::MAX, 1].iter().sum::<i32>()` panics ("attempt to add with
  overflow") with overflow checks on and returns `i32::MIN` with them off. Normalizing to either fixed
  behaviour is wrong for the other profile.
- Laziness: `xs.iter().map(|x| side_effect(x));` without a consumer calls the closure zero times.
- `zip` length: zipping 3 and 5 elements yields 3; indexing the longer source by the shorter count is
  right, the reverse reads out of bounds.
- Stateful `filter` closure: `filter(|_| { n += 1; n % 2 == 0 })` depends on call order and count; a
  `Filter` predicate must be pure.
- `into_iter()` on a `Vec<String>` moves and drops each element; treating it as a read-only view
  changes when destructors run.

## Switching it off

`--disable-opt O0158`; active at `--opt balanced`, `speed`, `size` and `memory`, off at
`--opt none`.

## Interactions with other passes

runs in phase `recognize` on Source Semantic IR before the frontend lowers iterator
generics to `next` calls; produces IR equal, under the correspondence that [#195](https://github.com/Hawkynt/Polypiler/issues/195),
[#194](https://github.com/Hawkynt/Polypiler/issues/194) and [#197](https://github.com/Hawkynt/Polypiler/issues/197) state between their span forms and the sequence forms of
[#201](https://github.com/Hawkynt/Polypiler/issues/201), to what the index-loop rules produce for the same computation (modulo the overflow
contract).
