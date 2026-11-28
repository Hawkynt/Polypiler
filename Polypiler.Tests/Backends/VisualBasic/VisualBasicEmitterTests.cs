using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.VisualBasic;
using Polypiler.Core.Backends.VisualBasic.Passes;
using Polypiler.Core.Backends.VisualBasic.Printing;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>
/// What the Visual Basic backend writes for each construct of the IR: the form, that the printed module
/// reads back to the tree it was printed from, and the code of every construct it refuses.
/// </summary>
[TestFixture]
public sealed class VisualBasicEmitterTests {

  private static string Emitted(string methods, VisualBasicProfile? profile = null) {
    var translation = VisualBasicFixtures.Snippet(methods, profile);
    Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
    var read = VbSyntaxReader.ReadModule(translation.Source!);
    Assert.That(VbSyntaxRewriter.Shape(read), Is.EqualTo(VbSyntaxRewriter.Shape(translation.Tree!)), translation.Source);
    return translation.Source!;
  }

  private static string Refused(string methods, VisualBasicProfile? profile = null) {
    var translation = VisualBasicFixtures.Snippet(methods, profile);
    Assert.That(translation.Source, Is.Null);
    return string.Join("\n", translation.Problems);
  }

  [TestCase("public static int F(int a, int b) => a + b;", "PlyAdd32(a, b)")]
  [TestCase("public static int F(int a, int b) => a - b;", "PlySub32(a, b)")]
  [TestCase("public static int F(int a, int b) => a * b;", "PlyMul32(a, b)")]
  [TestCase("public static int F(int a, int b) => checked(a + b);", "CLng(a + b)")]
  [TestCase("public static int F(int a, int b) => checked(a - b);", "CLng(a - b)")]
  [TestCase("public static int F(int a, int b) => checked(a * b);", "CLng(a * b)")]
  [TestCase("public static int F(int a, int b) => a / b;", "PlyDiv32(a, b)")]
  [TestCase("public static int F(int a, int b) => a % b;", "PlyRem32(a, b)")]
  [TestCase("public static int F(int a, int b) => a << b;", "PlyShl32(a, b)")]
  [TestCase("public static int F(int a, int b) => a >> b;", "PlySar32(a, b)")]
  [TestCase("public static int F(int a, int b) => a >>> b;", "PlyShr32(a, b)")]
  [TestCase("public static int F(int a) => -a;", "PlyNeg32(a)")]
  [TestCase("public static int F(int a) => checked(-a);", "CLng(-a)")]
  [TestCase("public static int F(int a) => ~a;", "Not a")]
  [TestCase("public static int F(int a, int b) => a & b | a ^ b;", "a And b Or (a Xor b)")]
  [TestCase("public static uint F(uint a, uint b) => a / b;", "PlyDivU32(a, b)")]
  [TestCase("public static uint F(uint a, uint b) => a % b;", "PlyRemU32(a, b)")]
  [TestCase("public static uint F(uint a, uint b) => checked(a + b);", "PlyAddU32(a, b)")]
  [TestCase("public static uint F(uint a, uint b) => checked(a - b);", "PlySubU32(a, b)")]
  [TestCase("public static uint F(uint a, uint b) => checked(a * b);", "PlyMulU32(a, b)")]
  [TestCase("public static uint F(uint a, int b) => a >> b;", "PlyShr32(a, b)")]
  [TestCase("public static bool F(uint a, uint b) => a < b;", "(a Xor &H80000000) < (b Xor &H80000000)")]
  [TestCase("public static bool F(uint a, uint b) => a == b;", "a = b")]
  [TestCase("public static uint F() => 0xEDB88320;", "&HEDB88320")]
  [TestCase("public static int F() => int.MinValue;", "&H80000000")]
  public void Integers_AreWrittenWithHelpersWhereVisualBasicWouldTrap(string methods, string expected)
    => Assert.That(Emitted(methods), Does.Contain(expected));

  [TestCase("public static short F(int a) => (short)a;", "PlyWrap16(a)")]
  [TestCase("public static short F(int a) => checked((short)a);", "CInt(a)")]
  [TestCase("public static sbyte F(int a) => (sbyte)a;", "PlyWrap8(a)")]
  [TestCase("public static sbyte F(int a) => checked((sbyte)a);", "PlyChk8(a)")]
  [TestCase("public static byte F(int a) => (byte)a;", "CByte(a And 255)")]
  [TestCase("public static byte F(int a) => checked((byte)a);", "CByte(a)")]
  [TestCase("public static ushort F(int a) => (ushort)a;", "a And 65535&")]
  [TestCase("public static ushort F(int a) => checked((ushort)a);", "PlyChkU16(a)")]
  [TestCase("public static uint F(int a) => checked((uint)a);", "PlyChkSign32(a)")]
  [TestCase("public static int F(uint a) => checked((int)a);", "PlyChkSign32(a)")]
  [TestCase("public static short F(uint a) => checked((short)a);", "CInt(PlyChkSign32(a))")]
  [TestCase("public static sbyte F(short a) => checked((sbyte)a);", "PlyChk8(CLng(a))")]
  [TestCase("public static ushort F(short a) => checked((ushort)a);", "PlyChkU16(CLng(a))")]
  [TestCase("public static ushort F(sbyte a) => (ushort)a;", "CLng(a) And 65535&")]
  [TestCase("public static sbyte F(short a) => (sbyte)a;", "PlyWrap8(CLng(a))")]
  [TestCase("public static short F(sbyte a) => a;", "CInt(a)")]
  [TestCase("public static int F(byte a) => a;", "CLng(a)")]
  [TestCase("public static int F(ushort a) => a;", "F = a")]
  [TestCase("public static uint F(int a) => (uint)a;", "F = a")]
  [TestCase("public static double F(uint a) => a;", "PlyU32(a)")]
  [TestCase("public static float F(uint a) => a;", "CSng(PlyU32(a))")]
  [TestCase("public static double F(int a) => a;", "CDbl(a)")]
  [TestCase("public static float F(double a) => (float)a;", "CSng(a)")]
  [TestCase("public static double F(float a) => a;", "CDbl(a)")]
  [TestCase("public static int F(double a) => checked((int)a);", "CLng(Fix(a))")]
  [TestCase("public static short F(double a) => checked((short)a);", "CInt(Fix(a))")]
  [TestCase("public static byte F(double a) => checked((byte)a);", "CByte(Fix(a))")]
  [TestCase("public static sbyte F(double a) => checked((sbyte)a);", "PlyChk8(CLng(Fix(a)))")]
  [TestCase("public static ushort F(double a) => checked((ushort)a);", "PlyChkU16(CLng(Fix(a)))")]
  [TestCase("public static uint F(double a) => checked((uint)a);", "PlyToU32(a)")]
  [TestCase("public static int F(double a) => (int)a;", "PlySat(a")]
  [TestCase("public static uint F(double a) => (uint)a;", "PlyWrap32(PlySat(a")]
  [TestCase("public static byte F(double a) => (byte)a;", "CByte(CLng(PlySat(a")]
  [TestCase("public static short F(double a) => (short)a;", "PlyWrap16(CLng(PlySat(a")]
  public void Conversions_UseTheConversionFunctionOrAHelper(string methods, string expected)
    => Assert.That(Emitted(methods), Does.Contain(expected));

  [TestCase("public static double F(double a, double b) => a * b + a / b - a;", "a * b + a / b - a")]
  [TestCase("public static float F(float a, float b) => a * b;", "CSng(a * b)")]
  [TestCase("public static double F(double a) => System.Math.Sqrt(a);", "Sqr(a)")]
  [TestCase("public static float F(float a) => System.MathF.Sqrt(a);", "CSng(Sqr(a))")]
  [TestCase("public static double F(double a) => System.Math.Floor(a) + System.Math.Ceiling(a) + System.Math.Truncate(a);", "Int(a) + (-Int(-a)) + Fix(a)")]
  [TestCase("public static double F(double a) => System.Math.Round(a);", "Round(a)")]
  [TestCase("public static double F(double a) => System.Math.Round(a, System.MidpointRounding.AwayFromZero);", "PlyRoundAway(a)")]
  [TestCase("public static double F(double a) => System.Math.Abs(a);", "Abs(a)")]
  [TestCase("public static double F(double a, double b) => System.Math.Min(a, b) + System.Math.Max(a, b);", "PlyFMin(a, b) + PlyFMax(a, b)")]
  [TestCase("public static int F(double a) => System.Math.Sign(a);", "CLng(Sgn(a))")]
  [TestCase("public static double F() => 0.1;", "0.1")]
  [TestCase("public static float F() => 0.5f;", "0.5!")]
  [TestCase("public static bool F(double a, double b) => a <= b;", "a <= b")]
  public void Floats_AreVisualBasicsOwn(string methods, string expected)
    => Assert.That(Emitted(methods), Does.Contain(expected));

  [Test]
  public void ShortCircuit_IsAnIfWhereTheRightOperandCanTrap() {
    var text = Emitted("public static bool F(int[] a, int i) => i < a.Length && a[i] > 0;");
    Assert.That(text, Does.Contain("If test Then"));
    var pure = Emitted("public static bool F(int a, int b) => a > 0 || b > 0;");
    Assert.That(pure, Does.Contain("a > 0 Or b > 0"));
    var or = Emitted("public static bool F(int[] a, int i) => i >= a.Length || a[i] > 0;");
    Assert.That(or, Does.Contain("If Not test Then"));
  }

  [Test]
  public void Conditional_IsIIfOrAnIf() {
    Assert.That(Emitted("public static int F(bool c, int a, int b) => c ? a : b;"), Does.Contain("CLng(IIf(c, a, b))"));
    Assert.That(Emitted("public static int F(bool c, int a, int b) => c ? a / b : b;"), Does.Contain("If c Then"));
    Assert.That(Emitted("public static string F(bool c) => c ? \"x\" : \"y\";"), Does.Contain("CStr(IIf(c, \"x\", \"y\"))"));
  }

  [Test]
  public void Loops_AreDoLoopsWithLabelsForContinue() {
    var text = Emitted("""
      public static int F(int n) {
        int s = 0;
        for (int i = 0; i < n; i++) {
          if (i == 3) continue;
          if (i == 7) break;
          s += i;
        }
        int j = 0;
        do { j++; if (j == 2) continue; } while (j < 5);
        while (true) { j--; if (j < 0) break; }
        return s + j;
      }
      """);
    Assert.That(text, Does.Contain("Do While i < n").And.Contain("GoTo Continue").And.Contain("Continue:").And.Contain("Exit Do").And.Contain("Loop While j < 5").And.Contain("Do\n"));
  }

  [Test]
  public void LoopCondition_ThatNeedsStatements_IsTestedInsideTheLoop() {
    var text = Emitted("public static int F(int[] a) { int i = 0; while (i < a.Length && a[i] != 0) i++; return i; }");
    Assert.That(text, Does.Contain("If Not test Then").And.Contain("Exit Do"));
    var post = Emitted("public static int F(int[] a) { int i = 0; do { i++; } while (i < a.Length && a[i] != 0); return i; }");
    Assert.That(post, Does.Contain("Exit Do"));
  }

  [Test]
  public void Switch_IsSelectCase() {
    var text = Emitted("""
      public static int F(int v, int w) {
        switch (v) {
          case 1: case 2: return 10;
          case 3:
            if (w > 0) break;
            return 30;
          default: return 0;
        }
        for (int i = 0; i < 3; i++) {
          switch (i) { case 1: continue; default: break; }
        }
        return -1;
      }
      """);
    Assert.That(text, Does.Contain("Select Case v").And.Contain("Case 1, 2").And.Contain("Case Else").And.Contain("GoTo EndSelect").And.Contain("EndSelect:"));
  }

  [Test]
  public void Exceptions_AreOnErrorGoToWithADispatch() {
    var text = Emitted("""
      public static int F(int a, int b) {
        try {
          for (int i = 0; i < 3; i++) {
            try { if (i == 1) break; return a / b; }
            catch (System.OverflowException) { return -2; }
          }
          throw new System.IndexOutOfRangeException();
        } catch (System.DivideByZeroException) {
          return -1;
        } catch (System.ArithmeticException) {
          throw;
        } catch (System.ArgumentException) {
          return -3;
        } catch (System.Exception) {
          return -4;
        }
      }
      """);
    Assert.That(text, Does.Contain("On Error GoTo Catch").And.Contain("Resume Dispatch").And.Contain("Err.Raise 9").And.Contain("errNumber2 = 11")
      .And.Contain("errNumber2 = 6 Or errNumber2 = 11").And.Contain("Err.Raise errNumber").And.Contain("On Error GoTo 0"));
    var uncaught = Emitted("public static int F(int a) { try { return 10 / a; } catch (System.DivideByZeroException) { return 0; } }");
    Assert.That(uncaught, Does.Contain("Err.Raise errNumber"));
  }

  [Test]
  public void Strings_UseTheBuiltInsAndHelpers() {
    var text = Emitted("""
      public static int F(string s, string t) {
        var r = s + t + "\n";
        if (s == t) return 1;
        return r.Length + s[0] + (string.CompareOrdinal(s, t) < 0 ? 1 : 0) + s.Substring(1, 2).Length + s.IndexOf(t, System.StringComparison.Ordinal) + s.IndexOf('x', 1) + s.Substring(1).Length;
      }
      public static string G(char c, int n) => new string(c, n) + c;
      """);
    Assert.That(text, Does.Contain("s & t & ChrW(10)").And.Contain("Len(").And.Contain("PlyCharAt(s, 0)").And.Contain("PlyCompare(s, t)")
      .And.Contain("PlySubstring(s, 1, 2)").And.Contain("PlyIndexOf(s, t, 0)").And.Contain("PlyIndexOf(s, ChrW(120), 1)").And.Contain("PlyRepeat(ChrW(c), n)").And.Contain("ChrW(c)"));
  }

  [Test]
  public void ClassicModule_RoundTripsThroughTheIr() {
    var source = System.IO.File.ReadAllText(System.IO.Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic", "Numeric.bas"));
    var imported = Polypiler.Core.Frontends.VisualBasic.VisualBasicFrontend.Import("Numeric.bas", source).Module!;
    foreach (var profile in VisualBasicProfile.All) {
      var translation = VisualBasicFixtures.Translate(imported, profile);
      Assert.That(translation.Problems, Is.Empty, string.Join("\n", translation.Problems));
      Assert.That(VbSyntaxRewriter.Shape(VbSyntaxReader.ReadModule(translation.Source!)), Is.EqualTo(VbSyntaxRewriter.Shape(translation.Tree!)));
      Assert.That(translation.Source, Does.Contain("CStr(").And.Contain("On Error GoTo Catch").And.Contain("Err.Raise").And.Contain("Private Sub PlyInit()").And.Contain("PlyIndexOf("));
    }
  }

  [Test]
  public void Arrays_AreDynamicArraysFromZero() {
    var text = Emitted("""
      public static int[] Make(int n) { var a = new int[n]; a[0] = 1; return a; }
      public static int F(int n) {
        var a = Make(n);
        var b = new[] { 1, 2, 3 };
        System.Array.Copy(b, 0, a, 1, 2);
        System.Array.Fill(a, 7);
        System.Array.Clear(a);
        a[1]++;
        int x = a[0]++ + --a[1];
        return a.Length + b[1] + Make(2)[0] + Make(3).Length + x;
      }
      """);
    Assert.That(text, Does.Contain("PlyNew_Long(n)").And.Contain("Public Function Make(ByVal n As Long) As Variant").And.Contain("PlyCopy_Long").And.Contain("PlyFill_Long").And.Contain("PlyLen_Long(a)")
      .And.Contain("Dim a() As Long"));
  }

  [Test]
  public void Records_AreUserDefinedTypes() {
    var text = Emitted("""
      public struct P { public int X; public short Y; }
      public struct Q { public P A; public P B; }
      public static P Make(int x) => new P { X = x, Y = 2 };
      public static int Move(P p) { p.X += 1; return p.X; }
      public static bool F(int x) {
        var q = new Q { A = Make(x), B = default };
        q.A.X = 4;
        var z = default(P);
        return Move(q.A) == z.X;
      }
      """);
    Assert.That(text, Does.Contain("Public Type P").And.Contain("Public Type Q").And.Contain("PlyZero_P()").And.Contain("ByRef p2In As P").And.Contain("q2.A.X = 4"));
  }

  [Test]
  public void ModuleState_IsInitializedOnFirstUse() {
    var text = Emitted("""
      private static int calls = 5;
      private static readonly int[] Table = { 1, 2, 3, 4 };
      private const string Name = "x";
      public static int F(int i) { calls++; return Table[i] + calls + Table.Length + Name.Length; }
      """);
    Assert.That(text, Does.Contain("Private Sub PlyInit()").And.Contain("If Not PlyReady Then").And.Contain("PlyReady = True"));
  }

  [Test]
  public void LongLong_IsWrittenOnlyForTheSixtyFourBitHost() {
    const string methods = "public static long F(long a, long b) => checked(a + b) ^ (a & b) | 5L;";
    Assert.That(Refused(methods), Does.Contain("PLY2656").And.Contain("LongLong"));
    var text = Emitted(methods, VisualBasicProfile.Vba7x64);
    Assert.That(text, Does.Contain("As LongLong").And.Contain("CLngLng(a + b)").And.Contain("5^"));
    Assert.That(Emitted("public static bool F(ulong a, ulong b) => a < b;", VisualBasicProfile.Vba7x64), Does.Contain("&H8000000000000000^"));
    Assert.That(Emitted("public static long F(int a) => a;", VisualBasicProfile.Vba7x64), Does.Contain("CLngLng(a)"));
    Assert.That(Emitted("public static long F(uint a) => a;", VisualBasicProfile.Vba7x64), Does.Contain("CLngLng(PlyU32(a))"));
    Assert.That(Emitted("public static int F(long a) => (int)a;", VisualBasicProfile.Vba7x64), Does.Contain("PlyWrap32(CDbl(a And"));
    Assert.That(Emitted("public static int F(long a) => checked((int)a);", VisualBasicProfile.Vba7x64), Does.Contain("CLng(a)"));
    Assert.That(Emitted("public static long F(double a) => checked((long)a);", VisualBasicProfile.Vba7x64), Does.Contain("CLngLng(Fix(a))"));
    Assert.That(Refused("public static long F(long a, long b) => a + b;", VisualBasicProfile.Vba7x64), Does.Contain("PLY2657"));
  }

  [TestCase("public class C { public int X; } public static int F(C c) => c.X;", "PLY2656")]
  [TestCase("public static System.Func<int, int> F() => x => x + 1;", "PLY265")]
  [TestCase("public static int F(int a) { try { return a; } finally { a = 0; } }", "PLY2655")]
  [TestCase("public static int F(int a) => throw new System.InvalidOperationException(\"m\");", "PLY2657")]
  [TestCase("public static double F() => double.PositiveInfinity;", "PLY2657")]
  [TestCase("public static double F(double a, double b) => a % b;", "PLY2657")]
  [TestCase("public static int F(int[] a) { int[] b = a; b[0] = 1; return a[0]; }", "PLY2657")]
  [TestCase("public static void F(int[] a) { a = new int[1]; }", "PLY2657")]
  [TestCase("public static int F(int? a) => a ?? 0;", "PLY2656")]
  [TestCase("public static string F(string s) => s.Trim();", "PLY2657")]
  public void ConstructsWithoutAVisualBasicForm_AreRefusedByName(string methods, string code)
    => Assert.That(Refused(methods), Does.Contain(code));

  [Test]
  public void PublicNamesThatDifferOnlyInCase_Collide() {
    var module = new IrModule("M", [
      new IrFunction("Run", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))])),
      new IrFunction("run", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 2))]))
    ]);
    Assert.That(VisualBasicEmitter.Check(module, VisualBasicProfile.Default).Select(problem => problem.Code), Does.Contain(VisualBasicEmitter.NameCollision));
    Assert.That(() => VisualBasicEmitter.Emit(module), Throws.ArgumentException);
  }

  [Test]
  public void ReservedNames_AreRenamed() {
    var text = Emitted("public static int Len(int Dim, int ply) { int Mod = Dim + ply; return Mod; }");
    Assert.That(text, Does.Contain("Public Function Len_(ByVal Dim_ As Long, ByVal ply_ As Long) As Long"));
  }

  [Test]
  public void HelpersNothingCalls_AreRemovedByThePass() {
    var module = VbSyntaxReader.ReadModule("Attribute VB_Name = \"M\"\nOption Explicit\n\nPublic Function F() As Long\n    F = PlyA()\nEnd Function\n\nPrivate Function PlyA() As Long\n    PlyA = 1\nEnd Function\n\nPrivate Function PlyB() As Long\n    PlyB = PlyC()\nEnd Function\n\nPrivate Function PlyC() As Long\n    PlyC = 1\nEnd Function\n");
    module = module with { Declarations = [.. module.Declarations.Select(declaration => declaration is VbProcedure { Name: not "F" } helper ? helper with { IsHelper = true } : declaration)] };
    var applied = new List<string>();
    var result = new VisualBasicUnusedDeclarations().Run(module, VisualBasicProfile.Default, applied.Add);
    Assert.That(result.Declarations.OfType<VbProcedure>().Select(procedure => procedure.Name), Is.EqualTo(new[] { "F", "PlyA" }));
    Assert.That(applied, Has.Count.EqualTo(2));
    Assert.That(new VisualBasicUnusedDeclarations().Run(result, VisualBasicProfile.Default, applied.Add), Is.SameAs(result));
    Assert.That(new VisualBasicUnusedDeclarations().Id, Is.EqualTo("O0501"));
    Assert.That(new VisualBasicUnusedDeclarations().Name, Is.EqualTo("unused declaration removal"));
  }

  [Test]
  public void EmitterEntryPoints_CheckTheirArguments() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static int F(int a) => a;"));
    Assert.That(VisualBasicEmitter.FileName(module), Is.EqualTo("Snippet.bas"));
    Assert.That(VisualBasicEmitter.Emit(module).Single().Content, Does.Contain("Attribute VB_Name = \"Snippet\""));
    Assert.That(VisualBasicEmitter.FunctionNames(module).Values, Does.Contain("F"));
    Assert.That(VisualBasicEmitter.HelperOrigins, Does.ContainKey("PlyAdd32"));
    Assert.That(VisualBasicEmitter.ErrorNumbers[IrTrap.DivideByZero], Is.EqualTo(11));
    Assert.That(() => VisualBasicEmitter.FileName(null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.Check(null!, VisualBasicProfile.Default), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.Check(module, null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.FunctionNames(null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.Writes(null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicEmitter.Legalize(module, () => module, null!, FloatMode.Accurate), Throws.ArgumentNullException);
    var broken = new IrModule("B", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]))]);
    Assert.That(VisualBasicEmitter.Check(broken, VisualBasicProfile.Default), Is.Not.Empty);
    Assert.That(() => VisualBasicEmitter.Emit(broken), Throws.ArgumentException);
  }

  [Test]
  public void Profiles_NameTheirDialects() {
    Assert.That(VisualBasicProfile.Names.Take(4), Is.EqualTo(new[] { "visualbasic/vb6", "visualbasic/vba6", "visualbasic/vba7", "visualbasic/vba7-64" }));
    Assert.That(VisualBasicProfile.Parse(null), Is.SameAs(VisualBasicProfile.Vba7));
    Assert.That(VisualBasicProfile.Parse("VBA6"), Is.SameAs(VisualBasicProfile.Vba6));
    Assert.That(VisualBasicProfile.Parse("visualbasic/vb6").ToString(), Is.EqualTo("visualbasic/vb6"));
    Assert.That(VisualBasicProfile.TryParse("vba8", out _, out var error), Is.False);
    Assert.That(error, Does.Contain("vba7-64"));
    Assert.That(() => VisualBasicProfile.Parse("x"), Throws.ArgumentException);
    Assert.That(VisualBasicProfile.Vba7x64.Title, Does.Contain("64-bit"));
  }

  [Test]
  public void CapabilityTable_ListsTheDialectsOfEachConstruct() {
    var table = VisualBasicCapabilities.Standard;
    Assert.That(table.Has(VisualBasicCapabilities.LongLong, VisualBasicProfile.Vba7x64), Is.True);
    Assert.That(table.Has(VisualBasicCapabilities.LongLong, VisualBasicProfile.Vba7), Is.False);
    Assert.That(table.Has(VisualBasicCapabilities.LongPtr, VisualBasicProfile.Vba7), Is.True);
    Assert.That(table.Missing(VisualBasicProfile.Vb6), Is.EqualTo(new[] { VisualBasicCapabilities.LongLong, VisualBasicCapabilities.LongPtr }));
    Assert.That(table[VisualBasicCapabilities.LongPtr].Replacement, Is.EqualTo("Long"));
    Assert.That(() => table["x"], Throws.ArgumentException);
    Assert.That(() => table.Has(VisualBasicCapabilities.LongLong, null!), Throws.ArgumentNullException);
    Assert.That(() => new VisualBasicCapabilities([new("a", []), new("a", [])]), Throws.ArgumentException);
    Assert.That(() => new VisualBasicCapabilities(null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Naming_KeepsNamesValidAndFree() {
    Assert.That(VisualBasicNaming.Module("Ns.A-Very.LongModuleNameThatGoesOnAndOnBeyondThirtyOne"), Has.Length.EqualTo(31));
    Assert.That(VisualBasicNaming.Valid("_x", "f"), Is.EqualTo("x"));
    Assert.That(VisualBasicNaming.Valid("1a", "f"), Is.EqualTo("x1a"));
    Assert.That(VisualBasicNaming.Valid("__", "f"), Is.EqualTo("f"));
    Assert.That(VisualBasicNaming.Valid("Loop", "f"), Is.EqualTo("Loop_"));
    Assert.That(VisualBasicNaming.Valid(new string('a', 300), "f"), Has.Length.EqualTo(255));
    Assert.That(VisualBasicNaming.Free("a", name => name is "a" or "a2"), Is.EqualTo("a3"));
    Assert.That(() => VisualBasicNaming.Module(null!), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicNaming.Valid(null!, "f"), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicNaming.Free(null!, _ => false), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicNaming.Free("a", null!), Throws.ArgumentNullException);
  }

  [Test]
  public void TargetPasses_CanBeTurnedOff() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static int F(int a, int b) => a + b;"));
    var translation = VisualBasicFixtures.Translate(module, null, TargetPassOptions.None);
    Assert.That(translation.Source, Does.Contain("PlyAdd32"));
    Assert.That(VbPrinter.Print(translation.Tree!), Is.EqualTo(translation.Source));
  }

}
