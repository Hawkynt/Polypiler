// The functions and properties of a Kotlin file (#422): top-level functions with block and
// expression bodies, recursion, default arguments, overloads, private helpers, constants, and a
// global the functions change. Written for this repository.
package functions

const val BASE: Int = 10

private var calls = 0

fun counted(a: Int): Int {
    calls = a
    calls++
    calls *= 3
    return calls
}

fun factorial(n: Int): Long = if (n <= 1) 1L else n * factorial(n - 1)

fun fibonacci(n: Int): Int {
    if (n < 2) return n
    return fibonacci(n - 1) + fibonacci(n - 2)
}

fun scaled(a: Int, factor: Int = 3): Int = a * factor + BASE

fun useDefault(a: Int): Int = scaled(a) - scaled(a, 2)

fun area(w: Int, h: Int): Int = w * h

fun area(r: Double): Double = r * r * 3.0

fun shapes(a: Int): Double = area(a, a + 1) + area(a.toDouble())

private fun helper(x: Int): Int = x * x - 1

fun useHelper(x: Int): Int = helper(x) + helper(x + 1)

fun ackermann(m: Int, n: Int): Int = when {
    m == 0 -> n + 1
    n == 0 -> ackermann(m - 1, 1)
    else -> ackermann(m - 1, ackermann(m, n - 1))
}

fun choose(flag: Boolean, a: Long, b: Long): Long = if (flag) a else b

fun digitsOf(n: Int): Int {
    var x = if (n < 0) -n else n
    var total = 0
    while (x > 0) {
        total += x % BASE
        x /= BASE
    }
    return total
}
