using NUnit.Framework;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Statements: the compound assignments clippy asks for and the arithmetic that keeps its method,
/// the steps of floating-point variables, a switch of only a default section, and the counted loops
/// that become ranges or element loops.
/// </summary>
public sealed partial class RustCoverageEmitTests {

  [Test]
  public void Assignment_OfABitwiseOperatorOrADivisionByAConstant_IsCompound() {
    var text = Emitted("public static int F(int x, int y) { int z = 0; z = z ^ y; z = y & z; z = z << 2; z = z / 3; z = z % 5; z = z / x; return z; }");
    Assert.That(text, Does.Contain("    z ^= y;\n    z &= y;\n    z = z.wrapping_shl(2);\n    z /= 3;\n    z %= 5;\n    z = ply_div_i32(z, x);\n"));
  }

  [Test]
  public void Assignment_OfFloatingPointArithmetic_IsCompound_ButNotWithTheVariableOnTheRight()
    => Assert.That(Emitted("public static double F(double x, double y) { double z = 1; z = z + y; z = z - y; z = z * y; z = z / y; z = z % y; z = y * z; return z; }"),
      Does.Contain("    z += y;\n    z -= y;\n    z *= y;\n    z /= y;\n    z %= y;\n    z = y * z;\n"));

  [TestCase("double", "z++; z--;", "    z += 1.0;\n    z -= 1.0;\n", TestName = "Step_OfADouble_AddsOne")]
  [TestCase("float", "++z;", "    z += 1.0;\n", TestName = "Step_OfAFloat_AddsOne")]
  public void Step_OfAFloatingPointVariable_IsCompound(string type, string steps, string expected)
    => Assert.That(Emitted($"public static {type} F({type} x) {{ {type} z = x; {steps} return z; }}"), Does.Contain(expected));

  [Test]
  public void Switch_OfOnlyADefault_EvaluatesTheSelectorForWhatItDoes() {
    Assert.That(Emitted("public static int F(int x) { int r = 0; switch (x + 1) { default: r = 2; break; } return r; }"),
      Does.Contain("    let mut r: i32 = 0;\n    let _ = x.wrapping_add(1);\n    r = 2;\n    r\n"));
    Assert.That(Emitted("public static int F(int x) { int r = 0; switch (x) { default: r = 2; break; } return r; }"),
      Does.Contain("    let mut r: i32 = 0;\n    r = 2;\n    r\n"));
  }

  [TestCase("for (int i = 0; i < x; i += 1) { r += i; }", TestName = "CountedLoop_SteppedByACompoundOne_IsARange")]
  [TestCase("for (int i = 0; i < x; i = i + 1) { r += i; }", TestName = "CountedLoop_SteppedByASumOfOne_IsARange")]
  [TestCase("int i = 0; while (i < x) { r += i; i = i + 1; }", TestName = "WhileLoop_ThatCounts_IsARange")]
  public void CountedLoop_SteppedByOne_IsARange(string loop)
    => Assert.That(Emitted($"public static int F(int x) {{ int r = 0; {loop} return r; }}"), Does.Contain("    for i in 0..x {\n        r = r.wrapping_add(i);\n    }\n"));

  [Test]
  public void CountedLoop_WhoseBodyBreaksInsideAnIf_StaysARangeWithTheBreak()
    => Assert.That(Emitted("public static int F(int x) { int r = 0; for (int i = 0; i < x; i++) { if (i > 5) { if (r > 3) break; } r += i; } return r; }"),
      Does.Contain("    for i in 0..x {\n        if i > 5 && r > 3 {\n            break;\n        }\n        r = r.wrapping_add(i);\n    }\n"));

  [TestCase("if (r > 3) { break; } else { r += a[i]; }", "        if r > 3 {\n            break;\n        } else {\n            r = r.wrapping_add(element);\n        }\n", TestName = "ElementLoop_BreakingInAnIfElse_ReadsTheElement")]
  [TestCase("{ r += a[i]; if (r > 9) break; }", "        r = r.wrapping_add(element);\n        if r > 9 {\n            break;\n        }\n", TestName = "ElementLoop_BreakingInABlock_ReadsTheElement")]
  public void ElementLoop_ThatOnlyReadsTheArray_IsAForOverTheSlice(string body, string expected)
    => Assert.That(Emitted($"public static int F(int[] a) {{ int r = 0; for (int i = 0; i < a.Length; i++) {{ {body} }} return r; }}"),
      Does.Contain("    for &element in a {\n" + expected + "    }\n"));

  [Test]
  public void ElementLoop_OverATable_IsAForOverItsBorrow()
    => Assert.That(Emitted("static readonly int[] T = { 1, 2, 3 }; public static int F() { int r = 0; for (int i = 0; i < T.Length; i++) { r += T[i]; r ^= 1; } return r; }"),
      Does.Contain("    for &element in &T {\n        r = r.wrapping_add(element);\n        r ^= 1;\n    }\n"));

  [TestCase("public static int F(int[] a) { int r = 0; for (int i = 0; i < a.Length; i++) { a[i]++; r += a[i]; } return r; }",
    "    for i in 0..a.len() as i32 {\n        a[i as usize] = a[i as usize].wrapping_add(1);\n", TestName = "CountedLoop_ThatStepsAnElement_KeepsTheIndex")]
  [TestCase("public static int F(int[] a, int[] b) { int r = 0; for (int i = 0; i < a.Length; i++) { b[i] = 1; r += a[i]; } return r; }",
    "    for i in 0..a.len() as i32 {\n        b[i as usize] = 1;\n", TestName = "CountedLoop_ThatIndexesAnotherArray_KeepsTheIndex")]
  public void CountedLoop_ThatUsesTheCounterOtherwise_KeepsTheIndex(string method, string expected)
    => Assert.That(Emitted(method), Does.Contain(expected));

}
