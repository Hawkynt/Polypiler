# O0254 — Lower async functions to state machines over an explicit scheduler contract

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#752](https://github.com/Hawkynt/Polypiler/issues/752). Once implemented it is switched off with `--disable-opt O0254`.

## What it does

`await` suspends a function until another computation finishes, and *something* must later continue it. PRD
§30.3: "Coroutines are not automatically threads, futures or `async` tasks … a real-mode DOS target has no
implicit thread/task environment. Concurrency/scheduling must be explicit in target profiles." §30.1 gives
the fallback — a resumable state machine plus a minimal scheduler — and §30.2 (OBJ-003) requires targets
that cannot provide it to reject, or to offer a separately selected emulation profile.

## Example

### Before

```
func @Twice(%a: i32) -> i32 resumable=async {
entry: %t = Call.coroutine<void, void, i32> @AddAsync(%a, %a)
       %v = Suspend.Await.i32 %t                       ; suspension point 1
       Ret.i32 %v }
```

### After

```
type Async_Twice = record { state: u16, a: i32, t: ptr<Task_i32>, self: Task_i32 }
func @Twice$step(%g: ptr<Async_Twice>) -> void {
entry: Switch.u16 (Load.u16 (FieldAddr.ptr<u16> %g, state)), bad, [0: start], [1: resume1]
start: %t = Call.ptr<Task_i32> @AddAsync$start(…)
       Store (FieldAddr %g, t), %t
       %done = ICmp.ne.u8 (Load.u8 (FieldAddr %t, status)), 0
       Br %done, resume1, park                         ; awaitCompleted=continue
park:  Store.u16 (FieldAddr %g, state), 1
       Call.void @ply_task_on_complete(%t, (FuncRef @Twice$step), %g)
       Ret.void
resume1: ; propagate failure if status = 2, else:
       Call.void @ply_task_complete((FieldAddr %g, self), (Load.i32 (FieldAddr (Load (FieldAddr %g, t)), result)))
       Ret.void
bad:   Trap.InvalidOperation }
```

## When it applies

(1) All preconditions of O0253. (2) A continuation runs at most once and only
after the awaited task completed. (3) Continuations of one task run in registration order; the ready queue
is FIFO. (4) Under `awaitCompleted=yield` the code after an `await` never runs before the caller regained
control, even for a completed task. (5) A failed task re-raises its failure at the `await` inside the
awaiting function's handler regions. (6) The scheduler service is present for the selected profile;
otherwise the rule refuses.

## How it is proven

`structural+interpreter`: validated by interpreting the async function (the interpreter's
in-place await under `continue`, and a reference FIFO job queue in the test project under `yield`) and the
lowered form with the emulation scheduler, comparing output order.

## When it must not fire

- Continuing synchronously after `await` of a resolved promise in JavaScript: `async function f() {
  log(1); await null; log(3); } f(); log(2);` prints 1 2 3; synchronous continuation prints 1 3 2.
- Always parking in C#: `await Task.CompletedTask` continues synchronously; the same program prints 1 3 2
  in C#.
- Running continuations in LIFO order: two awaiters of one task would swap their output.
- Mapping each async function to a thread: ordering becomes nondeterministic and the target may have no
  threads.

## Switching it off

`--disable-opt O0254`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(async-await)`. Disabled, the legalizer has no
alternative for these operations and reports `PLY0702`.

## Interactions with other passes

extends [#754](https://github.com/Hawkynt/Polypiler/issues/754); runs after [#479](https://github.com/Hawkynt/Polypiler/issues/479)'s async scopes are
checked; requests the service `scheduler` and, for escaping tasks, `heap`.
