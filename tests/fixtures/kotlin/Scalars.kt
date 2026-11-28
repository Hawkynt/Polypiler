// The numbers of Kotlin with their own meaning (#424): Int and Long wrap, / truncates and throws on
// a zero divisor, the smallest value divided by -1 wraps, shifts mask the count, Byte and Short
// arithmetic is Int arithmetic, the unsigned types, the literals and the conversions. Written for
// this repository.
package scalars

const val LIMIT: Int = 0x7FFF_FFFF
const val MASK: Long = 0xFFFF_FFFFL
const val SEED: UInt = 2166136261u

fun add(a: Int, b: Int): Int = a + b

fun sub(a: Int, b: Int): Int = a - b

fun mul(a: Int, b: Int): Int = a * b

fun div(a: Int, b: Int): Int = a / b

fun rem(a: Int, b: Int): Int = a % b

fun divLong(a: Long, b: Long): Long = a / b

fun remLong(a: Long, b: Long): Long = a % b

fun divByConstant(a: Int): Int = a / 7 + a % 7

fun negate(a: Int): Int = -a

fun shifts(a: Int, n: Int): Int = (a shl n) xor (a shr n) xor (a ushr n)

fun shiftsLong(a: Long, n: Int): Long = (a shl n) + (a shr n) - (a ushr n)

fun bits(a: Int, b: Int): Int = (a and b) or (a xor b).inv()

fun bytes(a: Byte, b: Byte): Int = a + b * 3

fun shorts(a: Short, b: Short): Int = a * b - a / 3

fun narrow(a: Int): Byte = a.toByte()

fun narrowShort(a: Long): Short = a.toShort()

fun widen(a: Byte): Long = a.toLong() * 1000000007L

fun unsignedAdd(a: UInt, b: UInt): UInt = a + b * SEED

fun unsignedDiv(a: UInt, b: UInt): UInt = a / b + a % b

fun unsignedShift(a: UInt, n: Int): UInt = (a shr n) or (a shl (32 - n))

fun unsignedCompare(a: UInt, b: UInt): Boolean = a < b

fun unsignedLong(a: ULong, b: ULong): ULong = a * b + (a xor b)

fun ubytes(a: UByte, b: UByte): UInt = a + b

fun toUnsigned(a: Int): Long = a.toUInt().toLong()

fun fromUnsigned(a: UInt): Int = a.toInt()

fun saturate(x: Double): Int = x.toInt()

fun saturateLong(x: Double): Long = x.toLong()

fun saturateByte(x: Double): Byte = x.toInt().toByte()

fun floats(a: Double, b: Double): Double = a * b + a / b - a % b

fun singles(a: Float, b: Float): Float = a * b - b / a

fun mixed(a: Int, b: Double): Double = a + b

fun compare(a: Long, b: Int): Boolean = a > b && b != 0

fun masked(a: Long): Long = a and MASK or LIMIT.toLong()

fun literals(): Long = 0b1011 + 0x10L + 1_000 + 'A'.code

fun counts(a: Int): Int = a.countOneBits() * 100 + a.countLeadingZeroBits() + a.countTrailingZeroBits()

fun rotate(a: Int, n: Int): Int = a.rotateLeft(n) xor a.rotateRight(n + 1)

fun extremes(a: Int): Long = minOf(a, 10).toLong() + maxOf(a.toLong(), -5L)

fun magnitude(a: Int): Int = kotlin.math.abs(a)

fun root(x: Double): Double = kotlin.math.sqrt(x) + kotlin.math.floor(x) + kotlin.math.ceil(x)
