// The primitive arrays and strings of Kotlin (#423): new arrays, literals, indexing that traps out
// of range, the size, loops over the elements and the indices, filled arrays, byte arithmetic, and
// strings measured and read code unit by code unit. Written for this repository.
package arrays

private val SQUARES = intArrayOf(0, 1, 4, 9, 16, 25, 36, 49)

fun sum(values: IntArray): Long {
    var total = 0L
    for (v in values) {
        total += v
    }
    return total
}

fun at(values: IntArray, index: Int): Int = values[index]

fun reverse(values: IntArray) {
    var i = 0
    var j = values.size - 1
    while (i < j) {
        val t = values[i]
        values[i] = values[j]
        values[j] = t
        i++
        j--
    }
}

fun prefix(values: LongArray) {
    for (i in 1 until values.size) {
        values[i] += values[i - 1]
    }
}

fun checksum(data: ByteArray): Int {
    var a = 1
    var b = 0
    for (i in data.indices) {
        a = (a + (data[i].toInt() and 0xFF)) % 65521
        b = (b + a) % 65521
    }
    return (b shl 16) or a
}

fun square(n: Int): Int = SQUARES[n]

fun table(n: Int): Int {
    val cells = IntArray(n) { i -> i * i + 1 }
    var acc = 0
    for (c in cells) acc = acc * 31 + c
    return acc
}

fun scale(values: DoubleArray, factor: Double) {
    for (i in values.indices) {
        values[i] = values[i] * factor
    }
}

fun clear(values: IntArray, value: Int): Int {
    values.fill(value)
    return values.size
}

fun blur(pixels: IntArray, width: Int) {
    val copy = IntArray(pixels.size)
    for (i in pixels.indices) copy[i] = pixels[i]
    for (i in 1 until width - 1) {
        pixels[i] = (copy[i - 1] + 2 * copy[i] + copy[i + 1]) / 4
    }
}

fun words(n: Int): Int {
    val text = "the quick brown fox"
    var spaces = 0
    for (c in text) {
        if (c == ' ') spaces++
    }
    return spaces * 100 + text.length + text[n].code
}
