# O0346 — Store pointers that all target one bounded region as narrow element indices

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0346`.

## What it does

A linked structure over a pool of a thousand nodes stores a thousand `next` pointers of four or eight
bytes each, although ten bits would name any node. When every pointer stored in a place is null or
points at an element of one fixed region, the place can hold an element number and the pointer is
rebuilt on load. On x86-16 that halves a far pointer; on a 64-bit target it quarters it. PB-Compiler
implements it for private pointer arrays (upstream O0324, in `DataLayoutTransforms`). PRD §4.8 (storage
form), §6.3 "narrow representations only when unobservable".

## Example

### Before

```
global @pool: Node[1000]
global @next: ptr<Node>[1000]            // every store: null, or ElemAddr @pool, k with 0 <= k < 1000
Store.ptr<Node> (ElemAddr @next, %i), %q   // %q = ElemAddr.ptr<Node>[inbounds #1] @pool, %k
%r = Load.ptr<Node> (ElemAddr @next, %j)
```

### After

```
global @pool: Node[1000]
global @next: u16[1000]
%k16 = Truncate.u16 %k
%enc = Add.u16[wrap] %k16, 1
Store.u16 (ElemAddr @next, %i), %enc
%c   = Load.u16 (ElemAddr @next, %j)
%nil = ICmp.eq.u16 %c, 0
%dec = Sub.u16[wrap] %c, 1
%ix  = Select.u16 %nil, 0, %dec
%e   = ElemAddr.ptr<Node>[inbounds #2] @pool, %ix      // proofs { #2 bounds: "0 <= %ix", "%ix < 1000" }
%r   = Select.ptr<Node> %nil, null, %e
```

## When it applies

For a place family `P` (all elements of one pointer array, or one pointer field of every instance of a record type) and a target region `R`: (1) *whole-module store proof*: every value stored into `P` — by `Store`, initializer, `Memory.Fill`, `InsertField`, `RecordNew` — has a [#93](https://github.com/Hawkynt/Polypiler/issues/93) fact whose sites are exactly `{R}` or empty (null), whose offset interval lies in `[0, (count − 1) × size]`, and which was formed by element addressing only (`ElemAddr`, `PtrAdd` in element units; a `PtrAddBytes` in the chain blocks), so the offset is a multiple of the element size; a value loaded from `P` itself qualifies; one store with `Unknown` provenance blocks the family; (2) `R` is a global or an `Alloc` with a literal count `count ≥ 1` that is never resized ([#121](https://github.com/Hawkynt/Polypiler/issues/121), [#111](https://github.com/Hawkynt/Polypiler/issues/111)); (3) `count + 1 ≤ 2^w` for the chosen width `w` and `w` is smaller than the pointer size of the target profile; (4) `P` is private: for an array, `EscapeOf` is `NoEscape` ([#82](https://github.com/Hawkynt/Polypiler/issues/82)), `LayoutDemandOf` is `Free`, no overlay or reinterpreting view exists; for a field of a record, the layout of that record must not be observable — conditions L1–L5 exactly as `OPT-MEM-DEADFIELD` states them ([#110](https://github.com/Hawkynt/Polypiler/issues/110), [#76](https://github.com/Hawkynt/Polypiler/issues/76), [#338](https://github.com/Hawkynt/Polypiler/issues/338), [#375](https://github.com/Hawkynt/Polypiler/issues/375)); (5) no element of `P` is volatile or atomic, and the address of an individual place is used only by `Load`/`Store` of the pointer; (6) block operations on `P` are whole-element `Memory.Copy`/`Move`/`Fill` among places of the same family and target region; (7) the decoded element address is computed only from an index that is in range: the null code selects index 0 before `ElemAddr`, so no out-of-range address is ever formed, and the pass records the bounds proof it derives from (1); (8) `ICostModel.IsAvailable("storage.compressed-pointer")` is true and [#155](https://github.com/Hawkynt/Polypiler/issues/155) accepts the trade of memory for the decode on each load.

## How it is proven

`provenance-facts+alias-facts+exhaustive-8bit`: by (1) every stored pointer is null or `&R[k]` with `0 ≤ k < count`; `encode` is injective on that set and `decode ∘ encode` is the identity, checked exhaustively at 8 bits and at the 16-bit boundaries. No solver, no timeout path.

## When it must not fire

- One store of a pointer into a second pool: its element number would be decoded as an element of the first.
- A pointer to a field inside a node (`FieldAddr`): its offset is not a multiple of the element size and has no code.
- A pool of 65 536 nodes at 16 bits: element 65 535 needs code 65 536, which wraps to 0 and reads back as null.
- Reserving the all-ones code for null instead of zero: a zero-initialized link array then reads as 'element 0' everywhere instead of null.
- Decoding without the null select: code 0 gives index 65 535 and an `ElemAddr` far outside the pool — a broken `inbounds` contract even if the result is discarded.
- The pointer array is passed to foreign code that expects machine pointers.

## Targets and cost gate

Pays off on targets with real pointers that are wider than the index: far pointers on x86-16 (4 bytes → 2), 32- and 64-bit machine code and C-family source. Those profiles list the storage form `storage.compressed-pointer`. On targets where [#496](https://github.com/Hawkynt/Polypiler/issues/496) already represents pointers as indices (BASICs, JavaScript, Python, the JVM) there is nothing to gain and the form is not listed. Target compilers cannot do this: it changes a stored type. Under `--opt speed` the cost model usually keeps full pointers, since every load pays a compare, a subtract and two selects. The form name `storage.compressed-pointer` and the `forms` list of `cost.yaml` that carries it are defined by `OPT-MEM-FORMS`.

## Switching it off

`--disable-opt O0346`; active at `--opt balanced`, `size` and `memory`; at `--opt speed` only when the cost model prefers it; off at `--opt none`.

## Interactions with other passes

runs in phase `specialize` as a module pass after `OPT-MEM-DEADFIELD` and before `OPT-MEM-FIELDORDER`, [#505](https://github.com/Hawkynt/Polypiler/issues/505) and [#501](https://github.com/Hawkynt/Polypiler/issues/501), which see an integer field or array and may pack it further; before [#496](https://github.com/Hawkynt/Polypiler/issues/496) and [#259](https://github.com/Hawkynt/Polypiler/issues/259).
