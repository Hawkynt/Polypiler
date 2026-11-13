#!/usr/bin/env python3
"""False-pass rate of each comparison option on two deliberately wrong kernels (ADR 0005).

Kernel A  sums 64 f32 values in an f32 accumulator; the wrong translation accumulates in f64 and
          narrows once at the end.
Kernel B  sums (a, b, -a) left to right in f64; the wrong translation reassociates to (a + -a) + b.

Neither kernel contains a tolerance-bounded intrinsic. 10 000 vectors each, SplitMix64 seed 20260101.
"""
import struct
from fractions import Fraction

MASK = (1 << 64) - 1
VECTORS = 10000


class SplitMix64:
  def __init__(self, seed):
    self.state = seed & MASK

  def next(self):
    self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
    z = self.state
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
    return z ^ (z >> 31)

  def unit(self):
    return (self.next() >> 11) / 9007199254740992.0


def f32(x):
  return struct.unpack("<f", struct.pack("<f", x))[0]


def ordinal(x, fmt, width):
  b = struct.unpack({"f": "<I", "d": "<Q"}[fmt], struct.pack("<" + fmt, x))[0]
  sign = 1 << (width - 1)
  return -(b & (sign - 1)) if b & sign else b & (sign - 1)


def distance(a, b, fmt):
  width = 32 if fmt == "f" else 64
  return abs(ordinal(a, fmt, width) - ordinal(b, fmt, width))


def modes(fmt):
  """(label, predicate(reference, candidate)) for every comparison a fixture could be given."""
  return [
    ("exact (option 1; option 3 `strict` and `accurate`)",
     lambda r, c: struct.pack("<" + fmt, r) == struct.pack("<" + fmt, c)),
    ("ulp 1 (option 2)", lambda r, c: distance(r, c, fmt) <= 1),
    ("ulp 4 (option 2)", lambda r, c: distance(r, c, fmt) <= 4),
    ("relative 1e-6 (option 2, option 3 `fast`)", lambda r, c: abs(c - r) <= 1e-6 * abs(r)),
    ("relative 1e-12 (option 2, option 3 `fast`)", lambda r, c: abs(c - r) <= 1e-12 * abs(r)),
    ("absolute 1e-6 (option 2)", lambda r, c: abs(c - r) <= 1e-6),
  ]


def next_after(x, up, fmt):
  width = 32 if fmt == "f" else 64
  code = {"f": "<I", "d": "<Q"}[fmt]
  b = struct.unpack(code, struct.pack("<" + fmt, x))[0]
  sign = 1 << (width - 1)
  if x == 0:
    b = 1 | (0 if up else sign)
  elif (x > 0) == up:
    b += 1
  else:
    b -= 1
  return struct.unpack("<" + fmt, struct.pack(code, b))[0]


def enclosure(exact, fmt):
  """The two neighbouring values of the format that enclose an exact rational result (option 4)."""
  near = f32(float(exact)) if fmt == "f" else float(exact)
  if Fraction(near) == exact:
    return near, near
  if Fraction(near) < exact:
    return near, next_after(near, True, fmt)
  return next_after(near, False, fmt), near


def report(title, fmt, cases):
  print("### " + title + "\n")
  print("| Comparison | vectors on which the wrong kernel is accepted | an 8-vector fixture passes | correct kernel rejected |")
  print("| --- | ---: | ---: | ---: |")
  differing = sum(1 for reference, wrong, _ in cases if distance(reference, wrong, fmt) != 0)
  for label, accept in modes(fmt):
    passed = sum(1 for reference, wrong, _ in cases if accept(reference, wrong))
    print("| %s | %d of %d | %.4f %% | 0 of %d |"
          % (label, passed, len(cases), 100.0 * (passed / len(cases)) ** 8, len(cases)))
  inside_wrong = inside_right = 0
  for reference, wrong, exact in cases:
    lo, hi = enclosure(exact, fmt)
    inside_wrong += lo <= wrong <= hi
    inside_right += lo <= reference <= hi
  print("| interval enclosing the exact real result (option 4) | %d of %d | %.4f %% | %d of %d |"
        % (inside_wrong, len(cases), 100.0 * (inside_wrong / len(cases)) ** 8, len(cases) - inside_right, len(cases)))
  print("\nThe wrong kernel's output differs from the correct one in %d of %d vectors.\n" % (differing, len(cases)))


def main():
  rng = SplitMix64(20260101)
  cases = []
  for _ in range(VECTORS):
    values = [f32(2.0 * rng.unit() - 1.0) for _ in range(64)]
    narrow = 0.0
    for v in values:
      narrow = f32(narrow + v)
    wide = 0.0
    for v in values:
      wide += v
    cases.append((narrow, f32(wide), sum(Fraction(v) for v in values)))
  report("Kernel A: f32 accumulation performed in f64", "f", cases)

  cases = []
  for i in range(VECTORS):
    a = (1.0 + rng.unit()) * 10.0 ** (int(rng.unit() * 20) + 1)   # 1e1 .. 2e20
    b = 2.0 * rng.unit() - 1.0
    if i == 0:
      a, b = 1e16, 1.0                                             # the case named in the work order
    cases.append(((a + b) + -a, (a + -a) + b, Fraction(b)))
  report("Kernel B: reassociated sum (a + b) + -a", "d", cases)


main()
