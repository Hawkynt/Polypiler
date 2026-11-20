# O0102 fixtures and provenance

`popcount.cs` is an executable source fixture imported into the current tree IR.
It covers the SWAR population count at 32 bits with the multiply fold and with
the shift-add fold (first step as a subtraction and as two masked adds), at 64
bits with the multiply fold, and at 16 bits through stable locals over a
widened `ushort`; and the counterexamples of the work order: a wrong second
mask `0x33333332`, 32-bit masks over a 64-bit value, a final shift of 16
instead of 24, and a second variable in the sequence.

The sequence is written for this repository from Warren, *Hacker's Delight*,
2nd ed., Fig. 5-2 (sourcing rung 3). No upstream implementation is copied, and
no upstream file at the work order's pins contains the idiom in isolation.
Independent correctness evidence comes from Z3 against a bit-by-bit definition
of the population count and from the BCL's `BitOperations.PopCount`.
