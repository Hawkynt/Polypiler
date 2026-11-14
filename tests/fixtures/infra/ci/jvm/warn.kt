// An unchecked cast: only a warning, which -Werror turns into a failed build.
fun main(args: Array<String>) {
  val items: Any = args.toList()
  val names = items as List<String>
  println(names.size)
}
