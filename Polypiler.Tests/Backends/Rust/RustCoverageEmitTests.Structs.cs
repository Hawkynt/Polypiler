using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Structs, constants and collections: the fields of array elements written in place and the values
/// that may not move past the index check, the constants Rust keeps as its own text and those it
/// refuses, the loops over and the searches in lists, and a kind of declaration the backend does not know.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  private const string Points = "public struct P { public int X; public double D; } public struct Q { public P A; public int Y; } ";

  [Test]
  public void FieldOfAnElement_UpdatedByItself_IsCompound_AndSettledValuesAreStored()
    => Assert.That(EmittedFile(Points + "public static class S { public static void F(P[] p, int i, int v) { p[i].X = p[i].X ^ v; p[0].X = -v; p[i].X = ~v; p[1].D = p[1].D * 2.0; } }"),
      Does.Contain("    p[i as usize].x ^= v;\n    p[0].x = v.wrapping_neg();\n    p[i as usize].x = !v;\n    p[1].d *= 2.0;\n"));

  [Test]
  public void FieldOfANestedStructOfAnElement_IsWrittenThroughThePlace()
    => Assert.That(EmittedFile(Points + "public static class S { public static void F(Q[] q, int i, int v, bool b) { q[i].A.X = v; q[i].Y = b ? v : 1; q[i].A.D = (double)v; } }"),
      Does.Contain("    q[i as usize].a.x = v;\n    q[i as usize].y = if b { v } else { 1 };\n    q[i as usize].a.d = v as f64;\n"));

  [Test]
  public void FieldOfAnElement_SetToElementsOfOtherArrays_IsStored()
    => Assert.That(EmittedFile(Points + "public static class S { public static void F(P[] p, int i, int[] a) { p[i].X = a[i] + p[0].X; } }"),
      Does.Contain("    p[i as usize].x = a[i as usize].wrapping_add(p[0].x);\n"));

  [Test]
  public void FieldOfAVariablesNestedStruct_UpdatedByItself_IsCompound()
    => Assert.That(EmittedFile(Points + "public static class S { public static int F(Q q, int v) { q.A.X = q.A.X | v; q.Y = q.Y & v; return q.A.X + q.Y; } }"),
      Does.Contain("    let mut q = q;\n    q.a.x |= v;\n    q.y &= v;\n"));

  [TestCase("p[i].X = v / i;", TestName = "FieldOfAnElement_SetToAQuotient_IsRefused")]
  [TestCase("p[i].X = checked(v + 1);", TestName = "FieldOfAnElement_SetToACheckedSum_IsRefused")]
  [TestCase("p[G()].X = v;", TestName = "FieldOfAnElement_AtACalledIndex_IsRefused")]
  [TestCase("p[i].X = G();", TestName = "FieldOfAnElement_SetToACall_IsRefused")]
  [TestCase("p[i].X = checked((int)(double)v);", TestName = "FieldOfAnElement_SetToATrappingConversion_IsRefused")]
  public void FieldOfAnElement_SetToAValueThatMayTrapOrActFirst_IsRefused(string store)
    => Assert.That(ProblemsOfFile(Points + $"public static class S {{ static int G() => 1; public static void F(P[] p, int i, int v) {{ {store} }} }}"),
      Has.Some.StartWith("PLY1550 'a field of an array element written with a value that may trap or have an effect' cannot be expressed"));

  // Constants.

  [TestCase("static readonly long[] T = { 1, 2 }; public static long F(int i) => T[i];", "static T: [i64; 2] = [1, 2];", TestName = "ConstantTable_OfLongs_IsAStaticArray")]
  [TestCase("static readonly double[] T = { 1.5, 2 }; public static double F(int i) => T[i];", "static T: [f64; 2] = [1.5, 2.0];", TestName = "ConstantTable_OfDoubles_IsAStaticArray")]
  public void ConstantTable_IsAStaticArray(string methods, string expected) => Assert.That(Emitted(methods), Does.Contain(expected));

  [TestCase("static readonly string T = \"\\uD800x\"; public static int F() => T.Length;", "constant of type str", TestName = "ConstantText_WithALoneSurrogate_IsRefused")]
  [TestCase("static readonly string[] T = { \"a\", \"\\uD800\" }; public static int F(int i) => T[i].Length;", "constant of type str[]", TestName = "ConstantTable_OfTextWithALoneSurrogate_IsRefused")]
  public void ConstantText_RustCannotHold_IsRefused(string methods, string what)
    => Assert.That(Problems(methods), Has.Some.EqualTo($"PLY1550 '{what}' cannot be expressed in Rust profile 'rust2024' (in the constant 'T')"));

  // Collections.

  [Test]
  public void LoopOverACollection_ThatNoVariableHolds_IsRefused()
    => Assert.That(Problems("static System.Collections.Generic.List<int> Make() => new System.Collections.Generic.List<int> { 1 }; public static int F() { int s = 0; foreach (var x in Make()) s += x; return s; }"),
      Has.Some.StartWith("PLY1550 'a loop over a collection that no variable holds' cannot be expressed"));

  [Test]
  public void LoopOverAList_ThatBreaks_IsAForOverItsEntries()
    => Assert.That(Emitted("public static int F(System.Collections.Generic.List<int> l) { int s = 0; foreach (var x in l) { if (x > 3) break; s += x; } return s; }"),
      Does.Contain("    for entry in l.iter() {\n        let x = *entry;\n        if x > 3 {\n            break;\n        }\n        s = s.wrapping_add(x);\n    }\n"));

  [Test]
  public void ContainsOfAFloatList_ComparesAsDotNetDoes()
    => Assert.That(Emitted("public static bool F(System.Collections.Generic.List<float> l, float v) => l.Contains(v);"),
      Does.Contain("    l.iter().any(|element| ply_same_value_f32(*element, v))\n"));

  [Test]
  public void List_PassedToAFunctionThatChangesIt_IsAMutableBorrow_AndToOneThatReads_ASlice() {
    Assert.That(Emitted("static void Add(System.Collections.Generic.List<int> l) => l.Add(1); public static int F() { var l = new System.Collections.Generic.List<int>(); Add(l); return l.Count; }"),
      Does.Contain("fn add(l: &mut Vec<i32>) {\n    l.push(1);\n}").And.Contain("    add(&mut l);\n"));
    Assert.That(Emitted("static int Count(System.Collections.Generic.List<int> l) => l.Count; public static int F() { var l = new System.Collections.Generic.List<int>(); return Count(l); }"),
      Does.Contain("fn count(l: &[i32]) -> i32 {").And.Contain("    count(&l)\n"));
  }

  // A declaration of a kind the IR does not know, which the backend never sees.

  private sealed class Opaque : IrTypeDeclaration {
    public Opaque() : base("Opaque") => this.Define([]);
  }

  [Test]
  public void Declaration_OfAKindTheIrDoesNotKnow_IsTheVerifiersFinding() {
    var module = new IrModule("m", [new IrFunction("F", true, IrType.I32, [], [], new IrBlock([new IrReturn(new IrConst(IrType.I32, 1))]))]) { Types = [new Opaque()] };
    Assert.That(RustEmitter.Check(module, RustProfile.Default).Select(problem => problem.Message),
      Is.EqualTo(new[] { "ill-typed IR in 'Opaque': a declaration of the kind Opaque" }));
  }

}
