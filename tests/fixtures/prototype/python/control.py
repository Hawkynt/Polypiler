"""Branches, loops over range with their else clauses, match on literals and assignments. Written for this repository."""


def sign(x: float) -> int:
    if x > 0:
        return 1
    elif x < 0:
        return -1
    else:
        return 0


def truth_branch(x: int) -> int:
    if x:
        return 1
    return 0


def sum_to(n: int) -> int:
    s = 0
    for i in range(1, n + 1):
        s += i
    return s


def digits_up(n: int) -> int:
    s = 0
    for i in range(n):
        s = s * 10 + i
    return s


def digits_down(n: int) -> int:
    s = 0
    for i in range(n, 0, -1):
        s = s * 10 + i
    return s


def stepped(start: int, stop: int, step: int) -> int:
    count = 0
    total = 0
    for i in range(start, stop, step):
        count += 1
        total = (total * 7 + i % 1000003) % 1000003
    return count * 1000003 + total


def last_value(n: int) -> int:
    i = -1
    for i in range(n):
        pass
    return i


def range_once(n: int) -> int:
    c = 0
    i = -1
    for i in range(n):
        n = 100
        i = i + 10
        c += 1
    return c * 100 + i


def is_prime(n: int) -> int:
    for i in range(2, n):
        if n % i == 0:
            break
    else:
        return 1
    return 0


def first_multiple(n: int, k: int) -> int:
    found = -1
    for i in range(1, n):
        if i % k != 0:
            continue
        found = i
        break
    else:
        found = 0
    return found


def collatz(n: int) -> int:
    steps = 0
    while n != 1:
        if n % 2 == 0:
            n //= 2
        else:
            n = 3 * n + 1
        steps += 1
    return steps


def while_else(n: int) -> int:
    r = 0
    while n > 0:
        n -= 3
        if n == 4:
            r = 100
            break
    else:
        r = n
    return r


def while_true(n: int) -> int:
    k = 0
    while True:
        k += 1
        if k * k >= n:
            break
    return k


def nested(n: int) -> int:
    count = 0
    for a in range(n):
        for b in range(a):
            if (a + b) % 3 == 0:
                continue
            count += 1
    return count


def classify(x: int) -> int:
    match x:
        case 0:
            return 10
        case 1 | 2:
            return 20
        case -1:
            return 25
        case _ if 3 <= x <= 9:
            return 30
        case _:
            return 40


def capture(x: int) -> int:
    match x:
        case 5:
            return 0
        case n if n > 100:
            return n - 100
        case n:
            return n * 2


def match_bool(b: bool) -> int:
    r = 0
    match b:
        case True:
            r = 1
        case False:
            r = 2
    return r


def match_float(x: float) -> int:
    match x:
        case 1:
            return 1
        case 2.5:
            return 2
    return 0


def swap(a: int, b: int) -> int:
    a, b = b, a
    return a * 10 + b


def rotate(a: int, b: int, c: int) -> int:
    a, b, c = b, c, a
    return a * 100 + b * 10 + c


def chained_assign(x: int) -> int:
    a = b = x * 2
    return a + b


def checked(x: int) -> int:
    assert x >= 0, "x must not be negative"
    return x * 2


def trap_in_loop(n: int) -> int:
    s = 0
    for i in range(n):
        s += 10 // (2 - i)
    return s


def gcd(a: int, b: int) -> int:
    while b:
        a, b = b, a % b
    return a
