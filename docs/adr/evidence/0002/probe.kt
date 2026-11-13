fun t(f: () -> Any?): String = try { f().toString() } catch (e: Throwable) { e::class.simpleName!! }
fun main() {
  val max = Int.MAX_VALUE; val min = Int.MIN_VALUE; val one = 1; val zero = 0; val m1 = -1
  println("add: ${max + one}  sub: ${min - one}  mul: ${65536 * (one * 65536)}  neg: ${-min}")
  println("1 shl 32: ${one shl 32}  1 shl 33: ${one shl 33}  1 shl -1: ${one shl m1}  -8 shr 33: ${-8 shr 33}  -8 ushr 33: ${-8 ushr 33}")
  println("7 / 0: ${t { 7 / zero }}  7 % 0: ${t { 7 % zero }}")
  println("MIN / -1: ${min / m1}  MIN % -1: ${min % m1}")
  println("-7 / 3: ${-7 / (one * 3)}  -7 % 3: ${-7 % (one * 3)}  7 % -3: ${7 % (m1 * 3)}  (-7).mod(3): ${(-7).mod(3)}  (-7).floorDiv(3): ${(-7).floorDiv(3)}")
  println("3e9.toInt(): ${(3e9 * one).toInt()}  (-3e9).toInt(): ${(-3e9 * one).toInt()}  NaN.toInt(): ${(Double.NaN * one).toInt()}  Inf.toInt(): ${(one / 0.0).toInt()}  300.toByte(): ${(300 * one).toByte()}  2.7.toInt(): ${(2.7 * one).toInt()}  (-2.7).toInt(): ${(-2.7 * one).toInt()}")
  println("UInt.MAX + 1u: ${UInt.MAX_VALUE + 1u}  (-1.0).toUInt(): ${(-1.0 * one).toUInt()}  3e10.toUInt(): ${(3e10 * one).toUInt()}  1.0 / 0: ${1.0 / zero}")
}
