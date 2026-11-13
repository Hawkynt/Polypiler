"""Brute-force check of d(a, b) <= m + n + max(m, n) in a toy binary format (p = 4, exponents -3..4)."""
from fractions import Fraction as F
import sys
P, EMIN, EMAX = 4, -3, 4
floats = sorted({F(s) * F(2) ** (e - P + 1) for e in range(EMIN, EMAX + 1) for s in range(0, 2 ** P)}
                | {-(F(s) * F(2) ** (e - P + 1)) for e in range(EMIN, EMAX + 1) for s in range(0, 2 ** P)})
index = {v: i for i, v in enumerate(floats)}


def ulp(x):
  x = abs(x)
  e = EMIN
  while F(2) ** (e + 1) <= x:
    e += 1
  return F(2) ** (e - P + 1)


worst = {}
lo, hi = F(2) ** -2, F(2) ** 3
steps = 64
x = lo
while x <= hi:
  u = ulp(x)
  for m in (1, 2, 3, 4):
    near = [v for v in floats if abs(v - x) <= m * u]
    worst.setdefault(m, {})
    for n in (1, 2, 3, 4):
      near_n = [v for v in floats if abs(v - x) <= n * u]
      d = max(abs(index[a] - index[b]) for a in near for b in near_n)
      key = (m, n)
      if d > worst.get(key, (0, None))[0]:
        worst[key] = (d, x)
  x += u / steps
bad = 0
for (m, n), (d, at) in sorted((k, v) for k, v in worst.items() if isinstance(k, tuple)):
  bound = m + n + max(m, n)
  print(m, n, "max distance", d, "bound", bound, "at", float(at))
  bad += d > bound
sys.exit(bad)
