# O0245 — Lower closures to environment records and lifted functions

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#753](https://github.com/Hawkynt/Polypiler/issues/753). Once implemented it is switched off with `--disable-opt O0245`.

## What it does

[#120](https://github.com/Hawkynt/Polypiler/issues/120) covers kernels that are consumed where they are written and leaves "closures as first-class
values that escape, delegates, function pointers" to this area. A lambda that outlives its function needs
the variables it captured to outlive it too, and two closures over one variable must see each other's
writes. PRD §30.1 row "Delegates, closures, lambdas": capture by value or reference, lifetime and invocation
are the semantics; lifted functions with a context UDT and function pointers the fallback. §32 lists
"closure capture and lifetime".

## Example

### Before

```
func @MakeCounter() -> fn<() -> i32> {
entry: %n = Alloc.ptr<i32>[stack] 1                      ; int n = 0
       Store.i32[bounds] %n, 0
       %c = Closure.Make.fn<() -> i32> @MakeCounter$lambda [capture=(%n by ref)]
       Ret %c }
```

### After

```
type Env_MakeCounter_1 = record { n: i32 }
type Closure_i32 = record { code: fn<(ptr<void>) -> i32>, env: ptr<void> }
func @MakeCounter() -> Closure_i32 {
entry: %e = Alloc.ptr<Env_MakeCounter_1>[heap] 1          ; lifetime static: the closure is returned
       Store.i32[bounds] (FieldAddr.ptr<i32> %e, n), 0
       %c = RecordNew.Closure_i32 (FuncRef @MakeCounter$lambda), %e
       Ret %c }
func @MakeCounter$lambda(%env: ptr<Env_MakeCounter_1>) -> i32 {
entry: %p = FieldAddr.ptr<i32> %env, n
       %v = Add.i32[wrap] (Load.i32[bounds] %p), 1
       Store.i32[bounds] %p, %v
       Ret.i32 %v }
```

## When it applies

(1) Every access to a by-reference captured variable, in the enclosing
function and in every closure over it, goes through the same environment cell. (2) The environment is
created when the declaring scope is entered, once per entry. (3) The environment's lifetime covers the
lifetime of every closure that refers to it: `stack` only with the fact that no closure over it escapes
the function. (4) By-value captures are copied at `Closure.Make`, after which the original and the copy
are independent. (5) `Closure.Invoke` passes the environment unchanged and the arguments in order.

## How it is proven

`structural+alias-facts`: validated by interpreting `Closure.*` and the lowered form on
every fixture, with effect traces compared; lifetimes rest on escape facts and fall back to `static`.

## When it must not fire

- Copying a by-reference captured variable into the environment and leaving the original on the stack:
  `int total = 0; Action<int> add = x => total += x; add(3); add(4);` must leave `total` at 7 in the
  enclosing function; the copy leaves it at 0.
- One environment for a variable declared inside a loop body: closures created in three iterations of
  `foreach (var i in new[] {0, 1, 2})` must return 0, 1, 2; a shared cell returns 2, 2, 2.
- A fresh environment per iteration for a `for (int i = 0; i < 3; i++)` variable: C# shares it; the
  closures return 3, 3, 3.
- A stack environment for a returned closure: the second call reads a dead frame.

## Switching it off

`--disable-opt O0245`; `Kind = Required`: the rule runs at every `--opt` level, including
`none`, on targets whose capabilities lack `Feature(closure)`. Disabled, the legalizer has no alternative
for these operations and reports `PLY0702`.

## Interactions with other passes

runs after kernels consumed in place were inlined or lifted by [#120](https://github.com/Hawkynt/Polypiler/issues/120)'s lifting;
before [#470](https://github.com/Hawkynt/Polypiler/issues/470) fixes reference representations; non-escaping closures invoked once are candidates
for inlining instead.
