// Adler-32 of the ASCII bytes "abc", or of no bytes with the argument --empty, as RFC 1950 §8.2
// computes it: prints 38600999, or 1.
fun main(args: Array<String>) {
  val data = if (args.firstOrNull() == "--empty") ByteArray(0) else byteArrayOf(0x61, 0x62, 0x63)
  var a = 1L
  var b = 0L
  for (value in data) {
    a = (a + (value.toInt() and 0xff)) % 65521
    b = (b + a) % 65521
  }
  println((b shl 16) or a)
}
