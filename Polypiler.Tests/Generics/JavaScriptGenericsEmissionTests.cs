using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Backends.Testing;

namespace Polypiler.Tests.Generics;

/// <summary>
/// The instantiations and copies of a module written by the JavaScript backend: those whose text is
/// the same are one function under the source name, the others are named after their type
/// arguments; the equality of a generic definition keeps .NET's meaning on NaN and -0.0. Running the
/// output in node is the acceptance tests' part; here every profile parses it.
/// </summary>
[TestFixture]
internal sealed class JavaScriptGenericsEmissionTests {

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, module, module == "iife" ? "G" : null, typed)
  ];

  private static string Emit(IrModule module, JavaScriptProfile profile) {
    Assert.That(LegalizedJavaScript.Check(module, profile), Is.Empty);
    var text = LegalizedJavaScript.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(IrModule module, string level) => Emit(module, JavaScriptProfile.Parse(level, level == "es5" ? "cjs" : "esm"));

  private static string Emit(string source, string level) => Emit(PrototypeFixture.Import("Snippet.cs", "#nullable disable\n" + source), level);

  /// <summary>Test design 1 and 12: every fixture is written in every profile, nothing inexpressible, and the output parses at its level.</summary>
  [Test, Category("Slow")]
  public void Fixtures_AreWrittenInEveryProfile() {
    var modules = new[] { GenericsFixtures.Imported("Search"), GenericsFixtures.Imported("Ring"), GenericsFixtures.ImportedScript("middle.js") };
    foreach (var module in modules)
      foreach (var profile in _profiles)
        Assert.That(LegalizedJavaScript.Check(module, profile).Where(problem => problem.Code is JavaScriptEmitter.Inexpressible), Is.Empty, $"{module.Name} in {profile}");

    foreach (var module in modules)
      foreach (var profile in _profiles)
        Emit(module, profile);

    foreach (var module in modules)
      foreach (var profile in CSharpProfile.All)
        Assert.That(LegalizedCSharp.Check(module, profile).Where(problem => problem.Code is CSharpEmitter.Inexpressible), Is.Empty, $"{module.Name} for {profile}");
  }

  /// <summary>Test design 4: Swap over i32 and f64 is one function, IndexOf over them two, the equality of doubles is never === alone nor indexOf.</summary>
  [TestCase("es5")]
  [TestCase("es2015")]
  [TestCase("es2022")]
  public void Search_MergesWhatIsTheSame(string level) {
    var text = Emit(GenericsFixtures.Imported("Search"), level);
    Assert.That(text.Split("function Swap(").Length, Is.EqualTo(2), text);
    Assert.That(text, Does.Contain("function IndexOf$f64(").And.Contain("function IndexOf$i32(").And.Contain("function Max("));
    Assert.That(text, Does.Contain("function Make(").And.Contain("function Def$Box("));
    Assert.That(text, Does.Contain("$sameValueZero($get(a, i), v)").And.Contain("$get(a, i) === v"));
    Assert.That(text, Does.Not.Contain("indexOf(").And.Not.Contain("includes("));
    Assert.That(text, Does.Contain("// Swap(a: ").And.Contain(" | "));
  }

  /// <summary>Test design 4: Ring over i64 and f64 differs in its text and is two classes.</summary>
  [TestCase("es5")]
  [TestCase("es2015")]
  [TestCase("es2020")]
  public void Ring_IsTwoClasses(string level) {
    var text = Emit(GenericsFixtures.Imported("Ring"), level);
    Assert.That(text, Does.Contain(level == "es5" ? "function Ring$i64(" : "class Ring$i64 {").And.Contain(level == "es5" ? "function Ring$f64(" : "class Ring$f64 {"), text);
    Assert.That(text, Does.Contain("new Ring$i64(3)"));
    if (level == "es2020")
      Assert.That(text, Does.Contain("BigInt64Array"));
  }

  /// <summary>The copies of a JavaScript function with the same text are one function again.</summary>
  [TestCase("es5")]
  [TestCase("es2022")]
  public void Copies_MergeAgain(string level) {
    var text = Emit(GenericsFixtures.ImportedScript("middle.js"), level);
    Assert.That(text.Split("function middle(").Length, Is.EqualTo(2), text);
  }

  /// <summary>Test design 5: one equality of doubles over variables is written inline; two share the helper; an operand that is no variable takes the helper.</summary>
  [Test]
  public void SameValueZero_InlineOrHelper() {
    var one = Emit("public static class Snippet { static bool Same<T>(T a, T b) where T : System.IEquatable<T> => a.Equals(b); public static bool F(double a, double b) => Same(a, b); }", "es2022");
    Assert.That(one, Does.Contain("a === b || (a !== a && b !== b)").And.Not.Contain("function $sameValueZero"), one);
    var other = Emit("public static class Snippet { public static bool F(double[] a, double b) => a[0].Equals(b); }", "es2022");
    Assert.That(other, Does.Contain("function $sameValueZero"), other);
    var floats = Emit("public static class Snippet { public static bool F(float a, float b) => a.Equals(b) && b.Equals(a); }", "es2022");
    Assert.That(floats, Does.Contain("$sameValueZero(a, b) && $sameValueZero(b, a)"), floats);
  }

  /// <summary>The equality and the order of every other kind of value: integers, narrow integers, truth values, references and records.</summary>
  [Test]
  public void EqualityAndOrder_OfEveryKind() {
    var text = Emit("""
      public sealed class Box { }
      public sealed record Pair(int A);
      public static class Snippet {
        static bool Same<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);
        static int Order<T>(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b);
        public static int F(byte a, byte b, bool c, long d) {
          var box = new Box();
          return (Same(a, b) ? 1 : 0) + Order(a, b) + (Same(c, c) ? 2 : 0) + Order(c, !c) + (Same(box, box) ? 4 : 0) + (Same(new Pair(1), new Pair(1)) ? 8 : 0) + Order(d, d) + (Same(d, d) ? 16 : 0);
        }
      }
      """, "es2022");
    Assert.That(text, Does.Contain("function $compare").And.Contain("$equals_Pair(a, b)"), text);
    var halves = Emit("public static class Snippet { static int Order<T>(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b); public static int F(long a, long b) => Order(a, b); }", "es5");
    Assert.That(halves, Does.Not.Contain("$compare("), halves);
  }

  /// <summary>The order of a 64-bit value that is no variable, below ECMAScript 2020, is the library's comparison of the two limbs (O0075), which reads each operand once.</summary>
  [Test]
  public void OrderOfAComputed64BitValue_IsTheLibrarysComparisonBelowBigInt() {
    var module = PrototypeFixture.Import("Snippet.cs", "public static class Snippet { public static int F(long a, long b) => (a + b).CompareTo(b); }");
    Assert.That(LegalizedJavaScript.Check(module, JavaScriptProfile.Parse("es5", "cjs")), Is.Empty);
    Assert.That(LegalizedJavaScript.Emit(module, JavaScriptProfile.Parse("es5", "cjs")).Single().Content, Does.Contain("return CompareSigned64(Add64(a, b), b);"));
    Assert.That(LegalizedJavaScript.Check(module, JavaScriptProfile.Parse("es2020", "esm")), Is.Empty);
  }

  /// <summary>The helpers of the generic definitions are listed with the others, for every profile.</summary>
  [Test]
  public void Helpers_AreListed() {
    var names = JavaScriptEmitter.HelperSources(JavaScriptProfile.Parse("es5")).Select(helper => helper.Name).ToList();
    Assert.That(names, Does.Contain("$sameValueZero").And.Contain("$compare"));
  }

}
