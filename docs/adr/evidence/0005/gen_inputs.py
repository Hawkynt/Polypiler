#!/usr/bin/env python3
"""Writes the fixed-seed input set of ADR 0005 to stdout.

One line per evaluation: <function> <class> <operand bits as 16 hex digits>...
The generator is SplitMix64 seeded with 20260101; no library random source is used, so the file is
identical on every interpreter version.
"""
import struct
import sys

SEED = 20260101
MASK = (1 << 64) - 1
GRID = 10000


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
    """A double in [0, 1) with 53 random bits."""
    return (self.next() >> 11) / 9007199254740992.0

  def sign(self):
    return -1.0 if self.next() & 1 else 1.0


def bits(x):
  return struct.unpack("<Q", struct.pack("<d", x))[0]


def value(b):
  return struct.unpack("<d", struct.pack("<Q", b))[0]


def log_uniform(rng, lo_exp, hi_exp):
  """A positive double whose binary exponent is uniform in [lo_exp, hi_exp) and whose significand is random."""
  e = lo_exp + int(rng.unit() * (hi_exp - lo_exp))
  m = rng.next() & ((1 << 52) - 1)
  if e < -1022:  # subnormal: shift the significand down
    return value(((1 << 52) | m) >> (-1022 - e))
  return value(((e + 1023) << 52) | m)


def uniform(rng, lo, hi):
  return lo + (hi - lo) * rng.unit()


def any_finite(rng):
  while True:
    b = rng.next()
    if (b >> 52) & 0x7FF != 0x7FF:
      return value(b)


def grid(name, rng, i):
  """The i-th grid operand tuple of a function; the first half is the core range, the second the whole domain."""
  core = i < GRID // 2
  if name in ("sin", "cos", "tan"):
    return (rng.sign() * (log_uniform(rng, -20, 20) if core else log_uniform(rng, -1074, 1024)),)
  if name == "exp":
    return (uniform(rng, -745.2, 709.8) if core else rng.sign() * log_uniform(rng, -60, 9),)
  if name in ("log", "log2", "log10"):
    return (uniform(rng, 0.5, 2.0) if core else log_uniform(rng, -1074, 1024),)
  if name == "pow":
    if core:
      return (log_uniform(rng, -8, 8), uniform(rng, -40.0, 40.0))
    return (uniform(rng, 0.9, 1.1), uniform(rng, -5000.0, 5000.0))
  if name == "atan2":
    lo, hi = (-8, 8) if core else (-1074, 1024)
    return (rng.sign() * log_uniform(rng, lo, hi), rng.sign() * log_uniform(rng, lo, hi))
  if name in ("sinh", "cosh"):
    return (rng.sign() * log_uniform(rng, -40, 5) if core else uniform(rng, -710.4, 710.4),)
  if name == "tanh":
    return (rng.sign() * (log_uniform(rng, -8, 5) if core else log_uniform(rng, -1074, 6)),)
  if name == "cbrt":
    return (rng.sign() * (log_uniform(rng, -8, 8) if core else log_uniform(rng, -1074, 1024)),)
  if name == "hypot":
    if core:
      x = log_uniform(rng, -500, 500)
      return (rng.sign() * x, rng.sign() * x * uniform(rng, 0.25, 4.0))
    return (rng.sign() * log_uniform(rng, -1074, 1024), rng.sign() * log_uniform(rng, -1074, 1024))
  if name == "sqrt":
    return (log_uniform(rng, -8, 8) if core else log_uniform(rng, -1074, 1024),)
  if name in ("add", "sub", "mul", "div"):
    if core:  # operands of similar magnitude: cancellation, ties and sticky bits
      e = int(uniform(rng, -900, 900))
      return (rng.sign() * log_uniform(rng, e, e + 60), rng.sign() * log_uniform(rng, e, e + 60))
    return (any_finite(rng), any_finite(rng))
  if name == "fma":
    if core:  # the addend nearly cancels the product
      a = rng.sign() * log_uniform(rng, -200, 200)
      b = rng.sign() * log_uniform(rng, -200, 200)
      c = -(a * b)
      return (a, b, value((bits(c) + (rng.next() % 9) - 4) & MASK))
    return (rng.sign() * log_uniform(rng, -300, 300), rng.sign() * log_uniform(rng, -300, 300),
            rng.sign() * log_uniform(rng, -600, 600))
  raise ValueError(name)


INF = float("inf")
NAN = float("nan")
MIN_SUB = value(1)
MAX_SUB = value((1 << 52) - 1)
MIN_NORMAL = value(1 << 52)
MAX_FINITE = value(0x7FEFFFFFFFFFFFFF)
UNARY_SPECIAL = [0.0, -0.0, INF, -INF, NAN, MIN_SUB, -MIN_SUB, MAX_SUB, -MAX_SUB, MIN_NORMAL, -MIN_NORMAL,
                 MAX_FINITE, -MAX_FINITE, 1.0, -1.0]
BINARY_SPECIAL = [0.0, -0.0, INF, -INF, NAN, MIN_SUB, -MIN_SUB, MAX_FINITE, -MAX_FINITE, 1.0, -1.0, 2.0, 0.5, 3.0]
TERNARY_SPECIAL = [0.0, -0.0, INF, -INF, NAN, MIN_SUB, MAX_FINITE, -MAX_FINITE, 1.0, -1.0]

FUNCTIONS = [("sin", 1), ("cos", 1), ("tan", 1), ("exp", 1), ("log", 1), ("log2", 1), ("log10", 1), ("pow", 2),
             ("atan2", 2), ("sinh", 1), ("cosh", 1), ("tanh", 1), ("cbrt", 1), ("hypot", 2), ("add", 2), ("sub", 2),
             ("mul", 2), ("div", 2), ("sqrt", 1), ("fma", 3)]


def main():
  out = sys.stdout
  for index, (name, arity) in enumerate(FUNCTIONS):
    rng = SplitMix64(SEED + index)
    for i in range(GRID):
      out.write(name + " grid " + " ".join("%016x" % bits(v) for v in grid(name, rng, i)) + "\n")
    if arity == 1:
      specials = [(a,) for a in UNARY_SPECIAL]
    elif arity == 2:
      specials = [(a, b) for a in BINARY_SPECIAL for b in BINARY_SPECIAL]
    else:
      specials = [(a, b, c) for a in TERNARY_SPECIAL for b in TERNARY_SPECIAL for c in TERNARY_SPECIAL]
    for operands in specials:
      out.write(name + " special " + " ".join("%016x" % bits(v) for v in operands) + "\n")


main()
