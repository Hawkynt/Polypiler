"""Defaults and keyword arguments, nested functions and closures, lambdas, module constants, tables and globals, imports of the standard library and of another module, walrus and generators. Written for this repository."""

import math
from math import floor as round_down
from typing import Iterator

import helpers_lib as hl
from helpers_lib import Limits, scale

BASE = 5
TABLE = [i * i for i in range(6)]
WEIGHTS = (1.5, 2.5, 4.0)
COUNT = 0


def _with_default(a: int, b: int = BASE * 2) -> int:
    return a - b


BASE = 100


def defaults(x: int, named: bool) -> int:
    return _with_default(b=x, a=1) if named else _with_default(x)


def _kinds(a: int, /, b: int = 2, *, c: int = 3) -> int:
    return a * 100 + b * 10 + c


def call_kinds(n: int) -> int:
    return _kinds(n) + _kinds(n, 5) + _kinds(n, c=7) + _kinds(n, b=1, c=0) + BASE


def order(n: int) -> int:
    log = [0]

    def note(v: int) -> int:
        log.append(v)
        return v

    _kinds(note(1), c=note(3), b=note(2))
    return log[1] * 100 + log[2] * 10 + log[3] + n


def closure(a: int, b: int) -> int:
    best = a

    def check(v: int) -> bool:
        nonlocal best
        if v >= best:
            return False
        best = v
        return True

    if check(b):
        check(b - 1)
    return best


def late_binding(n: int) -> int:
    x = 1

    def read() -> int:
        return x * 10

    x = n
    return read()


def counter_closure(n: int) -> int:
    count = 0

    def bump(k: int) -> None:
        nonlocal count
        count += k

    for i in range(n):
        bump(i)
    return count


def two_levels(n: int) -> int:
    base = n

    def outer(k: int) -> int:
        def inner(j: int) -> int:
            return base * 100 + k * 10 + j

        return inner(k + 1)

    return outer(2)


def recursive_nested(n: int) -> int:
    def fact(k: int) -> int:
        return 1 if k <= 1 else k * fact(k - 1)

    return fact(n)


def square(x: int) -> int:
    return x * x


def lambdas(n: int) -> int:
    values = [3, -1, n, 2]
    total = 0
    for v in map(lambda x: x * 2, values):
        total = total * 10 + abs(v)
    evens = list(filter(lambda x: x % 2 == 0, values))
    return total + len(evens) * 1000 + min(values, key=lambda x: abs(x - 1)) + sum(map(lambda x: x + 1, values))


def map_function(n: int) -> int:
    return sum(map(square, range(n))) + max([n, -n, 2], key=square)


def module_table(i: int) -> int:
    return TABLE[i] + len(TABLE)


def weights(i: int) -> float:
    return WEIGHTS[0] + WEIGHTS[2] * i


def _add_count(n: int) -> None:
    global COUNT
    COUNT += n


def global_counter(n: int) -> int:
    global COUNT
    COUNT = 0
    _add_count(n)
    _add_count(n)
    return COUNT


def math_calls(x: float) -> float:
    return (math.sqrt(abs(x)) + math.fabs(x) + math.copysign(1.0, x) + (1.0 if math.isnan(x) else 0.0)
            + (2.0 if math.isinf(x) else 0.0) + (4.0 if math.isfinite(x) else 0.0) + math.pi)


def math_ints(x: float) -> int:
    return math.floor(x) * 1000 + math.ceil(x) * 100 + math.trunc(x) * 10 + round_down(x)


def math_sqrt(x: float) -> float:
    return math.sqrt(x)


def integer_math(a: int, b: int) -> int:
    return math.gcd(a, b) * 1000 + math.isqrt(abs(a))


def walrus(n: int) -> int:
    total = 0
    values = [n, n + 1, n + 2]
    if (k := len(values)) > 2:
        total = k
    while (n := n - 1) > 0:
        total += n
    return total + sum(y for x in values if (y := x * 2) > 4)


def _countdown(n: int) -> Iterator[int]:
    while n > 0:
        yield n
        n -= 1


def _evens(n: int) -> Iterator[int]:
    for i in range(n):
        if i % 2:
            continue
        yield i
    yield from [100, 200]


def generators(n: int) -> int:
    total = 0
    for v in _countdown(n):
        total = total * 10 + v
    return total + sum(_countdown(n)) + len(list(_evens(n))) + sum(_evens(n))


def generator_early(n: int) -> int:
    for v in _countdown(10):
        if v == n:
            return v * 100
    return -1


def imported(n: int) -> int:
    return scale(n) + hl.scale(n, 3) + hl.OFFSET + Limits(n).high + hl.Limits(n).low
