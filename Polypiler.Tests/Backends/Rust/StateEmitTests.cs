using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Module state that has no atomic form: a static of the process behind a <c>Mutex</c>, accessed
/// through <c>ply_with</c> with every operand that may take a lock evaluated before; an array as the
/// fixed array it always is, a string as a vector of its code units, a first value Rust cannot make
/// in a constant through a <c>LazyLock</c>; each form gated by the release that made it possible.
/// </summary>
[TestFixture]
public sealed class StateEmitTests {

  private static string Emitted(string members, RustProfile? profile = null) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(members), profile));

  private static string[] Problems(string members, RustProfile? profile = null)
    => [.. RustCrates.Check(RustCrates.Import(members), profile).Select(problem => problem.Message)];

  [Test]
  public void GlobalArray_IsAFixedArrayBehindAMutex() {
    var text = Emitted("static readonly uint[] buckets = new uint[4]; public static uint F(int v) { ++buckets[v]; return buckets[v & 3] + (uint)buckets.Length; }");
    Assert.That(text, Does.Contain("static BUCKETS: Mutex<[u32; 4]> = Mutex::new([0; 4]);"));
    Assert.That(text, Does.Contain("let value = ply_with(&BUCKETS, |buckets| buckets[v as usize]).wrapping_add(1);\n    ply_with(&BUCKETS, |buckets| buckets[v as usize] = value);"));
    Assert.That(text, Does.Contain("ply_with(&BUCKETS, |buckets| buckets[(v & 3) as usize]).wrapping_add(4)"));
    Assert.That(text, Does.Contain("fn ply_with<T, R>(cell: &Mutex<T>, access: impl FnOnce(&mut T) -> R) -> R {\n    let mut guard = cell.lock().unwrap_or_else(PoisonError::into_inner);\n    access(&mut guard)\n}"));
    Assert.That(text, Does.Contain("use std::sync::Mutex;\nuse std::sync::PoisonError;"));
  }

  [Test]
  public void OperandThatMayTakeALock_IsEvaluatedBefore() {
    var text = Emitted("static readonly int[] table = new int[8]; static int Next(int i) => table[i] + 1; "
      + "public static int F(int i) { table[Next(i) & 7] = table[i] + Next(i); var s = table[Next(i) & 7]; return s + table[i]++ + ++table[i]; }");
    Assert.That(text, Does.Contain("let index = (next(i) & 7) as usize;\n    let value = ply_with(&TABLE, |table| table[i as usize]).wrapping_add(next(i));\n    ply_with(&TABLE, |table| table[index] = value);"));
    Assert.That(text, Does.Contain("let s: i32 = {\n        let index1 = (next(i) & 7) as usize;\n        ply_with(&TABLE, |table| table[index1])\n    };"));
    Assert.That(text, Does.Contain("let old = table[i as usize];\n        table[i as usize] = table[i as usize].wrapping_add(1);\n        old"));
    Assert.That(text, Does.Contain("table[i as usize] = table[i as usize].wrapping_add(1);\n        table[i as usize]\n    }))"));
  }

  [Test]
  public void GlobalString_IsAVectorBehindAMutex() {
    var text = Emitted("static string text = \"\"; static readonly string[] ring = { \"\", \"\" }; "
      + "public static int F(int c) { text = text + (char)c; ring[c & 1] = text; return text.Length + ring[0].Length; }");
    Assert.That(text, Does.Contain("static TEXT: Mutex<Vec<u16>> = Mutex::new(Vec::new());"));
    Assert.That(text, Does.Contain("static RING: Mutex<[Vec<u16>; 2]> = Mutex::new([Vec::new(), Vec::new()]);"));
    Assert.That(text, Does.Contain("ply_with(&TEXT, |text| *text = value);"));
    Assert.That(text, Does.Contain("let value1 = ply_with(&TEXT, |text| text.clone());"));
    Assert.That(text, Does.Contain("ply_with(&RING, |ring| ring[0].clone()).len()"));
  }

  [Test]
  public void FirstValueMadeAtRunTime_IsALazyLock() {
    var text = Emitted("static string text = \"ab\"; static readonly string[] names = { \"x\", \"\" }; public static int F() { names[1] = text; return text.Length + names[0].Length; }");
    Assert.That(text, Does.Contain("static TEXT: LazyLock<Mutex<Vec<u16>>> = LazyLock::new(|| Mutex::new(ply_utf16(\"ab\")));"));
    Assert.That(text, Does.Contain("static NAMES: LazyLock<Mutex<[Vec<u16>; 2]>> =\n    LazyLock::new(|| Mutex::new([ply_utf16(\"x\"), Vec::new()]));"));
    Assert.That(text, Does.Contain("use std::sync::LazyLock;"));
  }

  [Test]
  public void GlobalFloat_IsItselfBehindAMutex() {
    var text = Emitted("static double scale = 1.5; public static double F(double x) { scale = scale * x; return scale; }");
    Assert.That(text, Does.Contain("static SCALE: Mutex<f64> = Mutex::new(1.5);"));
    Assert.That(text, Does.Contain("let value = ply_with(&SCALE, |scale| *scale) * x;"));
    Assert.That(text, Does.Contain("ply_with(&SCALE, |scale| *scale)\n}"));
  }

  [Test]
  public void StepOfAnElement_AsAStatement_IsOneLockedAccess() {
    var global = new Polypiler.Core.IR.IrGlobal("counts", Polypiler.Core.IR.IrType.Array(Polypiler.Core.IR.IrType.I32), new Polypiler.Core.IR.IrNewArray(Polypiler.Core.IR.IrType.Array(Polypiler.Core.IR.IrType.I32), new Polypiler.Core.IR.IrConst(Polypiler.Core.IR.IrType.I32, 2)));
    var index = new Polypiler.Core.IR.IrLocal("i", Polypiler.Core.IR.IrType.I32);
    var element = new Polypiler.Core.IR.IrArrayLoad(new Polypiler.Core.IR.IrGlobalRef(global), new Polypiler.Core.IR.IrLocalRef(index));
    var function = new Polypiler.Core.IR.IrFunction("Count", true, Polypiler.Core.IR.IrType.Void, [index], [],
      new Polypiler.Core.IR.IrBlock([new Polypiler.Core.IR.IrExprStmt(new Polypiler.Core.IR.IrStep(element, true, true, false))]));
    var module = new Polypiler.Core.IR.IrModule("State", [function], []) { Globals = [global] };
    var text = RustCrates.ModuleText(RustEmitter.Emit(module, RustProfile.Default));
    Assert.That(text, Does.Contain("ply_with(&COUNTS, |counts| {\n        counts[i as usize] = counts[i as usize].wrapping_add(1);\n    });"));
  }

  [TestCase("static string text = \"\"; public static int F() => text.Length;", "1.56", "'a global of the type str (a Mutex in a static, Rust 1.63)'")]
  [TestCase("static string text = \"ab\"; public static int F() => text.Length;", "1.79", "'a global of the type str with a first value made at run time (LazyLock, Rust 1.80)'")]
  public void StateOfAnOlderRust_IsRefusedWithTheRelease(string members, string version, string construct)
    => Assert.That(Problems(members, RustProfile.Rust2021.WithRustVersion(Version.Parse(version))), Has.Some.Contain(construct));

  [TestCase("static int[] made = Make(); static int[] Make() => new int[3]; public static int F() => made[0];", "'a global array of the type i32[] without a first value of a known length'")]
  [TestCase("static readonly int[] table = new int[2]; static int Sum(int[] a) => a[0] + a[1]; public static int F() => Sum(table);", "'a global array used as a whole'")]
  public void StateOutsideTheForm_IsRefused(string members, string construct)
    => Assert.That(Problems(members), Has.Some.Contain(construct));

}
