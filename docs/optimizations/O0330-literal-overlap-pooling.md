# O0330 — Place a read-only literal whose code units occur inside another literal within it

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0330`.

## What it does

A program image for a 6502, a DOS `.COM` file or a tokenized BASIC listing pays for every byte of text, and
message tables repeat themselves: `"World!"` is already inside `"Hello, World!"`. A backend that lays out
its own data can address the shorter literal as a slice of the longer one, because every use of a literal
in PolyIR carries its own length. PB-Compiler does this for its string-literal pool (upstream O0011) and
saves 11 of 24 bytes on the three-literal sample of its document. PRD §4.8 (size and memory objectives),
§6.3 "narrow representations only when unobservable".

## Example

### Before

```
global @lit0: u8[13] = "Hello, World!" [readonly]
global @lit1: u8[6]  = "World!"        [readonly]
global @lit2: u8[5]  = "lo, W"         [readonly]
Memory.Copy.u8 %out, @lit1, 6
%e = PtrAdd.ptr<u8> @lit2, %i
%c = Load.u8[inbounds #1] %e          // proofs { #1 bounds: "0 <= %i", "%i < 5" }
```

### After

```
global @lit0: u8[13] = "Hello, World!" [readonly]
%p1 = PtrAdd.ptr<u8> @lit0, 7
Memory.Copy.u8 %out, %p1, 6
%p2 = PtrAdd.ptr<u8> @lit0, 3
%e  = PtrAdd.ptr<u8> %p2, %i
%c  = Load.u8[inbounds #1] %e
```

## When it applies

For a placed global `G` and a candidate `S`: (1) both are `[readonly]`, have static lifetime, the same element type and address space, are not volatile and carry no `align` attribute larger than the element size ([#121](https://github.com/Hawkynt/Polypiler/issues/121)); (2) the initializer of `S`, compared element by element **including any terminator it contains**, occurs in the initializer of `G`; (3) *length-addressed uses only*: every use of `@S` is the base of a `PtrAdd` chain ([#123](https://github.com/Hawkynt/Polypiler/issues/123)) that ends in a `Load`, in a reading operand of `Memory.Copy`, `Memory.Move`, `Memory.Equal`, `Memory.Compare`, `Memory.Mismatch` or `Memory.Find` ([#189](https://github.com/Hawkynt/Polypiler/issues/189)), or in an argument of a direct call to a function of the module whose parameter satisfies this same rule (followed to depth 8 through [#87](https://github.com/Hawkynt/Polypiler/issues/87): `CapturesParameter` false); (4) every such access is proven inside the extent of `S`: `ValueFacts.IsInBounds` is `True` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)), or the access carries an `inbounds #id` proof against `S`, or offset and count are literals inside the extent — an access under `[bounds]` whose index is not proven would trap at the end of `S` before and read the neighbouring bytes of `G` after; (5) the identity of `S` is unobservable: `@S` reaches no `PtrCmp`, `PtrDiff`, `PtrToInt`, no store of the address, no return, no unknown or external call ([#82](https://github.com/Hawkynt/Polypiler/issues/82): `EscapeOf` must not be `GlobalEscape`); (6) `ICostModel.IsAvailable("data.overlap")` is true for the target profile ([#155](https://github.com/Hawkynt/Polypiler/issues/155)).

## How it is proven

`structural+alias-facts+range-facts`: under (3)–(5) the only observations of `S` are element reads at offsets inside its extent, and (2) makes each of them return the same element from `G`. No solver, no timeout path.

## When it must not fire

- `Load.u8[bounds] (PtrAdd @lit2, %i)` with `i = 5`: before, the access is outside the 5 elements of `@lit2` and traps `IndexOutOfRange`; after, offset 3 + 5 = 8 is inside `@lit0` and the load returns `'o'` (0x6F).
- A C string: `"lo, W\0"` is six elements and does not occur in `"Hello, World!\0"`; merging on the five visible characters makes `puts` print `lo, World!`. `"World!\0"` (7 elements) does occur, at offset 7, and may be merged.
- `PtrCmp[eq] @lit1, (PtrAdd @lit0, 7)`: false before (different allocations never compare equal, [#123](https://github.com/Hawkynt/Polypiler/issues/123)), true after.
- A global without `[readonly]`: a store into the shorter one would change the longer one.
- `u16` code units inside a `u8` literal at an odd byte offset: different element types are never merged here.

## Targets and cost gate

Pays off only where Polypiler itself lays out the data image: machine-code and assembly backends, 8-bit and DOS targets, tokenized BASIC. A target profile declares that by listing the storage form `data.overlap` in its `cost.yaml`; profiles of source-language targets with a compiler or runtime behind them do not list it, so their literals stay whole and readable. Where the form is available the rewrite never costs anything (`Cost.Memory` falls by the candidate's size, nothing rises), so [#155](https://github.com/Hawkynt/Polypiler/issues/155) is asked for availability only. The form name `data.overlap` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0330`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs as the last module pass of phase `specialize`, after constant folding has fixed all literal contents and before `OPT-MEM-CONSTMERGE`; enabled by `OPT-MEM-DEADGLOBAL` (fewer literals to place); enables smaller images for [#259](https://github.com/Hawkynt/Polypiler/issues/259) size ceilings.
