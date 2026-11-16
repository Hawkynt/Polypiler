using NUnit.Framework;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The tidying of the statement-by-statement translation: the exchange of two elements as
/// <c>swap</c> where both indices are pure, a binding initialized by the branches that follow it, and
/// the last statements of a function made its value.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  // One index of each kind that can neither panic nor change anything, and one of each that can.

  [TestCase("a[~j]", "!j as usize", TestName = "Swap_OfANegatedIndex_IsASwap")]
  [TestCase("a[j | 1]", "(j | 1) as usize", TestName = "Swap_OfAParenthesizedOperatorIndex_IsASwap")]
  [TestCase("a[j << 1]", "j.wrapping_shl(1) as usize", TestName = "Swap_OfAWrappingShiftIndex_IsASwap")]
  [TestCase("a[unchecked(j + 1)]", "j.wrapping_add(1) as usize", TestName = "Swap_OfAWrappingSumIndex_IsASwap")]
  [TestCase("a[3]", "3", TestName = "Swap_OfALiteralIndex_IsASwap")]
  public void Swap_OfPureIndices_IsTheSwapOfTheSlice(string other, string index)
    => Assert.That(Emitted($"public static void F(int[] a, int i, int j) {{ int t = a[i]; a[i] = {other}; {other} = t; }}"),
      Does.Contain($") {{\n    a.swap(i as usize, {index});\n}}"));

  [TestCase("a[j / 2]", "a[(j / 2) as usize] = t;", TestName = "Swap_OfAQuotientIndex_StaysThreeStatements")]
  [TestCase("a[(byte)j]", "a[i32::from(j as u8) as usize] = t;", TestName = "Swap_OfAConvertedIndex_StaysThreeStatements")]
  [TestCase("a[a[j]]", "a[a[j as usize] as usize] = t;", TestName = "Swap_OfAnElementIndex_StaysThreeStatements")]
  public void Swap_OfAnIndexThatMayPanic_StaysThreeStatements(string other, string last) {
    var text = Emitted($"public static void F(int[] a, int i, int j) {{ int t = a[i]; a[i] = {other}; {other} = t; }}");
    Assert.That(text, Does.Not.Contain("swap"));
    Assert.That(text, Does.Contain("let t: i32 = a[i as usize];").And.Contain(last));
  }

  [Test]
  public void Swap_WhoseBindingIsReadAfter_OrWhoseStoresDoNotMatch_StaysThreeStatements() {
    Assert.That(Emitted("public static void F(int[] a, int i, int j) { int t = a[i]; a[i] = a[j]; a[j] = t; a[0] = t; }"),
      Does.Contain("a[j as usize] = t;\n    a[0] = t;").And.Not.Contain("swap"));
    Assert.That(Emitted("public static void F(int[] a, int i, int j) { int t = a[i]; a[i] = a[j]; a[i] = t; }"),
      Does.Contain("a[i as usize] = a[j as usize];\n    a[i as usize] = t;").And.Not.Contain("swap"));
    Assert.That(Emitted("public static void F(int[] a, int[] b, int i, int j) { int t = a[i]; a[i] = b[j]; a[j] = t; }"),
      Does.Contain("a[i as usize] = b[j as usize];\n    a[j as usize] = t;").And.Not.Contain("swap"));
  }

  [Test]
  public void Swap_WhoseFirstIndexIsHoisted_StaysThreeStatements() {
    // An index that is no variable is evaluated once before the store, which leaves no exchange to see.
    var text = Emitted("public static void F(int[] a, int i, int j) { int t = a[i ^ j]; a[i ^ j] = a[~j]; a[~j] = t; }");
    Assert.That(text, Does.Contain("let index = (i ^ j) as usize;\n    a[index] = a[!j as usize];\n    a[!j as usize] = t;"));
  }

  // The binding initialized by what follows it.

  [Test]
  public void LateInitialization_ByAnElseIfChain_IsTheChainsValue()
    => Assert.That(Emitted("public static int F(int x) { int y; if (x > 0) { y = 1; } else if (x < -5) { y = 3; } else { y = 2; } return y; }"),
      Does.Contain("pub fn f(x: i32) -> i32 {\n    if x > 0 {\n        1\n    } else if x < -5 {\n        3\n    } else {\n        2\n    }\n}"));

  [Test]
  public void LateInitialization_ByAMatchWithAReturningArm_IsTheMatchsValue()
    => Assert.That(Emitted("public static int F(int x) { int y; switch (x) { case 1: y = 4; break; case 2: return 7; default: y = 5; break; } return y; }"),
      Does.Contain("pub fn f(x: i32) -> i32 {\n    match x {\n        1 => 4,\n        2 => 7,\n        _ => 5,\n    }\n}"));

  [Test]
  public void LateInitialization_ByAnArmThatNamesItBefore_StaysADeclaration() {
    var text = Emitted("public static int F(int x) { int y; switch (x) { case 1: y = 4; x++; break; default: y = x; break; } return y + x; }");
    Assert.That(text, Does.Contain("let y: i32;\n    match x {\n        1 => {\n            y = 4;\n            x = x.wrapping_add(1);\n        }"));
  }

  [Test]
  public void LateInitialization_ByABranchThatReadsItFirst_StaysADeclaration()
    => Assert.That(Emitted("public static int F(int x) { int y; if (x > 0) { y = x; y = y + 1; } else { y = 2; } return y; }"),
      Does.Contain("let mut y: i32;\n    if x > 0 {\n        y = x;\n        y = y.wrapping_add(1);\n    } else {\n        y = 2;\n    }\n    y"));

  [Test]
  public void LateInitialization_ByAnIfWithoutElse_StaysADeclaration()
    => Assert.That(Emitted("public static int F(int x) { int y; if (x > 0) { y = 1; } return 0; }"),
      Does.Contain("let _y: i32;\n    if x > 0 {\n        _y = 1;\n    }\n    0"));

  // The function's value as its tail.

  [Test]
  public void Tail_OfANestedIfThatReturns_IsTheValueOfEveryBranch()
    => Assert.That(Emitted("public static int F(int x) { if (x > 1) { if (x > 5) return 3; else return 4; } else if (x < 0) { return 1; } else { return 2; } }"),
      Does.Contain("pub fn f(x: i32) -> i32 {\n    if x > 1 {\n        if x > 5 { 3 } else { 4 }\n    } else if x < 0 {\n        1\n    } else {\n        2\n    }\n}"));

  [Test]
  public void Tail_OfAnElseIfThatGivesNoValue_KeepsTheReturn()
    => Assert.That(Emitted("public static int F(int x) { if (x > 1) { return 3; } else if (x < 0) { x = 2; } return x; }"),
      Does.Contain("    if x > 1 {\n        return 3;\n    } else if x < 0 {\n        x = 2;\n    }\n    x\n}"));

  [Test]
  public void Tail_OfAMatchWhoseArmEndsInAnIf_KeepsTheArmABlock()
    => Assert.That(Emitted("public static int F(int x) { switch (x) { case 1: return 4; default: { if (x > 3) return 9; return 5; } } }"),
      Does.Contain("    match x {\n        1 => 4,\n        _ => {\n            if x > 3 {\n                return 9;\n            }\n            5\n        }\n    }\n}"));

  [Test]
  public void Tail_OfAMatchWithAnArmThatGoesOn_KeepsItsReturns()
    => Assert.That(Emitted("public static int F(int x) { switch (x) { case 1: return 4; case 2: x = 3; break; default: return 5; } return x; }"),
      Does.Contain("    match x {\n        1 => {\n            return 4;\n        }\n        2 => {\n            x = 3;\n        }\n        _ => {\n            return 5;\n        }\n    }\n    x\n}"));

  [Test]
  public void Tail_OfAVoidMatchWhoseArmsReturn_DropsTheReturns()
    => Assert.That(Emitted("public static void F(int x, int[] a) { switch (x) { case 1: a[0] = 4; return; default: a[1] = 3; return; } }"),
      Does.Contain("    match x {\n        1 => {\n            a[0] = 4;\n        }\n        _ => {\n            a[1] = 3;\n        }\n    }\n}"));

}
