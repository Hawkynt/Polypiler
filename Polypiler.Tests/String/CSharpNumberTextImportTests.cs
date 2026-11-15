using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The C# constructs that convert numbers and truth values to text and back (#1315), each run by the
/// interpreter against .NET running the original with the invariant culture, the caveat of a
/// conversion that leaves the culture to the thread, and the constructs the frontend refuses by
/// name: every row of <see cref="CSharpNumberTextApi"/>.
/// </summary>
[TestFixture]
public sealed class CSharpNumberTextImportTests {

  private const string Invariant = "System.Globalization.CultureInfo.InvariantCulture";

  private static IrModule Import(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static readonly (string Row, string Method, object[][] Calls)[] _cases = [
    ("x.ToString(CultureInfo.InvariantCulture), x.ToString(\"G\" | \"R\", CultureInfo.InvariantCulture)",
      $"public static string F(double x, float y, long n) => x.ToString({Invariant}) + \"|\" + y.ToString(\"R\", {Invariant}) + \"|\" + n.ToString(\"G\", {Invariant}) + \"|\" + x.ToString(System.Globalization.NumberFormatInfo.InvariantInfo);",
      [[1e21, 0.1f, long.MinValue], [-0.0, 16777217f, 0L], [1.5e-6, float.NaN, 7L]]),
    ("x.ToString(), x.ToString(\"G\" | \"R\") without a provider",
      "public static string F(double x, byte b, short s) => x.ToString() + \"|\" + x.ToString(\"R\") + \"|\" + b.ToString() + \"|\" + s.ToString(\"g\");", [[2.5, (byte)255, (short)-7]]),
    ("string + x, interpolation {x}, string.Concat(…, x)",
      "public static string F(double x, int n, uint u, ulong l, sbyte s) => \"v=\" + x + $\"<{n}>\" + string.Concat(\"[\", u, \"]\") + l + s;", [[1e17, -3, uint.MaxValue, ulong.MaxValue, (sbyte)-128]]),
    ("b.ToString(), string + b", "public static string F(bool b) => b.ToString() + \"|\" + (\"b=\" + b) + $\"{b}\" + b.ToString(" + Invariant + ");", [[true], [false]]),
    ("Convert.ToString(x[, provider]) of a number or a bool",
      $"public static string F(double x, bool b, int n) => System.Convert.ToString(x) + System.Convert.ToString(b) + System.Convert.ToString(n, {Invariant});", [[0.1, true, -5]]),
    ("x.ToString(\"F<n>\"[, provider]), interpolation {x:F<n>}, n from 0 to 15",
      $"public static string F(double x, float y, int n) => x.ToString(\"F2\", {Invariant}) + \"|\" + x.ToString(\"F\") + \"|\" + y.ToString(\"f0\", {Invariant}) + $\"|{{x:F15}}|{{n:F1}}\";",
      [[0.125, 2.5f, -3], [-0.0, 0.5f, 0], [1e21, 1.5f, int.MinValue], [1.005, -2.5f, 1]]),
    ("x.ToString(\"D<n>\"[, provider]) of an integer", $"public static string F(int n, long l) => n.ToString(\"D5\") + \"|\" + l.ToString(\"d\", {Invariant});", [[-5, long.MinValue], [123456, 0L]]),
    ("x.ToString(\"x<n>\" | \"X<n>\"[, provider]) of an integer",
      $"public static string F(int n, sbyte s, ulong l) => n.ToString(\"x8\") + \"|\" + s.ToString(\"X\") + \"|\" + l.ToString(\"x\", {Invariant});", [[-1, (sbyte)-1, ulong.MaxValue], [255, (sbyte)15, 0UL]]),
    ("Convert.ToString(v, 2 | 8 | 10 | 16)",
      "public static string F(int n, long l, short s, byte b) => System.Convert.ToString(n, 2) + \"|\" + System.Convert.ToString(l, 8) + \"|\" + System.Convert.ToString(s, 16) + \"|\" + System.Convert.ToString(b, 2) + \"|\" + System.Convert.ToString(n, 10);",
      [[-1, long.MinValue, (short)-1, (byte)5], [5, 7L, (short)255, (byte)0]]),
    ("T.Parse(s[, CultureInfo.InvariantCulture]) of every integer type, float and double",
      $"public static string F(string s) => double.Parse(s, {Invariant}) + \"|\" + float.Parse(s, {Invariant}) + \"|\" + int.Parse(s) + \"|\" + ulong.Parse(s, {Invariant}) + \"|\" + byte.Parse(s);",
      [["12"], [" 255 "], ["1,000"], ["-1"], ["256"], ["0x10"]])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Method, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 1: every supported row, interpreted and run by .NET with the same arguments under the invariant culture.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatDotNetComputes(string method, object[][] calls) {
    var source = "using System.Globalization; " + PrototypeFixture.Wrap(method);
    var module = Import(source);
    var compiled = GeneratedCSharp.Compile(source, false).Method("F");
    var culture = CultureInfo.CurrentCulture;
    try {
      CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
      foreach (var arguments in calls) {
        var expected = Outcome(() => compiled.Invoke(null, [.. arguments]));
        var actual = Outcome(() => IrInterpreter.Call(module, "F", [.. arguments]));
        Assert.That(actual, Is.EqualTo(expected), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
      }
    } finally {
      CultureInfo.CurrentCulture = culture;
    }
  }

  private static string Outcome(Func<object?> call) {
    try {
      return PrototypeFixture.Show(call());
    } catch (TargetInvocationException exception) when (exception.InnerException is FormatException or OverflowException) {
      return exception.InnerException is FormatException ? "invalid format" : "integer overflow";
    } catch (IrTrapException trap) {
      return IrTrapException.Describe(trap.Trap);
    }
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(CSharpNumberTextApi.Supported.Select(member => member.Construct)));

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(CSharpNumberTextApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(CSharpNumberTextApi.Members.Select(member => member.Construct), Is.Unique);
  }

  /// <summary>Test design 12: a conversion without a provider is announced, one with the invariant provider is not.</summary>
  [TestCase("public static string F(double x) => \"a\" + x;", true)]
  [TestCase("public static string F(double x) => $\"{x}\";", true)]
  [TestCase("public static string F(int x) => x.ToString();", true)]
  [TestCase("public static int F(string s) => int.Parse(s);", true)]
  [TestCase("public static string F(double x) => System.Convert.ToString(x);", true)]
  [TestCase("public static string F(double x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);", false)]
  [TestCase("public static string F(bool b) => \"a\" + b;", false)]
  [TestCase("public static string F(int x) => System.Convert.ToString(x, 16);", false)]
  [TestCase("public static double F(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);", false)]
  public void Caveat_NamesTheImpliedCulture(string method, bool implied)
    => Assert.That(Import(PrototypeFixture.Wrap(method)).Caveats, Is.EqualTo(implied ? new[] { CSharpFrontend.InvariantCulture } : Array.Empty<string>()));

  /// <summary>Test design 10: every refused construct of the table, by name, with its reason.</summary>
  [TestCase("public static string F(double x) => x.ToString(System.Globalization.CultureInfo.GetCultureInfo(\"de-DE\"));",
    "Double.ToString(IFormatProvider) with the provider System.Globalization.CultureInfo.GetCultureInfo(\"de-DE\"), which is a culture other than the invariant one")]
  [TestCase("public static string F(double x) => x.ToString(\"N2\");", "Double.ToString(String) with the format \"N2\"")]
  [TestCase("public static string F(double x) => x.ToString(\"E3\", System.Globalization.CultureInfo.InvariantCulture);", "Double.ToString(String, IFormatProvider) with the format \"E3\"")]
  [TestCase("public static string F(double x) => x.ToString(\"G17\");", "Double.ToString(String) with the format \"G17\"")]
  [TestCase("public static string F(double x) => x.ToString(\"F16\");", "Double.ToString(String) with the format \"F16\"")]
  [TestCase("public static string F(double x) => x.ToString(\"0.00\");", "Double.ToString(String) with the format \"0.00\"")]
  [TestCase("public static string F(double x) => x.ToString(\"D2\");", "Double.ToString(String) with the format \"D2\"")]
  [TestCase("public static string F(int x) => x.ToString(\"R\");", "Int32.ToString(String) with the format \"R\"")]
  [TestCase("public static string F(long x) => x.ToString(\"F2\");", "Int64.ToString(String) with the format \"F2\"")]
  [TestCase("public static string F(int x) => x.ToString(\"D100\");", "Int32.ToString(String) with the format \"D100\"")]
  [TestCase("public static string F(double x, string f) => x.ToString(f);", "Double.ToString(String) with a format that is no constant")]
  [TestCase("public static double F(string s) => double.Parse(s, System.Globalization.NumberStyles.Float);", "Double.Parse(String, NumberStyles) with NumberStyles")]
  [TestCase("public static bool F(string s) { return int.TryParse(s, out _); }", "Int32.TryParse(String, Int32), whose result is an out parameter (#216)")]
  [TestCase("public static string F(int x, int r) => System.Convert.ToString(x, r);", "Convert.ToString(Int32, Int32) with a radix other than the constant 2, 8, 10 or 16")]
  [TestCase("public static string F(int x) => System.Convert.ToString(x, 3);", "Convert.ToString(Int32, Int32) with a radix other than the constant 2, 8, 10 or 16")]
  [TestCase("public static string F(int x) => $\"{x,5}\";", "an interpolation with an alignment")]
  [TestCase("public static string F(bool b) => $\"{b:F2}\";", "string + bool of a bool with a format")]
  public void Construct_IsRefusedByName(string method, string what) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((CSharpFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

}
