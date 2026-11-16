using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Arrays and dispatch: the vectors a choice gives, lengths of each signedness, element stores and
/// steps, the counted loops that stay indexed because the body changes the array, the arguments of a
/// virtual call, the conversions and type tests of objects that may be null, the numbers as text
/// only another frontend asks for, and the tails of functions that end in a loop that never ends.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  [TestCase("public static int[] F(bool c, int n) => c ? new int[n] : new int[] { 1, 2 };",
    "    if c {\n        vec![0; usize::try_from(n).expect(\"IntegerOverflow\")]\n    } else {\n        vec![1, 2]\n    }\n", TestName = "Vector_OfAChoiceOfNewArrays_IsTheChoiceOfVectors")]
  [TestCase("static int[] Make() => new int[3]; public static int[] F(bool c) => c ? Make() : new int[1];",
    "    if c { make() } else { vec![0; 1] }\n", TestName = "Vector_OfAChoiceOfACallAndANewArray_IsTheChoiceOfVectors")]
  [TestCase("public static int F(uint n) { var a = new int[n]; return a.Length; }",
    "    let a: Vec<i32> = vec![0; n as usize];\n", TestName = "NewArray_OfAnUnsignedLength_CastsTheLength")]
  public void Vector_IsWrittenAsItsSourceMakesIt(string methods, string expected) => Assert.That(Emitted(methods), Does.Contain(expected));

  [Test]
  public void ElementStore_WhoseIndexIsComputed_BindsTheIndexFirst()
    => Assert.That(Emitted("public static int F(int[] a, int i) { a[i + 1] = a[i] + 1; return a[0]; }"),
      Does.Contain("    let index = i.wrapping_add(1) as usize;\n    a[index] = a[i as usize].wrapping_add(1);\n"));

  [Test]
  public void ElementStep_OfAFloatArray_IsCompound()
    => Assert.That(Emitted("public static void F(double[] a, int i) { a[i]++; a[i] += 1.5; }"), Does.Contain("    a[i as usize] += 1.0;\n    a[i as usize] += 1.5;\n"));

  [TestCase("static int G(int[] b) { b[0] = 1; return 1; } public static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { s += a[i] + G(a); } return s; }",
    "    for i in 0..a.len() as i32 {\n        s = s.wrapping_add(a[i as usize].wrapping_add(g(a)));\n", TestName = "CountedLoop_ThatHandsTheArrayToAWriter_KeepsTheIndex")]
  [TestCase("public static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { s += a[i]; System.Array.Fill(a, 0); } return s; }",
    "    for i in 0..a.len() as i32 {\n        s = s.wrapping_add(a[i as usize]);\n        a.fill(0);\n", TestName = "CountedLoop_ThatFillsTheArray_KeepsTheIndex")]
  [TestCase("public static int F(string[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { s += a[i].Length; } return s; }",
    "    for i in 0..a.len() as i32 {\n        s = s.wrapping_add(a[i as usize].len() as i32);\n", TestName = "CountedLoop_OverElementsThatAreNoValues_KeepsTheIndex")]
  [TestCase("public static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { s += a[i]; a = new int[1]; } return s; }",
    "    while i < a.len() as i32 {\n        s = s.wrapping_add(a[i as usize]);\n        a = vec![0; 1];\n", TestName = "CountedLoop_ThatReplacesTheArray_IsAWhile")]
  public void CountedLoop_ThatChangesOrCannotCopyTheElements_KeepsTheIndex(string methods, string expected) => Assert.That(Emitted(methods), Does.Contain(expected));

  [Test]
  public void ElementLoop_WhoseElementVariableChanges_CopiesTheElement()
    => Assert.That(Emitted("public static int F(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) { int v = a[i]; v++; s += v; } return s; }"),
      Does.Contain("    for &element in a {\n        let mut v: i32 = element;\n        v = v.wrapping_add(1);\n"));

  [TestCase("a[j % 3]", "(j % 3) as usize", TestName = "Swap_OfARemainderIndex_StaysThreeStatements")]
  [TestCase("a[unchecked(j + a[0])]", "j.wrapping_add(a[0]) as usize", TestName = "Swap_OfAWrappingSumOfAnElement_StaysThreeStatements")]
  [TestCase("a[(j | 1) ^ (i / 2)]", "((j | 1) ^ (i / 2)) as usize", TestName = "Swap_OfAnOperatorOverAQuotient_StaysThreeStatements")]
  public void Swap_OfAnIndexWithAnOperandThatMayPanic_StaysThreeStatements(string other, string index)
    => Assert.That(Emitted($"public static void F(int[] a, int i, int j) {{ int t = a[i]; a[i] = {other}; {other} = t; }}"),
      Does.Contain($"    a[i as usize] = a[{index}];\n    a[{index}] = t;\n"));

  [Test]
  public void Swap_OfAWrappingShiftRight_IsASwap()
    => Assert.That(Emitted("public static void F(int[] a, int i, int j) { int t = a[i]; a[i] = a[j >> 1]; a[j >> 1] = t; }"), Does.Contain("    a.swap(i as usize, j.wrapping_shr(1) as usize);\n"));

  [TestCase("if (c) { return 1; } else { while (true) { } }", "bool c", "    if c {\n        return 1;\n    } else {\n        loop {}\n    }\n}", TestName = "Tail_OfAnIfWhoseElseNeverEnds_KeepsTheReturn")]
  [TestCase("switch (x) { case 1: return 1; default: while (true) { } }", "int x", "    match x {\n        1 => {\n            return 1;\n        }\n        _ => {\n            loop {}\n        }\n    }\n}", TestName = "Tail_OfAMatchWithAnArmThatNeverEnds_KeepsTheReturn")]
  public void Tail_ThatEndsInALoopThatNeverEnds_KeepsItsReturns(string body, string parameter, string expected)
    => Assert.That(Emitted($"public static int F({parameter}) {{ {body} }}"), Does.Contain(expected));

  // Dispatch.

  [Test]
  public void VirtualCall_WithTextAndAnArray_BorrowsBoth()
    => Assert.That(EmittedFile("public class A { public virtual int M(string s, int[] a) => s.Length + a.Length; } public sealed class B : A { public override int M(string s, int[] a) => 2; } "
      + "public static class S { public static int F(A x) => x.M(\"a\", new int[2]); }"),
      Does.Contain("    A::m(&**ply_non_null(&x), &ply_utf16(\"a\"), &vec![0; 2])\n"));

  [Test]
  public void Conversion_OfABaseThatMayBeNull_ToADerivedClass_IsTheOptionsAndThen()
    => Assert.That(EmittedFile("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static int F(bool c) { A a = c ? new A() : null; B b = a as B; return b == null ? 0 : 1; } }"),
      Does.Contain("    let b: Option<Rc<B>> = a.clone().and_then(PlyObject::into_b);\n"));

  [Test]
  public void TypeTest_OfADerivedValueThatMayBeNull_IsItsPresence()
    => Assert.That(EmittedFile("public class A { public virtual int M() => 1; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static bool F(bool c) { B b = c ? new B() : null; return b is A; } }"),
      Does.Contain("    let b: Option<Rc<B>> = if c { Some(B::new()) } else { None };\n    b.is_some()\n"));

  [Test]
  public void FieldOfAHierarchy_ThatMayBeNull_IsReadThroughTheAccessor()
    => Assert.That(EmittedFile("public class A { public int N; public virtual int M() => N; } public sealed class B : A { public override int M() => 2; } "
      + "public static class S { public static int G(bool c) { A a = c ? new B() : null; return a.N; } }"),
      Does.Contain("    A::field_n(&**ply_non_null(&a)).get()\n"));

  // Numbers as text the C# frontend does not import.

  private static readonly IrLocal _double = new("x", IrType.F64);

  private static IrModule TextOf(IrIntrinsic operation, params IrLocal[] parameters)
    => new("m", [new IrFunction("F", true, IrType.String, parameters, [], new IrBlock([new IrReturn(operation)]))]);

  [Test]
  public void EcmaText_OfADouble_IsTheHelperInEcmaScriptsMode()
    => Assert.That(EmittedModule(TextOf(new IrIntrinsic(IrIntrinsicOp.FormatEcma, IrType.String, [new IrLocalRef(_double)]), _double)),
      Does.Contain("    ply_format_f64(x, true)\n"));

  [Test]
  public void RadixText_OfASignAndMagnitudeOtherThanDecimal_IsRefused() {
    var value = new IrLocal("v", IrType.I32);
    var operation = new IrIntrinsic(IrIntrinsicOp.FormatRadix, IrType.String,
      [new IrLocalRef(value), new IrConst(IrType.I32, 36), new IrConst(IrType.I32, 0), new IrConst(IrType.Bool, 0), new IrConst(IrType.I32, (long)RadixMode.SignMagnitude)]);
    Assert.That(RustEmitter.Check(TextOf(operation, value), RustProfile.Default).Select(problem => problem.Message),
      Has.Some.StartWith("'FormatRadix' cannot be expressed in Rust profile 'rust2024'"));
  }

}
