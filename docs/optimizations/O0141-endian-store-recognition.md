# O0141 — Recognize little- and big-endian stores from shifted byte stores

<!-- status: implemented (prototype) -->
> **Status: implemented (prototype).** The rule `O0141EndianStore` in `Polypiler.Core.Recognize` runs in the pass pipeline of `translate` ([#1307](https://github.com/Hawkynt/Polypiler/issues/1307)); the full target state stays tracked in [#567](https://github.com/Hawkynt/Polypiler/issues/567). Switched off with `--disable-opt O0141`.

## What it does

Portable code writes a multi-byte integer one byte at a time. The rule replaces 2, 4 or 8 such stores
by `StoreLittle` or `StoreBig` of the IR.

## Example

`polypiler translate Snippet.cs --from csharp --to csharp --profile net10.0` on

```csharp
public static void F(byte[] b, int i, uint v) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }
```

### Before

With `--opt none`:

```csharp
public static void F(byte[] b, int i, uint v) {
  unchecked {
    b[i] = (byte)v;
    b[i + 1] = (byte)(v >> 8);
    b[i + 2] = (byte)(v >> 16);
    b[i + 3] = (byte)(v >> 24);
  }
}
```

### After

By default; `--explain-opt` prints `O0141 endian store from byte stores: 'F' statement 1`:

```csharp
public static void F(byte[] b, int i, uint v) {
  unchecked {
    if ((i >= 0) && (i <= (b.Length - 4))) {
      Store(b, i, (ulong)v, 4, true);
    } else {
      b[i] = (byte)v;
      b[i + 1] = (byte)(v >> 8);
      b[i + 2] = (byte)(v >> 16);
      b[i + 3] = (byte)(v >> 24);
    }
  }
}
```

## When it applies

2, 4 or 8 consecutive statements store `(byte)(v >> 8m)` into `b[i + k]`: one `byte[]` variable, one
base index plus consecutive constants, one value `v`, each stored value exactly byte `m` of `v` by its
weighted bits (`WeightEvaluator`); offset `k` receives byte `k` (little-endian) or `n - 1 - k`
(big-endian), the statements in either order. Array, base index and value have no effect and cannot
trap, and no index may trap on its own - JavaScript's sums in doubles are left alone, constant indices
are taken.

The byte stores write the elements inside the array before the first one outside traps; the combined
store traps before writing anything. Those partial writes are visible to whoever catches the trap, so
the replacement runs the combined store only where every element lies inside the array and keeps the
byte stores, with their trap and partial writes, everywhere else:
`if (i >= 0 && i <= b.Length - n) StoreLittle(b, i, v); else { the byte stores }`. The stores it keeps
are not combined again.

## How it is proven

The rule decides on the exact meaning of the IR, not on a likely one. Its fixtures `tests/fixtures/prototype/recognize/RecognizeEndianStore.cs` and `endianstore.js` hold every listed pattern and the near misses below, each function marked with the applications it expects; `RecognitionFixtureTests` checks that the rule fires exactly there, that switching it off with `--disable-opt` keeps it from firing while the other rules still do, and that the IR interpreter computes the same on the IR before and after - result, kind of trap and the arrays a trap leaves behind - for every value of every 8- and 16-bit parameter, and for every combination of the edges plus 10 000 random tuples (seed 2026) of the wider ones. The optimized C# fixture is also compared with the C# compiler's build of the original, and every optimized fixture is written by every profile of both backends. What the full target state adds - SSA graph matching, SMT bit-vector proof obligations, range and known-bits facts (issue [#276](https://github.com/Hawkynt/Polypiler/issues/276)) - is not part of the prototype.

## When it must not fire

- A gap, a wrong byte, or two values.
- A store between that changes the value (`v = v >> 8;`).
- Bytes that are not whole bytes of the value (`(byte)(v >> 4)`).
- A JavaScript index `i + k`.

## Switching it off

`--disable-opt O0141`; on at `--opt default`, off at `--opt none`.

## Interactions with other passes

Runs in the pass pipeline of `translate` (`Polypiler.Core.Optimize`), between the frontend and the
backend, with the other rules in the order of their numbers, until a pass changes nothing or eight
passes have run. Without range facts the guard stays; the full target state proves the range and drops it.
