# O0104 fixtures and provenance

`debruijn.cs` is an executable source fixture imported into the current tree IR.
It covers the trailing-zero scan and the smear (floor-log2) scan at 32 and 64
bits, and the counterexamples of the work order: a table with a wrong entry, a
table that is written elsewhere, `x & (x - 1)` instead of `x & -x`, and a shift
amount other than `w - log2 w`.

The method is Leiserson, Prokop and Randall, "Using de Bruijn Sequences to
Index a 1 in a Computer Word" (1998), sourcing rung 3. The 32-bit constants
`0x077CB531` and `0x07C4ACDD` and their tables are the public-domain ones from
Sean Anderson's *Bit Twiddling Hacks*, copied exactly. The 64-bit constant
`0x03F79D71B4CB0A89` is the one in the same literature (and on the Chess
Programming Wiki); its smear table equals the wiki's `index64` table. The
64-bit trailing-zero table was computed from the constant as
`table[(2^i * C mod 2^64) >> 58] = i` and checked to be a permutation; the
rule's own table check and the tests' comparison with `BitOperations` are its
evidence.

No upstream implementation is copied. A table that is written anywhere is a
global of the module rather than a constant, which is how the frontends mark
it, and is refused.
