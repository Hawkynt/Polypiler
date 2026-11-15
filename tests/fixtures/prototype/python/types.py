"""Classes, dataclasses, named tuples, enums, None and exceptions, one function per construct. Written for this repository."""

from dataclasses import dataclass
from enum import Enum, IntEnum
from typing import NamedTuple, Optional


@dataclass
class Point:
    x: int
    y: int = 0

    def norm1(self) -> int:
        return abs(self.x) + abs(self.y)


@dataclass(frozen=True)
class Frozen:
    a: int
    b: float


@dataclass(eq=False)
class Identity:
    v: int


class Counter:
    """A plain class: its attributes are those __init__ assigns."""

    def __init__(self, start: int):
        self.count = start
        self.steps = 0

    def add(self, n: int) -> int:
        self.count += n
        self.steps += 1
        return self.count

    @property
    def mean(self) -> float:
        return self.count / self.steps

    @staticmethod
    def twice(n: int) -> int:
        return 2 * n


class Grid:
    def __init__(self, width: int, height: int):
        self.width = width
        self.height = height
        self.cells = bytearray(width * height)

    def __len__(self) -> int:
        return self.width * self.height

    def __getitem__(self, i: int) -> int:
        return self.cells[i]

    def __setitem__(self, i: int, v: int) -> None:
        self.cells[i] = v


class Money:
    def __init__(self, cents: int):
        self.cents = cents

    def __eq__(self, other: "Money") -> bool:
        return self.cents == other.cents


class Pair(NamedTuple):
    left: int
    right: int


class Color(Enum):
    RED = 1
    GREEN = 2
    BLUE = 4


class Method(IntEnum):
    YADIF = 0
    BWDIF = 1
    GREEDY_HM = 2


class MyError(ValueError):
    pass


class DeepError(MyError):
    """A subclass of a subclass of a built-in exception."""


def point(x: int, y: int) -> int:
    p = Point(x)
    q = Point(y=y, x=x)
    return p.norm1() * 100 + q.norm1() + int(p == Point(x, 0)) * 1000


def dataclass_equality(a: int) -> bool:
    return Point(a, 2) == Point(1, 2)


def plain_equality(a: int) -> bool:
    c = Counter(a)
    d = c
    return c == d and not (Counter(a) == Counter(a)) and c is d


def frozen(a: int) -> float:
    f = Frozen(a, 0.5)
    return f.a + f.b + int(f == Frozen(a, 0.5))


def identity(v: int) -> bool:
    return Identity(v) == Identity(v)


def counter(n: int) -> float:
    c = Counter(10)
    for i in range(n):
        c.add(i)
    return c.mean + Counter.twice(c.steps)


def grid(i: int, v: int) -> int:
    g = Grid(3, 2)
    g[i] = v
    return len(g) * 1000 + g[i] + g[0]


def money(a: int, b: int) -> bool:
    return Money(a) == Money(b)


def _bump(c: Counter, n: int) -> None:
    c.add(n)


def sharing(n: int) -> int:
    c = Counter(0)
    cells = [c, c]
    _bump(c, n)
    return cells[1].count + cells[0].steps


def named_tuple(a: int, b: int) -> int:
    p = Pair(a, b)
    q = Pair(right=b, left=a)
    return p.left * 10 + q.right + int(p == q)


def enum_value(n: int) -> int:
    c = Color(n)
    return c.value * 10 + len(c.name)


def enum_compare(n: int) -> bool:
    return Color(n) == Color.GREEN and Color(n) is Color.GREEN and Color.RED != 1


def enum_iteration(n: int) -> int:
    total = 0
    for c in Color:
        total = total * 10 + c.value
    return total + n


def int_enum(m: int) -> int:
    k = Method(m)
    match k:
        case Method.YADIF:
            return 10
        case Method.BWDIF:
            return 20
        case _:
            return 30 + k


def optional_value(a: int, none: bool) -> int:
    x: Optional[int] = None if none else a
    return -1 if x is None else x


def optional_truth(a: int, none: bool) -> int:
    x = None if none else a
    return x if x else -1


def optional_arithmetic(a: int, none: bool) -> int:
    x = None if none else a
    return x + 1


def optional_object(n: int) -> int:
    p: Optional[Point] = Point(n) if n > 0 else None
    if p is not None:
        return p.x
    return -1


def none_attribute(n: int) -> int:
    p = Point(n) if n > 0 else None
    return p.x


def _maybe(n: int) -> int:
    if n > 0:
        return n


def maybe(n: int) -> int:
    r = _maybe(n)
    return 0 if r is None else r


def raises(n: int) -> int:
    if n < 0:
        raise ValueError("negative")
    if n == 0:
        raise ZeroDivisionError
    return 100 // n


def catches(a: int, b: int) -> int:
    try:
        if a < 0:
            raise ValueError("negative")
        return a // b
    except ArithmeticError:
        return -1


def handlers(n: int) -> int:
    try:
        return [1, 2, 3][n] * (10 // (n - 1))
    except (IndexError, KeyError):
        return -1
    except ZeroDivisionError:
        return -2


def lookup(n: int) -> int:
    try:
        return [1, 2][n]
    except LookupError:
        return -2


def else_finally(n: int) -> int:
    log = 0
    try:
        log = log * 10 + 1
        if n == 1:
            raise MyError("one")
        if n == 2:
            return log
    except MyError:
        log = log * 10 + 2
    else:
        log = log * 10 + 3
    finally:
        log = log * 10 + 4
    return log


def user_errors(n: int) -> int:
    try:
        if n == 1:
            raise DeepError("deep")
        if n == 2:
            raise MyError("mine")
        if n == 3:
            raise ValueError("plain")
        return 0
    except DeepError:
        return 1
    except MyError as e:
        return 2 + len(str(e))
    except ValueError:
        return 3


def reraise(n: int) -> int:
    try:
        try:
            return 10 // n
        except ZeroDivisionError:
            raise
    except ArithmeticError:
        return -5


def _deeper(n: int) -> int:
    if n == 1:
        raise IndexError("i")
    if n == 2:
        raise RuntimeError("r")
    if n == 3:
        assert n < 0, "neg"
    return n


def _deep(n: int) -> int:
    return _deeper(n)


def nested_try(n: int) -> int:
    try:
        try:
            try:
                return _deep(n)
            except KeyError:
                return 1
        except IndexError:
            return 2
    except RuntimeError:
        return 3


def uncaught(n: int) -> int:
    return _deep(n)


def loop_finally(n: int) -> int:
    total = 0
    for i in range(n):
        try:
            if i == 2:
                break
            if i == 1:
                continue
            total += 10
        finally:
            total += 1
    return total


def bare_except(n: int) -> int:
    try:
        return 10 // n
    except:
        return -1


def raise_in_handler(n: int) -> int:
    try:
        try:
            return 10 // n
        except ZeroDivisionError:
            raise ValueError("again")
    except ValueError as e:
        return len(str(e))


def not_implemented(n: int) -> int:
    try:
        if n:
            raise NotImplementedError("later")
        return 0
    except RuntimeError:
        return 7


def instance_check(n: int) -> int:
    try:
        if n == 1:
            raise MyError("x")
        raise KeyError("k")
    except Exception as e:
        return 1 if isinstance(e, ValueError) else 2


def raised_object(n: int) -> int:
    error = MyError("made")
    try:
        if n:
            raise error
        return 0
    except ValueError as caught:
        return 1 if caught is error else 2
