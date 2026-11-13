# O0210 — Lower BitPermute and BitRoute to a proven network chosen for the target

<!-- status: planned -->
> **Status: planned.** Not implemented yet — tracked in [#530](https://github.com/Hawkynt/Polypiler/issues/530). Once implemented it is switched off with `--disable-opt O0210`.

## What it does

A `BitPermute` or `BitRoute` in the IR is a statement of meaning; no backend can print it. PRD §20.5 wants
the synthesizer to "generate typed Target IR" chosen by target cost, and PRD §22 gives the scenario: a
mapping "represented by a PEXT/PDEP construction", a CPU without BMI2, and the requirement that "legal
shifts, masks, rotations or software helpers are emitted and target tests match the same mapping oracle".
This rewrite is where mapping, search, proof and cost model meet inside the compiler pipeline.

## Example

### Before

```
%r = BitPermute.u8 %x, [4,5,6,7,0,1,2,3]
```
- **After (IR)** (cost table with `rotate`):
```
%r = RotateLeft.u8 %x, 4
```
and with a table that has only `shift` and the logic operations:
```
%a = And.u8 %x, 0x0F
%b = Shl.u8[strict] %a, 4
%c = And.u8 %x, 0xF0
%d = LShr.u8[strict] %c, 4
%r = Or.u8 %b, %d
```

## When it applies

(1) the network passed `NetworkVerifier`; (2) `NetworkEquivalence.Prove` returned
`Proved` against the operation's mapping; (3) every primitive used is in the target's cost table;
(4) for a mapping with don't-care outputs: either the demanded-bits query of [#88](https://github.com/Hawkynt/Polypiler/issues/88) proves that no
use of `%r` reads a don't-care bit — then equivalence on `RequiredOutputMask` suffices — or the network
is proven on **all** bits against the reference value (don't-care = 0, [#529](https://github.com/Hawkynt/Polypiler/issues/529)). Composite networks
meet the second form by the term contract; any other candidate that fails it gets a trailing `And` with
`RequiredOutputMask` and is proven again. An unknown answer counts as "is read".

## How it is proven

that of the verdict — `exhaustive-<n>bit` up to 24 input bits, `smt-bitvector` or
`table-enumeration+smt-bitvector` above. On `Refuted`, `Inconclusive` or a timeout the candidate is
discarded; if none survives, the registered decomposition of [#529](https://github.com/Hawkynt/Polypiler/issues/529) is used and reported with class
`validated-rule+differential`.

## When it must not fire

- Inversion forgotten: `BitRoute.u8 %x, [~0,1,2,3,4,5,6,7]` lowered to `%x`: for `x = 0x00` the mapping gives `0x01`.
- Don't-care leaked: `%r = BitRoute.u8 %x, [0,?,?,?,?,?,?,?]; %s = Add.u8 %r, 1` with `%r` lowered to `%x`:
  for `x = 0x02` the reference gives `%s = 0x01`, the naive lowering `0x03`.
- Carrying multiply: `BitRoute.u8 %x, [0,?,1,?,?,?,?,?]` lowered to `Mul(And(%x, 3), 3)`: for `x = 0x03` bit 2 is 0, the mapping demands 1.
- Missing instruction: a `BitExtract` network under a table without `pext`.

## Switching it off

`--disable-opt O0210` (every mapping then takes the registered decomposition); active at
`--opt balanced`, `speed`, `size` and `memory`; at `--opt none` the decomposition is used.

## Interactions with other passes

runs after the recovery and algebra rules ([#544](https://github.com/Hawkynt/Polypiler/issues/544), [#533](https://github.com/Hawkynt/Polypiler/issues/533) and the
naming rules), so only mappings without a simpler name remain; before intrinsic legalization
([#259](https://github.com/Hawkynt/Polypiler/issues/259)) and the backends.
