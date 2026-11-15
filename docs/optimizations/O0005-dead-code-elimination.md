# O0005 — Remove instructions whose results and effects are unobservable

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0005DeadCode` runs among the passes over the legalized module ([#1353](https://github.com/Hawkynt/Polypiler/issues/1353)), over the library functions and every function a helper was inlined into, and removes the library functions nothing calls any more. Removal on SSA with effect summaries is its target state. Switched off with `--disable-opt O0005`.

## What it does

- A choice on a constant keeps the branch it takes - in a block of its own where the branch declares a
  variable, whose scope must not grow - and a loop whose condition is false from the start goes.
- Every statement after a return, a throw, a break or a continue in its block goes.
- A variable nothing reads goes with its declaration and its assignments, where their values may go
  unevaluated; an empty block, and an empty choice on such a value, goes.
- A store into a variable its block declares goes where no later statement of the block reads it - the
  last value of a counter an unrolled loop leaves - and so does the declaration no later statement
  names, even where a variable of the same name in another block is read.
- A nested block that declares nothing is spliced into the block around it.
- A call whose value nothing uses goes where the effect summary of its function (`IrEffects`,
  [#1361](https://github.com/Hawkynt/Polypiler/issues/1361)) says it writes nothing, cannot trap and
  ends - `Sq(x);` with `static int Sq(int x) => x * x` -, and so does such a call in a value that may
  go unevaluated, `int t = Sq(x);` with `t` unread. A variable an expression binds keeps its
  declaration, read or not.
- A function legalization copied in from the libraries that nothing calls any more - everything it did
  was inlined - goes, and with it its entry in `IrModule.Implementations`; its licence notice stays.

## Example

A 64-bit big-endian load for `net2.0`, after inlining, propagation and unrolling.

### Before

With `--disable-opt O0005`:

```csharp
int index = 0;
bool little = false;
int index2 = 0;
int size = 8;
if (0 > (a.Length - 8)) {
  throw new global::System.IndexOutOfRangeException("index out of range");
}
ulong value = 0;
{
  {
    value = (ulong)a[0];
  }
  ...
  int offset = 8;
}
return value;
```

### After

```csharp
if (0 > (a.Length - 8)) {
  throw new global::System.IndexOutOfRangeException("index out of range");
}
ulong value = 0;
value = (ulong)a[0];
value = (value << 8) | (ulong)a[1];
...
return value;
```

## When it must not fire

- `int t = a / b;` with `t` unread: the division may trap, so the declaration stays.
- An assignment of a call to an unread variable, or a call standing as a statement, where the
  function writes something, may trap (`10 / x`) or may not end (recursion, a loop without a proven
  bound): the call has an effect.
- A parameter, or a variable a lambda names, is never removed.
- A store into a variable declared outside its block - the loop around it may read it in the next pass -
  or one a later statement of the block reads, and a store before the declaration of its name in the block.
- A function of the program nothing calls: the prototype removes only what the libraries put in.

## Switching it off

`--disable-opt O0005`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

After [O0001](O0001-constant-folding.md), whose folded conditions it decides; the target pass
[O0501](O0501-target-unused-declaration-removal.md) removes what the target passes leave unused.

## How it is proven

`IrPassTests` checks the branch of a constant choice with and without a declaration, a false loop, the
statements after a return, an unread variable with a value that may trap and one without, a store
nothing reads before its block ends and the stores that may be read, and the library function nothing
calls; `EffectPassTests` the unused calls of a function that reads, that is pure, that traps, that
recurses and that writes, and the variable a binding keeps; `HelperEquivalenceTests` checks every
helper after all passes.
