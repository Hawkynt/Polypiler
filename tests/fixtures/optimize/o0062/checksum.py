"""A checksum whose every int O0062 proves to fit a fixed width: no arbitrary-precision integer is left. Written for this repository (O0062)."""


def checksum() -> int:
    total = 0
    for i in range(1000):
        total = (total * 31 + i) & 0xFFFF
    return total
