#!/usr/bin/env python3
"""Expected checksums of the Cipher baselines, computed without Cipher.

The message is the one tests/fixtures/bench/cipher-checksum.js builds: the top byte of each state of a
32-bit linear congruential generator (a = 1664525, c = 1013904223, seed 0). Adler-32 and CRC-32 come
from zlib; Fletcher-32 sums little-endian 16-bit words modulo 65535 (J. G. Fletcher, IEEE Trans.
Commun. 30(1), 1982) and is printed sum2 before sum1, big-endian. The output is oracle.txt.
"""
import zlib


def message(size):
    state, out = 0, bytearray(size)
    for i in range(size):
        state = (state * 1664525 + 1013904223) & 0xFFFFFFFF
        out[i] = state >> 24
    return bytes(out)


def fletcher32(data):
    if len(data) % 2:
        data += b"\0"
    a = b = 0
    for i in range(0, len(data), 2):
        a = (a + (data[i] | data[i + 1] << 8)) % 65535
        b = (b + a) % 65535
    return b << 16 | a


for size in (1024, 65536, 1048576):
    data = message(size)
    print(f"{size:>8} adler32 {zlib.adler32(data):08x} fletcher32 {fletcher32(data):08x} crc32 {zlib.crc32(data):08x}")
