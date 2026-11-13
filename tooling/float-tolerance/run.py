#!/usr/bin/env python3
"""Evaluates the ADR 0005 input set with the Python math module; an exception is written as "trap"."""
import math
import operator
import struct
import sys

FUNCTIONS = {
  "sin": math.sin, "cos": math.cos, "tan": math.tan, "exp": math.exp, "log": math.log, "log2": math.log2,
  "log10": math.log10, "pow": math.pow, "atan2": math.atan2, "sinh": math.sinh, "cosh": math.cosh,
  "tanh": math.tanh, "cbrt": math.cbrt, "hypot": math.hypot, "add": operator.add, "sub": operator.sub,
  "mul": operator.mul, "div": operator.truediv, "sqrt": math.sqrt, "fma": getattr(math, "fma", None),
}

out = []
for line in sys.stdin:
  parts = line.split()
  function = FUNCTIONS[parts[0]]
  if function is None:
    out.append("n/a")
    continue
  operands = [struct.unpack("<d", struct.pack("<Q", int(h, 16)))[0] for h in parts[2:]]
  try:
    out.append("%016x" % struct.unpack("<Q", struct.pack("<d", function(*operands)))[0])
  except (ValueError, OverflowError, ZeroDivisionError):
    out.append("trap")
sys.stdout.write("\n".join(out) + "\n")
