using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Verification.Proofs;

namespace Polypiler.Tests.Verification.Proofs;

/// <summary>
/// Each row of the encoding table of #71 evaluated on constants whose results are worked out by hand
/// (test design 3), by Z3 through <see cref="SmtEncoder"/> and by the reference interpreter through
/// <see cref="ExhaustiveProver"/>: an obligation whose before side computes the row and whose after side
/// is the hand-computed constant must be proved by both, and so the two agree with each other and with
/// the SMT-LIB theory <c>FixedSizeBitVectors</c>.
/// </summary>
[TestFixture]
public sealed class SmtEncoderTests {

  private static readonly ProofBudget _budget = new(SolverTimeout: TimeSpan.FromSeconds(5));

  /// <summary>An obligation computing an instruction on constants, against its expected value.</summary>
  private static ProofObligation Row(string instruction, string type, string expected)
    => new("row", instruction, $"%r = Xor.{type} {expected}, {(type == "bool" ? "false" : "0")}", []);

  private static IEnumerable<TestCaseData> Rows() {
    (string Instruction, string Type, string Expected)[] rows = [
      ("%r = Add.u8 200, 100", "u8", "44"),
      ("%r = Sub.u8 3, 5", "u8", "254"),
      ("%r = Mul.u8 16, 17", "u8", "16"),
      ("%r = Neg.u8 1", "u8", "255"),
      ("%r = Neg.i8 -128", "i8", "-128"),
      ("%r = And.u8 0xF0, 0x3C", "u8", "0x30"),
      ("%r = Or.u8 0xF0, 0x3C", "u8", "0xFC"),
      ("%r = Xor.u8 0xF0, 0x3C", "u8", "0xCC"),
      ("%r = Not.u8 0x0F", "u8", "0xF0"),
      ("%r = Shl.u8[mask] 0x81, 9", "u8", "0x02"),
      ("%r = Shl.u8[strict] 0x81, 1", "u8", "0x02"),
      ("%r = Shl.u8[unbounded] 0x81, 8", "u8", "0"),
      ("%r = Shl.u64[strict] 1, 63", "u64", "0x8000000000000000"),
      ("%r = LShr.u8 0x80, 7", "u8", "1"),
      ("%r = LShr.u8[unbounded] 0x80, 200", "u8", "0"),
      ("%r = AShr.u8[strict] 0x80, 7", "u8", "0xFF"),
      ("%r = AShr.i8[unbounded] -128, 200", "i8", "-1"),
      ("%r = AShr.i8[unbounded] 64, 200", "i8", "0"),
      ("%r = AShr.i64[mask] -2, 65", "i64", "-1"),
      ("%r = RotateLeft.u8 0x81, 1", "u8", "0x03"),
      ("%r = RotateLeft.u8 0x81, 9", "u8", "0x03"),
      ("%r = RotateRight.u8 0x81, 1", "u8", "0xC0"),
      ("%r = RotateRight.u16 0x0001, 0", "u16", "1"),
      ("%r = UDiv.u8 200, 7", "u8", "28"),
      ("%r = URem.u8 200, 7", "u8", "4"),
      ("%r = SDiv.i8 -7, 2", "i8", "-3"),
      ("%r = SRem.i8 -7, 2", "i8", "-1"),
      ("%r = SDiv.i8 -128, -1", "i8", "-128"),
      ("%r = SRem.i8[ovf=wrap] -128, -1", "i8", "0"),
      ("%r = SDiv.i64 -9223372036854775808, -1", "i64", "-9223372036854775808"),
      ("%r = Eq.u8 5, 5", "bool", "true"),
      ("%r = Ne.u8 5, 5", "bool", "false"),
      ("%r = ULt.u8 1, 255", "bool", "true"),
      ("%r = ULe.u8 255, 255", "bool", "true"),
      ("%r = UGt.u8 1, 255", "bool", "false"),
      ("%r = UGe.u8 0, 1", "bool", "false"),
      ("%r = SLt.i8 1, -1", "bool", "false"),
      ("%r = SLe.i8 -1, -1", "bool", "true"),
      ("%r = SGt.i8 1, -1", "bool", "true"),
      ("%r = SGe.i8 -128, 127", "bool", "false"),
      ("%r = Eq.bool true, true", "bool", "true"),
      ("%r = And.bool true, false", "bool", "false"),
      ("%r = Or.bool true, false", "bool", "true"),
      ("%r = Xor.bool true, true", "bool", "false"),
      ("%r = Not.bool true", "bool", "false"),
      ("%r = Select.u8 true, 1, 2", "u8", "1"),
      ("%r = Select.bool false, true, false", "bool", "false"),
      ("%r = Add.u8[wrap] 255, 1", "u8", "0"),
      ("%r = UDiv.u8[trap DivideByZero] 9, 3", "u8", "3"),
    ];
    return rows.Select(row => new TestCaseData(row.Instruction, row.Type, row.Expected).SetName($"Row {row.Instruction}"));
  }

  [TestCaseSource(nameof(Rows))]
  public void EachRow_IsTheValueComputedByHand_ForTheSolverAndTheInterpreter(string instruction, string type, string expected) {
    var obligation = Row(instruction, type, expected);
    Assert.That(new SmtBitvectorProver([8]) { DumpDirectory = null }.Prove(obligation, _budget), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)), SmtEncoder.ToSmtLib(obligation, 8));
    Assert.That(new ExhaustiveProver().Prove(obligation, ProofBudget.Default), Is.EqualTo(new ProofResult.Proved("exhaustive-0bit", 1)));
  }

  /// <summary>A wrong expected value is refuted by both: the rows above are not proved by accident.</summary>
  [Test]
  public void AWrongValue_IsRefutedByBoth() {
    var obligation = Row("%r = AShr.u8[strict] 0x80, 7", "u8", "0x7F");
    Assert.That(new SmtBitvectorProver([8]) { DumpDirectory = null }.Prove(obligation, _budget), Is.InstanceOf<ProofResult.Refuted>().With.Property("BeforeValue").EqualTo(0xFFUL));
    Assert.That(new ExhaustiveProver().Prove(obligation, ProofBudget.Default), Is.InstanceOf<ProofResult.Refuted>().With.Property("AfterValue").EqualTo(0x7FUL));
  }

  /// <summary>The conversions read a value of one width: an input pinned by a precondition.</summary>
  [TestCase("x: u8", "%p = Eq.u8 %x, 0x80", "%r = ZeroExtend.u16 %x", "u16", "0x0080")]
  [TestCase("x: u8", "%p = Eq.u8 %x, 0x80", "%r = SignExtend.u16 %x", "u16", "0xFF80")]
  [TestCase("x: u8", "%p = Eq.u8 %x, 0x80", "%r = ZeroExtend.u8 %x", "u8", "0x80")]
  [TestCase("x: u12", "%p = Eq.u12 %x, 0x9AB", "%r = Truncate.u4 %x", "u4", "0xB")]
  [TestCase("x: u12", "%p = Eq.u12 %x, 0x9AB", "%r = Truncate.u4[wrap] %x", "u4", "0xB")]
  [TestCase("x: u8, n: u2", "%p1 = Eq.u8 %x, 1; %p2 = Eq.u2 %n, 3; %p = And.bool %p1, %p2", "%r = Shl.u8[strict] %x, %n", "u8", "8")]
  [TestCase("x: u8, n: u2", "%p1 = Eq.u8 %x, 1; %p2 = Eq.u2 %n, 3; %p = And.bool %p1, %p2", "%r = Shl.u8[mask] %x, %n", "u8", "8")]
  [TestCase("x: u8, n: u2", "%p1 = Eq.u8 %x, 1; %p2 = Eq.u2 %n, 3; %p = And.bool %p1, %p2", "%r = AShr.u8[unbounded] %x, %n", "u8", "0")]
  [TestCase("x: u8, n: u2", "%p1 = Eq.u8 %x, 0x81; %p2 = Eq.u2 %n, 1; %p = And.bool %p1, %p2", "%r = RotateLeft.u8 %x, %n", "u8", "0x03")]
  [TestCase("x: u4, n: u8", "%p1 = Eq.u4 %x, 1; %p2 = Eq.u8 %n, 3; %p = And.bool %p1, %p2", "%r = Shl.u4[strict] %x, %n", "u4", "8")]
  public void AValueRow_IsTheValueComputedByHand(string inputs, string precondition, string instruction, string type, string expected) {
    var obligation = new ProofObligation("row", instruction, $"%r = Xor.{type} {expected}, 0", ObligationFile.Inputs(inputs), [precondition], "r");
    Assert.That(new SmtBitvectorProver([8]) { DumpDirectory = null }.Prove(obligation, _budget), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)), SmtEncoder.ToSmtLib(obligation, 8));
    Assert.That(new ExhaustiveProver().Prove(obligation, ProofBudget.Default), Is.InstanceOf<ProofResult.Proved>().With.Property("Evaluated").EqualTo(1L));
  }

  /// <summary>Amounts wider than the shifted value: compared in their own width before they are cut.</summary>
  [TestCase("%r = Shl.u8[mask] %x, %n", "9", "2")]
  [TestCase("%r = Shl.u8[unbounded] %x, %n", "300", "0")]
  [TestCase("%r = LShr.u8[strict] %x, %n", "7", "0")]
  [TestCase("%r = RotateRight.u8 %x, %n", "263", "2")]
  public void AWideAmount_IsComparedBeforeItIsCut(string instruction, string amount, string expected) {
    var obligation = new ProofObligation("row", instruction, $"%r = Xor.u8 {expected}, 0", [new("x", "u8"), new("n", "u16")], [$"%p1 = Eq.u8 %x, 1; %p2 = Eq.u16 %n, {amount}; %p = And.bool %p1, %p2"], "r");
    Assert.That(new SmtBitvectorProver([8]) { DumpDirectory = null }.Prove(obligation, _budget), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)), SmtEncoder.ToSmtLib(obligation, 8));
  }

  /// <summary>A signed division that traps on overflow makes the smallest value divided by -1 invalid; the wrapping one does not.</summary>
  [TestCase("SDiv")]
  [TestCase("SRem")]
  public void ATrappingSignedDivision_IsInvalidOnOverflow(string op) {
    var obligation = new ProofObligation("row", $"%r = {op}.i8 %x, -1", $"%r = {op}.i8[ovf=trap] %x, -1", [new("x", "i8")]);
    var exhaustive = (ProofResult.Refuted)new ExhaustiveProver().Prove(obligation, ProofBudget.Default);
    Assert.That((exhaustive.Inputs["x"], exhaustive.AfterFault), Is.EqualTo((0x80UL, $"{op}.i8[ovf=trap]: the smallest value divided by -1")));
    var solver = (ProofResult.Refuted)new SmtBitvectorProver([8]) { DumpDirectory = null }.Prove(obligation, _budget);
    Assert.That(solver.Inputs["x"], Is.EqualTo(0x80UL));
    var checkedForm = new ProofObligation("row", $"%r = {op}.i8[checked] %x, %y", $"%r = {op}.i8[ovf=trap] %x, %y", [new("x", "i8"), new("y", "i8")]);
    Assert.That(new ExhaustiveProver().Prove(checkedForm, ProofBudget.Default), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 65536 - 256 - 1)));
  }

  [Test]
  public void TheScript_DeclaresTheInputs_AndEndsInCheckSat() {
    var text = SmtEncoder.ToSmtLib(new("r", "%r = Add.u<w> %x, 1", "%r = Add.u<w> 1, %x", [new("x", "u<w>"), new("c", "bool")]), 32);
    Assert.That(text, Does.StartWith("(set-logic QF_BV)\n(declare-const |x| (_ BitVec 32))\n(declare-const |c| Bool)\n"));
    Assert.That(text, Does.EndWith("(check-sat)\n"));
  }

  [TestCase(0)]
  [TestCase(65)]
  public void AWidthOutside1To64_IsRefused(int width)
    => Assert.That(() => SmtEncoder.ToSmtLib(new("r", "%r = Add.u<w> %x, 1", "%r = Add.u<w> 1, %x", [new("x", "u<w>")]), width), Throws.TypeOf<ArgumentOutOfRangeException>());

  [Test]
  public void AnUnmodelledOperation_IsNotSupported() {
    Assert.That(() => SmtEncoder.ToSmtLib(new("r", "%r = Add.f64 %x, 1", "%r = Add.f64 1, %x", [new("x", "f64")]), 32), Throws.TypeOf<NotSupportedException>().With.Message.EqualTo("Add.f64"));
    Assert.That(() => SmtEncoder.ToSmtLib(null!, 32), Throws.ArgumentNullException);
  }

}
