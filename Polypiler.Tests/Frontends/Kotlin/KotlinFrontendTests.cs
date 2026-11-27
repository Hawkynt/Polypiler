using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Kotlin;
using Polypiler.Core.Frontends.Kotlin.Syntax;
using Polypiler.Core.IR;
using Polypiler.Kotlin.Testing;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Kotlin;

/// <summary>
/// The Kotlin frontend: every fixture of <c>tests/fixtures/kotlin</c> imported and interpreted call by
/// call as the pinned <c>kotlinc</c> recorded it, and every construct outside the subset refused with
/// the code of its family at its place.
/// </summary>
[TestFixture]
public sealed class KotlinFrontendTests {

  private static IEnumerable<string> Fixtures => KotlinFixture.Names;

  [TestCaseSource(nameof(Fixtures))]
  public void Fixture_InterpretsAsKotlincRecordedIt(string fixture) {
    var module = KotlinFixture.Import(fixture);
    var calls = KotlinFixture.Cases(module);
    var recorded = KotlinFixture.RecordedLines(fixture);
    var lines = calls.Select(call => KotlinFixture.Interpreted(module, call)).ToArray();
    var mismatches = lines.Zip(recorded).Where(pair => pair.First != pair.Second).Select(pair => $"interpreter {pair.First}, kotlinc {pair.Second}").ToList();
    Assert.Multiple(() => {
      Assert.That(lines, Has.Length.EqualTo(recorded.Length));
      Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {lines.Length} calls differ");
    });
  }

  private static IrModule Import(string source) {
    var result = KotlinFrontend.Import("Snippet.kt", source);
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  private static string Run(string source, string function, params object[] arguments)
    => PrototypeFixture.InterpretedOutcome(Import(source), new PrototypeCall(function, arguments));

  [TestCase("fun f(a: Int, b: Int): Int = a / b", -2147483648, -1, "-2147483648")]
  [TestCase("fun f(a: Int, b: Int): Int = a % b", -2147483648, -1, "0")]
  [TestCase("fun f(a: Int, b: Int): Int = a / b", 7, 0, "trap: divide by zero")]
  [TestCase("fun f(a: Int, b: Int): Int = a shl b", 1, 33, "2")]
  [TestCase("fun f(a: Int, b: Int): Int = (a.toByte() + b.toByte()).toInt()", 127, 1, "128")]
  [TestCase("fun f(a: Int, b: Int): Int = if (a in 1..b) 1 else 0", 3, 3, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = if (a !in 1 until b) 1 else 0", 3, 3, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = maxOf(a.toShort(), b.toShort()).toInt()", -3, 5, "5")]
  [TestCase("fun f(a: Int, b: Int): Int = a.coerceIn(0, b)", -3, 5, "0")]
  [TestCase("fun f(a: Int, b: Int): Int = a.coerceAtLeast(b) + a.coerceAtMost(b)", 2, 5, "7")]
  [TestCase("fun f(a: Int, b: Int): Int = (a.toUByte() and b.toUByte()).toInt() + a.toUShort().inv().toInt()", 300, 7, "65239")]
  [TestCase("fun f(a: Int, b: Int): Int = a.shl(b) + a.and(b)", 3, 1, "7")]
  [TestCase("fun f(a: Int, b: Int): Int { var x = a; x -= b; x *= 2; x /= 3; x %= 5; return x }", 20, 2, "2")]
  [TestCase("fun f(a: Int, b: Int): Int { val c = 'a' + a; return c - 'a' + (c.code - b) }", 2, 90, "11")]
  [TestCase("fun f(a: Int, b: Int): Int = Char(a).code + b.toChar().code", 65, 66, "131")]
  [TestCase("fun f(a: Int, b: Int): Int = \"x$a${a + b}\".length", 5, 10, "4")]
  [TestCase("fun f(a: Int, b: Int): Int = (\"ab\" + a + true + 'c').length", 12, 0, "9")]
  [TestCase("fun f(a: Int, b: Int): Int = if (\"abc\" < \"abd\" && \"a\" != \"b\") a else b", 1, 2, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = if (\"hello\".startsWith(\"he\") && \"hello\".endsWith(\"lo\") && \"hello\".contains(\"ll\")) a else b", 1, 2, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = \"hello\".substring(a, b).length + \"hello\".substring(a).length", 1, 3, "6")]
  [TestCase("fun f(a: Int, b: Int): Int = if (\"a\".equals(\"a\")) \"b\".compareTo(\"a\") else 0", 0, 0, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = when { a > b -> 1; a < b -> -1; else -> 0 }", 1, 2, "-1")]
  [TestCase("fun f(a: Int, b: Int): Int = when (a) { b -> 10; in 0..5 -> 20; else -> 30 }", 3, 3, "10")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; for (i in a downTo b step 2) t += i; return t }", 10, 1, "30")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; for (i in a..<b step 3) t += i; return t }", 0, 10, "18")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; for (c in 'a'..'e') t += c.code; return t + a + b }", 0, 0, "495")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0L; for (i in a.toLong()..b.toLong()) t += i; return t.toInt() }", 1, 4, "10")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0u; for (i in a.toUInt()..b.toUInt() step 2) t += i; return t.toInt() }", 1, 5, "9")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; while (true) { n++; if (n > a) break }; return n + b }", 3, 1, "5")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; outer@ while (n < 100) { n++; var m = 0; while (m < 5) { m++; if (m == a) continue@outer; if (n == b) break@outer } }; return n }", 3, 7, "7")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; loop@ for (i in 0 until a) { when (i) { 1 -> continue@loop; 5 -> break@loop; else -> n += i } }; return n + b }", 10, 0, "9")]
  [TestCase("fun f(a: Int, b: Int): Int { val x = when (a) { 1 -> { val y = b * 2; y + 1 } else -> b }; return x }", 1, 4, "9")]
  [TestCase("fun f(a: Int, b: Int): Int { val x: Long; x = if (a > b) { a.toLong() } else { b.toLong() }; return x.toInt() }", 1, 4, "4")]
  [TestCase("fun f(a: Int, b: Int): Int { require(a > 0) { \"positive\" }; check(b >= 0); return a + b }", 1, 0, "1")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; repeat(a) { i -> t += i * b }; return t }", 4, 2, "12")]
  [TestCase("fun f(a: Int, b: Int): Int { val x = IntArray(a) { it * b }; return x[a - 1] + x.lastIndex }", 4, 3, "12")]
  [TestCase("fun f(a: Int, b: Int): Int { val c = charArrayOf('a', 'b'); return c[1].code + a + b }", 0, 0, "98")]
  [TestCase("fun f(a: Int, b: Int): Int { val s = \"abc\"; return s[a].code + s.lastIndex + b }", 1, 0, "100")]
  [TestCase("fun f(a: Int, b: Int): Int = (Double.fromBits(a.toLong()).toRawBits() + Float.fromBits(b).toRawBits()).toInt()", 5, 6, "11")]
  [TestCase("fun f(a: Int, b: Int): Int = if (a.toDouble().isNaN() || Double.NaN.isFinite() || (1.0 / b).isInfinite()) 1 else 0", 1, 0, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = (Int.MAX_VALUE + a) + Long.SIZE_BITS + Int.SIZE_BYTES + UInt.MIN_VALUE.toInt() + Byte.MAX_VALUE + b", 1, 0, "-2147483453")]
  [TestCase("fun f(a: Int, b: Int): Int = if (Double.POSITIVE_INFINITY > Double.MAX_VALUE && Float.NEGATIVE_INFINITY < -Float.MAX_VALUE && Double.MIN_VALUE > 0.0) a else b", 1, 2, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = (-a.toLong()).toInt() + (-1.5).toInt() + (+b)", 3, 4, "0")]
  [TestCase("fun f(a: Int, b: Int): Int = kotlin.math.round(2.5).toInt() + kotlin.math.truncate(-2.7).toInt() + Math.abs(-3) + kotlin.math.abs(-1.5f).toInt() + (-a).absoluteValue + b", 1, 0, "5")]
  [TestCase("fun f(a: Int, b: Int): Int = a.countOneBits() + a.toLong().rotateRight(b).toInt()", 3, 1, "3")]
  [TestCase("fun f(a: Int, b: Int): Int { val x = !(a == b) xor (a > b) and true or false; return if (x) 1 else 0 }", 1, 2, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = if (if (a > 0) a > b else b > a) 1 else 2", 3, 2, "1")]
  [TestCase("fun f(a: Int, b: Int): Int = (a.toFloat() * b - 0.5f).toInt() + 1e2.toInt() + 0x_FF_u.toInt() + 0b1L.toInt()", 3, 2, "361")]
  public void Snippet_HasKotlinsMeaning(string source, int a, int b, string expected)
    => Assert.That(Run(source, "f", a, b), Is.EqualTo(expected));

  [TestCase("fun f(a: Int, b: Int): Int { val x = if (a > 0) 1 else if (a < 0) { val t = 2; t } else 3; return x + b }", -4, 0, "2")]
  [TestCase("fun f(a: Int, b: Int): Int { val x = if (a > 0) throw IllegalStateException(\"x\") else { val t = b; t }; return x }", 0, 5, "5")]
  [TestCase("fun f(a: Int, b: Int): Int { val x = when (a) { 1 -> { val t = b; t * 2 } 2 -> throw IllegalStateException(); else -> when (b) { 0 -> 7 else -> { val u = 1; u } } }; return x }", 3, 0, "7")]
  [TestCase("fun f(a: Int, b: Int): Int { var x = 0; x = when (a) { 1 -> { x += 1; 5 } else -> { b } }; return x }", 1, 9, "5")]
  [TestCase("fun f(a: Int, b: Int): Int { when (a) { 1 -> return 10; 2 -> return 20; else -> return b } }", 2, 0, "20")]
  [TestCase("fun f(a: Int, b: Int): Int { if (a > b) return a else return b }", 2, 5, "5")]
  [TestCase("fun f(a: Int, b: Int): Int { while (true) { if (a > 0) return a } }", 2, 0, "2")]
  [TestCase("fun f(a: Int, b: Int): Int { do { if (b == 0) return a } while (true) }", 3, 0, "3")]
  [TestCase("fun f(a: Int, b: Int): Int { if (a > 0) {} else { return 1 }; return when { b > 0 -> b else -> { val z = a; z } } }", 2, 0, "2")]
  [TestCase("fun f(a: Int, b: Int): Int { val c = a.toChar(); return when (c) { 'a' -> 1; 'b', 'c' -> 2; else -> 3 } + b }", 98, 0, "2")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; when (a.toLong()) { 1L -> t = 1; 5L -> { t = 2; if (b > 0) t = 3 } else -> {} }; return t }", 5, 1, "3")]
  [TestCase("fun f(a: Int, b: Int): Int { var t = 0; for (i in 0 until a) { when (i) { 2 -> break; else -> t += i } }; return t + b }", 9, 0, "1")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; while (n < a) n++; for (i in 0..b) ; return n }", 4, 2, "4")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; do n++ while (n < a); return n + b }", 4, 2, "6")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; for (c in \"abc\") n += c.code; return n + a + b }", 0, 0, "294")]
  [TestCase("fun f(a: Int, b: Int): Int { val arr = intArrayOf(a, b); var n = 0; for (x in arr) n = n * 10 + x; return n }", 3, 4, "34")]
  [TestCase("fun f(a: Int, b: Int): Int { val arr = intArrayOf(a, b); arr[1] += 5; arr[f2(arr)] *= 3; return arr[0] + arr[1] }\nfun f2(x: IntArray): Int = x.size - 2", 1, 2, "10")]
  [TestCase("fun f(a: Int, b: Int): Int { val arr = intArrayOf(a, b); arr[0]++; ++arr[1]; return arr[0] * arr[1] }", 1, 2, "6")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = a; n++; ++n; n--; --n; return n + b }", 1, 2, "3")]
  [TestCase("fun f(a: Int, b: Int): Int { while (\"abc\".substring(a).length > b) { return 1 }; return 0 }", 0, 1, "1")]
  [TestCase("fun f(a: Int, b: Int): Int { var n = 0; while (IntArray(a) { it }.size > n) n++; return n + b }", 3, 0, "3")]
  public void MoreSnippets_HaveKotlinsMeaning(string source, int a, int b, string expected)
    => Assert.That(Run(source, "f", a, b), Is.EqualTo(expected));

  [Test]
  public void Output_AndUnitFunctions_AreStatements() {
    var module = Import("""
      val f = 1
      fun f(): Unit = println("x")
      fun g(a: Int) {
          println()
          println(a)
          if (a > 0) {} else {}
          if (a > 0) println("p")
          when (a) { 1 -> println("one") }
          return println(true)
      }
      fun h(a: Int): Int = if (a > 0) a else throw IllegalStateException()
      fun k(a: Int): Int { return throw IllegalStateException() }
      fun m(a: Int) { a; 1; "x"; f }
      """);
    var writes = module.Functions.SelectMany(function => IrClosures.Within(function.Body)).OfType<IrWriteLine>().Count();
    Assert.Multiple(() => {
      Assert.That(writes, Is.EqualTo(6));
      Assert.That(module.Functions.Select(function => function.Name), Has.Member("f_2").And.Member("g"));
      Assert.That(module.Globals.Concat<object>(module.Constants).Count(), Is.EqualTo(1));
    });
  }

  [Test]
  public void Properties_AreConstantsTablesAndGlobals() {
    var module = Import("""
      const val A: Int = 3
      const val B = true
      val COMPUTED = intArrayOf(A, square())
      val TABLE = intArrayOf(1, 2, 3)
      val WRITTEN = intArrayOf(4, 5)
      val NAME = "x"
      var counter = 0
      val square = A * A
      fun square(): Int = 2
      fun f(i: Int): Int {
          WRITTEN[0] = i
          counter += TABLE[i] + WRITTEN.size
          counter++
          ++counter
          counter--
          if (!B) return 0
          return counter + square + NAME.length + TABLE.size + COMPUTED.size - 2
      }
      """);
    Assert.Multiple(() => {
      Assert.That(module.Constants.Select(constant => constant.Name), Is.EqualTo(new[] { "A", "B", "TABLE", "NAME" }));
      Assert.That(module.Globals.Select(global => global.Name), Is.EqualTo(new[] { "COMPUTED", "WRITTEN", "counter", "square" }));
      Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [1])), Is.EqualTo("18"));
    });
  }

  [Test]
  public void Overloads_AndDefaults_ResolveByTheArguments() {
    var module = Import("""
      fun g(a: Int, b: Int = 10): Int = a + b
      fun g(a: Long): Long = a * 2L
      private fun h(): Unit {}
      fun f(x: Int): Long {
          return g(x) + g(x, 1) + g(x.toLong())
      }
      """);
    Assert.Multiple(() => {
      Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "g", "g_i64", "h", "f" }));
      Assert.That(module.Functions.Single(function => function.Name == "h").IsPublic, Is.False);
      Assert.That(PrototypeFixture.InterpretedOutcome(module, new PrototypeCall("f", [2])), Is.EqualTo("19"));
    });
  }

  [Test]
  public void Throws_AreTheLibrarysClasses() {
    var module = Import("""
      fun f(a: Int): Int {
          if (a == 1) throw ArithmeticException("x")
          if (a == 2) error("y")
          return if (a > 5) a else throw IllegalStateException("z")
      }
      """);
    Assert.That(IrClosures.Within(module.Functions[0].Body).OfType<IrThrow>().Count(), Is.EqualTo(2));
  }

  [TestCase("fun f(a: IntArray) { a.size = 3 }", "PLY1610")]
  [TestCase("val A = IntArray(2) { it }", "PLY1630")]
  [TestCase("fun f(a: Int): Int { val x = if (a > 0) { } else { 2 }; return x }", "PLY1610")]
  [TestCase("fun f(a: Int): Int = when { a > 0 -> 1 }", "PLY1610")]
  [TestCase("fun f(a: Int): Int { val x: String; x = when (a) { 1 -> \"a\" else -> 2 }; return 0 }", "PLY1601")]
  [TestCase("fun f(", "PLY1600")]
  [TestCase("fun f() = 'ab'", "PLY1600")]
  [TestCase("fun <T> f(a: T): T = a", "PLY1600")]
  [TestCase("fun Int.f(): Int = this", "PLY1600")]
  [TestCase("fun f(vararg a: Int) {}", "PLY1600")]
  [TestCase("fun f() { g(a = 1) }", "PLY1600")]
  [TestCase("fun f() { g(*a) }", "PLY1600")]
  [TestCase("fun f() { for ((a, b) in m) {} }", "PLY1600")]
  [TestCase("fun f() { val x = object {} }", "PLY1600")]
  [TestCase("fun f() { when (val x = 1) { else -> {} } }", "PLY1600")]
  [TestCase("fun f(a: Any) { when (a) { is Int -> {} } }", "PLY1600")]
  [TestCase("fun f(g: (Int) -> Int) {}", "PLY1600")]
  [TestCase("fun f() = 99999999999999999999", "PLY1600")]
  [TestCase("fun f() = \"open", "PLY1600")]
  [TestCase("fun f() = 1e", "PLY1600")]
  [TestCase("fun f() = 0x", "PLY1600")]
  [TestCase("/* open", "PLY1600")]
  [TestCase("fun f() = `open", "PLY1600")]
  [TestCase("fun f() = \"\"\"open", "PLY1600")]
  [TestCase("fun f() = '\\q'", "PLY1600")]
  [TestCase("fun f() = \"${\"", "PLY1600")]
  [TestCase("fun f() = 1 §", "PLY1600")]
  [TestCase("val x = 1 2", "PLY1600")]
  [TestCase("class C", "PLY1640")]
  [TestCase("data class P(val x: Int)", "PLY1640")]
  [TestCase("enum class E { A }", "PLY1640")]
  [TestCase("fun f(a: Int?) {}", "PLY1640")]
  [TestCase("fun f(a: String) = a.length", "PLY1630")]
  [TestCase("fun f(): Int { val x = null; return 1 }", "PLY1640")]
  [TestCase("fun f(a: Int): Int = a ?: 1", "PLY1640")]
  [TestCase("fun f(a: Int): Int = a!!", "PLY1601")]
  [TestCase("fun f(a: Int): Int = a?.inc()", "PLY1640")]
  [TestCase("fun f(a: Int): Int = (a as Int)", "PLY1601")]
  [TestCase("fun f(a: Int): Boolean = a is Int", "PLY1601")]
  [TestCase("fun f(a: Int): Int = a + 1L", "PLY1601")]
  [TestCase("fun f(a: Int): Long = a", "PLY1601")]
  [TestCase("fun f(a: UInt): UInt = a + 1", "PLY1601")]
  [TestCase("fun f(a: UInt): UInt = -a", "PLY1601")]
  [TestCase("fun f(a: Long): Long = a shl 2L", "PLY1601")]
  [TestCase("fun f(a: Byte): Byte = a and a", "PLY1601")]
  [TestCase("fun f(a: UInt): UInt = a ushr 2", "PLY1601")]
  [TestCase("fun f(a: Boolean): Boolean = a + a", "PLY1601")]
  [TestCase("fun f(a: Byte): Byte { var b = a; b += 1; return b }", "PLY1601")]
  [TestCase("fun f(a: String): Int = a.toInt()", "PLY1601")]
  [TestCase("fun f(a: Int): Int = a.unknown()", "PLY1601")]
  [TestCase("fun f(a: Double): Double = a.unknown()", "PLY1601")]
  [TestCase("fun f(a: Char): Int = a * 2", "PLY1601")]
  [TestCase("fun f(a: Char, b: Long): Char = a + b", "PLY1601")]
  [TestCase("fun f(a: Int): Int = kotlin.math.sqrt(a)", "PLY1601")]
  [TestCase("fun f(a: UInt): UInt = kotlin.math.abs(a)", "PLY1601")]
  [TestCase("fun f(a: String): String = maxOf(a, a)", "PLY1601")]
  [TestCase("fun f(a: Int): Int = if (a) 1 else 2", "PLY1610")]
  [TestCase("fun f(a: Int): Int { if (a > 0) return 1 }", "PLY1610")]
  [TestCase("fun f(a: Int): Int = if (a > 0) 1", "PLY1610")]
  [TestCase("fun g(x: Int): Int = x\nfun f(a: Int): Int = g(if (a > 0) { val b = 1; b } else 2)", "PLY1610")]
  [TestCase("fun f(a: Int): Int = when (a) { 1 -> 2 }", "PLY1610")]
  [TestCase("fun f(a: Int): Int { val x = when (a) { 1 -> 2 }; return x }", "PLY1610")]
  [TestCase("fun f() { break }", "PLY1610")]
  [TestCase("fun f() { while (true) { break@nowhere } }", "PLY1610")]
  [TestCase("fun f(a: Double) { for (x in 0.0..a) {} }", "PLY1610")]
  [TestCase("fun f(a: Long) { for (x in a) {} }", "PLY1610")]
  [TestCase("fun f(a: IntArray) { for (x in a.withIndex()) {} }", "PLY1610")]
  [TestCase("fun f(a: Int): Boolean = a in listOf(1)", "PLY1610")]
  [TestCase("fun f(a: Int): Int = (0..a).count()", "PLY1610")]
  [TestCase("fun g(x: Int): Int = x\nfun f(a: Int): Int = g(when (a) { 1 -> { val b = 2; b } else -> 3 })", "PLY1610")]
  [TestCase("fun f(a: Int) { var x = 0; do { x++ } while (IntArray(x) { it }.size > 0) }", "PLY1610")]
  [TestCase("fun f(a: UIntArray) {}", "PLY1620")]
  [TestCase("fun f(a: BooleanArray) {}", "PLY1620")]
  [TestCase("fun f(a: String) { a[0] = 'x' }", "PLY1620")]
  [TestCase("fun f(a: String) { a[0]++ }", "PLY1620")]
  [TestCase("fun f(a: Int) { a[0] = 1 }", "PLY1620")]
  [TestCase("fun f(a: Int): Int = a[0]", "PLY1620")]
  [TestCase("fun f(a: IntArray): Int = a.unknown()", "PLY1620")]
  [TestCase("fun f(a: String): Int = a.unknown()", "PLY1620")]
  [TestCase("fun f(a: String): Boolean = a.startsWith(1)", "PLY1620")]
  [TestCase("fun f(a: Double): String = \"$a\"", "PLY1620")]
  [TestCase("fun f() = print(1)", "PLY1630")]
  [TestCase("fun f() = println(1, 2)", "PLY1630")]
  [TestCase("fun f() = g()", "PLY1630")]
  [TestCase("fun f() = x", "PLY1630")]
  [TestCase("fun f(a: Int) { a = 2 }", "PLY1630")]
  [TestCase("val A = 1\nfun f() { A = 2 }", "PLY1630")]
  [TestCase("fun f() { y = 2 }", "PLY1630")]
  [TestCase("fun f() { val x; }", "PLY1630")]
  [TestCase("val A: Int", "PLY1630")]
  [TestCase("fun f() { fun g() {} }", "PLY1630")]
  [TestCase("fun f(a: Int) = a", "PLY1630")]
  [TestCase("fun f(a: Int) {}\nfun f(b: Int) {}", "PLY1630")]
  [TestCase("fun f(a: Int): Int = f(\"x\")", "PLY1601")]
  [TestCase("fun f() { listOf(1).forEach { } }", "PLY1630")]
  [TestCase("fun f(): Int = { 1 }", "PLY1630")]
  [TestCase("fun f() { require(true) { val a = 1; \"x\" } }", "PLY1630")]
  [TestCase("fun f() { repeat(2) { a, b -> } }", "PLY1630")]
  [TestCase("fun f(): Int { val a = IntArray(2) { a, b -> 1 }; return 0 }", "PLY1630")]
  [TestCase("fun f(): Int { val a = IntArray(2) { val x = 1 }; return 0 }", "PLY1630")]
  [TestCase("fun f(): Boolean = require(true)", "PLY1630")]
  [TestCase("fun f(a: Int): Int = a.toInt().also { }", "PLY1630")]
  [TestCase("fun f(a: IntArray): Int = (a.size)()", "PLY1620")]
  [TestCase("fun f(a: Int): Int = Math.floorMod(a, 2)", "PLY1630")]
  [TestCase("fun f(a: Int): Int = a.size", "PLY1630")]
  [TestCase("fun f(a: Int): Int = Int.UNKNOWN", "PLY1630")]
  [TestCase("fun f(a: Int): Double = Double.UNKNOWN", "PLY1630")]
  [TestCase("fun f(a: Int): Boolean = Boolean.UNKNOWN", "PLY1630")]
  public void Construct_OutsideTheSubset_IsRefusedWithTheCodeOfItsFamily(string source, string code) {
    var result = KotlinFrontend.Import("Snippet.kt", source);
    Assert.Multiple(() => {
      Assert.That(result.Module, Is.Null);
      Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(code), string.Join("\n", result.Diagnostics));
      Assert.That(result.Diagnostics[0].Line, Is.GreaterThan(0));
    });
  }

  [Test]
  public void Lexer_ReadsKotlinsLiterals() {
    Assert.Multiple(() => {
      Assert.That(KotlinLexer.Integer("0xFF_FFuL"), Is.EqualTo((0xFFFFUL, true, true)));
      Assert.That(KotlinLexer.Integer("0b101"), Is.EqualTo((5UL, false, false)));
      Assert.That(KotlinLexer.Integer("1_000U"), Is.EqualTo((1000UL, true, false)));
      Assert.That(KotlinLexer.Integer("99999999999999999999"), Is.Null);
      Assert.That(KotlinLexer.Floating("1.5e3F"), Is.EqualTo((1500.0, true)));
      Assert.That(KotlinLexer.Floating("2_5.0"), Is.EqualTo((25.0, false)));
      Assert.That(KotlinLexer.Unescape("a\\tb\\u0041\\$", default), Is.EqualTo("a\tbA$"));
      Assert.That(KotlinLexer.Tokens("a /* x /* y */ z */ b // c\n.5").Select(token => token.Text), Is.EqualTo(new[] { "a", "b", ".5", "" }));
      Assert.That(KotlinLexer.Tokens("`fun` 'x' \"\"\"r\"\"\"\" ").Select(token => token.Kind), Is.EqualTo(new[] { KTokenKind.Name, KTokenKind.Character, KTokenKind.RawString, KTokenKind.End }));
      Assert.That(KotlinLexer.Tokens("x\ny")[1].NewlineBefore, Is.True);
      Assert.That(new KPosition(3, 4).ToString(), Is.EqualTo("3:4"));
      Assert.Throws<KotlinSyntaxException>(() => KotlinLexer.Escape("\\", 0, default));
      Assert.Throws<KotlinSyntaxException>(() => KotlinLexer.Tokens("'"));
      Assert.Throws<KotlinSyntaxException>(() => KotlinLexer.Tokens("'ab"));
    });
  }

  [Test]
  public void Parser_ReadsFilesWithPackagesImportsAnnotationsAndRefusedDeclarations() {
    var file = KotlinParser.Parse(""""
      @file:Suppress("X")
      package a.b
      import kotlin.math.*
      import kotlin.math.abs as absolute
      @JvmName("g") private inline fun g(@Suppress("Y") x: Int): Int = x;
      interface I { fun m() }
      object O
      typealias T = Int
      val raw = """line ${'$'}x"""
      fun h() { label@ { } }
      """");
    Assert.Multiple(() => {
      Assert.That(file.Package, Is.EqualTo("a.b"));
      Assert.That(file.Imports, Is.EqualTo(new[] { "kotlin.math.*", "kotlin.math.abs as absolute" }));
      Assert.That(file.Declarations.Select(declaration => declaration.GetType().Name), Is.EqualTo(new[] { "KFunction", "KUnsupportedDeclaration", "KUnsupportedDeclaration", "KUnsupportedDeclaration", "KProperty", "KFunction" }));
      Assert.That(file.Declarations[0].Modifiers, Is.EqualTo(new[] { "private", "inline" }));
      Assert.That(KotlinAstWalker.Descendants(file).Count(), Is.GreaterThan(5));
      Assert.Throws<KotlinSyntaxException>(() => KotlinParser.ParseExpression("1 2", default));
      Assert.Throws<KotlinSyntaxException>(() => KotlinParser.ParseExpression("'", default));
      Assert.That(KotlinParser.ParseExpression("a?.b?:c", default), Is.InstanceOf<KBinary>());
      Assert.That(KotlinParser.ParseExpression("f<Int>(1)", default), Is.InstanceOf<KCall>());
      Assert.That(KotlinParser.ParseExpression("a < b", default), Is.InstanceOf<KBinary>());
      Assert.That(KotlinParser.ParseExpression("a as? Int", default), Is.InstanceOf<KAs>());
      Assert.That(KotlinParser.ParseExpression("a !is Int", default), Is.InstanceOf<KIs>());
      Assert.That(KotlinParser.ParseExpression("{ a: Int, b -> a }", default), Is.InstanceOf<KLambda>());
      Assert.That(KotlinParser.ParseExpression("{ a }", default), Is.InstanceOf<KLambda>());
    });
  }

  [Test]
  public void ModuleName_IsTheFileNameInUpperCamelCase() {
    Assert.Multiple(() => {
      Assert.That(KotlinFrontend.ModuleName("adler32.kt"), Is.EqualTo("Adler32"));
      Assert.That(KotlinFrontend.ModuleName("my-file.kt"), Is.EqualTo("My_file"));
      Assert.That(KotlinFrontend.ModuleName("9.kt"), Is.EqualTo("Kotlin9"));
      Assert.Throws<ArgumentNullException>(() => KotlinFrontend.Import(null!, ""));
      Assert.Throws<ArgumentNullException>(() => KotlinFrontend.ModuleName(null!));
    });
  }

}
