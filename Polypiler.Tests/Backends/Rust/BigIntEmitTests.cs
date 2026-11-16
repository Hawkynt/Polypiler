using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.BigInt;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// The arbitrary-precision integer of the IR as the Rust backend writes it (#1390): <c>PlyBigInt</c>,
/// a struct of the crate whose helpers are converted from num-bigint, each operation a call of its
/// helper that borrows the operands, a comparison the operator of the type's order, a variable cloned
/// where a place takes its value. What the crate computes the pinned toolchain decides in the Rust
/// layer (<c>RustBigIntTests</c>); here the forms, the helpers a crate carries and the refusals.
/// </summary>
[TestFixture]
public sealed class BigIntEmitTests {

  private static readonly IrLocal A = new("a", IrType.BigInt), B = new("b", IrType.BigInt);

  private static string Emitted(IrModule module, RustProfile? profile = null, OptimizationLevel level = OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(module, profile, level: level));

  private static IrModule Single(IrType result, IReadOnlyList<IrLocal> parameters, IrExpr value)
    => new("Big", [new IrFunction("F", true, result, parameters, [], new IrBlock([new IrReturn(value)]))]);

  private static string[] Problems(IrModule module) => [.. RustCrates.Check(module).Select(problem => problem.Message)];

  // Test design 1, happy path: every operation and conversion of the IR, in every profile.
  [TestCase("rust2018"), TestCase("rust2021"), TestCase("rust2024")]
  public void EveryProfile_WritesEveryOperation(string profile)
    => Assert.That(RustCrates.Check(BigIntCases.Module(), RustProfile.All.Single(candidate => candidate.Name == profile)), Is.Empty);

  // Test design 2, one form per equivalence class of operation.
  [TestCase("pub fn add(a: PlyBigInt, b: PlyBigInt) -> PlyBigInt {\n    ply_big_add(&a, &b)\n}")]
  [TestCase("ply_big_sub(&a, &b)")]
  [TestCase("ply_big_mul(&a, &b)")]
  [TestCase("ply_big_div(&a, &b)")]
  [TestCase("ply_big_rem(&a, &b)")]
  [TestCase("ply_big_and(&a, &b)")]
  [TestCase("ply_big_or(&a, &b)")]
  [TestCase("ply_big_xor(&a, &b)")]
  [TestCase("pub fn less(a: PlyBigInt, b: PlyBigInt) -> bool {\n    a < b\n}")]
  [TestCase("a == b")]
  [TestCase("a != b")]
  [TestCase("a <= b")]
  [TestCase("a > b")]
  [TestCase("a >= b")]
  [TestCase("ply_big_shift(&a, i64::from(n))")]
  [TestCase("ply_big_shift(&a, -i64::from(n))")]
  [TestCase("ply_big_neg(&a)")]
  [TestCase("ply_big_not(&a)")]
  [TestCase("ply_big_low(&a) as i8")]
  [TestCase("ply_big_low(&a) as u32")]
  [TestCase("pub fn wrap_u64(a: PlyBigInt) -> u64 {\n    ply_big_low(&a)\n}")]
  [TestCase("ply_big_fit(&a, -2147483648, 2147483647) as i32")]
  [TestCase("ply_big_fit(&a, -9223372036854775808, 9223372036854775807) as i64")]
  [TestCase("ply_big_fit(&a, 0, 18446744073709551615) as u64")]
  [TestCase("ply_big_to_f64(&a)")]
  [TestCase("ply_big_of_f64(x)")]
  [TestCase("ply_big(i128::from(i))")]
  [TestCase("ply_big(i128::from(u))")]
  [TestCase("ply_big(1000000000000000000000000000007)")]
  [TestCase("ply_big(-5)")]
  [TestCase("ply_big_xor(&ply_big_shift(&a, 3), &ply_big_shift(&a, -3))")]
  public void Forms_AreTheHelpersOfTheType(string form)
    => Assert.That(Emitted(BigIntCases.Module()), Does.Contain(form));

  /// <summary>A variable whose value a place takes is cloned, since it stays the variable's; a returned one is moved; a reassigned parameter is bound again.</summary>
  [Test]
  public void Variables_AreClonedWhereAPlaceTakesThem_MovedWhereReturned() {
    var copy = new IrLocal("copy", IrType.BigInt);
    var module = new IrModule("Big", [
      new IrFunction("Twice", true, IrType.BigInt, [A], [copy], new IrBlock([
        new IrDeclare(copy, new IrLocalRef(A)),
        new IrAssign(A, new IrBinary(IrBinaryOp.Xor, new IrLocalRef(A), new IrLocalRef(copy))),
        new IrReturn(new IrLocalRef(copy))]))
    ]);
    var text = Emitted(module);
    Assert.That(text, Does.Contain("pub fn twice(a: PlyBigInt) -> PlyBigInt {\n    let mut a = a;\n    let copy: PlyBigInt = a.clone();\n    a = ply_big_xor(&a, &copy);\n    copy\n}"));
  }

  /// <summary>Test design 3, boundaries of a constant's literal: an <c>i128</c> holds it up to 2^127 - 1 on either side; beyond, its digits are parsed.</summary>
  [TestCase("170141183460469231731687303715884105727", "ply_big(170141183460469231731687303715884105727)")]
  [TestCase("-170141183460469231731687303715884105727", "ply_big(-170141183460469231731687303715884105727)")]
  [TestCase("170141183460469231731687303715884105728", "ply_big_parse(\"170141183460469231731687303715884105728\")")]
  [TestCase("-170141183460469231731687303715884105728", "ply_big_parse(\"-170141183460469231731687303715884105728\")")]
  [TestCase("0", "ply_big(0)")]
  public void Constant_IsAnI128LiteralWhereOneHoldsIt(string digits, string form)
    => Assert.That(Emitted(Single(IrType.BigInt, [], new IrBigConst(BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture)))), Does.Contain(form));

  /// <summary>The conversions the module of <c>BigIntCases</c> has no form of: from <c>f32</c> through <c>f64</c>, from a constant as its literal.</summary>
  [Test]
  public void Conversions_FromSingleAndFromAConstant() {
    var x = new IrLocal("x", IrType.F32);
    Assert.That(Emitted(Single(IrType.BigInt, [x], new IrConvert(IrType.BigInt, new IrLocalRef(x), IrConversion.Trap))), Does.Contain("ply_big_of_f64(f64::from(x))"));
    Assert.That(Emitted(Single(IrType.BigInt, [], new IrConvert(IrType.BigInt, new IrConst(IrType.U64, -1)))), Does.Contain("ply_big(18446744073709551615)"));
  }

  /// <summary>A shift by a constant count is the literal count, negated for a right shift; the operand of an operation that is no variable is borrowed in place.</summary>
  [Test]
  public void ShiftOfAnOperation_BorrowsItsValue() {
    var n = new IrLocal("n", IrType.I32);
    var text = Emitted(Single(IrType.BigInt, [A, n], new IrBinary(IrBinaryOp.ShiftRight, new IrBinary(IrBinaryOp.Add, new IrLocalRef(A), new IrBigConst(1)), new IrConst(IrType.I32, int.MinValue))));
    Assert.That(text, Does.Contain("ply_big_shift(&ply_big_add(&a, &ply_big(1)), 2147483648)"));
  }

  /// <summary>A comparison of an operation's value compares the new value itself, a variable in place.</summary>
  [Test]
  public void ComparisonOfAnOperation_ComparesItsValue()
    => Assert.That(Emitted(Single(IrType.Bool, [A], new IrBinary(IrBinaryOp.Less, new IrBinary(IrBinaryOp.Add, new IrLocalRef(A), new IrBigConst(1)), new IrLocalRef(A)))),
      Does.Contain("ply_big_add(&a, &ply_big(1)) < a"));

  /// <summary>The struct, its order and only the helpers the code calls - with the ones those call - are in the crate; the header names num-bigint.</summary>
  [Test]
  public void Crate_CarriesTheTypeAndOnlyTheHelpersItCalls() {
    var files = RustCrates.Emit(Single(IrType.BigInt, [A, B], new IrBinary(IrBinaryOp.Add, new IrLocalRef(A), new IrLocalRef(B))), level: OptimizationLevel.None);
    var text = RustCrates.ModuleText(files);
    Assert.That(text, Does.Contain("#[derive(Clone, Debug, Default, PartialEq, Eq)]\npub struct PlyBigInt {\n    pub negative: bool,\n    pub magnitude: Vec<u32>,\n}"));
    Assert.That(text, Does.Contain("impl Ord for PlyBigInt {"));
    Assert.That(text, Does.Contain("impl PartialOrd for PlyBigInt {"));
    foreach (var helper in new[] { "ply_big_add", "ply_big_make", "ply_big_add_magnitudes", "ply_big_sub_magnitudes", "ply_big_compare" })
      Assert.That(text, Does.Contain($"fn {helper}("), helper);
    foreach (var helper in new[] { "ply_big_div_rem", "ply_big_mul", "ply_big_shift", "ply_big_parse", "ply_big_to_f64" })
      Assert.That(text, Does.Not.Contain($"fn {helper}("), helper);

    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Contain("//! PlyBigInt and the ply_big functions are converted from num-bigint (https://github.com/rust-num/num-bigint), Copyright (c) 2014 The Rust Project Developers, under the MIT licence."));
    var helpers = files.Single(file => file.Path == "src/big.rs").Helpers;
    Assert.That(helpers.Select(helper => helper.Symbol), Is.SupersetOf(new[] { "ply_big_add", "ply_big_compare", "ply_big_make" }));
    Assert.That(helpers.Select(helper => (helper.Licence, helper.Origin)).Distinct(), Is.EqualTo(new[] { ("MIT", "Hawkynt/Polypiler:Polypiler.Core.Backends.Rust/Helpers/BigInts.cs") }));
  }

  /// <summary>A crate without the type has neither the struct nor the notice.</summary>
  [Test]
  public void CrateWithoutTheType_HasNoneOfIt() {
    var i = new IrLocal("i", IrType.I32);
    var files = RustCrates.Emit(Single(IrType.I32, [i], new IrLocalRef(i)), level: OptimizationLevel.None);
    Assert.That(RustCrates.ModuleText(files), Does.Not.Contain("PlyBigInt"));
    Assert.That(RustCrates.Text(files, "src/lib.rs"), Does.Not.Contain("num-bigint"));
  }

  /// <summary>Every helper the module of <c>BigIntCases</c> needs, and the parse of a constant beyond <c>i128</c>, read from its text and printed back.</summary>
  [Test]
  public void EveryHelper_IsInTheCrate() {
    var big = Emitted(new IrModule("Big", [.. BigIntCases.Module().Functions, .. Single(IrType.BigInt, [], new IrBigConst(BigInteger.Pow(2, 1100))).Functions]));
    string[] helpers = [
      "ply_big", "ply_big_parse", "ply_big_make", "ply_big_compare", "ply_big_add_magnitudes", "ply_big_sub_magnitudes", "ply_big_add", "ply_big_neg",
      "ply_big_sub", "ply_big_mul", "ply_big_shift_up", "ply_big_shift_down", "ply_big_div_rem", "ply_big_div", "ply_big_rem", "ply_big_drop_bits",
      "ply_big_shift", "ply_big_negate_carry", "ply_big_twos", "ply_big_bitwise", "ply_big_and", "ply_big_or", "ply_big_xor", "ply_big_not",
      "ply_big_low", "ply_big_fit", "ply_big_to_f64", "ply_big_of_f64"
    ];
    foreach (var helper in helpers)
      Assert.That(big, Does.Contain($"\nfn {helper}("), helper);

    // Each top-level item of the file reads back into the tree and prints as it stands.
    foreach (var item in RustSyntaxReader.Read(big).Items)
      Assert.That(big, Does.Contain(RustPrinter.Print(item).TrimEnd('\n')));
  }

  /// <summary>Test design 4, the error path: a place the type has no form in yet is refused with PLY1550, naming it.</summary>
  [Test]
  public void ListOfTheType_IsRefused()
    => Assert.That(Problems(Single(IrType.I32, [new IrLocal("a", IrType.List(IrType.BigInt))], new IrConst(IrType.I32, 0))),
      Has.Some.Contains("'a list of bigint' cannot be expressed in Rust profile 'rust2024'"));

  /// <summary>Scenario: Python's <c>2**100 + n</c> is written, the power folded by the frontend into the constant the literal holds.</summary>
  [Test]
  public void PythonPowerOfTwo_IsAConstantAndAnAddition() {
    var module = PythonFixture.Module(PythonFixture.Import("big.py", "def big(n: int) -> int:\n    return 2 ** 100 + n\n"));
    Assert.That(RustCrates.Check(module), Is.Empty);
    Assert.That(Emitted(module, level: OptimizationLevel.Default), Does.Contain("pub fn big(n: PlyBigInt) -> PlyBigInt {\n    ply_big_add(&ply_big(1267650600228229401496703205376), &n)\n}"));
    Assert.That(IrInterpreter.Call(module, "big", [BigInteger.One]), Is.EqualTo(BigInteger.Parse("1267650600228229401496703205377", System.Globalization.CultureInfo.InvariantCulture)));
  }

  /// <summary>Scenario: a Python list of <c>int</c> is still refused, by name.</summary>
  [Test]
  public void PythonListOfInt_IsRefusedByName() {
    var module = PythonFixture.Module(PythonFixture.Import("first.py", "def first(n: int) -> int:\n    values = [n, 2 ** 70]\n    return values[0]\n"));
    Assert.That(Problems(module), Has.Some.Contains("'a list of bigint' cannot be expressed in Rust profile 'rust2024'"));
  }

  /// <summary>The patterns the helpers of the type are written with: a tuple in a <c>for</c>, a reference as a closure's parameter.</summary>
  [TestCase("fn f(a: &mut [u32], b: &[u32]) {\n    for (place, &digit) in a.iter_mut().zip(b) {\n        *place += digit;\n    }\n}\n")]
  [TestCase("fn f(a: &[u32]) -> u64 {\n    a.iter().fold(0, |sum, &digit| sum + u64::from(digit))\n}\n")]
  [TestCase("fn f(a: &mut [u32]) {\n    for &mut place in a.iter_mut() {\n        g(place);\n    }\n}\n")]
  public void Patterns_ReadBackAsTheyStand(string source)
    => Assert.That(RustPrinter.Print(RustSyntaxReader.Read(source)), Is.EqualTo(source));

}
