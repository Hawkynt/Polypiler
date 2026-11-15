"""Module-level functions that call each other, recursion, and helpers whose types come from their calls. Written for this repository."""


def _square(x):
    return x * x


def _half(x):
    return x / 2


def sum_of_squares(a: int, b: int) -> int:
    return _square(a) + _square(b)


def hypotenuse_squared(a: float, b: float) -> float:
    return a * a + b * b


def halves(a: int) -> float:
    return _half(a) + _half(a + 1)


def factorial(n: int) -> int:
    if n <= 1:
        return 1
    return n * factorial(n - 1)


def fibonacci(n: int) -> int:
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a


def is_even(n: int) -> bool:
    if n == 0:
        return True
    return is_odd(n - 1)


def is_odd(n: int) -> bool:
    if n == 0:
        return False
    return is_even(n - 1)


def ackermann(m: int, n: int) -> int:
    if m == 0:
        return n + 1
    if n == 0:
        return ackermann(m - 1, 1)
    return ackermann(m - 1, ackermann(m, n - 1))


def count_down(n: int) -> None:
    while n > 0:
        n -= 1


def uses_none_function(n: int) -> int:
    count_down(n)
    return n


def mean(a: float, b: float, c: float) -> float:
    total = a + b + c
    return total / 3


def adler32_step(a: int, b: int, byte: int) -> int:
    a = (a + byte) % 65521
    b = (b + a) % 65521
    return (b << 16) | a
