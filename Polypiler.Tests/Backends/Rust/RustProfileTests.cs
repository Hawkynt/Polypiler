using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.Backends.Rust.Passes;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Target;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>The profiles and the capability table of the Rust backend, the names it gives, the target passes and the API map.</summary>
[TestFixture]
public sealed class RustProfileTests {

  [TestCase(null, null, "rust2024 (Rust 1.85)")]
  [TestCase("rust2018", null, "rust2018 (Rust 1.31)")]
  [TestCase("rust2021", null, "rust2021 (Rust 1.56)")]
  [TestCase("rust2021", "1.77", "rust2021 (Rust 1.77)")]
  [TestCase("rust2021", "1.77.2", "rust2021 (Rust 1.77)")]
  [TestCase("rust2024", "1.85", "rust2024 (Rust 1.85)")]
  public void Profile_IsReadFromItsNames(string? name, string? version, string expected) {
    Assert.That(RustProfile.TryParse(name, version, out var profile, out var error), Is.True, error);
    Assert.That(profile!.ToString(), Is.EqualTo(expected));
  }

  [TestCase("rust2015", null, "unknown Rust profile 'rust2015'; the profiles are rust2018, rust2021, rust2024")]
  [TestCase("rust2024", "1.84", "Rust 1.84 is older than the 2024 edition of the profile 'rust2024', which needs 1.85")]
  [TestCase("rust2024", "2.0", "'2.0' is not a Rust version of the form 1.<minor>")]
  [TestCase("rust2024", "1", "'1' is not a Rust version of the form 1.<minor>")]
  [TestCase("rust2024", "1.x", "'1.x' is not a Rust version of the form 1.<minor>")]
  [TestCase("rust2024", "1.85.0.1", "'1.85.0.1' is not a Rust version of the form 1.<minor>")]
  public void WrongNames_AreRefused(string name, string? version, string error) {
    Assert.That(RustProfile.TryParse(name, version, out var profile, out var message), Is.False);
    Assert.That((profile, message), Is.EqualTo(((RustProfile?)null, error)));
  }

  [Test]
  public void RustVersion_IsNeverLowered() {
    Assert.Throws<ArgumentException>(() => RustProfile.Rust2021.WithRustVersion(new Version(1, 40)));
    Assert.That(RustProfile.Rust2021.WithRustVersion(new Version(1, 90, 3)).RustVersionName, Is.EqualTo("1.90"));
    Assert.That(RustProfile.All.Select(profile => profile.Edition), Is.EqualTo(new[] { 2018, 2021, 2024 }));
    Assert.That(RustProfile.Default, Is.SameAs(RustProfile.Rust2024));
  }

  /// <summary>Each row of the table on its release, just below it and above it.</summary>
  [TestCase(RustCapabilities.RoundTiesEven, 76, false)]
  [TestCase(RustCapabilities.RoundTiesEven, 77, true)]
  [TestCase(RustCapabilities.LabeledBlock, 64, false)]
  [TestCase(RustCapabilities.LabeledBlock, 65, true)]
  [TestCase(RustCapabilities.TryFrom, 33, false)]
  [TestCase(RustCapabilities.TryFrom, 34, true)]
  [TestCase(RustCapabilities.IsSubnormal, 53, true)]
  [TestCase(RustCapabilities.RustVersionKey, 55, false)]
  public void Capability_FollowsTheRustVersion(string name, int minor, bool expected) {
    var profile = minor < 31 ? RustProfile.Rust2018 : RustProfile.Rust2018.WithRustVersion(new Version(1, minor));
    Assert.That(RustCapabilities.Default.Has(name, profile), Is.EqualTo(expected));
  }

  [Test]
  public void Table_CanLoseARow_AndKnowsItsRows() {
    var without = RustCapabilities.Default.With(RustCapabilities.CopySign, null);
    Assert.That(without.Has(RustCapabilities.CopySign, RustProfile.Rust2024), Is.False);
    Assert.That(without.Rows[RustCapabilities.CopySign], Is.Null);
    Assert.Throws<ArgumentException>(() => RustCapabilities.Default.Has("no-such-row", RustProfile.Rust2024));
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("public static double F(double a, double b) => System.Math.CopySign(a, b);"), capabilities: without));
    Assert.That(text, Does.Not.Contain(".copysign(").And.Contain("f64::from_bits("));
  }

  /// <summary>The outline of #459's naming scenario, and the classes of its test design.</summary>
  [TestCase("ComputeCrc32", "compute_crc32")]
  [TestCase("HTTPServer", "http_server")]
  [TestCase("rotl32", "rotl32")]
  [TestCase("type", "r#type")]
  [TestCase("Self", "self_")]
  [TestCase("crate", "crate_")]
  [TestCase("2fast", "_2fast")]
  [TestCase("already_snake", "already_snake")]
  [TestCase("$t3", "t3")]
  [TestCase("$", "_v")]
  [TestCase("gen", "r#gen")]
  public void Name_IsSnakeCase(string ir, string rust) => Assert.That(RustNames.Snake(ir, 2024), Is.EqualTo(rust));

  [Test]
  public void Keywords_FollowTheEdition() {
    Assert.That(RustNames.Snake("gen", 2021), Is.EqualTo("gen"));
    Assert.That(RustNames.Snake("async", 2018), Is.EqualTo("r#async"));
    Assert.That(RustNames.Screaming("maxValue", 2024), Is.EqualTo("MAX_VALUE"));
    Assert.That(RustNames.UpperCamel("http_server", 2024), Is.EqualTo("HttpServer"));
    Assert.That(RustNames.Words("parseHTTPHeader2Fast"), Is.EqualTo(new[] { "parse", "HTTP", "Header2", "Fast" }));
  }

  [Test]
  public void CollidingNames_TakeSuffixes()
    => Assert.That(RustNames.Unique(["doIt", "do_it", "DoIt", "type", "Type"], name => RustNames.Snake(name, 2024), new HashSet<string>()),
      Is.EqualTo(new[] { "do_it", "do_it_2", "do_it_3", "r#type", "type_2" }));

  [Test]
  public void PrivateCollidingFunctions_TakeSuffixes() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("static int doIt(int a) => a + 1; static int DoIt(int a) => a + 2; public static int F(int a) => doIt(a) + DoIt(a);"), level: Polypiler.Core.Optimize.OptimizationLevel.None));
    Assert.That(text, Does.Contain("fn do_it(a: i32) -> i32").And.Contain("fn do_it_2(a: i32) -> i32").And.Contain("do_it(a).wrapping_add(do_it_2(a))"));
  }

  [Test]
  public void CollidingExports_AreRefused() {
    var module = RustCrates.Import("public static int GetValue(int a) => a; public static int get_value(int a) => a + 1;");
    var problems = RustCrates.Check(module);
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { RustEmitter.ExportCollision }));
    Assert.That(problems[0].Message, Is.EqualTo("exported names 'GetValue' and 'get_value' both map to 'get_value'"));
    Assert.That(RustEmitter.Exports(module, RustProfile.Default), Is.EqualTo(new Dictionary<string, string> { ["GetValue"] = "snippet::get_value" }));
    Assert.Throws<ArgumentException>(() => RustCrates.Emit(module));
  }

  [Test]
  public void Exports_NameTheirPaths()
    => Assert.That(RustEmitter.Exports(RustCrates.Import("public static int Gcd(int a) => a; static int Helper(int a) => a;"), RustProfile.Default),
      Is.EqualTo(new Dictionary<string, string> { ["Gcd"] = "snippet::gcd" }));

  [Test]
  public void ModuleNamedLikeAKeyword_TakesAnUnderscore()
    => Assert.That(RustEmitter.ModuleName("Type", RustProfile.Default), Is.EqualTo("type_"));

  /// <summary>Test design 16 of #459: emitting twice gives the same bytes.</summary>
  [Test]
  public void Emission_IsReproducible() {
    var module = RustCrates.Import("public static uint F(uint a, int n) => (a << n) | (a >> (32 - n)); public static double G(double a) => System.Math.Round(a);");
    var first = RustCrates.Emit(module);
    var second = RustCrates.Emit(module);
    Assert.That(second.Select(file => (file.Path, file.Content)), Is.EqualTo(first.Select(file => (file.Path, file.Content))));
  }

  [Test]
  public void ModuleState_IsAnAtomicStatic() {
    var module = RustCrates.Import("static int counter = 5; static bool seen; public static int F() { seen = true; return ++counter; }");
    Assert.That(RustCrates.Check(module), Is.Empty);
    var text = RustCrates.ModuleText(RustCrates.Emit(module));
    Assert.That(text, Does.Contain("static COUNTER: AtomicI32 = AtomicI32::new(5);"));
    Assert.That(text, Does.Contain("static SEEN: AtomicBool = AtomicBool::new(false);"));
    Assert.That(text, Does.Contain("SEEN.store(true, Ordering::Relaxed);"));
    Assert.That(text, Does.Contain("COUNTER.fetch_add(1, Ordering::Relaxed).wrapping_add(1)"));
  }

  [Test]
  public void ModuleState_OfAnotherType_IsRefusedByName() {
    var problems = RustCrates.Check(RustCrates.Import("static double scale; public static double F(double x) { scale = x; return scale; }"));
    Assert.That(problems.Select(problem => problem.Message), Has.Some.Contain("'a global of the type f64'"));
  }

  [Test]
  public void ConstantTable_IsAnArrayConstant() {
    var text = RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import("static readonly uint[] Table = { 1, 2, 3 }; public static uint F(int i) => Table[i] + (uint)Table.Length;")));
    Assert.That(text, Does.Contain("static TABLE: [u32; 3] = [1, 2, 3];"));
    Assert.That(text, Does.Contain("TABLE[i as usize]"));
  }

  /// <summary>O0500 and O0501: a helper of one expression is inlined, then removed; at --opt none neither runs.</summary>
  [Test]
  public void TargetPasses_InlineAndRemoveHelpers() {
    var function = new RsFunction("twice", "", [new RsParameter("x", "u32")], "u32", new RsBlock([], new RsMethodCall(new RsPath("x"), "wrapping_mul", [new RsLiteral("2")]))) { IsHelper = true };
    var caller = new RsFunction("f", "pub", [new RsParameter("a", "u32")], "u32", new RsBlock([new RsLet("b", false, "u32", new RsCall(new RsPath("twice"), [new RsPath("a")]))],
      new RsBinary(new RsCall(new RsPath("twice"), [new RsPath("b")]), "+", new RsLiteral("1"))));
    var unused = new RsConst("UNUSED", "", "u32", new RsLiteral("1"));
    var crate = new RustCrate("p", "", new RsFile([], [], []), "p", new RsFile([], [], [unused, function, caller]));
    var result = RustTargetPasses.Pipeline.Run(crate, RustProfile.Default, TargetPassOptions.Default);
    var text = Polypiler.Core.Backends.Rust.Printing.RustPrinter.Print(result.Tree.Module);
    Assert.That(text, Is.EqualTo("pub fn f(a: u32) -> u32 {\n    let b: u32 = a.wrapping_mul(2);\n    b.wrapping_mul(2) + 1\n}\n"));
    Assert.That(result.Applications.Select(application => application.Id), Is.EqualTo(new[] { "O0500", "O0500", "O0501", "O0501" }));
    Assert.That(RustTargetPasses.Pipeline.Run(crate, RustProfile.Default, TargetPassOptions.None).Tree, Is.SameAs(crate));
  }

  [Test]
  public void HelperInlining_KeepsWhatCouldChangeTheProgram() {
    var helper = new RsFunction("h", "", [new RsParameter("x", "u32")], "u32", new RsBlock([], new RsBinary(new RsPath("x"), "+", new RsPath("x")))) { IsHelper = true };
    var effectful = new RsCall(new RsPath("h"), [new RsCall(new RsPath("g"), [])]);
    var shadowing = new RsFunction("k", "", [new RsParameter("y", "u32")], "u32", new RsBlock([], new RsBinary(new RsPath("x"), "+", new RsPath("y")))) { IsHelper = true };
    var caller = new RsFunction("f", "pub", [new RsParameter("x", "u32")], "u32", new RsBlock([], new RsBinary(effectful, "+", new RsCall(new RsPath("k"), [new RsPath("x")]))));
    var crate = new RustCrate("p", "", new RsFile([], [], []), "p", new RsFile([], [], [helper, shadowing, caller]));
    var result = new RustHelperInlining().Run(crate, RustProfile.Default, _ => { });
    Assert.That(result, Is.SameAs(crate));
  }

  [Test]
  public void Liveness_FindsTheValuesNeverRead() {
    var body = new RsBlock([
      new RsLet("a", true, "u32", new RsLiteral("1")),
      new RsExpressionStatement(new RsAssign(new RsPath("a"), "=", new RsLiteral("2"))),
      new RsLet("b", true, "u32", new RsLiteral("0")),
      new RsExpressionStatement(new RsWhile(null, new RsBinary(new RsPath("b"), "<", new RsLiteral("3")), new RsBlock([
        new RsExpressionStatement(new RsAssign(new RsPath("b"), "+=", new RsLiteral("1"))),
        new RsExpressionStatement(new RsIf(new RsPath("c"), new RsBlock([new RsExpressionStatement(new RsBreak(null, null))], null), null))
      ], null)))
    ], new RsBinary(new RsPath("a"), "+", new RsPath("b")));
    Assert.That(RustLiveness.DeadStores(new RsFunction("f", "", [], "u32", body)), Is.EqualTo(new[] { "a" }));
  }

  [Test]
  public void Liveness_FollowsLabelsAndReturns() {
    var body = new RsBlock([
      new RsLet("a", true, "u32", new RsLiteral("1")),
      new RsExpressionStatement(new RsLoop("l0", new RsBlock([
        new RsExpressionStatement(new RsLabeledBlock("c1", new RsBlock([
          new RsExpressionStatement(new RsIf(new RsPath("p"), new RsBlock([new RsExpressionStatement(new RsBreak("c1", null))], null), null)),
          new RsExpressionStatement(new RsAssign(new RsPath("a"), "=", new RsLiteral("2")))
        ], null))),
        new RsExpressionStatement(new RsIf(new RsPath("a"), new RsBlock([new RsExpressionStatement(new RsReturn(new RsPath("a")))], null), null)),
        new RsExpressionStatement(new RsContinue("l0"))
      ], null))),
      new RsExpressionStatement(new RsMacro("unreachable", []))
    ], null);
    Assert.That(RustLiveness.DeadStores(new RsFunction("f", "", [], "u32", body)), Is.Empty);
  }

  [Test]
  public void Writes_KnowsWhatIsNative() {
    var x = new IrLocalRef(new IrLocal("x", IrType.F64));
    Assert.That(RustEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [x]), RustProfile.Default), Is.True);
    Assert.That(RustEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.Sin, IrType.F64, [x]), RustProfile.Default), Is.False);
    Assert.That(RustEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrStrConst("a")]), RustProfile.Default), Is.False);
    Assert.That(RustEmitter.Writes(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), []), RustProfile.Default), Is.False);
    Assert.That(RustEmitter.TypeName(IrType.String), Is.Null);
  }

}
