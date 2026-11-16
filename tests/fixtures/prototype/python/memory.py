"""Lists, tuples, slices, comprehensions, bytes, struct layouts and strings, one function per construct. Written for this repository."""

import struct


def index(i: int) -> int:
    a = [1, 2, 3, 4, 5]
    return a[i]


def store(i: int, v: int) -> int:
    a = [10, 20, 30]
    a[i] = v
    return a[0] * 100 + a[1] * 10 + a[2]


def augmented_store(i: int) -> int:
    a = [1, 2, 3]
    a[i] += 10
    a[-1] *= 2
    return a[0] + a[1] + a[2]


def slice_sum(start: int, stop: int, step: int) -> int:
    a = [1, 2, 3, 4, 5]
    total = 0
    for x in a[start:stop:step]:
        total = total * 10 + x
    return total


def slice_open(n: int) -> int:
    a = [1, 2, 3, 4, 5]
    return len(a[n:]) * 100 + len(a[:n]) * 10 + len(a[::-1][:n])


def repeat(n: int) -> int:
    return len([7] * n) + len(n * [0])


def aliased_rows(alias: bool) -> int:
    m = [[0] * 2] * 2 if alias else [[0] * 2 for _ in range(2)]
    m[0][0] = 1
    return m[1][0]


def append_pop(n: int) -> int:
    a: list[int] = []
    for i in range(n):
        a.append(i * i)
    total = 0
    while a:
        total = total * 2 + a.pop()
    return total


def pop_empty(n: int) -> int:
    a = [n]
    a.pop()
    return a.pop()


def extend_self(n: int) -> int:
    a = [1, 2]
    for _ in range(n):
        a.extend(a)
    return len(a)


def concat_copy(n: int) -> int:
    a = [1, 2, 3]
    b = a + [n]
    c = a.copy()
    d = a[:]
    c.append(9)
    d[0] = 7
    return len(a) * 1000 + len(b) * 100 + len(c) * 10 + a[0] + d[0]


def contains(x: int) -> bool:
    return x in [3, 1, 4, 1, 5] and x not in [9, 2]


def list_equal(a: int, b: int) -> bool:
    return [a, b] == [1, 2] or [[a], [b]] == [[b], [a]]


def iterate_growing(n: int) -> int:
    a = [1]
    count = 0
    for x in a:
        count += x
        if len(a) < n:
            a.append(x + 1)
    return count


def enumerated(start: int) -> int:
    total = 0
    for i, x in enumerate([5, 6, 7], start):
        total += i * x
    return total


def zipped(n: int) -> int:
    total = 0
    for x, y in zip([1, 2, 3, 4], [10, 20, n]):
        total += x * y
    return total


def reversed_loop(n: int) -> int:
    a = [1, 2, 3, 4]
    total = 0
    for x in reversed(a):
        total = total * 10 + x
        if x == n:
            a.pop()
    return total


def sorted_values(reverse: bool) -> int:
    total = 0
    for x in sorted([3, 1, 2, 5, 4], reverse=reverse):
        total = total * 10 + x
    return total


def sorted_by_key(n: int) -> int:
    total = 0
    for x in sorted([n, -5, 3, -2], key=lambda v: v * v):
        total = total * 100 + x + 50
    return total


def folds(n: int) -> int:
    a = [n, 3, -1, 8]
    return sum(a) * 1000 + max(a) * 100 + min(a) * 10 + int(any(a)) + int(all(a))


def min_empty(n: int) -> int:
    return min([x for x in range(n)])


def max_args(a: int, b: int) -> int:
    return max(a, b, 3) - min(a, b)


def sum_start(n: int) -> int:
    return sum([1, 2, 3], n) + sum(x * x for x in range(n))


def comprehension(n: int) -> int:
    x = 99
    r = [x * x for x in range(n) if x % 2]
    r.append(x)
    return len(r) * 1000 + r[0] + r[-1]


def nested_comprehension(n: int) -> int:
    pairs = [i * 10 + j for i in range(n) for j in range(i) if (i + j) % 2]
    return sum(pairs) + len(pairs)


def generator_any(n: int) -> bool:
    return any(x > n for x in [1, 5, 9]) and all(x < 20 for x in [1, 5, 9])


def list_of(n: int) -> int:
    a = list(range(n))
    b = list(reversed(a))
    return sum(a) * 100 + (b[0] if b else -1)


def bytes_index(i: int) -> int:
    b = b"\x78\x56\x34\x12"
    return b[i]


def bytes_slice(n: int) -> int:
    b = b"\x01\x02\x03\x04\x05"
    return len(b[n:]) * 10 + b[1:][0]


def bytearray_store(v: int) -> int:
    b = bytearray(2)
    b[0] = v
    return b[0] + len(b)


def bytes_of_list(v: int) -> int:
    b = bytes([1, v, 3])
    return b[1] + len(b)


def bytes_ops(n: int) -> int:
    a = b"ab" + bytes(n)
    c = bytearray(a)
    c[0] = 0
    return len(a) * 100 + a[0] + c[0] + int(a == c) + int(b"\x00" * 2 == bytes(2))


def from_bytes(little: bool) -> int:
    b = b"\x78\x56\x34\x12"
    return int.from_bytes(b, "little") if little else int.from_bytes(b, "big")


def from_bytes_signed(n: int) -> int:
    b = bytes([255] * n)
    return int.from_bytes(b, "big", signed=True) + int.from_bytes(b[:1], "little")


def to_bytes(n: int) -> int:
    b = n.to_bytes(2, "little", signed=True)
    return b[0] * 1000 + b[1]


def to_bytes_unsigned(n: int) -> int:
    b = n.to_bytes(4, "big")
    return b[0] + b[3]


def struct_unpack(n: int) -> int:
    b = struct.pack("<HbI", n, -2, 70000)
    a, c, d = struct.unpack("<HbI", b)
    return a + c * 10 + d + struct.calcsize("<HbI")


def struct_big(n: int) -> int:
    b = struct.pack(">hQ", -n, 2 ** 40)
    return struct.unpack(">hQ", b)[0] + struct.unpack_from(">Q", b, 2)[0] // 1000


def struct_from(off: int) -> int:
    b = b"\x78\x56\x34\x12"
    return struct.unpack_from("<H", b, off)[0]


def struct_floats(x: float) -> float:
    b = struct.pack("<fd?", x, x * 2, x > 0)
    f, d, flag = struct.unpack("<fd?", b)
    return f + d + (1.0 if flag else 0.0)


def struct_short(n: int) -> int:
    return struct.unpack("<I", bytes(n))[0]


def struct_range(v: int) -> int:
    return len(struct.pack("<B", v))


def adler32(n: int) -> int:
    data = b"Wikipedia" * n
    a = 1
    b = 0
    for x in data:
        a = (a + x) % 65521
        b = (b + a) % 65521
    return (b << 16) | a


def tuples(a: int, b: int) -> int:
    t = (a, b)
    x, y = t
    u = t + (3,)
    return x * 100 + y * 10 + u[2] + u[-1] * 0 + int(t == (a, b))


def swap_return(a: int, b: int) -> int:
    def pair(x: int, y: int) -> tuple[int, int]:
        return y, x
    p, q = pair(a, b)
    return p * 10 + q


def divmod_tuple(a: int, b: int) -> int:
    t = divmod(a, b)
    return t[0] * 100 + t[1]


def strings(n: int) -> int:
    s = "héllo" + "😀" * n
    t = s[1:3]
    return len(s) * 1000 + len(t) * 100 + ord(s[1]) % 100 + int(s[-1] == "😀")


def string_iteration(n: int) -> int:
    total = 0
    for c in "a😀b" * n:
        total = total * 2 + (1 if c == "😀" else 0)
    return total


def encode(n: int) -> int:
    b = ("é😀" * n).encode("utf-8")
    return len(b) * 100 + (b[0] if b else 0) % 100


def encode_surrogate(n: int) -> int:
    return len((chr(0xD800) * n).encode())


def characters(n: int) -> int:
    return ord(chr(n)) + len(chr(n))


def string_in(n: int) -> bool:
    return "bc" * n in "abcbcd"


def float_list(x: float) -> float:
    a = [x, 1.5, -2.0]
    a[0] += 0.25
    return max(a) - min(a) + a[0]
