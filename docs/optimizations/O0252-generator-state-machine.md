# O0252 — Lower generators to resumable state records with an entry function

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#760](https://github.com/Hawkynt/Polypiler/issues/760). Once implemented it is switched off with `--disable-opt O0252`.

## What it does

A generator leaves its function in the middle and comes back later with its locals intact. PRD §30.3
(COR-001): "an iterator need not allocate a scheduler … The inverse lowering into manual state machines must
preserve local captures, re-entry, ordering and cleanup across every suspension point", and "a PowerBASIC
output may use generated state records and entry functions". §30.1 gives the fallback: a resumable state
machine with a minimal iterator protocol.

## Example

### Before

```
func @count(%n: i32) -> i32 resumable=generator yields i32 {
entry: Br head
head:  %i = Phi.i32 [entry: 0], [next: %j]
       %c = ICmp.slt.i32 %i, %n
       Br %c, body, done
body:  Suspend.Yield.void %i                 ; suspension point 1
       Br next
next:  %j = Add.i32[wrap] %i, 1
       Br head
done:  Ret.i32 %i }
```

### After

```
type Gen_count = record { state: u16, n: i32, i: i32 }
func @count$next(%g: ptr<Gen_count>) -> step<i32, i32> {
entry: %s = Load.u16 (FieldAddr.ptr<u16> %g, state)
       Switch.u16 %s, bad, [0: start], [1: resume1]
start: Store.i32 (FieldAddr.ptr<i32> %g, i), 0
       Br head
head:  %i = Load.i32 (FieldAddr.ptr<i32> %g, i)
       %c = ICmp.slt.i32 %i, (Load.i32 (FieldAddr.ptr<i32> %g, n))
       Br %c, body, done
body:  Store.u16 (FieldAddr.ptr<u16> %g, state), 1
       Ret (Step.Yielded %i)
resume1: Store.i32 (FieldAddr.ptr<i32> %g, i), (Add.i32[wrap] (Load.i32 (FieldAddr.ptr<i32> %g, i)), 1)
       Br head
done:  Store.u16 (FieldAddr.ptr<u16> %g, state), 65535
       Ret (Step.Completed %i)
bad:   Trap.InvalidOperation }
```

## When it applies

(1) Every SSA value live across a suspension point is stored in the record
before the return and reloaded after the resume; values not live across any suspension stay SSA. (2) The
`Switch` has one case per suspension point plus the start. (3) The state is set to the running marker
65534 on entry and to the next state before returning, so re-entry is detected. (4) `$dispose` runs, for
state k, exactly the cleanups of the regions that enclose point k. (5) Effects between two suspension
points keep their order.

## How it is proven

`structural+interpreter`: validated by interpreting the resumable function and the
lowered form on every fixture and comparing the event traces (the lowered form's returns and re-entries
are mapped to `Suspend`/`Resume`).

## When it must not fire

- Keeping the loop counter in a local of `$next`: it restarts at its initial value on every call and the
  generator yields 0 forever.
- Returning to the start of the block containing the yield instead of the instruction after it: the
  yielded value's computation runs twice.
- A `$dispose` that only marks the generator finished: `foreach (var x in Guarded()) break;` must run the
  `finally`.
- Running the body at creation: a C# iterator runs nothing until the first `MoveNext`.

## Switching it off

`--disable-opt O0252`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(generator)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

runs after [#759](https://github.com/Hawkynt/Polypiler/issues/759) has made the non-exceptional exits explicit inside the body
and before [#470](https://github.com/Hawkynt/Polypiler/issues/470)-style storage decisions; the state `Switch` is ordinary control flow for later
passes.
