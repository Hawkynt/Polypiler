// 32-bit Int arithmetic, which Kotlin/JVM shares with Java: addition wraps, the count of a shift is
// masked with 31. Without arguments it prints Int.MAX_VALUE + 1 and -1 ushr 28; each argument
// "add:<n>" prints Int.MAX_VALUE + n, each "ushr:<n>" prints -1 ushr n.
fun main(args: Array<String>) {
  if (args.isEmpty()) {
    println(Int.MAX_VALUE + 1)
    println(-1 ushr 28)
    return
  }

  for (argument in args) {
    val n = argument.substring(argument.indexOf(':') + 1).toInt()
    println(if (argument.startsWith("add:")) Int.MAX_VALUE + n else -1 ushr n)
  }
}
