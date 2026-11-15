"""Hashes and generators written the idiomatic Python way: an int masked to emulate a register. Written for this repository (O0062)."""


def fnv1a64(value: int) -> int:
    """FNV-1a, 64 bits, over the 8 bytes of a value, the lowest first."""
    rest = value & 0xFFFFFFFFFFFFFFFF
    h = 0xCBF29CE484222325
    for _ in range(8):
        h ^= rest & 0xFF
        h = (h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
        rest >>= 8
    return h


def fnv1a32(value: int) -> int:
    """FNV-1a, 32 bits, over the 4 bytes of a value, the lowest first."""
    rest = value & 0xFFFFFFFF
    h = 0x811C9DC5
    for _ in range(4):
        h ^= rest & 0xFF
        h = (h * 16777619) & 0xFFFFFFFF
        rest >>= 8
    return h


def splitmix64(seed: int) -> int:
    """One output of SplitMix64 for a state."""
    z = (seed + 0x9E3779B97F4A7C15) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & 0xFFFFFFFFFFFFFFFF
    return z ^ (z >> 31)


def xorshift64(state: int) -> int:
    """One step of Marsaglia's xorshift64 (13, 7, 17)."""
    x = state & 0xFFFFFFFFFFFFFFFF
    x ^= (x << 13) & 0xFFFFFFFFFFFFFFFF
    x ^= x >> 7
    x ^= (x << 17) & 0xFFFFFFFFFFFFFFFF
    return x


def xorshift32_sum(seed: int) -> int:
    """Sixteen steps of xorshift32 (13, 17, 5), their outputs summed modulo 2**32."""
    x = (seed & 0xFFFFFFFF) | 1
    total = 0
    for _ in range(16):
        x ^= (x << 13) & 0xFFFFFFFF
        x ^= x >> 17
        x ^= (x << 5) & 0xFFFFFFFF
        total = (total + x) % 4294967296
    return total


def checksum() -> int:
    """A checksum of the numbers below 1000, which needs no parameter."""
    total = 0
    for i in range(1000):
        total = (total * 31 + i) & 0xFFFF
    return total


def unmasked_product(a: int, b: int) -> int:
    """A product used unmasked as well as masked: it stays exact."""
    product = a * b
    low = product & 0xFFFFFFFF
    return low + product


def quotient_low(a: int, b: int) -> int:
    """A floor division inside a mask: the low bits of a quotient depend on the high bits of the dividend."""
    return (a // b) & 0xFFFFFFFF


def shifted_low(a: int) -> int:
    """A right shift inside a mask pulls in bits above the mask."""
    return (a >> 3) & 0xFF


def power(n: int) -> int:
    """2 ** n, which exceeds 64 bits from n = 64 on."""
    return 2 ** n
