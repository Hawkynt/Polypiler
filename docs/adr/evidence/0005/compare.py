#!/usr/bin/env python3
"""Builds the tables of ADR 0005 from the runner outputs in a work directory.

Two measures are used and never mixed:
  error     |(computed - lo) / 2^q - frac| - the distance of a result from the exact value in units of
            the spacing of doubles at the exact value (real-valued);
  distance  the number of doubles between two results (integer), with -0 and +0 at the same position.
"""
import sys
from fractions import Fraction
from pathlib import Path

FUNCTIONS = ["sin", "cos", "tan", "exp", "log", "log2", "log10", "pow", "atan2", "sinh", "cosh", "tanh", "cbrt",
             "hypot", "add", "sub", "mul", "div", "sqrt", "fma"]
RUNTIMES = ["dotnet-sdk", "node", "gcc", "rust", "jdk", "python", "perl"]   # the runtimes the tolerance must cover
EXTRA = ["jdk-strict", "go", "x87"]                                          # reported, not part of the maximum
SIGN = 1 << 63
ABS = SIGN - 1
INF = 0x7FF0000000000000


def is_nan(b):
  return b & ABS > INF


def ordinal(b):
  """Position on the number line counted in doubles; both zeros are 0, the infinities one past the largest finite."""
  return -(b & ABS) if b & SIGN else b & ABS


def exact_value(b):
  """The value of a non-NaN double as a fraction; an infinity counts as +-2^1024."""
  magnitude = b & ABS
  exponent, significand = magnitude >> 52, magnitude & ((1 << 52) - 1)
  if exponent == 0x7FF:
    v = Fraction(2) ** 1024
  elif exponent == 0:
    v = Fraction(significand, 1 << 1074)
  else:
    v = Fraction((1 << 52) | significand) * Fraction(2) ** (exponent - 1075)
  return -v if b & SIGN else v


def fmt(x):
  return "—" if x is None else ("%.3f" % x)


def main():
  work = Path(sys.argv[1])
  inputs = [line.split() for line in (work / "inputs.txt").read_text().split("\n") if line]
  reference = [line.split() for line in (work / "out.reference.txt").read_text().split("\n") if line]
  outputs = {}
  for name in RUNTIMES + EXTRA:
    path = work / ("out." + name + ".txt")
    if path.exists():
      outputs[name] = path.read_text().split()
      assert len(outputs[name]) == len(inputs), name
  names = [n for n in RUNTIMES + EXTRA if n in outputs]

  max_error = {(f, n): None for f in FUNCTIONS for n in names}
  worst = {}
  misrounded = {(f, n): 0 for f in FUNCTIONS for n in names}
  evaluated = {(f, n): 0 for f in FUNCTIONS for n in names}
  special = {n: {"trap": 0, "zero sign": 0, "class": 0, "value": 0} for n in names}
  special_examples = {n: [] for n in names}
  nan_patterns = {n: {} for n in names}
  pair_distance = {f: (0, None) for f in FUNCTIONS}
  pair_distance_all = {f: (0, None) for f in FUNCTIONS}
  nan_by_class_only = 0

  for i, (line, ref) in enumerate(zip(inputs, reference)):
    function = line[0]
    cr = int(ref[0], 16)
    numeric = {}
    for n in names:
      text = outputs[n][i]
      if text == "n/a":
        continue
      if text == "trap":
        special[n]["trap"] += 1
        if len(special_examples[n]) < 400:
          special_examples[n].append((function, line[2:], "trap", ref[0]))
        continue
      c = int(text, 16)
      evaluated[(function, n)] += 1
      if is_nan(c):
        nan_patterns[n][text] = nan_patterns[n].get(text, 0) + 1
      if ref[1] == "nan" or is_nan(c):
        if (ref[1] == "nan") != is_nan(c):
          special[n]["class"] += 1
          special_examples[n].append((function, line[2:], text, ref[0]))
        continue
      numeric[n] = c
      if ref[1] == "exact":
        if c != cr:
          kind = "zero sign" if (c | cr) & ABS == 0 else "value"
          special[n][kind] += 1
          special_examples[n].append((function, line[2:], text, ref[0]))
          misrounded[(function, n)] += kind == "value"
        continue
      lo, q, frac = int(ref[2], 16), int(ref[3]), Fraction(ref[4])
      spacing = Fraction(2) ** q
      position = (exact_value(c) - exact_value(lo)) / spacing
      if lo & SIGN:
        position = -position
      error = float(min(abs(position - frac), Fraction(10) ** 300))
      if c != cr:
        misrounded[(function, n)] += 1
      key = (function, n)
      if max_error[key] is None or error > max_error[key]:
        max_error[key] = error
        worst[key] = (line[2:], text, ref[0])
    for group, table in ((RUNTIMES, pair_distance), (names, pair_distance_all)):
      present = [(ordinal(numeric[n]), n) for n in group if n in numeric]
      if len(present) > 1:
        low, high = min(present), max(present)
        if high[0] - low[0] > table[function][0]:
          table[function] = (high[0] - low[0], (low[1], high[1], " ".join(line[2:])))

  print("### Maximum error against the exact value, in ULP\n")
  print("| Function | " + " | ".join(names) + " |")
  print("| --- |" + " ---: |" * len(names))
  for f in FUNCTIONS:
    print("| `%s` | " % f + " | ".join(fmt(max_error[(f, n)]) if evaluated[(f, n)] else "n/a" for n in names) + " |")

  print("\n### Results that are not the correctly rounded value, of 10 000 grid inputs plus specials\n")
  print("| Function | " + " | ".join(names) + " |")
  print("| --- |" + " ---: |" * len(names))
  for f in FUNCTIONS:
    print("| `%s` | " % f + " | ".join(str(misrounded[(f, n)]) if evaluated[(f, n)] else "n/a" for n in names) + " |")

  print("\n### Maximum distance between two runtimes, in doubles\n")
  print("| Function | among " + ", ".join(RUNTIMES) + " | pair | including " + ", ".join(EXTRA) + " | pair |")
  print("| --- | ---: | --- | ---: | --- |")
  for f in FUNCTIONS:
    d, who = pair_distance[f]
    da, whoa = pair_distance_all[f]
    print("| `%s` | %d | %s | %d | %s |" % (f, d, who[0] + " / " + who[1] if who else "—", da,
                                            whoa[0] + " / " + whoa[1] if whoa else "—"))

  print("\n### Special-value and exact-result disagreements with the reference\n")
  print("| Runtime | raised an error | wrong sign of zero | NaN where a number is defined, or the reverse | wrong value |")
  print("| --- | ---: | ---: | ---: | ---: |")
  for n in names:
    s = special[n]
    print("| %s | %d | %d | %d | %d |" % (n, s["trap"], s["zero sign"], s["class"], s["value"]))

  print("\n### NaN encodings produced\n")
  print("| Runtime | encodings (count) |")
  print("| --- | --- |")
  for n in names:
    print("| %s | %s |" % (n, ", ".join("`%s` (%d)" % kv for kv in sorted(nan_patterns[n].items()))))

  probes = [("sub", "∞ − ∞", ["7ff0000000000000", "7ff0000000000000"]),
            ("mul", "0 × ∞", ["0000000000000000", "7ff0000000000000"]),
            ("add", "NaN + 1", ["7ff8000000000000", "3ff0000000000000"]),
            ("log", "log(−1)", ["bff0000000000000"]),
            ("pow", "pow(−1, 0.5)", ["bff0000000000000", "3fe0000000000000"]),
            ("hypot", "hypot(NaN, 1)", ["7ff8000000000000", "3ff0000000000000"])]
  print("\n### Encoding of the NaN returned by selected operations\n")
  print("| Operation | " + " | ".join(names) + " |")
  print("| --- |" + " --- |" * len(names))
  for function, label, operands in probes:
    index = next(i for i, line in enumerate(inputs) if line[0] == function and line[1] == "special" and line[2:] == operands)
    cells = []
    for n in names:
      text = outputs[n][index]
      cells.append("error" if text == "trap" else text if text == "n/a" else "`" + text[:4] + "…" + text[-1] + "`")
    print("| %s | " % label + " | ".join(cells) + " |")

  with open(work / "details.txt", "w") as details:
    for key in sorted(worst):
      details.write("worst %s %s: input %s result %s correctly rounded %s error %.3f\n"
                    % (key[0], key[1], " ".join(worst[key][0]), worst[key][1], worst[key][2], max_error[key]))
    for n in names:
      for function, operands, got, expected in special_examples[n]:
        details.write("special %s %s(%s) = %s, reference %s\n" % (n, function, " ".join(operands), got, expected))


main()
