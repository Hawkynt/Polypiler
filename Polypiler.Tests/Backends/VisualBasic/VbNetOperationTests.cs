using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// One C# method per operation of the IR the Visual Basic .NET backend writes in its own way - the
/// string operations of <c>System.String</c>, the text of numbers, the narrow conversions, a step as a
/// statement, a discarded value, the equality of references, the exception a catch clause holds -
/// translated, compiled in memory and called with the arguments given, against the IR interpreter.
/// </summary>
[TestFixture]
public sealed class VbNetOperationTests {

  [TestCase("static string F(string s, int n) { return new string('x', n) + (char)(65 + n); }", "ab", 3)]
  [TestCase("static int F(string s, int n) { return (string.CompareOrdinal(s, \"b\") < 0 ? 10 : 0) + s.IndexOf(\"b\", System.StringComparison.Ordinal); }", "abc", 0)]
  [TestCase("static int F(string s, int n) { return (s.StartsWith(\"a\", System.StringComparison.Ordinal) ? 1 : 0) + (s.EndsWith(\"c\", System.StringComparison.Ordinal) ? 2 : 0) + (s.Contains('b') ? 4 : 0) + s.LastIndexOf('c') * 8; }", "abc", 0)]
  [TestCase("static string F(string s, int n) { return s.Replace('a', 'z') + s.Trim() + s.TrimStart() + s.TrimEnd() + s.IndexOf('c', n); }", " abc ", 1)]
  [TestCase("static string F(string s, int n) { return n.ToString(\"X8\") + n.ToString(\"x2\") + ((double)n / 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + (n > 2); }", "", -7)]
  [TestCase("static int F(string s, int n) { ushort u = (ushort)(sbyte)n; uint w = (uint)(short)n; ulong v = (ulong)(sbyte)n; return u + (int)(w >> 20) + (int)(v >> 60) + System.Math.Abs((short)n); }", "", -3)]
  [TestCase("static int F(string s, int n) { int x = n; x++; x--; long y = n; y += 2; return x + (int)y; }", "", 5)]
  [TestCase("static int F(string s, int n) { try { return 10 / n; } catch (System.Exception e) { return e.Message.Length > 0 ? -1 : -2; } }", "", 0)]
  [TestCase("static double F(string s, int n) { float f = n; double d = System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(n)); return System.BitConverter.Int32BitsToSingle(System.BitConverter.SingleToInt32Bits(f)) + d; }", "", 3)]
  [TestCase("static int F(string s, int n) { var t = new int[] { 1, 2, 3 }; System.Array.Fill(t, n); System.Array.Copy(t, 0, t, 1, 2); return t[0] + t[2] + t.Length; }", "", 4)]
  public void Operation_ComputesWhatTheInterpreterComputes(string method, string text, int number) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public " + method));
    var translation = VbNetFixtures.Translate(module);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var call = new PrototypeCall("F", [text, number]);
    var actual = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(VbNetFixtures.Load(translation.Source!), "F"), call);
    Assert.That(actual, Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), translation.Source);
  }

  [Test]
  public void WriteLine_IsConsoleWriteLine() {
    var translation = VbNetFixtures.Snippet("public static void F(int n) { System.Console.WriteLine(\"n\" + n); }");
    Assert.That(translation.Source, Does.Contain("System.Console.WriteLine("));
    Assert.That(VbNetFixtures.Complaints(translation.Source!, VisualBasicProfile.VbNetLatest), Is.Empty);
  }

  [TestCase("vbnet/vb16.9/net4.8")]
  [TestCase("vbnet/vb16.9/net2.0")]
  public void FloatBits_BelowNetStandard21_GoThroughTheBytes(string profile) {
    var translation = VbNetFixtures.Snippet("public static float F(int n) { float f = n; return System.BitConverter.Int32BitsToSingle(System.BitConverter.SingleToInt32Bits(f) + 1); }", VisualBasicProfile.Parse(profile));
    Assert.That(translation.Source, Does.Contain("System.BitConverter.GetBytes").And.Not.Contain("SingleToInt32Bits"));
    var call = new PrototypeCall("F", [7]);
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static float F(int n) { float f = n; return System.BitConverter.Int32BitsToSingle(System.BitConverter.SingleToInt32Bits(f) + 1); }"));
    Assert.That(PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(VbNetFixtures.Load(translation.Source!), "F"), call), Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)));
  }

  [Test]
  public void StructWithoutFields_IsPly2656() {
    var translation = VbNetFixtures.Translate(PrototypeFixture.Import("Snippet.cs", "public struct E { } public static class Snippet { public static int F(int n) { E e = new E(); return n; } }"));
    Assert.That(translation.Problems, Has.Some.Contains("PLY2656").And.Some.Contains("no fields"));
  }

}
