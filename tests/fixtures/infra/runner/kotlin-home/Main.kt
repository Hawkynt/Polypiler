import java.io.File
import java.io.IOException

// Greets, then tries to read the file named by its first argument and to create the file named by
// its second, and says how each attempt ended.
fun main(args: Array<String>) {
  println("hello from kotlin")
  println(try { File(args[0]).readText(); "read: leaked" } catch (e: IOException) { "read: refused" })
  println(try { File(args[1]).writeText("x"); "write: written" } catch (e: IOException) { "write: " + e.message })
}
