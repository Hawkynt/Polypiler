# Permutation recognition oracles

The C# fixtures in `o0105/` and `o0106/` are independently written regression
inputs imported into the structured tree IR. They are not serialized SSA.

`oracle-vectors.json` records outputs from the work orders' pinned sources:

- `Hawkynt/CompressionWorkbench@21e609396e8fceb80e49185cc34cab7bce7f6483`,
  `Compression.Core/BitIO/BitHelpers.cs`: the original `ReverseBits` function,
  compiled unchanged under .NET 10.0.9. Six inputs and eight code lengths give
  48 vectors, including -1, 0, 1, 31, 32 and 33 length boundaries. The original
  C# masked shift behavior is retained even outside the usual length domain.
- `Hawkynt/Hawkynt.github.io@b3a8a8e92cbca83475986391c3cac56f0b185d29`,
  `Cipher/algorithms/hash/cityhash.js`: its original `bswap64` method, executed
  under Node v22.16.0 with native BigInt adapters for `OrN`, `AndN`, `ShiftLn`
  and `ShiftRn`, and `BigInt.asUintN(64, value)` for its entry mask. Eight
  unsigned 64-bit inputs cover zero, low and high bytes, the sign bit, mixed
  bytes and all ones.

Both upstream repositories identify these sources as LGPL-3.0-or-later. Only
their numeric outputs are committed here; no upstream source is copied into the
product. `CapturedPinnedUpstreamOutputs_Agree` compares both original and
optimized IR with every recorded output. Separate tests use the exhaustive
prover and 10,000 seeded inputs per wide rule/width against independent bit and
byte oracles.
