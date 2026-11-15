"""One function for every operator and built-in on int, float and bool. Written for this repository."""


def add(a: int, b: int) -> int:
    return a + b


def sub(a: int, b: int) -> int:
    return a - b


def mul(a: int, b: int) -> int:
    return a * b


def true_div(a: int, b: int) -> float:
    return a / b


def floor_div(a: int, b: int) -> int:
    return a // b


def mod(a: int, b: int) -> int:
    return a % b


def power(a: int, b: int) -> int:
    return a ** b


def power_negative(a: int) -> float:
    return a ** -2


def shift_left(a: int, b: int) -> int:
    return a << b


def shift_right(a: int, b: int) -> int:
    return a >> b


def shift_right_by_3(a: int) -> int:
    return a >> 3


def bit_and(a: int, b: int) -> int:
    return a & b


def bit_or(a: int, b: int) -> int:
    return a | b


def bit_xor(a: int, b: int) -> int:
    return a ^ b


def invert(a: int) -> int:
    return ~a


def negate(a: int) -> int:
    return -a


def plus(a: int) -> int:
    return +a


def fadd(a: float, b: float) -> float:
    return a + b


def fsub(a: float, b: float) -> float:
    return a - b


def fmul(a: float, b: float) -> float:
    return a * b


def fdiv(a: float, b: float) -> float:
    return a / b


def ffloor_div(a: float, b: float) -> float:
    return a // b


def fmod(a: float, b: float) -> float:
    return a % b


def fpow(a: float, b: float) -> float:
    return a ** b


def fnegate(a: float) -> float:
    return -a


def mixed_add(a: int, b: float) -> float:
    return a + b


def mixed_floor_div(a: float, b: int) -> float:
    return a // b


def mixed_mod(a: int, b: float) -> float:
    return a % b


def bool_add(a: bool, b: bool) -> int:
    return a + b


def bool_and(a: bool, b: bool) -> bool:
    return a & b


def bool_or(a: bool, b: bool) -> bool:
    return a | b


def bool_xor(a: bool, b: bool) -> bool:
    return a ^ b


def bool_invert(a: bool) -> int:
    return ~a


def less(a: int, b: int) -> bool:
    return a < b


def less_equal(a: float, b: float) -> bool:
    return a <= b


def equal_int_float(a: int, b: float) -> bool:
    return a == b


def less_int_float(a: int, b: float) -> bool:
    return a < b


def greater_float_int(a: float, b: int) -> bool:
    return a > b


def not_equal_float_int(a: float, b: int) -> bool:
    return a != b


def chained(a: int, b: int, c: int) -> bool:
    return a < b <= c


def logical_not(a: int) -> bool:
    return not a


def float_truth(a: float) -> bool:
    return not a


def first_true(a: int, b: int) -> int:
    return a or b


def first_false(a: int, b: int) -> int:
    return a and b


def both(a: bool, b: bool) -> bool:
    return a and b


def either(a: float, b: float, c: float) -> float:
    return a or b or c


def choose(c: int, a: float, b: float) -> float:
    return a if c else b


def to_int(a: float) -> int:
    return int(a)


def to_float(a: int) -> float:
    return float(a)


def to_bool(a: float) -> bool:
    return bool(a)


def absolute(a: int) -> int:
    return abs(a)


def fabsolute(a: float) -> float:
    return abs(a)


def rounded(a: float) -> int:
    return round(a)


def pow_builtin(a: float, b: int) -> float:
    return pow(a, b)


def quotient_and_remainder(a: int, b: int) -> int:
    q, r = divmod(a, b)
    return q * 1000 + r


def literals() -> int:
    return 0xFFFF_FFFF + 0o17 + 0b101 + 1_000


def hash_step(h: int, c: int) -> int:
    return ((h * 33) ^ c) & 0xFFFFFFFF


def masked_sum(a: int, b: int) -> int:
    return (a + b) & 0xFFFFFFFF


def precedence(a: int) -> int:
    return -a ** 2
