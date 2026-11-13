# O0351 — Replace loads from a constant table whose entries are a simple function of the index by that computation

<!-- status: planned -->
> **Status: planned.** Not implemented yet. Once implemented it is switched off with `--disable-opt O0351`.

## What it does

The reverse trade of `OPT-MEM-LUTGEN`. Old code is full of tables that were faster than arithmetic on
the machine they were written for: an identity table, `i AND mask`, `i + 13` modulo 256. On a target
where the operation is one instruction, the table costs a load, a cache line and its bytes in the
image; and a translator that keeps it carries a 256-entry literal into readable source. PB-Compiler
eliminates such tables (upstream O0333, pass `LookupTableElimination`). PRD §4.5 (recognition),
§4.8.

## Example

### Before

```
global @mask: u8[256] = [0, 0, 2, 2, 0, 0, 2, 2, 8, 8, 10, 10, …] [readonly]     // entry i = i and 0x5A, all 256 listed
%v = Load.u8[inbounds #1] (ElemAddr @mask, %i)                                   // %i: u32 in [0, 255]
```

### After

```
%t = Truncate.u8 %i
%v = And.u8 %t, 0x5A
```

## When it applies

(1) The global is `[readonly]` with a constant initializer, of an integer element type `T`, not volatile, and does not carry `generated=O0350` ([#121](https://github.com/Hawkynt/Polypiler/issues/121), `OPT-MEM-LUTGEN`). (2) *Whole-table match*: one formula `F` of the six reproduces every entry, `F(i) = table[i]` for all `0 ≤ i < n`, where `i` is converted to `T` by zero-extension or truncation. Candidate parameters are derived from the entries and then verified against all of them: `c = table[0]` for the constant and the add form, `m = table[0]` for `Or` and `Xor`, `m =` the bitwise OR of all entries for `And`. When several formulas match, the first in the order constant, identity, `And`, `Or`, `Xor`, `Add` is used. A table with a single deviating entry matches nothing and stays. (3) A load is rewritten only if its index is proven inside `[0, n)`: the access is `inbounds`, or `IsInBounds` is `True` ([#93](https://github.com/Hawkynt/Polypiler/issues/93)), or the index range lies in the table ([#94](https://github.com/Hawkynt/Polypiler/issues/94)); a load under `[bounds]` with an unproven index keeps the table, because the formula has no out-of-range trap. (4) The table is deleted only when every use has been rewritten and its address is used by nothing else ([#82](https://github.com/Hawkynt/Polypiler/issues/82): no escape); otherwise it stays for the remaining uses. (5) [#155](https://github.com/Hawkynt/Polypiler/issues/155) prefers the computation: the cost of the conversion and the one operation against the cost of the load, with the table's bytes credited to `Cost.Memory` when it can be deleted.

## How it is proven

`exhaustive-table+range-facts`: the formula is compared with every entry of the table, which for a finite table is a complete proof, and every rewritten index is proven inside it. No solver, no timeout path.

## When it must not fire

- A table equal to `i and 0x5A` except `table[7] = 3` (the formula gives 2): a hand-patched special case; rewriting changes the result for index 7.
- `Load.u8[bounds] (ElemAddr @mask, %i)` with `i = 300`: the load traps `IndexOutOfRange`; `And(Truncate.u8 300, 0x5A)` returns 8.
- A 256-entry `u8` table of `i + 200` holds `(i + 200) mod 256`; computing the sum in the index type `u32` and truncating late is equal, computing it with a saturating or trapping add is not — the rewritten add is `[wrap]` in the element type.
- A table the program writes at start-up (no `[readonly]`): its initializer is not its content.
- A table produced by `OPT-MEM-LUTGEN` for an identity-like function: eliminating it re-creates the calls' computation the cost model just decided against.

## Targets and cost gate

Every target. The six operations are native everywhere, so the computation is rarely more expensive than an indexed load; where it is — the cost model knows, per profile and objective — the table stays. For source backends the gain is as much readability as speed: a 256-entry literal becomes one expression, and no target compiler can remove a table it only sees as data.

## Switching it off

`--disable-opt O0351`; active at `--opt balanced`, `speed`, `size` and `memory`, off at `--opt none`.

## Interactions with other passes

runs in phase `optimize` as a module pass after [#164](https://github.com/Hawkynt/Polypiler/issues/164) and range propagation (which bound the indices) and before `OPT-MEM-CONSTMERGE`; enables [#154](https://github.com/Hawkynt/Polypiler/issues/154), [#163](https://github.com/Hawkynt/Polypiler/issues/163) and recognition rules on the now visible arithmetic, and `OPT-MEM-DEADGLOBAL`-style removal of the table.
