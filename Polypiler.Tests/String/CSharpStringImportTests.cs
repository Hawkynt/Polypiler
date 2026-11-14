using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The C# constructs on strings the frontend imports, each run by the interpreter against .NET running
/// the original, and the constructs it refuses by name: every row of <see cref="CSharpStringApi"/>.
/// </summary>
[TestFixture]
public sealed class CSharpStringImportTests {

  private static IrModule Import(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  /// <summary>
  /// One function per supported row of the table, with the calls it is made with. The first entry
  /// names the row, so that every row is shown to have a case.
  /// </summary>
  private static readonly (string Row, string Method, object[][] Calls)[] _cases = [
    ("string local, parameter, result, const, static readonly, string[]",
      "private const string K = \"k\"; private static readonly string R = \"r\"; private static readonly string[] T = { \"t0\", \"t1\" }; "
      + "public static string F(string s, string[] a) { string l = s; return l + K + R + T[1] + a[0]; }", [["x", new[] { "y" }]]),
    ("literal", "public static string F() => \"\\u00E9\\U0001F600\" + @\"\\\" + \"\"\"raw\"\"\";", [[]]),
    ("s.Length", "public static int F(string s) => s.Length;", [[""], ["a\U0001F600"]]),
    ("s[i]", "public static char F(string s, int i) => s[i];", [["abc", 1], ["abc", 3], ["abc", -1]]),
    ("string + string", "public static string F(string a, string b) => a + b + a;", [["x", "y"]]),
    ("string + char", "public static string F(string a, char c) => c + a + c;", [["x", 'y']]),
    ("interpolated string", "public static string F(string a, char c) => $\"<{a}|{c}>\";", [["x", 'y']]),
    ("string.Concat(string, string, ...)", "public static string F(string a, string b) => string.Concat(a, b, a, b, a);", [["x", "y"]]),
    ("==", "public static bool F(string a, string b) => a == b;", [["x", "x"], ["x", "X"]]),
    ("!=", "public static bool F(string a, string b) => a != b;", [["x", "x"], ["x", "X"]]),
    ("string.Equals(string, string)", "public static bool F(string a, string b) => string.Equals(a, b);", [["x", "x"], ["e\u0301", "\u00E9"]]),
    ("Equals(string)", "public static bool F(string a, string b) => a.Equals(b);", [["x", "x"], ["x", "y"]]),
    ("Equals(string, StringComparison.Ordinal)", "public static bool F(string a, string b) => a.Equals(b, System.StringComparison.Ordinal) && string.Equals(a, b, System.StringComparison.Ordinal);", [["x", "x"], ["x", "y"]]),
    ("string.CompareOrdinal(string, string) against 0", "public static bool F(string a, string b) => string.CompareOrdinal(a, b) < 0 || 0 == string.CompareOrdinal(a, b);", [["a", "B"], ["B", "a"], ["a", "a"]]),
    ("string.Compare(string, string, StringComparison.Ordinal) against 0", "public static bool F(string a, string b) => 0 > string.Compare(a, b, System.StringComparison.Ordinal);", [["a", "B"], ["B", "a"]]),
    ("Substring(int)", "public static string F(string s, int i) => s.Substring(i);", [["abc", 0], ["abc", 3], ["abc", 4]]),
    ("Substring(int, int)", "public static string F(string s, int i, int n) => s.Substring(i, n);", [["abc", 1, 2], ["abc", 3, 0], ["abc", 2, 2], ["abc", -1, 1]]),
    ("IndexOf(char)", "public static int F(string s, char c) => s.IndexOf(c);", [["abcb", 'b'], ["abc", 'x']]),
    ("IndexOf(char, int)", "public static int F(string s, char c, int i) => s.IndexOf(c, i);", [["abcb", 'b', 2], ["abc", 'a', 3], ["abc", 'a', 4]]),
    ("IndexOf(string, StringComparison.Ordinal)", "public static int F(string s, string n) => s.IndexOf(n, System.StringComparison.Ordinal);", [["abcbc", "bc"], ["abc", ""], ["e\u0301", "e"]]),
    ("IndexOf(string, int, StringComparison.Ordinal)", "public static int F(string s, string n, int i) => s.IndexOf(n, i, System.StringComparison.Ordinal);", [["abcbc", "bc", 2], ["abc", "", 3]]),
    ("LastIndexOf(char)", "public static int F(string s, char c) => s.LastIndexOf(c);", [["abcb", 'b'], ["", 'b']]),
    ("LastIndexOf(string, StringComparison.Ordinal)", "public static int F(string s, string n) => s.LastIndexOf(n, System.StringComparison.Ordinal);", [["abcbc", "bc"], ["abc", "x"]]),
    ("Contains(char)", "public static bool F(string s, char c) => s.Contains(c);", [["abc", 'b'], ["abc", 'x']]),
    ("Contains(string)", "public static bool F(string s, string n) => s.Contains(n);", [["abc", "bc"], ["abc", "x"], ["abc", ""]]),
    ("StartsWith(char)", "public static bool F(string s, char c) => s.StartsWith(c);", [["abc", 'a'], ["", 'a']]),
    ("StartsWith(string, StringComparison.Ordinal)", "public static bool F(string s, string n) => s.StartsWith(n, System.StringComparison.Ordinal);", [["e\u0301", "e"], ["abc", "b"]]),
    ("EndsWith(char)", "public static bool F(string s, char c) => s.EndsWith(c);", [["abc", 'c'], ["", 'c']]),
    ("EndsWith(string, StringComparison.Ordinal)", "public static bool F(string s, string n) => s.EndsWith(n, System.StringComparison.Ordinal);", [["abc", "bc"], ["abc", "b"]]),
    ("Replace(char, char)", "public static string F(string s) => s.Replace('b', '-');", [["abcb"]]),
    ("Replace(string, string)", "public static string F(string s, string n) => s.Replace(n, \"XY\");", [["abcb", "b"], ["aaa", "aa"], ["ab", ""]]),
    ("Split(char)", "public static string[] F(string s) => s.Split(',');", [["a,b,,c"], [""], [","]]),
    ("Split(string) with a constant separator", "public static string[] F(string s) => s.Split(\"::\");", [["a::b:c"], [""]]),
    ("string.Join(string, string[])", "public static string F(string[] a) => string.Join(\", \", a) + string.Join(\"-\", \"x\", \"y\");", [[new[] { "a", "", "c" }]]),
    ("Trim()", "public static string F(string s) => s.Trim();", [[" \t\u0085a\uFEFF "], ["\uFEFFa\uFEFF"]]),
    ("TrimStart()", "public static string F(string s) => s.TrimStart();", [[" a "]]),
    ("TrimEnd()", "public static string F(string s) => s.TrimEnd();", [[" a \u2028"]]),
    ("PadLeft(int)", "public static string F(string s, int n) => s.PadLeft(n);", [["a", 3], ["abc", 2]]),
    ("PadLeft(int, char)", "public static string F(string s, int n) => s.PadLeft(n, '*');", [["a", 3]]),
    ("PadRight(int)", "public static string F(string s, int n) => s.PadRight(n);", [["a", 3]]),
    ("PadRight(int, char)", "public static string F(string s, int n) => s.PadRight(n, '*');", [["a", 3], ["a", 0]]),
    ("new string(char, int)", "public static string F(char c, int n) => new string(c, n);", [['x', 3], ['x', 0]]),
    ("new string(char[])", "public static string F(char[] a) => new string(a);", [[new[] { 'a', 'b' }]]),
    ("new string(char[], int, int)", "public static string F(char[] a, int i, int n) => new string(a, i, n);", [[new[] { 'a', 'b', 'c' }, 1, 2], [new[] { 'a' }, 1, 1]]),
    ("ToCharArray()", "public static char[] F(string s) => s.ToCharArray();", [["a\uD800"]]),
    ("char.ToString()", "public static string F(char c) => c.ToString() + char.ToString(c);", [['z']]),
    ("switch on a string",
      "public static int F(string s) { int n = 0; for (int i = 0; i < 2; i++) { switch (s) { case \"a\": case \"b\": n += 1; break; case \"c\": n += 10; continue; default: n += 100; break; } n += 1000; } return n; }",
      [["a"], ["b"], ["c"], ["d"]]),
    ("foreach (char c in s)", "public static int F(string s) { int n = 0; foreach (char c in s) n = n * 31 + c; return n; }", [["a\U0001F600"], [""]])
  ];

  private static IEnumerable<TestCaseData> Cases() => _cases.Select(entry => new TestCaseData(entry.Method, entry.Calls).SetName("Imports: " + entry.Row));

  /// <summary>Test design 2: every supported row, interpreted and run by .NET with the same arguments.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_IsImported_AndComputesWhatDotNetComputes(string method, object[][] calls) {
    var source = PrototypeFixture.Wrap(method);
    var module = Import(source);
    var compiled = GeneratedCSharp.Compile(source, false).Method("F");
    foreach (var arguments in calls) {
      var expected = CSharpStringEmissionTests.Outcome(() => Invoke(compiled, arguments));
      var actual = CSharpStringEmissionTests.Outcome(() => IrInterpreter.Call(module, "F", [.. arguments]));
      Assert.That(actual, Is.EqualTo(expected), string.Join(", ", arguments.Select(PrototypeFixture.Show)));
    }
  }

  private static object? Invoke(MethodInfo method, object[] arguments) {
    try {
      var result = method.Invoke(null, [.. arguments]);
      return result switch {
        char unit => (ushort)unit,
        char[] units => Array.ConvertAll(units, unit => (ushort)unit),
        _ => result
      };
    } catch (TargetInvocationException exception) {
      throw exception.InnerException!;
    }
  }

  [Test]
  public void EverySupportedRow_HasACase()
    => Assert.That(_cases.Select(entry => entry.Row), Is.EquivalentTo(CSharpStringApi.Supported.Select(member => member.Construct)));

  [Test]
  public void Switch_OnAStringChoosesBySectionInSourceOrder() {
    var module = Import(PrototypeFixture.Wrap("public static int F(string s) { switch (s) { case \"x\": return 1; default: return 0; case \"y\": return 2; } }"));
    Assert.That(new[] { "x", "y", "z" }.Select(text => IrInterpreter.Call(module, "F", [text])), Is.EqualTo(new object[] { 1, 2, 0 }));
  }

  /// <summary>Test design 9 and 10: every refused row of the table, by name, with its reason.</summary>
  [TestCase("public static int F(string s) => s.IndexOf(\"e\");", "string.IndexOf(string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static int F(string s) => s.LastIndexOf(\"e\");", "string.LastIndexOf(string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static bool F(string s) => s.StartsWith(\"e\");", "string.StartsWith(string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static bool F(string s) => s.EndsWith(\"e\");", "string.EndsWith(string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static int F(string a, string b) => string.Compare(a, b) < 0 ? 1 : 0;", "string.Compare(string, string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static int F(string a, string b) => a.CompareTo(b);", "string.CompareTo(string), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static bool F(string a) => a.StartsWith(\"e\", System.StringComparison.OrdinalIgnoreCase);", "string.StartsWith(string, System.StringComparison), which compares by culture: pass StringComparison.Ordinal")]
  [TestCase("public static int F(string a, string b) => string.CompareOrdinal(a, b);", "the value of CompareOrdinal other than its sign, which .NET documents only by its sign")]
  [TestCase("public static int F(string a, string b) => string.CompareOrdinal(a, b) < 1 ? 1 : 0;", "the value of CompareOrdinal other than its sign, which .NET documents only by its sign")]
  [TestCase("public static string[] F(string a) => a.Split(new[] { ',' }, 2);", "string.Split(char[], int), whose last part keeps the rest of the string")]
  [TestCase("public static string F(string a) => a.ToUpper();", "string.ToUpper(), which is case mapping")]
  [TestCase("public static string F(string a) => a.ToLower();", "string.ToLower(), which is case mapping")]
  [TestCase("public static string F(string a) => a.ToUpperInvariant();", "string.ToUpperInvariant(), which is case mapping")]
  [TestCase("public static string F(string a) => a.ToLowerInvariant();", "string.ToLowerInvariant(), which is case mapping")]
  [TestCase("public static string F(string a) => a.Normalize();", "string.Normalize()")]
  [TestCase("public static string F(string a) => string.Format(\"{0}\", a);", "string.Format(string, object)")]
  [TestCase("public static string F(string a) => new System.Text.StringBuilder(a).ToString();", "StringBuilder.ToString")]
  [TestCase("public static string F() => null;", "null of the type string")]
  [TestCase("public static string[] F(int n) => new string[n];", "new string[n], an array of strings without its elements, which .NET fills with null")]
  [TestCase("public static string F(string a) => a + 1;", "string + int, which converts a number to text, not supported until number-to-text conversion")]
  [TestCase("public static string F(int n) => $\"{n}\";", "string + int, which converts a number to text, not supported until number-to-text conversion")]
  [TestCase("public static string F(string a) => $\"{a,5}\";", "an interpolation with an alignment or a format")]
  [TestCase("public static string[] F(string a, string b) => a.Split(b);", "string.Split(string, System.StringSplitOptions)")]
  [TestCase("public static bool F(char c) => char.IsLetter(c);", "Char.IsLetter")]
  public void Construct_IsRefusedByName(string method, string what) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((CSharpFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  [Test]
  public void EveryRefusedRow_HasAReason() {
    Assert.That(CSharpStringApi.Refused.Select(member => member.Refusal), Has.All.Not.Empty);
    Assert.That(CSharpStringApi.Members.Select(member => member.Construct), Is.Unique);
  }

}
