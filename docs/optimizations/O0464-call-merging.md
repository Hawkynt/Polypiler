# O0464 — Merge identical calls of pure and read-only functions

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** `O0464CallMerging` runs among the passes over the legalized module ([#1361](https://github.com/Hawkynt/Polypiler/issues/1361)), after [O0002](O0002-partial-evaluation.md) and before [O0005](O0005-dead-code-elimination.md), and looks at every function. Merging across statements is [global value numbering](O0004-global-value-numbering.md)'s, hoisting out of a loop [loop-invariant code motion](O0012-loop-invariant-code-motion.md)'s. Switched off with `--disable-opt O0464`; off at `--opt none`.

## What it does

A small helper lambda called twice in one condition, `a[i] < lim() && a[i] > -lim()`, costs two calls
where a hand-written loop computes the limit once. The pass asks the effect summary of the callee
(`IrEffects`): what it reads and writes outside its own variables, whether it allocates, traps or may
run forever. Within one expression of a statement, a call of a function that writes and allocates
nothing - pure, or read-only - whose arguments have no effect and cannot trap is evaluated once:

1. The first call keeps its place and stores its value in a new variable, declared before the
   statement (`(t = lim(k))`); every later identical call reads the variable.
2. A later call is merged only where the first always runs before it: it is not in a branch of a
   choice, nor on the right of `&&`, `||` or `??`, that the first call is outside of. A later call in
   such a branch after a first call outside it merges; one outside after a first one inside does not.
3. Nothing evaluated between the two calls may write what the function or its arguments read - a
   global, a field, the elements of arrays of its element type, a variable. A write of anything else
   does not stop the merge; a call of a function that writes, or through a function value, writes
   everything.

Nothing moves: the first call is evaluated where it was, so a trap or an endless run happens at the
same point, and the later call would have done the same with the same inputs. A loop condition is
evaluated each pass, and so is the first call in it.

## Example

`polypiler translate Ex.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static int H(int[] a, int k) {
  Func<int> lim = () => k * 2;
  int s = 0;
  for (int i = 0; i < a.Length; i++)
    if (a[i] < lim() && a[i] > -lim()) s++;
  return s;
}
```

### Before

With `--disable-opt O0464` - [O0463](O0463-lambda-specialisation.md) lifted the lambda, but the call
stays where `a[i]`, which may trap, is evaluated before it, so inlining leaves it:

```csharp
while (i < a.Length) {
  if ((a[i] < H_lim(k)) && (a[i] > -H_lim(k))) {
    s = s + 1;
  }
  i = i + 1;
}
```

### After

By default; `--explain-opt` prints `O0464 call merging: 'H' statement 2.2.1` and classifies the
lifted function, `O0463 lambda specialisation: 'H_lim' of 'H' is static, lifted, pure`:

```csharp
while (i < a.Length) {
  int _H_lim = 0;
  if ((a[i] < (_H_lim = H_lim(k))) && (a[i] > -_H_lim)) {
    s = s + 1;
  }
  i = i + 1;
}
```

`H([1, 5, -3], 2)` returns 2 either way.

## When it applies

- The calls are direct calls of a function of the module whose summary writes nothing and allocates
  nothing, with the same arguments, node for node, each of which may be evaluated anywhere.
- They stand in one expression of a declaration, an assignment, a store, an expression statement, a
  choice, a loop condition, a switch, a return or a throw. The three expressions of a store into an
  array element are merged each on its own.
- The first call always runs before the later one, and nothing between writes what they read.

## When it must not fire

- `Get(x) + SetG() + Get(x)` where `SetG` writes the global `Get` reads: the second call sees another
  value. With `SetH()` writing another global the calls merge.
- `Get(y) + y++ + Get(y)`: the argument changed.
- `(x > 0 ? Get(x) : 0) + Get(x)`: the first call does not always run.
- `inc(); inc();` with `Func<int> inc = () => ++n`: a call through a function value is the worst case,
  and `inc` writes what it captures - both calls stay.
- `Mk(1).Length + Mk(1).Length` with `Mk` making a new array: two arrays are not one.
- `Get(10 / x) + Get(10 / x)`: an argument that may trap.

## Switching it off

`--disable-opt O0464`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

It runs after evaluation ([O0002](O0002-partial-evaluation.md)), which leaves no pure call with
constant arguments to merge, and before dead-code removal ([O0005](O0005-dead-code-elimination.md)),
which keeps the variable a binding assigns. Constant propagation ([O0003](O0003-sccp.md)) replaces no
read of a variable the same expression binds. The summaries it asks are those
[O0005](O0005-dead-code-elimination.md) and [O0002](O0002-partial-evaluation.md) ask, and those
loop-invariant code motion ([O0012](O0012-loop-invariant-code-motion.md), [#508](https://github.com/Hawkynt/Polypiler/issues/508))
will ask to hoist a call out of a loop.

## How it is proven

`EffectPassTests` runs the rows of the test design of #1361 on C# snippets in the interpreter before
and after, each compiled by Roslyn as well: a write of the read set between two calls, an unrelated
write and none; a written argument; the branches of choices, `&&`, `||` and `??`; a writing, an
allocating and a trapping case; calls merged inside the arguments of merged calls; every kind of
statement; and the generated C# compiled for .NET Framework 2.0, whose definite assignment the merged
variable must satisfy. The acceptance scenario "Identical read-only calls are merged" of
`tests/features/optimize/opt-effect-summaries.feature` runs the output in every profile of both
backends.
