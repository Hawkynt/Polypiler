// The control flow of Kotlin (#421): if and when as statements and values, while and do-while, for
// loops over ranges and progressions - up to the largest value, down, with a step - and labelled
// jumps out of nested loops. Written for this repository.
package control

fun sign(a: Int): Int = if (a > 0) 1 else if (a < 0) -1 else 0

fun classify(a: Int): Int = when (a) {
    0 -> 100
    1, 2, 3 -> 200
    in 10..19 -> 300
    !in -100..100 -> 400
    else -> 500
}

fun grade(score: Int): Int {
    val result = when {
        score >= 90 -> 4
        score >= 80 -> 3
        score >= 70 -> 2
        else -> 0
    }
    return result
}

fun sumTo(n: Int): Long {
    var total = 0L
    for (i in 1..n) {
        total += i
    }
    return total
}

fun countUntil(n: Int): Int {
    var count = 0
    for (i in 0 until n) {
        if (i % 3 == 0) continue
        count++
    }
    return count
}

fun countDown(n: Int): Int {
    var acc = 0
    for (i in n downTo 0 step 3) {
        acc = acc * 7 + i
    }
    return acc
}

fun stepped(from: Int, to: Int, step: Int): Int {
    var acc = 0
    for (i in from..to step step) {
        acc += i
    }
    return acc
}

fun nearMax(start: Int): Int {
    var count = 0
    for (i in start..Int.MAX_VALUE) {
        count++
    }
    return count
}

fun collatz(start: Long): Int {
    var n = start
    var steps = 0
    while (n != 1L && steps < 1000) {
        n = if (n % 2L == 0L) n / 2L else 3L * n + 1L
        steps++
    }
    return steps
}

fun digits(a: Int): Int {
    var n = a
    var count = 0
    do {
        n /= 10
        count++
    } while (n != 0)
    return count
}

fun firstPair(limit: Int, target: Int): Int {
    var found = -1
    outer@ for (i in 0 until limit) {
        for (j in 0 until limit) {
            if (i * j == target) {
                found = i * 1000 + j
                break@outer
            }
            if (j > i) continue@outer
        }
    }
    return found
}

fun branches(a: Int, b: Int): Int {
    val big = if (a > b) {
        val d = a - b
        d * 2
    } else {
        b - a
    }
    return big
}

fun gcd(a: Int, b: Int): Int {
    var x = a
    var y = b
    while (y != 0) {
        val t = x % y
        x = y
        y = t
    }
    return x
}

fun repeated(n: Int): Int {
    var total = 0
    repeat(n) {
        total += it * it
    }
    return total
}
