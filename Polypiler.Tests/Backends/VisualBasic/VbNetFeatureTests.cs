using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// The fixtures of records, strings and exceptions through the Visual Basic .NET backend, compiled in
/// memory, against the IR interpreter; and the constructs the backend refuses, each with its code.
/// </summary>
[TestFixture]
public sealed class VbNetFeatureTests {

  private static void Compare(IrModule module, IEnumerable<PrototypeCall> calls, string what) {
    var translation = VbNetFixtures.Translate(module);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var assembly = VbNetFixtures.Load(translation.Source!);
    var mismatches = new List<string>();
    foreach (var call in calls) {
      var expected = PrototypeFixture.InterpretedOutcome(module, call);
      var actual = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(assembly, call.Method), call);
      if (actual != expected)
        mismatches.Add($"{call}: expected {expected}, actual {actual}");
    }

    Assert.That(mismatches, Is.Empty, what + "\n" + string.Join("\n", mismatches.Take(20)) + "\n" + translation.Source);
  }

  private static IEnumerable<string> Records() => RecordFixtures.CSharp.Select(fixture => fixture.File);

  [TestCaseSource(nameof(Records))]
  public void RecordFixture_ComputesWhatTheInterpreterComputes(string file)
    => Compare(RecordFixtures.Imported(file), RecordFixtures.Calls().Where(call => call.File == file).Select(call => call.Call), file);

  private static IEnumerable<string> Strings() => StringFixtures.CSharp.Where(name => name is "Fnv1aText" or "Hex");

  [TestCaseSource(nameof(Strings))]
  public void StringFixture_ComputesWhatTheInterpreterComputes(string name) {
    var module = PrototypeFixture.Import(name + ".cs", System.IO.File.ReadAllText(StringFixtures.PathOf(name + ".cs")));
    Compare(module, StringFixtures.CSharpCalls().Where(call => call.Class == name).Select(call => call.Call), name);
  }

  /// <summary>The string fixtures that split and pad as ECMA-262 does, and the exceptions of classes of their own, are refused.</summary>
  [Test]
  public void FixturesBeyondTheSubset_AreRefused() {
    var tokens = PrototypeFixture.Import("Tokens.cs", System.IO.File.ReadAllText(StringFixtures.PathOf("Tokens.cs")));
    Assert.That(VbNetFixtures.Translate(tokens).Problems, Has.All.Contains("PLY2657").And.Some.Contains("StrSplit"));
    Assert.That(VbNetFixtures.Translate(ExceptionFixtures.Imported("Traps")).Problems, Has.Some.Contains("PLY2656"));
  }

  /// <summary>Snippets of C# whose IR the backend writes with its helpers: steps, assignments as values, throws as values, checked operations.</summary>
  [TestCase("static int F(int n, int i) { var a = new int[n + 3]; var x = a[i]++; a[i]--; --a[i]; return x + ++a[i]; }", 2)]
  [TestCase("static long F(int n, int i) { long a = n; checked { a++; return a * i + (-a); } }", 3)]
  [TestCase("static int F(int a, int i) { checked { return a + i - (a * 2); } }", 4)]
  [TestCase("static uint F(int n, int i) { uint a = (uint)n; checked { return a + (uint)i; } }", 5)]
  [TestCase("static byte F(int n, int i) { byte a = (byte)n; checked { a++; return (byte)(a + i); } }", 6)]
  [TestCase("static byte F(int n, int i) { byte a = (byte)n; a--; a++; double d = n; d++; return a; }", 0)]
  [TestCase("static int F(int a, int i) { return a > i ? a : throw new System.InvalidOperationException(\"no\"); }", 7)]
  [TestCase("static int F(int a, int i) { switch (a) { case 1: case 2: return 10; case 3: break; default: return i; } return -1; }", 3)]
  [TestCase("static int F(int a, int i) { int s = 0; for (int k = 0; k < a; k++) { if (k == i) continue; s += k; } return s; }", 9)]
  [TestCase("static int F(int a, int i) { try { return a / i; } catch (System.DivideByZeroException) { return -1; } finally { } }", 0)]
  [TestCase("static double F(int a, int i) { double d = a; return System.Math.Max(d, i) + System.Math.Min(-0.0, 0.0) + System.Math.Round(d / 3) + System.Math.Floor(-d / 4) + System.Math.Sqrt(d) + System.Math.Abs(-d); }", 5)]
  [TestCase("static float F(int a, int i) { float f = a; return System.MathF.Sqrt(f) + System.Math.Max(f, i) + (float)System.Math.Truncate(f / 3); }", 7)]
  [TestCase("static string F(int a, int i) { string s = \"ab\" + a + \"\\n\" + (char)(65 + i); return s + s.Length + s.IndexOf('b') + s.Substring(1, 2) + (s == \"x\") + s.Replace(\"a\", \"z\") + s.Contains(\"b\"); }", 2)]
  [TestCase("static long F(int a, int i) { ulong u = (ulong)a; uint w = (uint)i; return (long)(u >> 1) + (a >>> 1) + (long)(w % 3) + (long)(sbyte)a + (short)i; }", -3)]
  [TestCase("static int F(int a, int i) { byte[] b = new byte[a]; b[0] = (byte)i; System.Array.Clear(b); return b[0] + b.Length; }", 4)]
  [TestCase("static int F(int a, int i) { long x = a; ulong y = (ulong)i; int[] t = new int[3]; t[(int)(x % 3)] = 7; return t[(int)(x % 3)] + (int)y + (int)(float)a + (int)(double)i; }", 5)]
  public void Snippet_ComputesWhatTheInterpreterComputes(string method, int argument) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public " + method));
    Compare(module, [new PrototypeCall("F", [argument, 3])], method);
  }

  [TestCase("static int F(int a) { return a.ToString().Length; }", 12)]
  public void Snippet_WithArraysOfArraysOrText_ComputesWhatTheInterpreterComputes(string method, int argument) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public " + method));
    Compare(module, [new PrototypeCall("F", [argument])], method);
  }

  [TestCase("public static double F(double x) { return x.ToString(System.Globalization.CultureInfo.InvariantCulture).Length; }", "vbnet/vb16.9/net4.8", "PLY2657")]
  [TestCase("public static string F(string s) { return s.Trim(); }", "vbnet/vb16.9/net3.5", "PLY2657")]
  [TestCase("public static string[] F(string s) { return s.Split(','); }", "vbnet/vb16.9", "PLY2657")]
  [TestCase("public static int F(System.Collections.Generic.List<int> l) { return l.Count; }", "vbnet/vb16.9", "PLY2656")]
  [TestCase("public static int f(int a) { return a; } public static int F(int a) { return a; }", "vbnet/vb16.9", "PLY2658")]
  public void Construct_TheProfileCannotWrite_IsRefused(string methods, string profile, string code) {
    var translation = VbNetFixtures.Snippet(methods, VisualBasicProfile.Parse(profile));
    Assert.That(translation.Problems, Has.Some.Contains(code));
  }

  [Test]
  public void Emit_OfAModuleItCannotWrite_Throws() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static string[] F(string s) { return s.Split(','); }"));
    Assert.That(() => VisualBasicEmitter.EmitNet(module, VisualBasicProfile.VbNetLatest, FloatMode.Accurate, null), Throws.ArgumentException);
    var broken = new IrModule("M", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]))]);
    Assert.That(VisualBasicEmitter.Check(broken, VisualBasicProfile.VbNetLatest), Is.Not.Empty);
    Assert.That(() => VisualBasicEmitter.BuildNet(broken, VisualBasicProfile.VbNetLatest, FloatMode.Accurate, null), Throws.ArgumentException);
    Assert.That(VisualBasicEmitter.NetFileName(module), Is.EqualTo("Snippet.vb"));
  }

}
