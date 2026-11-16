using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Verification.Runners;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Rust;

/// <summary>
/// The scenarios of #1391 on the pinned toolchain: an iterator function as an <c>Iterator</c> state
/// machine stays lazy - an infinite counter taken 5 items runs its body 5 times - and runs its finally
/// blocks where the body leaves them and where the consumer drops it, innermost first. Every value is
/// the C# compiler's build of the same source, which the interpreter gives as well; the crate must give
/// it with overflow checks on and off, and pass clippy with <c>-D warnings</c> and <c>cargo fmt --check</c>.
/// </summary>
[TestFixture]
[Layer("rust")]
[Category("Slow")]
public sealed class RustIteratorScenarioTests {

  private const string Usings = "using System; using System.Collections.Generic; ";

  /// <summary>The scenario of the issue: an infinite counter, taken 5 items by a loop that breaks.</summary>
  private const string Counter = "static int Calls; static IEnumerable<int> Count() { var i = 0; while (true) { ++Calls; yield return i; ++i; } } "
    + "public static int Taken() { Calls = 0; var r = 0; var taken = 0; foreach (var v in Count()) { r = r * 10 + v; if (++taken == 5) break; } return r * 100 + Calls; }";

  /// <summary>Finally blocks around yields, traced through a global as Iterators.cs traces them through an array.</summary>
  private const string Finally = "static int T; "
    + "static IEnumerable<int> Traced() { T = T * 10 + 7; try { T = T * 10 + 1; yield return 1; T = T * 10 + 2; yield return 2; } finally { T = T * 10 + 9; } T = T * 10 + 6; } "
    + "static IEnumerable<int> Inner() { try { yield return 1; yield return 2; } finally { T = T * 10 + 8; } } "
    + "static IEnumerable<int> Outer() { try { foreach (var v in Inner()) yield return v; T = T * 10 + 3; yield return 3; } finally { T = T * 10 + 9; } } "
    + "static IEnumerable<int> Cut(int n) { try { yield return 1; if (n > 0) yield break; yield return 2; } finally { T = T * 10 + 5; } } "
    + "public static int EarlyBreak() { T = 0; var s = Traced(); T = T * 10 + 5; foreach (var v in s) { T = T * 10 + 8; break; } return T; } "
    + "public static int Full() { T = 0; foreach (var v in Traced()) T = T * 10 + 8; return T; } "
    + "public static int Returned() { T = 0; foreach (var v in Traced()) if (v == 2) return T; return -1; } "
    + "public static int NestedClose() { T = 0; foreach (var v in Outer()) { T = T * 10 + v; break; } return T; } "
    + "public static int NestedSecond() { T = 0; foreach (var v in Outer()) { T = T * 10 + v; if (v == 2) break; } return T; } "
    + "public static int NestedFull() { T = 0; foreach (var v in Outer()) T = T * 10 + v; return T; } "
    + "public static int Cutting(int n) { T = 0; foreach (var v in Cut(n)) T = T * 10 + v; return T; }";

  /// <summary>Sequences that borrow arrays and strings, generic sequence parameters, loops that suspend.</summary>
  private const string Shapes = "static IEnumerable<int> Evens(int[] a) { foreach (var x in a) if (x % 2 == 0) yield return x; } "
    + "static IEnumerable<int> Pairs(int[] a, int[] b) { for (var i = 0; i < a.Length && i < b.Length; ++i) yield return a[i] * b[i]; } "
    + "static IEnumerable<char> Chars(string s) { for (var i = 0; i < s.Length; ++i) yield return s[i]; } "
    + "static IEnumerable<int> Take(IEnumerable<int> source, int count) { var taken = 0; foreach (var item in source) { if (taken++ == count) yield break; yield return item; } } "
    + "static IEnumerable<int> Odd(int n) { var i = 0; do { ++i; if (i % 2 == 0) continue; if (i > 7) break; yield return i; } while (i < n); "
    + "for (var j = 0; j < 3; ++j) { if (j == 1) continue; yield return -j; } while (n > 0) { --n; if (n == 2) break; yield return n * 100; } } "
    + "static IEnumerable<Func<int>> Makers(int n) { for (var i = 0; i < n; ++i) { var k = i; yield return () => k * 10; } } "
    + "static int Sum(IEnumerable<int> values) { var sum = 0; foreach (var v in values) sum += v; return sum; } "
    + "public static int Borrowing() { var a = new[] { 1, 2, 4, 6 }; var b = new[] { 3, 5 }; var e = Evens(a); IEnumerable<int> all = a; return Sum(e) * 1000 + Sum(Pairs(a, b)) * 10 + Sum(Take(e, 2)) + Sum(all); } "
    + "public static int Text(string s) { var n = 0; foreach (var c in Chars(s)) n = n * 3 + c; return n; } "
    + "public static int Jumps(int n) { var t = 0; foreach (var v in Odd(n)) t = t * 3 + v; return t; } "
    + "public static int Closures() { var t = 0; foreach (var f in Makers(4)) t += f(); return t; }";

  private static string Source(string methods) => Usings + PrototypeFixture.Wrap(methods);

  private static async Task Expect(string methods, string method, object[] arguments) {
    var module = PrototypeFixture.Import("Snippet.cs", Source(methods));
    var call = new PrototypeCall(method, arguments);
    var expected = GeneratedCSharp.CompileSource(Source(methods)).Outcome(call);
    Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(expected), "the interpreter and the C# compiler");
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That((await RustRun.OutcomesAsync(module, [call], checks)).Single(), Is.EqualTo(expected), $"Rust with overflow checks {checks}");
  }

  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task InfiniteCounterTakenFive_GivesZeroToFourAndRunsItsBodyFiveTimes() {
    var module = PrototypeFixture.Import("Snippet.cs", Source(Counter));
    var call = new PrototypeCall("Taken", []);
    foreach (var checks in new[] { OverflowChecks.Panic, OverflowChecks.Wrap })
      Assert.That((await RustRun.OutcomesAsync(module, [call], checks)).Single(), Is.EqualTo("123405"), $"Rust with overflow checks {checks}");
  }

  [TestCase("EarlyBreak", new object[0])]
  [TestCase("Full", new object[0])]
  [TestCase("Returned", new object[0])]
  [TestCase("NestedClose", new object[0])]
  [TestCase("NestedSecond", new object[0])]
  [TestCase("NestedFull", new object[0])]
  [TestCase("Cutting", new object[] { 0 })]
  [TestCase("Cutting", new object[] { 1 })]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task FinallyBlocks_RunAsInDotNet(string method, object[] arguments) => Expect(Finally, method, arguments);

  [TestCase("Borrowing", new object[0])]
  [TestCase("Text", new object[] { "" })]
  [TestCase("Text", new object[] { "abc" })]
  [TestCase("Jumps", new object[] { 0 })]
  [TestCase("Jumps", new object[] { 5 })]
  [TestCase("Jumps", new object[] { 9 })]
  [TestCase("Closures", new object[0])]
  [RequiresToolchain(RustRunner.Toolchain)]
  public Task IteratorShapes_ComputeAsInDotNet(string method, object[] arguments) => Expect(Shapes, method, arguments);

  /// <summary>Every crate of the scenarios builds without a warning under clippy and is formatted.</summary>
  [Test]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task ScenarioCrates_AreLintAndFormatClean() {
    var runner = RustRun.Runner(OverflowChecks.Panic);
    foreach (var methods in new[] { Counter, Finally, Shapes }) {
      var package = RustCrates.Package(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", Source(methods))));
      Assert.That(await runner.ClippyAsync(package), Is.Empty);
      Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
    }
  }

  /// <summary>The parts of the sequences family the backend writes are lint and format clean too.</summary>
  [TestCase("Iterators")]
  [TestCase("Pipelines")]
  [RequiresToolchain(RustRunner.Toolchain)]
  public async Task SequenceFixtures_AreLintAndFormatClean(string fixture) {
    var module = fixture == "Iterators" ? SequenceFixtures.Imported() : LinqFixtures.Imported(fixture);
    var written = module.Functions.Where(function => function.IsPublic && !function.Name.Contains('.') && RustCrates.Check(RustFamilyDifferentialTests.Written(module, [function.Name])).Count == 0)
      .Select(function => function.Name).ToList();
    var package = RustCrates.Package(RustCrates.Emit(RustFamilyDifferentialTests.Written(module, written)));
    var runner = RustRun.Runner(OverflowChecks.Panic);
    Assert.That(await runner.ClippyAsync(package), Is.Empty);
    Assert.That(await runner.FormatCheckAsync(package), Is.Empty);
  }

}
