using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;

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
    var text = Emitted("public static int Odd(int n) { int s = 0; for (int i = 0; i < n; i += 2) { if (i % 3 == 0) continue; s += i; } return s; }");
    Assert.That(text, Does.Contain("if i % 3 == 0 {\n            i = i.wrapping_add(2);\n            continue;\n        }"));
    Assert.That(text, Does.Contain("s = s.wrapping_add(i);\n        i = i.wrapping_add(2);\n    }"));
  }

  /// <summary>A counted loop with a continue is a for loop too: the range steps the counter on every continue.</summary>
  [Test]
  public void CountedForWithContinue_IsARangeLoop() {
    var text = Emitted("public static int Odd(int n) { int s = 0; for (int i = 0; i < n; i++) { if (i % 2 == 0) continue; s += i; } return s; }");
    Assert.That(text, Does.Contain("for i in 0..n {\n        if i % 2 == 0 {\n            continue;\n        }"));
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
    Assert.That(problems.First(problem => problem.Code == RustEmitter.ExceptionRegion).Message, Does.Contain("'a catch of a class a trap belongs to, around code that may trap'"));
  }

  [Test]
  public void ThrowOfALibraryException_IsAPanicOfItsTrap()
    => Assert.That(Emitted("public static int F(int a) { if (a < 0) throw new System.ArgumentOutOfRangeException(); return a; }"), Does.Contain("panic!(\"ArgumentOutOfRange\");"));

  /// <summary>Scenario: a loop whose exit carries a value becomes loop with break value; the loop is the function's value.</summary>
  [Test]
  public void LoopWhoseExitCarriesAValue_BreaksWithIt() {
    var text = Emitted("public static uint NextPowerOfTwo(uint x) { uint p = 1; while (true) { if (p >= x) break; p <<= 1; } return p; }");
    Assert.That(text, Does.Contain("    let mut p: u32 = 1;\n    loop {\n        if p >= x {\n            break p;\n        }\n        p = p.wrapping_shl(1);\n    }\n}"));
    Assert.That(text, Does.Not.Contain("return"));
  }

  /// <summary>Every break of the loop carries the value, a computed one too; a return inside it stays a return.</summary>
  [Test]
  public void LoopWithSeveralExits_BreaksWithTheValueAtEach() {
    var text = Emitted("public static int F(int x) { int i = 0; while (true) { if (i > x) break; if (i == 7) return -1; i += 2; if (i * i > 50) break; } return i * 3; }");
    Assert.That(text, Does.Contain("break i.wrapping_mul(3);"));
    Assert.That(text, Does.Contain("return -1;"));
    Assert.That(text.Split("break i.wrapping_mul(3);").Length, Is.EqualTo(3));
  }

  /// <summary>Not before a return in the middle of a body: the loop is the value of that return.</summary>
  [Test]
  public void LoopBeforeAnEarlyReturn_IsTheReturnedValue()
    => Assert.That(Emitted("public static int F(int x) { if (x < 0) { int i = 0; while (true) { if (i > x) break; i++; } return i; } return 5; }"),
      Does.Contain("return loop {").Or.Contain("        loop {\n            if i > x {\n                break i;"));

  [TestCase("public static void F(int x) { while (true) { if (x > 3) break; x++; } }", TestName = "LoopAtTheEndOfAVoidFunction_KeepsItsBreak")]
  [TestCase("public static void F(int x) { while (true) { if (x > 3) break; x++; } return; }", TestName = "LoopBeforeAReturnOfNothing_KeepsItsBreak")]
  [TestCase("public static int F(int x) { while (x < 9) { if (x > 3) break; x++; } return x; }", TestName = "LoopWithACondition_KeepsItsBreak")]
  [TestCase("public static string F(int x) { while (true) { if (x > 3) break; x++; } return \"a\"; }", TestName = "LoopBeforeTheReturnOfNoScalar_KeepsItsBreak")]
  [TestCase("public static int F(int x) { while (true) { if (x > 3) break; x++; } x++; return x; }", TestName = "LoopBeforeAnotherStatement_KeepsItsBreak")]
  [TestCase("public static int F(int x) { while (true) { x++; if (x > 3) return x; } }", TestName = "LoopWithoutABreak_ReturnsFromInside")]
  public void LoopThatIsNoValue_KeepsItsPlainBreak(string method) => Assert.That(Emitted(method), Does.Not.Match("break [a-z0-9]"));

  [Test]
  public void LoopInsideATry_KeepsItsPlainBreak() {
    const string Methods = "class E : System.Exception { } static int G(int x) { if (x > 100) throw new E(); return x; } "
      + "public static int F(int x) { try { while (true) { x = G(x) + 1; if (x > 3) break; } return x; } catch (E) { return -1; } }";
    Assert.That(Emitted(Methods), Does.Not.Contain("break x"));
  }

  [Test]
  public void LoopInsideALambda_KeepsItsPlainBreak()
    => Assert.That(Emitted("public static int F(int x) { System.Func<int, int> f = y => { while (true) { if (y > 3) break; y++; } return y; }; return f(x) + f(1); }"), Does.Not.Contain("break y"));

  /// <summary>Test design 9: an inclusive bound below the type's maximum is a range a..=b, typed by its end.</summary>
  [Test]
  public void CountedLoopToAnInclusiveConstant_IsAnInclusiveRange()
    => Assert.That(Emitted("public static int SumTo() { int s = 0; for (int i = 0; i <= 100; i++) s += i; return s; }"), Does.Contain("for i in 0..=100i32 {"));

  [TestCase("public static int F() { int s = 0; for (int i = 0; i <= int.MaxValue; i++) { s += i; if (s < 0) break; } return s; }", TestName = "CountedLoopToTheMaximum_StaysAWhile")]
  [TestCase("public static int F(int n) { int s = 0; for (int i = 0; i <= n; i++) s += i; return s; }", TestName = "CountedLoopToAVariableInclusively_StaysAWhile")]
  public void InclusiveLoopThatMayNotEnd_StaysAWhile(string method) => Assert.That(Emitted(method), Does.Contain("while i <= ").And.Not.Contain("..="));

  /// <summary>An inclusive counted loop over an array's length is no element loop: it reads one element past the end, which traps.</summary>
  [Test]
  public void InclusiveLoopOverAnArray_IsNoElementLoop()
    => Assert.That(Emitted("public static int F(int[] a) { int s = 0; for (int i = 0; i <= 3; i++) s += a[i]; return s; }"), Does.Contain("for i in 0..=3i32 {").And.Not.Contain("for &"));

  private static string RustEmitted(string source)
    => RustCrates.ModuleText(RustCrates.Emit(RustFixture.Import("snippet.rs", source, Polypiler.Core.Frontends.Rust.OverflowChecks.Panic)));

  /// <summary>Scenario: an inclusive range ending at the type's maximum is Rust's own range again, which ends without overflow.</summary>
  [Test]
  public void RustInclusiveRangeToTheMaximum_IsTheRange()
    => Assert.That(RustEmitted("pub fn count_bytes() -> u32 { let mut c = 0u32; for _ in 0..=255u8 { c += 1; } c }"), Does.Contain("    for _ in 0..=255u8 {\n        c = c.checked_add(1).expect(\"IntegerOverflow\");\n    }"));

  [TestCase("pub fn f(n: u8) -> u32 { let mut s = 0u32; for i in 0..=n { s += i as u32; } s }", "for i in 0..=end {")]
  [TestCase("pub fn f(a: i8, b: i8) -> i32 { let mut s = 0i32; for i in a..=b { s += i as i32; } s }", "for i in start..=end {")]
  [TestCase("pub fn f() -> u32 { let mut c = 0u32; for _ in -128i8..=127 { c += 1; } c }", "for _ in i8::MIN..=127 {")]
  [TestCase("pub fn f() -> u32 { let mut c = 0u32; for _ in 5u8..=3 { c += 1; } c }", "for _ in 5..=3u8 {")]
  [TestCase("pub fn f(n: u64) -> u64 { let mut s = 0u64; for i in 1..=n { if i % 3 == 0 { continue; } s += i; } s }", "for i in 1..=end {")]
  public void RustInclusiveRange_IsTheRange(string source, string expected) => Assert.That(RustEmitted(source), Does.Contain(expected).And.Not.Contain("done"));

  private static readonly IrLocal _done = new("done", IrType.Bool);
  private static readonly IrLocal _index = new("index", IrType.U8);
  private static readonly IrLocal _end = new("end", IrType.U8);
  private static readonly IrLocal _count = new("count", IrType.U32);

  private static IrLocalRef Ref(IrLocal local) => new(local);

  private static IrExpr Wide(IrLocal local) => new IrConvert(IrType.U64, Ref(local), IrConversion.Wrap);

  /// <summary>
  /// The loop over <c>0..=end</c> as the Rust frontend imports it, with one part replaced: the flag's
  /// first value, the counter's first value, the loop, the statements after it.
  /// </summary>
  private static string Inclusive(IrExpr? empty = null, IrExpr? start = null, System.Func<IrStmt[], IrStmt[], IrStmt>? loop = null, IrStmt[]? body = null, IrStmt[]? step = null, IrStmt[]? after = null) {
    body ??= [new IrAssign(_count, new IrBinary(IrBinaryOp.Add, Ref(_count), new IrConvert(IrType.U32, Ref(_index), IrConversion.Wrap)))];
    step ??= [
      new IrAssign(_done, new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Subtract, Wide(_end), Wide(_index)), new IrConst(IrType.U64, 1))),
      new IrAssign(_index, new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Add, Wide(_index), new IrConst(IrType.U64, 1)), IrConversion.Wrap))
    ];
    loop ??= (statements, advance) => new IrFor(new IrUnary(IrUnaryOp.LogicalNot, Ref(_done)), new IrBlock(statements), new IrBlock(advance));
    IrStmt[] statements = [
      new IrDeclare(_count, new IrConst(IrType.U32, 0)),
      new IrDeclare(_done, empty ?? new IrBinary(IrBinaryOp.Greater, new IrConst(IrType.U32, 0), new IrConvert(IrType.U32, Ref(_end), IrConversion.Wrap))),
      new IrDeclare(_index, start ?? new IrConst(IrType.U8, 0)),
      loop(body, step),
      .. after ?? [],
      new IrReturn(Ref(_count))
    ];
    var module = new IrModule("edge", [new IrFunction("F", true, IrType.U32, [_end], [_count, _done, _index], new IrBlock(statements))]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return RustCrates.ModuleText(RustCrates.Emit(module, level: OptimizationLevel.None));
  }

  private static IrStmt[] Step(IrExpr reached, IrExpr current, IrExpr moved) => [
    new IrAssign(_done, new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Subtract, reached, current), new IrConst(IrType.U64, 1))),
    new IrAssign(_index, new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Add, moved, new IrConst(IrType.U64, 1)), IrConversion.Wrap))
  ];

  [Test]
  public void InclusiveLoopOfTheFrontend_IsTheRange() {
    Assert.That(Inclusive(), Does.Contain("for index in 0..=end {"));
    Assert.That(Inclusive(new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.LessOrEqual, new IrConvert(IrType.U32, new IrConst(IrType.U8, 0), IrConversion.Wrap), new IrConvert(IrType.U32, Ref(_end), IrConversion.Wrap)))), Does.Contain("for index in 0..=end {"));
    Assert.That(Inclusive(loop: (statements, advance) => new IrWhile(new IrUnary(IrUnaryOp.LogicalNot, Ref(_done)), new IrBlock([.. statements, .. advance]))), Does.Contain("for index in 0..=end {"));
    Assert.That(Inclusive(body: [new IrExprStmt(new IrStep(Ref(_count), true, false, false))]), Does.Contain("for _ in 0..=end {"));
  }

  /// <summary>Every part of the shape that differs keeps the flag and the loop, which mean the same in any case.</summary>
  [TestCase("first value of the flag")]
  [TestCase("flag of another start")]
  [TestCase("flag of the bounds turned around")]
  [TestCase("loop of another flag")]
  [TestCase("while with a continue")]
  [TestCase("step against another bound")]
  [TestCase("step of another counter")]
  [TestCase("step that moves another counter")]
  [TestCase("step by two")]
  [TestCase("bound assigned in the body")]
  [TestCase("counter assigned in the body")]
  [TestCase("flag read in the body")]
  [TestCase("counter read after the loop")]
  [TestCase("bound out of the counter's type")]
  [TestCase("loop of one step")]
  public void InclusiveLoopOfAnotherShape_StaysAWhile(string change) {
    var text = change switch {
      "first value of the flag" => Inclusive(empty: new IrConst(IrType.Bool, 0)),
      "flag of another start" => Inclusive(empty: new IrBinary(IrBinaryOp.Greater, new IrConst(IrType.U32, 1), new IrConvert(IrType.U32, Ref(_end), IrConversion.Wrap))),
      "loop of another flag" => Inclusive(loop: (statements, advance) => new IrFor(new IrUnary(IrUnaryOp.LogicalNot, new IrConst(IrType.Bool, 0)), new IrBlock(statements), new IrBlock(advance))),
      "while with a continue" => Inclusive(loop: (statements, advance) => new IrWhile(new IrUnary(IrUnaryOp.LogicalNot, Ref(_done)), new IrBlock([new IrIf(new IrBinary(IrBinaryOp.Equal, new IrConvert(IrType.U32, Ref(_index), IrConversion.Wrap), new IrConst(IrType.U32, 3)), new IrBlock([new IrContinue()]), null), .. statements, .. advance]))),
      "flag of the bounds turned around" => Inclusive(empty: new IrBinary(IrBinaryOp.Greater, new IrConvert(IrType.U32, Ref(_end), IrConversion.Wrap), new IrConst(IrType.U32, 0))),
      "step against another bound" => Inclusive(step: Step(new IrConst(IrType.U64, 1000), Wide(_index), Wide(_index))),
      "step of another counter" => Inclusive(step: Step(Wide(_end), Wide(_end), Wide(_index))),
      "step that moves another counter" => Inclusive(step: Step(Wide(_end), Wide(_index), Wide(_end))),
      "step by two" => Inclusive(step: [Step(Wide(_end), Wide(_index), Wide(_index))[0], new IrAssign(_index, new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Add, Wide(_index), new IrConst(IrType.U64, 2)), IrConversion.Wrap))]),
      "bound assigned in the body" => Inclusive(body: [new IrAssign(_end, new IrConst(IrType.U8, 3))]),
      "counter assigned in the body" => Inclusive(body: [new IrAssign(_index, new IrConst(IrType.U8, 3))]),
      "flag read in the body" => Inclusive(body: [new IrIf(Ref(_done), new IrBlock([new IrExprStmt(new IrStep(Ref(_count), true, false, false))]), null)]),
      "counter read after the loop" => Inclusive(after: [new IrAssign(_count, new IrConvert(IrType.U32, Ref(_index), IrConversion.Wrap))]),
      "bound out of the counter's type" => Inclusive(step: Step(new IrConst(IrType.U64, 300), Wide(_index), Wide(_index)), empty: new IrConst(IrType.Bool, 0)),
      _ => Inclusive(loop: (statements, advance) => new IrFor(new IrUnary(IrUnaryOp.LogicalNot, Ref(_done)), new IrBlock(statements), new IrBlock([advance[1]])))
    };
    Assert.That(text, Does.Not.Contain("..="));
  }

}
