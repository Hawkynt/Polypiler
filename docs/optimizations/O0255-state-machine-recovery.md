# O0255 — Recover generators from hand-compiled state machines

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#951](https://github.com/Hawkynt/Polypiler/issues/951). Once implemented it is switched off with `--disable-opt O0255`.

## What it does

A C or Rust programmer without `yield` writes a struct with a `state` field and a `next` function that
switches on it. PRD §30.3 (COR-001): "A frontend recognizing a compiler-generated state machine may recover
a `Coroutine`/`Generator` IR node only after proving state/transition, value, lifetime and exception
equivalence." §30.5 names "a Rust/Perl generated state machine" as fixture. Recovered, the same code becomes
a C# iterator, a Python generator or a Kotlin sequence instead of a transliterated switch.

## Example

### Before

```
type Counter = record { state: u8, i: i32, n: i32 }
func @next(%c: ptr<Counter>) -> step<i32, void> {
entry: Switch.u8 (Load.u8 (FieldAddr %c, state)), dead, [0: start], [1: again]
start: Store.i32 (FieldAddr %c, i), 0
       Br test
again: Store.i32 (FieldAddr %c, i), (Add.i32[wrap] (Load.i32 (FieldAddr %c, i)), 1)
       Br test
test:  %i = Load.i32 (FieldAddr %c, i)
       Br (ICmp.slt.i32 %i, (Load.i32 (FieldAddr %c, n))), emit, fin
emit:  Store.u8 (FieldAddr %c, state), 1
       Ret (Step.Yielded %i)
fin:   Store.u8 (FieldAddr %c, state), 2
       Ret (Step.Completed)
dead:  Trap.InvalidOperation }
```

### After

```
func @counter(%n: i32) -> void resumable=generator yields i32 {
entry: Br test
test:  %i = Phi.i32 [entry: 0], [again: %j]
       Br (ICmp.slt.i32 %i, %n), emit, fin
emit:  Suspend.Yield.void %i
       Br again
again: %j = Add.i32[wrap] %i, 1
       Br test
fin:   Ret.void }
```

## When it applies

the four obligations (a)–(d) under *In scope*, each discharged as a fact
query; plus: (e) N has no other parameter that changes between calls (a per-call input makes it a
coroutine with sent values, which this rule does not recover); (f) no instruction with an effect lies
between the state store and the return; (g) the entry `Switch` is the first effectful instruction of N.

## How it is proven

`validated-rule+differential` with the listed fact obligations (reported as
`structural+interpreter` plus the fact classes): each candidate is re-lowered with O0252 and the result is
compared with the original fragment on [#119](https://github.com/Hawkynt/Polypiler/issues/119) over the rule's input generator; any difference, any
`Unknown` fact or a budget expiry leaves the IR unchanged.

## When it must not fire

- A `reset()` function that stores 0 to `state`: the machine can be restarted, a generator cannot;
  obligation (a) fails.
- A return path that leaves `state` unchanged (the caller is expected to retry): there is no suspension
  point to recover.
- After completion `next` keeps returning `None` and the caller polls twice: the recovered generator would
  trap on the second call; obligation (c) fails unless the second call is proven absent.
- The struct is `#[repr(C)]` and passed to a C callback that reads `i`: the field is observable;
  obligation (d) fails.

## Switching it off

`--disable-opt O0255`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt
none`; never required — with it disabled the low-level form is translated as it is.

## Interactions with other passes

runs in phase `recover` after memory facts are available and before recognition of
sequence pipelines; enables native iterator emission and iteration fusion.
