using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Structured control flow as Rust (#458): loops, branches, switches, exits and the bindings they
/// merge, for IR the C# frontend imports. The acceptance project runs the same shapes.
/// </summary>
[TestFixture]
public sealed class RustControlEmitTests {

  private static string Emitted(string methods, RustProfile? profile = null, OptimizationLevel level = OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), profile, level: level));

  [Test]
  public void PreTestLoop_IsWhileWithReboundParameters() {
    var text = Emitted("public static uint Gcd(uint a, uint b) { while (b != 0) { uint t = a % b; a = b; b = t; } return a; }");
    Assert.That(text, Does.Contain("    let mut a = a;\n    let mut b = b;\n    while b != 0 {\n        let t: u32 = ply_rem_u32(a, b);\n        a = b;\n        b = t;\n    }\n    a\n"));
    Assert.That(text, Does.Not.Contain("loop {").And.Not.Contain("'l"));
  }

  [Test]
  public void CountedLoop_IsForOverARange() {
    var text = Emitted("public static int Sum(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }");
    Assert.That(text, Does.Contain("for i in 0..n {\n        s = s.wrapping_add(i);\n    }"));
  }

  [Test]
  public void CountedLoopWithoutUseOfItsCounter_BindsNothing()
    => Assert.That(Emitted("public static int Count(int n) { int c = 0; for (int i = 0; i < n; i++) c += 2; return c; }"), Does.Contain("for _ in 0..n {"));

  [Test]
  public void CountedLoopOverConstants_TypesItsStart()
    => Assert.That(Emitted("public static uint Count() { uint c = 0; for (uint i = 0; i < 10; i++) c += i; return c; }"), Does.Contain("for i in 0u32..10 {"));

  [Test]
  public void LoopWhoseCounterIsReadAfterwards_StaysAWhile() {
    var text = Emitted("public static int Find(int n) { int i = 0; for (; i < n; i++) { } return i; }");
    Assert.That(text, Does.Contain("while i < n {").And.Not.Contain("for "));
  }

  [Test]
  public void LoopWhoseBoundChanges_StaysAWhile() {
    var text = Emitted("public static int Shrink(int n) { int s = 0; for (int i = 0; i < n; i++) { n--; s++; } return s; }");
    Assert.That(text, Does.Contain("while i < n {").And.Not.Contain("for "));
  }

  [Test]
  public void ForWithContinue_RunsTheStepBeforeEveryContinue() {
    var text = Emitted("public static int Odd(int n) { int s = 0; for (int i = 0; i < n; i++) { if (i % 2 == 0) continue; s += i; } return s; }");
    Assert.That(text, Does.Contain("if i % 2 == 0 {\n            i = i.wrapping_add(1);\n            continue;\n        }"));
    Assert.That(text, Does.Contain("s = s.wrapping_add(i);\n        i = i.wrapping_add(1);\n    }"));
  }

  [Test]
  public void EndlessLoop_IsLoop()
    => Assert.That(Emitted("public static int Spin(int x) { while (true) { x++; if (x > 10) return x; } }"), Does.Contain("loop {"));

  [Test]
  public void PostTestLoop_TestsLast() {
    var text = Emitted("public static int Up(int x) { do { x++; } while (x < 10); return x; }");
    Assert.That(text, Does.Contain("loop {\n        x = x.wrapping_add(1);\n        if x >= 10 {\n            break;\n        }\n    }"));
  }

  [Test]
  public void PostTestLoopWithContinue_LeavesALabelledBody() {
    var text = Emitted("public static int Up(int x) { do { x++; if (x == 5) continue; x++; } while (x < 10); return x; }");
    Assert.That(text, Does.Contain("'l0: loop {\n        'c1: {"));
    Assert.That(text, Does.Contain("break 'c1;"));
    Assert.That(text, Does.Contain("break 'l0;"));
  }

  [Test]
  public void PostTestLoopWithContinue_IsRefusedBeforeLabelledBlocks() {
    var problems = RustCrates.Check(RustCrates.Import("public static int Up(int x) { do { x++; if (x == 5) continue; x++; } while (x < 10); return x; }"), RustProfile.Rust2018);
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { RustEmitter.Inexpressible }));
    Assert.That(problems[0].Message, Does.Contain("continue in a do-while loop (labelled blocks, Rust 1.65)"));
  }

  [Test]
  public void Switch_IsAMatchWithMergedPatterns() {
    var text = Emitted("public static int Classify(int x) { switch (x) { case 0: return 1; case 1: case 2: return 2; case 10: case 11: case 12: case 13: case 14: case 15: case 16: case 17: case 18: case 19: return 3; default: return 4; } }");
    Assert.That(text, Does.Contain("match x {\n        0 => 1,\n        1 | 2 => 2,\n        10..=19 => 3,\n        _ => 4,\n    }"));
  }

  [Test]
  public void SwitchWithoutDefault_EndsWithAnEmptyArm()
    => Assert.That(Emitted("public static int F(int x) { int r = 0; switch (x) { case 1: r = 5; break; case 2: r = 7; break; } return r; }"), Does.Contain("_ => {}"));

  [Test]
  public void SwitchCoveringAByte_HasNoCatchAll() {
    var cases = string.Concat(Enumerable.Range(0, 256).Select(value => $"case {value}: "));
    Assert.That(Emitted($"public static int F(byte x) {{ int r = 0; switch (x) {{ {cases} r = 1; break; }} return r; }}"), Does.Contain("0..=255 => {").And.Not.Contain("_ =>"));
  }

  [Test]
  public void SwitchOfOneSection_IsAnIf()
    => Assert.That(Emitted("public static int F(int x) { int r = 0; switch (x) { case 3: case 4: r = 1; break; } return r; }"), Does.Contain("if x == 3 || x == 4 {"));

  [Test]
  public void SwitchOfTheDefaultAlone_IsItsBody()
    => Assert.That(Emitted("public static int F(int x) { int r = 0; switch (x) { default: r = 1; break; } return r; }"), Does.Not.Contain("match").And.Contain("r = 1;"));

  [Test]
  public void SwitchWithAMinimumLabel_NamesTheConstant()
    => Assert.That(Emitted("public static int F(int x) { switch (x) { case int.MinValue: return 1; case int.MaxValue: return 2; default: return 0; } }"), Does.Contain("i32::MIN => 1,\n        2147483647 => 2,"));

  [Test]
  public void BreakInsideASection_LeavesALabelledBlock() {
    var text = Emitted("public static int F(int x, bool y) { int z = 0; switch (x) { case 1: if (y) break; z = 1; break; default: z = 2; break; } return z; }");
    Assert.That(text, Does.Contain("'s0: {\n        match x {"));
    Assert.That(text, Does.Contain("break 's0;"));
  }

  [Test]
  public void BreakInsideASection_IsRefusedBeforeLabelledBlocks() {
    var problems = RustCrates.Check(RustCrates.Import("public static int F(int x, bool y) { int z = 0; switch (x) { case 1: if (y) break; z = 1; break; default: z = 2; break; } return z; }"), RustProfile.Rust2021);
    Assert.That(problems.Single().Message, Does.Contain("break out of a switch section (labelled blocks, Rust 1.65)"));
  }

  [Test]
  public void BreakOfALoopFromASwitch_NamesTheLoop() {
    var text = Emitted("public static int F(int n) { int i = 0; while (i < n) { switch (i) { case 5: break; default: i++; continue; } break; } return i; }");
    Assert.That(text, Does.Contain("'l0: while i < n {"));
    Assert.That(text, Does.Contain("continue 'l0;"));
  }

  [Test]
  public void ElseIf_IsFolded() {
    var text = Emitted("public static int Sign(int x) { if (x < 0) return -1; else if (x == 0) return 0; else return 1; }");
    Assert.That(text, Does.Contain("    if x < 0 {\n        -1\n    } else if x == 0 {\n        0\n    } else {\n        1\n    }\n"));
    Assert.That(text, Does.Not.Contain("return"));
  }

  [Test]
  public void EarlyReturn_StaysAReturn()
    => Assert.That(Emitted("public static int F(int x) { if (x < 0) return 0; return x; }"), Does.Contain("    if x < 0 {\n        return 0;\n    }\n    x\n"));

  [Test]
  public void ReturnAtTheEndOfAVoidFunction_IsDropped()
    => Assert.That(Emitted("public static void F(int x) { if (x < 0) { return; } }"), Does.Contain("pub fn f(x: i32) {\n    if x < 0 {}\n}"));

  [Test]
  public void ValueAssignedInEveryBranch_IsInitializedByTheIf()
    => Assert.That(Emitted("public static int F(bool c) { int r; if (c) r = 1; else r = 2; return r + 1; }"), Does.Contain("let r: i32 = if c { 1 } else { 2 };"));

  [Test]
  public void ValueAssignedInEveryArm_IsInitializedByTheMatch()
    => Assert.That(Emitted("public static int F(int x) { int r; switch (x) { case 1: r = 5; break; default: r = 7; break; } return r + 1; }"), Does.Contain("let r: i32 = match x {\n        1 => 5,\n        _ => 7,\n    };"));

  [Test]
  public void BindingReturnedAtOnce_IsTheValue()
    => Assert.That(Emitted("public static int F(int a) { int r = a * 3; return r; }"), Does.Contain("pub fn f(a: i32) -> i32 {\n    a.wrapping_mul(3)\n}"));

  [Test]
  public void NestedIfs_AreOneCondition()
    => Assert.That(Emitted("public static int F(bool a, bool b) { int x = 0; if (a) { if (b) { x = 1; } } return x; }"), Does.Contain("if a && b {"));

  [Test]
  public void EmptyThenBranch_TurnsTheConditionAround()
    => Assert.That(Emitted("public static int F(int a) { int x = 0; if (a < 3) { } else { x = 1; } return x; }"), Does.Contain("if a >= 3 {\n        x = 1;\n    }"));

  [Test]
  public void BindingAssignedOncePerPath_IsNotMutable() {
    var text = Emitted("public static int F(bool c, int a) { int r; if (c) { a++; r = a; } else { r = 2; } return r * 2; }");
    Assert.That(text, Does.Contain("let r: i32 = if c {"));
    Assert.That(text, Does.Not.Contain("let mut r"));
  }

  [Test]
  public void BindingAssignedInALoop_IsMutable()
    => Assert.That(Emitted("public static int F(int n) { int x = 1; while (n > 0) { x = x * 2; n--; } return x; }"), Does.Contain("let mut x: i32 = 1;"));

  [Test]
  public void ValueNeverRead_IsAllowedAsTheSourceWroteIt() {
    var files = RustCrates.Emit(RustCrates.Import("public static int F(int a) { int x = a; x = 3; return x; }"), level: OptimizationLevel.None);
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("unused_assignments"));
  }

  [Test]
  public void VariableNeverRead_HasAnUnderscore()
    => Assert.That(Emitted("public static int F(int a) { int unused = a; return 1; }"), Does.Contain("let _unused: i32 = a;"));

  [Test]
  public void ExceptionRegion_IsRefused() {
    var problems = RustCrates.Check(RustCrates.Import("public static int F(int a, int b) { try { return a / b; } catch (System.DivideByZeroException) { return 0; } }"));
    Assert.That(problems.Select(problem => problem.Code), Does.Contain(RustEmitter.ExceptionRegion));
    Assert.That(problems.First(problem => problem.Code == RustEmitter.ExceptionRegion).Message, Does.Contain("'exception handling'"));
  }

  [Test]
  public void ThrowOfALibraryException_IsAPanicOfItsTrap()
    => Assert.That(Emitted("public static int F(int a) { if (a < 0) throw new System.ArgumentOutOfRangeException(); return a; }"), Does.Contain("panic!(\"ArgumentOutOfRange\");"));

}
