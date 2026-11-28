// Small kernels of the first verified subset (#414): checksums and hashes over byte arrays, a table
// driven CRC, an integer square root, a pseudo-random generator and a separable image kernel. Written
// for this repository; CRC-32 uses the reflected polynomial of IEEE 802.3, FNV-1a the offset basis and
// prime of Fowler, Noll and Vo, xorshift the shifts 13, 17 and 5 of Marsaglia.
package kernels

private var crcTable: IntArray = buildTable()

private fun buildTable(): IntArray {
    val table = IntArray(256)
    for (n in 0 until 256) {
        var c = n
        for (k in 0 until 8) {
            c = if (c and 1 != 0) (c ushr 1) xor 0xEDB88320.toInt() else c ushr 1
        }
        table[n] = c
    }
    return table
}

fun crc32(data: ByteArray): Int {
    var crc = -1
    for (b in data) {
        crc = crcTable[(crc xor b.toInt()) and 0xFF] xor (crc ushr 8)
    }
    return crc.inv()
}

fun fnv1a(data: ByteArray): UInt {
    var hash = 2166136261u
    for (b in data) {
        hash = (hash xor b.toUByte().toUInt()) * 16777619u
    }
    return hash
}

fun isqrt(n: Long): Long {
    if (n < 2L) return n
    var x = n
    var y = (x + 1L) / 2L
    while (y < x) {
        x = y
        y = (x + n / x) / 2L
    }
    return x
}

fun xorshift(seed: Int, rounds: Int): Int {
    var x = seed
    repeat(rounds) {
        x = x xor (x shl 13)
        x = x xor (x ushr 17)
        x = x xor (x shl 5)
    }
    return x
}

fun deinterlace(top: IntArray, bottom: IntArray, out: IntArray) {
    for (i in out.indices) {
        val a = top[i % top.size]
        val b = bottom[i % bottom.size]
        out[i] = (a + b + 1) shr 1
    }
}

fun power(base: Long, exponent: Int): Long {
    var result = 1L
    var b = base
    var e = exponent
    while (e > 0) {
        if (e and 1 == 1) result *= b
        b *= b
        e = e shr 1
    }
    return result
}
