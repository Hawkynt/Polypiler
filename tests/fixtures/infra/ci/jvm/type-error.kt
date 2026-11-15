// Assigns a String to an Int: kotlinc rejects it with a type mismatch.
fun main() {
  val count: Int = "text"
  println(count)
}
