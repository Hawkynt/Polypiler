using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>The crate as a package (#459): its functions, their visibility and calls, and the unit tests it carries.</summary>
[TestFixture]
public sealed class RustPackageEmitTests {

  private const string Mixer = "public static uint Mix(uint h, uint b) => Step(h ^ b); static uint Step(uint h) => h * 16777619u; public static uint Twice(uint h) => Mix(Mix(h, 1), 2);";

  [Test]
  public void Functions_KeepTheirVisibilityAndCalls() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(Mixer), level: Polypiler.Core.Optimize.OptimizationLevel.None));
    Assert.That(text, Does.Contain("pub fn mix(h: u32, b: u32) -> u32 {\n    step(h ^ b)\n}"));
    Assert.That(text, Does.Contain("fn step(h: u32) -> u32 {\n    h.wrapping_mul(16777619)\n}"));
    Assert.That(text, Does.Contain("pub fn twice(h: u32) -> u32 {\n    mix(mix(h, 1), 2)\n}"));
  }

  [Test]
  public void Recursion_IsACallOfItself()
    => Assert.That(RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);"), level: Polypiler.Core.Optimize.OptimizationLevel.None)),
      Does.Contain("if n < 2 {\n        n\n    } else {\n        fib(n.wrapping_sub(1)).wrapping_add(fib(n.wrapping_sub(2)))\n    }"));

  [Test]
  public void Tests_AreTheUnitTestsOfTheModule() {
    var options = new RustEmitOptions {
      Tests = [
        new RustTestCase("MixOfA", "Mix", [new IrConst(IrType.U32, 2166136261u), new IrConst(IrType.U32, 97)], new IrConst(IrType.U32, 0xE40C292Cu)),
        new RustTestCase("rejects_overflow", "Twice", [new IrConst(IrType.U32, 0)], null, IrTrap.IntegerOverflow),
        new RustTestCase("runs", "Twice", [new IrConst(IrType.U32, 0)], null)
      ]
    };
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(Mixer), options: options));
    Assert.That(text, Does.Contain("#[cfg(test)]\nmod tests {\n    use super::*;\n\n    #[test]\n    fn mix_of_a() {\n        assert_eq!(mix(2166136261, 97), 3826002220);\n    }\n"));
    Assert.That(text, Does.Contain("    #[test]\n    #[should_panic(expected = \"IntegerOverflow\")]\n    fn rejects_overflow() {\n        twice(0);\n    }\n"));
    Assert.That(text, Does.EndWith("    #[test]\n    fn runs() {\n        twice(0);\n    }\n}\n"));
  }

  [Test]
  public void FloatTest_ComparesBits() {
    var options = new RustEmitOptions { Tests = [new RustTestCase("half", "Half", [new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 3.0))], new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, 1.5)))] };
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("public static double Half(double a) => a / 2;"), options: options));
    Assert.That(text, Does.Contain("assert_eq!(half(3.0).to_bits(), 1.5f64.to_bits());"));
  }

  private static string Folded(string methods, Polypiler.Core.Optimize.OptimizationLevel level = Polypiler.Core.Optimize.OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: level));

  /// <summary>Scenario: a wrapping reduction is a fold, never sum.</summary>
  [Test]
  public void WrappingReduction_IsAFold() {
    var text = Folded("public static uint Sum(byte[] data) { uint s = 0; foreach (var b in data) s += b; return s; }", Polypiler.Core.Optimize.OptimizationLevel.Default);
    Assert.That(text, Does.Contain("    data.iter()\n        .map(|&b| u32::from(b))\n        .fold(0u32, |acc, x| acc.wrapping_add(x))\n"));
    Assert.That(text, Does.Not.Contain(".sum()").And.Not.Contain("for "));
  }

  [TestCase("public static uint F(uint[] data) { uint s = 0; foreach (var v in data) s += v; return s; }", "data.iter().fold(0u32, |acc, &v| acc.wrapping_add(v))")]
  [TestCase("public static uint F(uint[] data) { uint s = 0; foreach (var b in data) s ^= b; return s; }", "data.iter().fold(0u32, |acc, &b| acc ^ b)")]
  [TestCase("public static uint F(uint[] data) { uint s = 0; foreach (var v in data) s |= v; return s; }", "data.iter().fold(0u32, |acc, &v| acc | v)")]
  [TestCase("public static uint F(uint[] data) { uint s = 0xFFFFFFFF; foreach (var v in data) s &= v; return s; }", "data.iter().fold(4294967295u32, |acc, &v| acc & v)")]
  [TestCase("public static ulong F(ulong[] data, ulong h) { ulong s = h; foreach (var v in data) s *= v; return s; }", "data.iter().fold(h, |acc, &v| acc.wrapping_mul(v))")]
  [TestCase("public static int F(int n) { var data = new int[n]; int s = 0; foreach (var v in data) s += v * 3; return s; }", "data.iter()\n        .map(|&v| v.wrapping_mul(3))\n        .fold(0i32, |acc, x| acc.wrapping_add(x))")]
  public void Reduction_IsAFoldOfItsOperation(string method, string expected) => Assert.That(Folded(method), Does.Contain(expected));

  /// <summary>A binding assigned again after the fold stays mutable.</summary>
  [Test]
  public void ReductionReassignedAfterwards_StaysMutable()
    => Assert.That(Folded("public static uint F(uint[] data) { uint s = 0; foreach (var v in data) s += v; s += 1; return s; }"), Does.Contain("let mut s: u32 = data.iter().fold(0u32, |acc, &v| acc.wrapping_add(v));"));

  [TestCase("public static uint F(byte[] data) { uint s = 0; foreach (var b in data) s = s * 31 + b; return s; }", TestName = "ReductionWhoseValueReadsTheBinding_StaysALoop")]
  [TestCase("public static double F(double[] data) { double s = 0; foreach (var v in data) s += v; return s; }", TestName = "FloatingPointReduction_StaysALoop")]
  [TestCase("public static int F(int[] data) { int s = 0; foreach (var v in data) s = checked(s + v); return s; }", TestName = "TrappingReduction_StaysALoop")]
  [TestCase("public static int F(int[] data) { int s = 0; int c = 0; foreach (var v in data) { s += v; c++; } return s + c; }", TestName = "LoopOfTwoStatements_StaysALoop")]
  [TestCase("public static int F(int[] data) { int s = 0; foreach (var acc in data) s += acc; return s; }", TestName = "ElementNamedLikeTheAccumulator_StaysALoop")]
  [TestCase("public static int F(int[] data, int acc) { int s = 0; foreach (var v in data) s += v * acc; return s; }", TestName = "ValueNamingTheAccumulator_StaysALoop")]
  [TestCase("public static int F(int[] data, int t) { int s = 0; foreach (var v in data) t += v; return s + t; }", TestName = "LoopThatAssignsAnotherVariable_StaysALoop")]
  [TestCase("public static int F(int[] data, int t) { int s = t * 2; foreach (var v in data) s += v; return s; }", TestName = "ReductionFromAComputedStart_StaysALoop")]
  [TestCase("public static int F(int[] data) { int s = 0; for (int i = 0; i < data.Length; i++) s += data[i] * i; return s; }", TestName = "CountedLoopThatIsNoElementLoop_StaysALoop")]
  public void ReductionThatIsNoFold_StaysALoop(string method) => Assert.That(Folded(method), Does.Contain("for ").And.Not.Contain(".fold("));

  /// <summary>A value that leaves the function, by the <c>?</c> of an exception, cannot move into a closure.</summary>
  [Test]
  public void ReductionWhoseValuePropagates_StaysALoop() {
    const string Methods = "class E : System.Exception { } static uint G(uint x) { if (x == 0) throw new E(); return x; } "
      + "static uint F(uint[] data) { uint s = 0; foreach (var v in data) s += G(v); return s; } public static uint H(uint[] data) { try { return F(data); } catch (E) { return 0; } }";
    Assert.That(Folded(Methods), Does.Contain("g(v)?").And.Not.Contain(".fold("));
  }

  private const string Layered = "public static uint Rotl(uint x, int n) => System.Numerics.BitOperations.RotateLeft(x, n); static uint Prime() => 16777619u; "
    + "public static uint Fnv1a(byte[] data) { uint h = 2166136261u; foreach (var b in data) { h ^= b; h *= Prime(); } return h; } "
    + "public static uint Mixed(uint h) => Rotl(h, 5) ^ h;";

  private static readonly RustEmitOptions _layout = new() { Modules = new System.Collections.Generic.Dictionary<string, string> { ["Rotl"] = "util", ["Prime"] = "util", ["Fnv1a"] = "hash", ["Mixed"] = "hash" } };

  private static System.Collections.Generic.IReadOnlyList<Polypiler.Core.GeneratedFile> Placed(string methods, RustEmitOptions options)
    => RustCrates.Emit(RustCrates.Import(methods), options: options, level: Polypiler.Core.Optimize.OptimizationLevel.None);

  /// <summary>Scenario: the IR modules util and hash, where hash calls util's rotl, are one file each that names what it uses.</summary>
  [Test]
  public void PlacedFunctions_AreModulesOfTheirOwn() {
    var files = Placed(Layered, _layout);
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/hash.rs", "src/util.rs" }));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.EndWith("#![forbid(unsafe_code)]\n\npub mod hash;\npub mod util;\n"));
    var hash = RustCrates.Text(files, "src/hash.rs");
    Assert.That(hash, Does.StartWith("use crate::util::prime;\nuse crate::util::rotl;\n\npub fn fnv1a(data: &[u8]) -> u32 {"));
    Assert.That(hash, Does.Contain("pub fn mixed(h: u32) -> u32 {\n    rotl(h, 5) ^ h\n}"));
    var util = RustCrates.Text(files, "src/util.rs");
    Assert.That(util, Does.StartWith("pub fn rotl(x: u32, n: i32) -> u32 {").And.Contain("pub(crate) fn prime() -> u32 {").And.Not.Contain("use "));
    Assert.That(RustEmitter.Exports(RustCrates.Prepared(RustCrates.Import(Layered), level: Polypiler.Core.Optimize.OptimizationLevel.None), RustProfile.Default, _layout),
      Is.EqualTo(new System.Collections.Generic.Dictionary<string, string> { ["Rotl"] = "util::rotl", ["Fnv1a"] = "hash::fnv1a", ["Mixed"] = "hash::mixed" }));
  }

  /// <summary>Test design 16: emitting twice gives the same files.</summary>
  [Test]
  public void PlacedFunctions_AreReproducible() {
    var first = Placed(Layered, _layout).Select(file => (file.Path, file.Content)).ToList();
    var second = Placed(Layered, _layout).Select(file => (file.Path, file.Content)).ToList();
    Assert.That(second, Is.EqualTo(first));
  }

  /// <summary>
  /// What stays in the module of the IR module - a function placed nowhere, a table, the tests - is
  /// named by the placed ones, and opened to the crate; a private module has no public item; the
  /// tests name the placed functions they call themselves.
  /// </summary>
  [Test]
  public void PlacedFunctionsAndWhatStays_NameEachOther() {
    const string Methods = "static readonly uint[] Table = { 1, 2, 3 }; static uint Pick(int i) => Table[i] + Base(); public static uint Base() => 7; public static uint Twice(int i) => Pick(i) * 2;";
    var options = new RustEmitOptions {
      Modules = new System.Collections.Generic.Dictionary<string, string> { ["Pick"] = "lookup", ["Missing"] = "other", ["Twice"] = "Snippet" },
      Tests = [new RustTestCase("picks", "Pick", [new IrConst(IrType.I32, 1)], new IrConst(IrType.U32, 9))]
    };
    var files = Placed(Methods, options);
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/snippet.rs", "src/lookup.rs" }));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.EndWith("\nmod lookup;\npub mod snippet;\n"));
    var lookup = RustCrates.Text(files, "src/lookup.rs");
    Assert.That(lookup, Does.StartWith("use crate::snippet::TABLE;\nuse crate::snippet::base;\n\npub(crate) fn pick(i: i32) -> u32 {"));
    var snippet = RustCrates.Text(files, "src/snippet.rs");
    Assert.That(snippet, Does.StartWith("use crate::lookup::pick;\n\npub(crate) static TABLE: [u32; 3] = [1, 2, 3];"));
    Assert.That(snippet, Does.Contain("mod tests {\n    use super::*;\n\n    #[test]"));
  }

  /// <summary>The tests module names a placed function its module's code does not call.</summary>
  [Test]
  public void TestsOfAPlacedFunction_NameIt() {
    var options = new RustEmitOptions {
      Modules = new System.Collections.Generic.Dictionary<string, string> { ["Rotl"] = "util" },
      Tests = [new RustTestCase("rotates", "Rotl", [new IrConst(IrType.U32, 1), new IrConst(IrType.I32, 1)], new IrConst(IrType.U32, 2))]
    };
    var text = RustCrates.Text(Placed("public static uint Rotl(uint x, int n) => System.Numerics.BitOperations.RotateLeft(x, n); public static uint Other(uint x) => x;", options), "src/snippet.rs");
    Assert.That(text, Does.StartWith("pub fn other(").And.Contain("mod tests {\n    use crate::util::rotl;\n\n    #[test]"));
  }

  /// <summary>A placed function names the standard library's items it uses itself; the parameters of its closures are no items.</summary>
  [Test]
  public void PlacedFunctionWithAClosure_UsesWhatItNames() {
    var options = new RustEmitOptions { Modules = new System.Collections.Generic.Dictionary<string, string> { ["G"] = "util" } };
    var files = Placed("static int Base() => 3; public static int G(int x) { int k = x * 2; System.Func<int, int> f = y => y + k + Base(); return f(x) + f(1); }", options);
    Assert.That(RustCrates.Text(files, "src/util.rs"), Does.StartWith("use crate::snippet::base;\nuse std::rc::Rc;\n\npub fn g(x: i32) -> i32 {"));
    Assert.That(RustCrates.Text(files, "src/snippet.rs"), Does.StartWith("pub(crate) fn base() -> i32 {").And.Not.Contain("use "));
  }

  /// <summary>A function that names a type of the crate stays with the types, whose fields are private to their module.</summary>
  [Test]
  public void PlacedFunctionThatNamesAType_StaysWithTheTypes() {
    var options = new RustEmitOptions { Modules = new System.Collections.Generic.Dictionary<string, string> { ["Get"] = "util", ["Twice"] = "util" } };
    var files = Placed("public struct P { public int X; } public static int Get(P p) => p.X; public static int Twice(int a) => a * 2;", options);
    Assert.That(RustCrates.Text(files, "src/snippet.rs"), Does.Contain("pub fn get(p: P) -> i32 {"));
    Assert.That(RustCrates.Text(files, "src/util.rs"), Does.Contain("pub fn twice(a: i32) -> i32 {").And.Not.Contain("fn get"));
  }

  private static System.Collections.Generic.Dictionary<string, string> Layout(params string[] pairs)
    => Enumerable.Range(0, pairs.Length / 2).ToDictionary(index => pairs[2 * index], index => pairs[2 * index + 1]);

  /// <summary>
  /// A module below another is a file in its parent's folder, which the parent declares - a parent
  /// without functions a file of declarations; a function named after its module's path is called
  /// by the rest of its name there, unless another function of the crate has that name.
  /// </summary>
  [Test]
  public void NestedModules_AreFilesOfTheirParents() {
    const string Methods = "public static uint UtilBitsRotl(uint x) => x * 3; static uint UtilBitsMix(uint h) => UtilBitsRotl(h) + 1; public static uint Mix(uint h) => h; public static uint HashRun(uint h) => UtilBitsMix(h) + Mix(h);";
    var options = new RustEmitOptions { Modules = Layout("UtilBitsRotl", "util::bits", "UtilBitsMix", "util::bits", "HashRun", "hash") };
    var files = Placed(Methods, options);
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/snippet.rs", "src/hash.rs", "src/util.rs", "src/util/bits.rs" }));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.EndWith("pub mod hash;\npub mod snippet;\npub mod util;\n"));
    Assert.That(RustCrates.Text(files, "src/util.rs"), Is.EqualTo("pub mod bits;\n"));
    Assert.That(RustCrates.Text(files, "src/util/bits.rs"), Does.Contain("pub fn rotl(x: u32) -> u32 {").And.Contain("pub(crate) fn util_bits_mix(h: u32) -> u32 {\n    rotl(h).wrapping_add(1)\n}"));
    Assert.That(RustCrates.Text(files, "src/hash.rs"), Does.StartWith("use crate::snippet::mix;\nuse crate::util::bits::util_bits_mix;\n\npub fn run(h: u32) -> u32 {"));
    var exports = RustEmitter.Exports(RustCrates.Prepared(RustCrates.Import(Methods), level: Polypiler.Core.Optimize.OptimizationLevel.None), RustProfile.Default, options);
    Assert.That(exports, Is.EqualTo(new System.Collections.Generic.Dictionary<string, string> { ["UtilBitsRotl"] = "util::bits::rotl", ["Mix"] = "snippet::mix", ["HashRun"] = "hash::run" }));
  }

  /// <summary>A module whose parent holds functions declares it in the parent's own file.</summary>
  [Test]
  public void ModuleBelowAModuleWithFunctions_IsDeclaredByIt() {
    var files = Placed("public static uint A(uint x) => x + 1; public static uint B(uint x) => A(x) * 2;", new RustEmitOptions { Modules = Layout("A", "outer", "B", "outer::inner") });
    Assert.That(RustCrates.Text(files, "src/outer.rs"), Does.StartWith("pub mod inner;\n\npub fn a(x: u32) -> u32 {"));
    Assert.That(RustCrates.Text(files, "src/outer/inner.rs"), Does.StartWith("use crate::outer::a;\n\npub fn b("));
  }

  /// <summary>Where the IR module is the crate root, what stays is written in src/lib.rs, its exports at the root; a test function is a #[test].</summary>
  [Test]
  public void CrateRoot_HoldsWhatStays() {
    const string Methods = "public static uint Base(uint x) => x + 1; public static uint Twice(uint x) => Base(x) * 2; static void Checks() { if (Twice(1) != 4) throw new System.InvalidOperationException(); }";
    var options = new RustEmitOptions { Modules = Layout("Twice", "calc"), CrateRoot = true, TestFunctions = ["Checks"] };
    var files = Placed(Methods, options);
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs", "src/calc.rs" }));
    var root = RustCrates.Text(files, "src/lib.rs");
    Assert.That(root, Does.Contain("#![forbid(unsafe_code)]\n\npub mod calc;\n\npub fn base(x: u32) -> u32 {"));
    Assert.That(root, Does.Contain("mod tests {\n    use crate::calc::twice;\n\n    #[test]\n    fn checks() {"));
    Assert.That(RustCrates.Text(files, "src/calc.rs"), Does.StartWith("use crate::base;\n\npub fn twice("));
    var exports = RustEmitter.Exports(RustCrates.Prepared(RustCrates.Import(Methods), level: Polypiler.Core.Optimize.OptimizationLevel.None), RustProfile.Default, options);
    Assert.That(exports, Is.EqualTo(new System.Collections.Generic.Dictionary<string, string> { ["Base"] = "base", ["Twice"] = "calc::twice" }));
  }

  /// <summary>A module named lib is the crate root: its file would be src/lib.rs.</summary>
  [Test]
  public void ModuleNamedLib_IsTheCrateRoot() {
    var module = PrototypeFixture.Import("lib.cs", "public static class Lib { public static int F(int a) => a; }");
    var files = RustCrates.Emit(module, options: new RustEmitOptions { PackageName = "p" });
    Assert.That(files.Select(file => file.Path), Is.EqualTo(new[] { "Cargo.toml", "src/lib.rs" }));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.EndWith("#![forbid(unsafe_code)]\n\npub fn f(a: i32) -> i32 {\n    a\n}\n"));
    Assert.That(RustEmitter.Exports(module, RustProfile.Default)["F"], Is.EqualTo("f"));
  }

  /// <summary>O0501: a private function whose every call the optimizer inlined is removed; Rust would call it dead code.</summary>
  [Test]
  public void PrivateFunctionInlinedEverywhere_IsRemoved() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("static uint Prime() => 16777619u; public static uint F(uint h) => h * Prime();")));
    Assert.That(text, Does.Not.Contain("fn prime").And.Contain("h.wrapping_mul(16777619)"));
  }

  [TestCase("lib")]
  [TestCase("Main")]
  public void ModuleOfTheCratesOwnFiles_IsRefused(string module) {
    var options = new RustEmitOptions { Modules = new System.Collections.Generic.Dictionary<string, string> { ["F"] = module } };
    var problems = RustEmitter.Check(RustCrates.Prepared(RustCrates.Import("public static int F(int a) => a;")), RustProfile.Default, null, options);
    Assert.That(problems.Single().Message, Does.Contain($"'the module name '{module.ToLowerInvariant()}''"));
  }

  [Test]
  public void TestOfAnUnknownFunction_IsRefused() {
    var options = new RustEmitOptions { Tests = [new RustTestCase("t", "Missing", [], null)] };
    var problems = RustEmitter.Check(RustCrates.Prepared(RustCrates.Import(Mixer)), RustProfile.Default, null, options);
    Assert.That(problems.Single().Message, Does.Contain("a test of the function 'Missing' with 0 arguments"));
  }

}
