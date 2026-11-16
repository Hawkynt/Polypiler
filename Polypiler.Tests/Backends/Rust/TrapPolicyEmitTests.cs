using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The trap policies of the Rust backend (ADR 0008): under <c>panic</c>, the default, a trap is a panic
/// and every function keeps its plain type; under <c>result</c> every function a trap can leave returns
/// a <c>Result</c>, every trap is an explicit check that hands its error on, the crate carries its
/// <c>Error</c> enum in <c>src/error.rs</c> or, where the module has exceptions, a trap is the exception
/// of its class that a catch clause sees - and what would still panic is refused with <c>PLY1552</c>.
/// </summary>
[TestFixture]
public sealed class TrapPolicyEmitTests {

  private static readonly RustProfile Result = RustProfile.Default.WithTrapPolicy(RustTrapPolicy.Result);

  private const string CheckedSum = "public static int CheckedSum(int a, int b, int c) => checked(a + b) / c;";

  private static IReadOnlyList<GeneratedFile> Files(string methods, RustProfile? profile = null, RustEmitOptions? options = null)
    => RustCrates.Emit(RustCrates.Import(methods), profile ?? Result, options);

  private static string Emitted(string methods, RustProfile? profile = null) => RustCrates.ModuleText(Files(methods, profile));

  private static string[] Problems(string methods, RustProfile? profile = null)
    => [.. RustCrates.Check(RustCrates.Import(methods), profile ?? Result).Select(problem => $"{problem.Code} {problem.Message}")];

  [Test]
  public void CheckedSum_UnderResult_ReturnsAResultAndChecksEveryTrap() {
    var files = Files(CheckedSum);
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("pub fn checked_sum(a: i32, b: i32, c: i32) -> Result<i32, Error> {"));
    Assert.That(text, Does.Contain("a.checked_add(b).ok_or(Error::IntegerOverflow)?"));
    Assert.That(text, Does.Contain("return Err(Error::DivideByZero);"));
    Assert.That(text, Does.StartWith("use crate::Error;\n"));
    Assert.That(text, Does.Not.Contain("panic!").And.Not.Contain("expect("));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("mod error;\npub mod snippet;\n\npub use error::Error;\n"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("Rust profile: rust2024 (Rust 1.85, trap policy result)."));
    Assert.That(RustCrates.Text(files, "src/error.rs"), Is.EqualTo(
      "/// A trap of the generated code as an error value: what the source would have thrown or panicked with.\n"
      + "#[derive(Clone, Copy, Debug, PartialEq, Eq)]\n#[repr(u16)]\npub enum Error {\n    DivideByZero = 1,\n    IntegerOverflow = 2,\n}\n\n"
      + "impl std::fmt::Display for Error {\n    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {\n        std::fmt::Debug::fmt(self, f)\n    }\n}\n\n"
      + "impl std::error::Error for Error {}\n"));
  }

  [Test]
  public void CheckedSum_UnderPanic_KeepsThePlainType() {
    var files = Files(CheckedSum, RustProfile.Default);
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("pub fn checked_sum(a: i32, b: i32, c: i32) -> i32 {"));
    Assert.That(text, Does.Contain("a.checked_add(b).expect(\"IntegerOverflow\")"));
    Assert.That(text, Does.Contain("panic!(\"DivideByZero\");"));
    Assert.That(files.Select(file => file.Path), Has.None.EqualTo("src/error.rs"));
  }

  [Test]
  public void Fallibility_IsAFixedPointOverTheCalls() {
    const string methods = "static int Leaf(int a) => a + 1; static int Risky(int a) => checked(a * 3); static int Both(int a) => Leaf(a) + Risky(a); "
      + "static int Even(int n) => n == 0 ? 1 : Odd(n - 1); static int Odd(int n) => n == 0 ? checked(n * n) : Even(n - 1); "
      + "public static int F(int a) => Both(a) + Even(a);";
    var text = Emitted(methods);
    Assert.That(text, Does.Contain("fn leaf(a: i32) -> i32 {"));
    Assert.That(text, Does.Contain("fn risky(a: i32) -> Result<i32, Error> {"));
    Assert.That(text, Does.Contain("fn both(a: i32) -> Result<i32, Error> {"));
    Assert.That(text, Does.Contain("fn even(n: i32) -> Result<i32, Error> {"));
    Assert.That(text, Does.Contain("fn odd(n: i32) -> Result<i32, Error> {"));
    var fallible = RustEmitter.Fallible(RustCrates.Prepared(RustCrates.Import(methods), Result), Result);
    Assert.That(fallible, Does.Contain("F").And.Contain("Even").And.Not.Contain("Leaf"));
  }

  [Test]
  public void ResultOfACall_IsHandedOnAsItIs()
    => Assert.That(Emitted("static int Risky(int a) => checked(a * 3); public static int F(int a) { if (a > 2) return Risky(a); return 0; }"),
      Does.Contain("return risky(a);"));

  [Test]
  public void ElementAccess_UnderResult_IsChecked() {
    var text = Emitted("public static int F(int[] data, int i) { data[i] = data[i + 1]; return data[0]; }");
    Assert.That(text, Does.Contain(".get_mut(").And.Contain(".ok_or(Error::IndexOutOfRange)?"));
    Assert.That(text, Does.Contain("*data.get(0).ok_or(Error::IndexOutOfRange)?"));
  }

  [Test]
  public void Conversions_UnderResult_MapTheirError() {
    var text = Emitted("public static byte F(int a) => checked((byte)a); public static int[] G(int n) => new int[n];");
    Assert.That(text, Does.Contain("pub fn f(a: i32) -> Result<u8, Error> {\n    u8::try_from(a).map_err(|_| Error::IntegerOverflow)\n}"));
    Assert.That(text, Does.Contain("Ok(vec![0; usize::try_from(n).map_err(|_| Error::IntegerOverflow)?])"));
  }

  [Test]
  public void SpellingFunctions_UnderResult_ReturnAResult() {
    var text = Emitted("public static int F(double x) => checked((int)x); public static void G(int[] a, int[] b, int n) => System.Array.Copy(a, 0, b, 1, n);");
    Assert.That(text, Does.Contain("fn ply_f2i_trap_i32(x: f64) -> Result<i32, Error> {"));
    Assert.That(text, Does.Contain("Ok(x as i32)\n    } else {\n        Err(Error::IntegerOverflow)\n    }"));
    Assert.That(text, Does.Contain("fn ply_range(start: i32, length: i32, size: usize) -> Result<std::ops::Range<usize>, Error> {"));
    Assert.That(text, Does.Contain("let from = ply_range(from, length, source.len())?;"));
    Assert.That(text, Does.Contain("Ok(())\n}"));
    var narrowing = RustCrates.ModuleText(Files("public static byte F(int a) => checked((byte)a);", RustProfile.Rust2018.WithTrapPolicy(RustTrapPolicy.Result)));
    Assert.That(narrowing, Does.Contain("fn ply_i2i_trap_i32_u8(x: i32) -> Result<u8, Error> {"));
    Assert.That(narrowing, Does.Contain("ply_i2i_trap_i32_u8(a)"));
  }

  [Test]
  public void FunctionWithoutTraps_UnderResult_KeepsItsTypeAndTheCrateHasNoErrorModule() {
    var files = Files("public static int F(int a) => a + 1;");
    Assert.That(RustCrates.ModuleText(files), Does.Contain("pub fn f(a: i32) -> i32 {"));
    Assert.That(files.Select(file => file.Path), Has.None.EqualTo("src/error.rs"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Not.Contain("error"));
  }

  [Test]
  public void TrapInsideATryBody_IsAMatchThatLeavesTheBody() {
    var text = Emitted("public static int F(int k) { try { return checked((byte)k); } catch (System.OverflowException) { return -1; } }");
    Assert.That(text, Does.Contain("Ok(value) => value,\n            Err(_) => break 't0 ply_trap(3),"));
  }

  [Test]
  public void ModuleWithExceptions_UnderResult_ConvertsWithTheClassAndTestsTheClass() {
    const string classes = "public class X : System.Exception { public X(string m) : base(m) { } public X(string m, System.Exception inner) : base(m, inner) { } } ";
    var tests = new[] { new RustTestCase("Narrows", "F", [new IrConst(IrType.I32, 300)], null, IrTrap.IntegerOverflow) };
    var module = PrototypeFixture.Import("Snippet.cs", classes + PrototypeFixture.Wrap("public static byte F(int k) => checked((byte)k); public static string G(string m) { try { throw new X(\"o\", new X(m)); } catch (X e) { return e.InnerException.Message; } } public static int H(int k) { if (k > 0) throw new X(\"x\"); return k; }"));
    var text = RustCrates.ModuleText(RustCrates.Emit(module, Result, new RustEmitOptions { Tests = tests }));
    Assert.That(text, Does.Contain("u8::try_from(k).map_err(|_| ply_trap(3))"));
    Assert.That(text, Does.Contain(".inner.as_ref().ok_or_else(|| ply_trap(5))?"));
    Assert.That(text, Does.Contain("assert_eq!(f(300).map_err(|error| error.class), Err(3));"));
    Assert.That(text, Does.Contain("pub fn h(k: i32) -> Result<i32, Rc<PlyError>> {"));
    var withoutTraps = RustCrates.ModuleText(RustCrates.Emit(PrototypeFixture.Import("Snippet.cs", classes + PrototypeFixture.Wrap("public static int H(int k) { if (k > 0) throw new X(\"x\"); return k; }")), Result));
    Assert.That(withoutTraps, Does.Contain("static PLY_ERROR_DISPLAY:").And.Not.Contain("PLY_ERROR_MESSAGES"));
  }

  [Test]
  public void ElementsOfEveryKind_UnderResult_AreChecked() {
    const string methods = "public struct P { public int X; } static readonly string[] Names = { \"a\", \"bc\" }; "
      + "public static int F(int i, string s, P[] ps, int[] a) { ps[i].X = 3; return Names[i].Length + s[i] + s.Substring(i, 1).Length + ps[i].X + a[i]++; } "
      + "public static int G(System.Func<int, int>[] fs, int i, bool b) { System.Func<int, int> f = b ? x => x + 1 : null; return fs[i](2) + f(i) + f(i + 1); } "
      + "public static int H(bool b) { int[] a = b ? new int[2] : null; return a.Length; }";
    Assert.That(Problems(methods), Is.Empty);
    var text = Emitted(methods);
    Assert.That(text, Does.Not.Contain("ply_non_null").And.Not.Contain("panic!"));
    Assert.That(text, Does.Contain("NAMES.get(").And.Contain("ps.get_mut(").And.Contain("fs.get(").And.Contain("a.get_mut("));
    Assert.That(text, Does.Contain(".as_ref().ok_or(Error::NullReference)?"));
  }

  [Test]
  public void LockedTextElement_UnderResult_IsCloned()
    => Assert.That(Emitted("static string[] names = { \"a\", \"bc\" }; public static void Set(int i, string s) { names[i] = s; } public static int F(int i) => names[i].Length;"), Does.Contain(".cloned()"));

  [Test]
  public void ErrorCodes_AreStable() {
    Assert.That(RustEmitter.ErrorCode(IrTrap.DivideByZero), Is.EqualTo(1));
    Assert.That(RustEmitter.ErrorCode(IrTrap.IntegerOverflow), Is.EqualTo(2));
    Assert.That(RustEmitter.ErrorCode(IrTrap.IndexOutOfRange), Is.EqualTo(3));
    Assert.That(RustEmitter.ErrorCode(IrTrap.InvalidOperation), Is.EqualTo(4));
    Assert.That(Enum.GetValues<IrTrap>().Select(RustEmitter.ErrorCode), Is.Unique.And.All.InRange(1, Enum.GetValues<IrTrap>().Length));
  }

  [Test]
  public void Tests_UnderResult_AssertTheResult() {
    var tests = new[] {
      new RustTestCase("Sums", "CheckedSum", [new IrConst(IrType.I32, 7), new IrConst(IrType.I32, 3), new IrConst(IrType.I32, 2)], new IrConst(IrType.I32, 5)),
      new RustTestCase("DividesByZero", "CheckedSum", [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2), new IrConst(IrType.I32, 0)], null, IrTrap.DivideByZero),
      new RustTestCase("Halves", "Half", [new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 3.0))], new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.5))),
      new RustTestCase("Runs", "Run", [new IrConst(IrType.I32, 1)], null)
    };
    var text = RustCrates.ModuleText(Files(CheckedSum + " public static double Half(double x) => checked(x / 2 + (new int[1])[(int)x - 3]); static int s; public static void Run(int a) { s = checked(a * 2); }",
      Result, new RustEmitOptions { Tests = tests }));
    Assert.That(text, Does.Contain("assert_eq!(checked_sum(7, 3, 2), Ok(5));"));
    Assert.That(text, Does.Contain("assert_eq!(checked_sum(1, 2, 0), Err(Error::DivideByZero));"));
    Assert.That(text, Does.Contain("assert_eq!(half(3.0).map(f64::to_bits), Ok(1.5f64.to_bits()));"));
    Assert.That(text, Does.Contain("assert_eq!(run(1), Ok(()));"));
    Assert.That(text, Does.Not.Contain("should_panic"));
  }

  [Test]
  public void Tests_UnderPanic_StayShouldPanic() {
    var tests = new[] { new RustTestCase("DividesByZero", "CheckedSum", [new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 2), new IrConst(IrType.I32, 0)], null, IrTrap.DivideByZero) };
    Assert.That(RustCrates.ModuleText(Files(CheckedSum, RustProfile.Default, new RustEmitOptions { Tests = tests })), Does.Contain("#[should_panic(expected = \"DivideByZero\")]"));
  }

  [Test]
  public void TrapsFixture_IsWrittenUnderResult_AndCatchesEachTrapByItsClass() {
    var module = ExceptionFixtures.Imported("Traps");
    Assert.That(RustCrates.Check(module, Result), Is.Empty);
    var text = RustCrates.ModuleText(RustCrates.Emit(module, Result));
    Assert.That(text, Does.Contain("pub fn classify(k: i32) -> Result<i32, Rc<PlyError>> {"));
    Assert.That(text, Does.Contain("if let Err(error) = raise(k) {\n            break 't0 error;\n        }"));
    Assert.That(text, Does.Contain("fn ply_trap(class: usize) -> Rc<PlyError> {"));
    Assert.That(text, Does.Contain("static PLY_ERROR_MESSAGES: [&str; 13] = ["));
    Assert.That(text, Does.Contain("impl std::fmt::Display for PlyError {\n    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {\n        f.write_str(PLY_ERROR_DISPLAY[self.class])\n    }\n}"));
    Assert.That(text, Does.Contain("None => break 't0 ply_trap(5),"));
    Assert.That(text, Does.Not.Contain("panic!").And.Not.Contain("ply_unhandled"));
  }

  [Test]
  public void TrapsFixture_IsRefusedUnderPanic()
    => Assert.That(RustCrates.Check(ExceptionFixtures.Imported("Traps")).Select(problem => problem.Message),
      Has.Some.Contain("under the trap policy 'panic': 'a catch of a class a trap belongs to, around code that may trap'"));

  [Test]
  public void ThrowOfALibraryException_UnderResultWithExceptions_IsCaught() {
    var text = Emitted("public static int F(int k) { try { if (k > 0) throw new System.InvalidOperationException(\"x\"); return checked(k * k); } catch (System.InvalidOperationException) { return -1; } catch (System.OverflowException) { return -2; } }");
    Assert.That(text, Does.Contain("break 't0 ply_error(7, ply_utf16(\"x\"), None);"));
    Assert.That(text, Does.Contain("Some(value) => value,\n            None => break 't0 ply_trap(3),"));
  }

  [Test]
  public void ThrowOfALibraryException_UnderResultWithoutExceptions_IsItsTrap()
    => Assert.That(Emitted("public static int F(int k) { if (k > 0) throw new System.InvalidOperationException(\"x\"); return k; }"),
      Does.Contain("return Err(Error::MissingValue);"));

  [Test]
  public void ObjectThatMayBeNull_UnderResult_IsChecked()
    => Assert.That(Emitted("public sealed class B { public int V; } public static int F(bool b) { B x = b ? new B() : null; return x.V; }"),
      Does.Contain(".as_ref().ok_or(Error::NullReference)?"));

  [Test]
  public void OptionalValue_UnderResult_IsChecked()
    => Assert.That(Emitted("public static int F(int? a) => a.Value;"), Does.Contain("pub fn f(a: Option<i32>) -> Result<i32, Error> {\n    a.ok_or(Error::MissingValue)\n}"));

  private static IEnumerable<TestCaseData> Limits() {
    yield return new TestCaseData("public static int F(System.Collections.Generic.List<int> l, int i) => l[i];", "the helper ply_list_index, which panics").SetName("HelperThatPanics");
    yield return new TestCaseData("public static int F(int a) { System.Func<int, int> g = x => checked(x * a); return g(2); }", "a trap inside a closure").SetName("TrapInAClosure");
    yield return new TestCaseData("static int[] t = new int[4]; public static int F(int i) => t[i]++;", "a step of an element of module state behind a lock").SetName("LockedStep");
    yield return new TestCaseData("public struct Error { public int A; } public static int F(Error e) => checked(e.A * 2);", "a type of the module named 'Error'").SetName("TypeNamedError");
    yield return new TestCaseData("static int R(int a) => checked(a * 2); public static int F(int a) { System.Func<int, int> g = x => R(x) + a; return g(a); }", "a call that may trap inside a closure").SetName("FallibleCallInAClosure");
  }

  [TestCaseSource(nameof(Limits))]
  public void WhatWouldStillPanic_IsRefusedWithPly1552(string methods, string construct)
    => Assert.That(Problems(methods), Has.Some.EqualTo($"PLY1552 trap policy 'result' is not available for '{construct}'").Or.Some.StartsWith($"PLY1552 trap policy 'result' is not available for '{construct}' (in "));

  [Test]
  public void ExceptionRegionTheAdrLeavesOut_StaysRefusedUnderResult()
    => Assert.That(Problems("static int s; public static int F(int k) { try { s = checked(k * 2); } catch (System.OverflowException) { s = -1; } finally { s++; } return s; }"),
      Has.Some.StartsWith("PLY1560 exception region in the function 'F' cannot be emitted under the trap policy 'result': 'a try with both catch clauses and a finally block'"));

  [Test]
  public void WhatWouldStillPanic_IsWrittenUnderPanic()
    => Assert.That(Problems("public static int F(System.Collections.Generic.List<int> l, int i) => l[i];", RustProfile.Default), Is.Empty);

  [Test]
  public void LockedModuleState_UnderResult_IsCheckedOutsideTheLock() {
    var text = Emitted("static int[] t = new int[4]; public static int F(int i, int v) { t[i] = v; return t[i + 1]; }");
    Assert.That(text, Does.Contain(".get_mut(").And.Contain(".map(|element| *element = v)").And.Contain(".copied()"));
    Assert.That(text, Does.Contain(".ok_or(Error::IndexOutOfRange)?"));
  }

  [Test]
  public void TrapPolicy_IsParsedFromItsName() {
    Assert.That(RustProfile.TryParse(null, null, "result", out var profile, out var error), Is.True);
    Assert.That((profile!.TrapPolicy, error), Is.EqualTo((RustTrapPolicy.Result, (string?)null)));
    Assert.That(RustProfile.TryParse("rust2021", "1.70", null, out profile, out _), Is.True);
    Assert.That((profile!.TrapPolicy, profile.Name, profile.RustVersionName), Is.EqualTo((RustTrapPolicy.Panic, "rust2021", "1.70")));
    Assert.That(RustProfile.TryParse(null, null, "unwind", out profile, out error), Is.False);
    Assert.That((profile, error), Is.EqualTo(((RustProfile?)null, "unknown trap policy 'unwind'; the trap policies are panic and result")));
    Assert.That(RustProfile.TryParse("rust1999", null, "result", out profile, out error), Is.False);
    Assert.That(error, Does.StartWith("unknown Rust profile 'rust1999'"));
  }

  [Test]
  public void TrapPolicy_SurvivesAnotherRustVersion_AndShowsInTheProfile() {
    var profile = Result.WithRustVersion(new Version(1, 90));
    Assert.That(profile.TrapPolicy, Is.EqualTo(RustTrapPolicy.Result));
    Assert.That(profile.ToString(), Is.EqualTo("rust2024 (Rust 1.90, trap policy result)"));
    Assert.That(RustProfile.Default.ToString(), Is.EqualTo("rust2024 (Rust 1.85)"));
  }

  [Test]
  public void EnumAndReExport_ReadBackAsPrinted() {
    var file = new RsFile([], [], [
      new RsUse("error::Error") { Visibility = "pub" },
      new RsEnum("Error", "pub", [new RsVariant("DivideByZero", "1"), new RsVariant("Other", null)]) { Attributes = ["repr(u16)"] },
      new RsEnum("Never", "", [])
    ]);
    var text = RustPrinter.Print(file);
    Assert.That(text, Is.EqualTo("pub use error::Error;\n\n#[repr(u16)]\npub enum Error {\n    DivideByZero = 1,\n    Other,\n}\n\nenum Never {}\n"));
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
  }

  [Test]
  public void RepeatedArrayTooWide_PutsTheValueAndTheCountOnLinesOfTheirOwn() {
    var count = new RsTry(new RsMethodCall(new RsCall(new RsPath("usize::try_from"), [new RsPath("zero")]), "map_err",
      [new RsClosure(false, [new RsParameter("_", "")], new RsCall(new RsPath("ply_error"), [new RsLiteral("3"), new RsCall(new RsPath("ply_utf16"), [new RsString("Arithmetic operation resulted in an overflow.")]), new RsPath("None")]))]));
    var text = RustPrinter.Print(new RsMacro("vec", [new RsArray([new RsLiteral("0")]) { Count = count }]) { Bracketed = true });
    Assert.That(text, Does.StartWith("vec![\n    0;\n    usize::try_from(zero).map_err("));
    Assert.That(text, Does.EndWith(")?\n]"));
  }

}
