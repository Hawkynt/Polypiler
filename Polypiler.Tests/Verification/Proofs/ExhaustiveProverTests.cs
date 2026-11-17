using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Proofs;

/// <summary>
/// The exhaustive prover (#70) on the obligations of <c>tests/fixtures/verify/exhaustive/</c>: every
/// scenario and row of the issue's test design, the known-true identities it must prove and the
/// known-false ones it must refute, each with its proof-report line worked out by hand.
/// </summary>
[TestFixture]
public sealed class ExhaustiveProverTests {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/verify/exhaustive";

  private static readonly ExhaustiveProver _prover = new();

  /// <summary>Reads a fixture obligation.</summary>
  public static ProofObligation Fixture(string name) => ObligationFile.Load(Path.Combine(PrototypeFixture.RepositoryRoot, Folder, name + ObligationFile.Extension));

  private static ProofResult Prove(string name) => _prover.Prove(Fixture(name), ProofBudget.Default);

  /// <summary>The fast fixtures with the line each adds to the proof report.</summary>
  private static readonly (string Name, string Report)[] _reports = [
    ("rotate-from-shifts", "rotate-from-shifts exhaustive-8bit proved 1792 cases"),
    ("rotate-with-arithmetic-shift", "rotate-with-arithmetic-shift exhaustive-8bit refuted: x=0x80: before 0xFF, after 0x01"),
    ("average-overflowing", "average-overflowing exhaustive-8bit refuted: a=0x01, b=0xFF: before 0x80, after 0x00"),
    ("budget-two-u16", "budget-two-u16 exhaustive-16bit budget-exceeded: 32 input bits, budget is 24"),
    ("vacuous-precondition", "vacuous-precondition exhaustive-8bit inconclusive: vacuous: no input satisfies the preconditions"),
    ("float-commutes", "float-commutes exhaustive unsupported: Add.f32"),
    ("mul2-is-shl1-u8", "mul2-is-shl1-u8 exhaustive-8bit proved 256 cases"),
    ("mul2-is-shl1-i8", "mul2-is-shl1-i8 exhaustive-8bit proved 256 cases"),
    ("select-swapped", "select-swapped exhaustive-8bit proved 131072 cases"),
    ("double-u4", "double-u4 exhaustive-4bit proved 16 cases"),
    ("rotate-masked", "rotate-masked exhaustive-8bit proved 65536 cases"),
    ("budget-25-bits", "budget-25-bits exhaustive-8bit budget-exceeded: 25 input bits, budget is 24"),
    ("single-input-satisfies", "single-input-satisfies exhaustive-8bit proved 1 cases"),
    ("differs-at-last-input", "differs-at-last-input exhaustive-8bit refuted: x=0xFF: before 0x00, after 0x01"),
    ("strict-shift-at-width", "strict-shift-at-width exhaustive-8bit proved 256 cases"),
    ("after-invalid", "after-invalid exhaustive-8bit refuted: x=0x00, n=0x08: before 0x00, after invalid (Shl.u8[strict]: shift amount of at least 8)"),
    ("memory-load", "memory-load exhaustive unsupported: Load"),
    ("call", "call exhaustive unsupported: Call"),
    ("unknown-opcode", "unknown-opcode exhaustive unsupported: Frobnicate.u8"),
    ("average-correct", "average-correct exhaustive-8bit proved 65536 cases"),
    ("subtract-by-complement", "subtract-by-complement exhaustive-8bit proved 65536 cases"),
    ("de-morgan", "de-morgan exhaustive-8bit proved 65536 cases"),
    ("de-morgan-wrong", "de-morgan-wrong exhaustive-8bit refuted: a=0x00, b=0x01: before 0xFF, after 0xFE"),
    ("signed-compare-as-unsigned", "signed-compare-as-unsigned exhaustive-8bit refuted: x=0x80: before 0x01, after 0x00")
  ];

  private static IEnumerable<TestCaseData> Reports => _reports.Select(report => new TestCaseData(report.Name, report.Report).SetName($"Fixture_{report.Name}"));

  [TestCaseSource(nameof(Reports))]
  public void EveryFixture_GivesItsReportLine(string name, string report)
    => Assert.That(ProofDiagnostics.ReportLine(name, Prove(name)), Is.EqualTo(report));

  [Test]
  public void EveryFixture_IsListed() {
    var files = Directory.EnumerateFiles(Path.Combine(PrototypeFixture.RepositoryRoot, Folder), "*" + ObligationFile.Extension)
      .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
    Assert.That(files, Is.EqualTo(_reports.Select(report => report.Name).Append("add-associative").Order(StringComparer.Ordinal)));
  }

  [Test]
  public void KnownTrueIdentities_AreAllProved_AndKnownFalseOnes_AllRefuted() {
    string[] known = ["rotate-from-shifts", "mul2-is-shl1-u8", "mul2-is-shl1-i8", "select-swapped", "rotate-masked", "average-correct", "subtract-by-complement", "de-morgan"];
    string[] wrong = ["rotate-with-arithmetic-shift", "average-overflowing", "differs-at-last-input", "after-invalid", "de-morgan-wrong", "signed-compare-as-unsigned"];
    Assert.That(known.Select(Prove), Has.All.InstanceOf<ProofResult.Proved>());
    Assert.That(wrong.Select(Prove), Has.All.InstanceOf<ProofResult.Refuted>());
  }

  /// <summary>Scenario 1: 256 values of x times the 7 amounts the precondition admits.</summary>
  [Test]
  public void ARotationWrittenAsTwoShifts_IsProvedOverEveryValidInput() {
    var result = Prove("rotate-from-shifts");
    Assert.That(result, Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 1792)));
    Assert.That(ProofDiagnostics.For("rotate-from-shifts", _prover.Id, result), Is.Null);
  }

  /// <summary>Scenario 2: x &lt; 0x80 agrees; at 0x80 the arithmetic shift fills with ones: 0x00 | 0xFF.</summary>
  [Test]
  public void AnArithmeticRightShift_IsRefutedWithTheFirstCounterexample() {
    var result = (ProofResult.Refuted)Prove("rotate-with-arithmetic-shift");
    Assert.That(result.Inputs, Is.EqualTo(new Dictionary<string, ulong> { ["x"] = 0x80 }));
    Assert.That((result.BeforeValue, result.AfterValue, result.AfterFault, result.Width), Is.EqualTo((0xFFUL, 0x01UL, (string?)null, 0)));
    var diagnostic = ProofDiagnostics.For("rotate-with-arithmetic-shift", _prover.Id, result)!;
    Assert.That((diagnostic.Code, diagnostic.ExitCode), Is.EqualTo(("PLY0056", (ExitCode?)ExitCode.SemanticMismatch)));
    Assert.That(diagnostic.Message, Is.EqualTo("proof of rotate-with-arithmetic-shift refuted: x=0x80: before 0xFF, after 0x01"));
  }

  /// <summary>Scenario 3: (0x01 &amp; 0xFF) + ((0x01 ^ 0xFF) &gt;&gt; 1) = 0x80, while (0x01 + 0xFF) mod 256 = 0, halved 0.</summary>
  [Test]
  public void TheOverflowingAverage_IsRefutedWhereTheCarryIsLost() {
    var result = (ProofResult.Refuted)Prove("average-overflowing");
    Assert.That(result.Inputs, Is.EqualTo(new Dictionary<string, ulong> { ["a"] = 0x01, ["b"] = 0xFF }));
    Assert.That((result.BeforeValue, result.AfterValue), Is.EqualTo((0x80UL, 0x00UL)));
    Assert.That(ProofDiagnostics.For("average", _prover.Id, result)!.Code, Is.EqualTo("PLY0056"));
  }

  /// <summary>Scenario 4.</summary>
  [Test]
  public void TwoSixteenBitInputs_ExceedTheBudget_AndNothingIsClaimed() {
    var result = Prove("budget-two-u16");
    Assert.That(result, Is.EqualTo(new ProofResult.BudgetExceeded("exhaustive-16bit", 32) { Budget = 24 }));
    var diagnostic = ProofDiagnostics.For("budget-two-u16", _prover.Id, result)!;
    Assert.That((diagnostic.Code, diagnostic.ExitCode, diagnostic.Message), Is.EqualTo(("PLY0057", (ExitCode?)ExitCode.BudgetExceeded, "exhaustive proof of budget-two-u16 needs 32 input bits, budget is 24")));
  }

  /// <summary>Scenario 5, test design 10.</summary>
  [Test]
  public void PreconditionsExcludingEveryInput_ProveNothing() {
    var result = Prove("vacuous-precondition");
    Assert.That(result, Is.EqualTo(new ProofResult.Inconclusive("exhaustive-8bit", ExhaustiveProver.VacuousReason)));
    var diagnostic = ProofDiagnostics.For("vacuous-precondition", _prover.Id, result)!;
    Assert.That((diagnostic.Code, diagnostic.Severity, diagnostic.ExitCode), Is.EqualTo(("PLY0055", DiagnosticSeverity.Warning, (ExitCode?)null)));
    Assert.That(diagnostic.Message, Is.EqualTo("proof of vacuous-precondition inconclusive (vacuous: no input satisfies the preconditions); rewrite not applied"));
  }

  /// <summary>Scenario 6, test design 16: floating point, a load and a call each name what is not modelled.</summary>
  [TestCase("float-commutes", "Add.f32")]
  [TestCase("memory-load", "Load")]
  [TestCase("call", "Call")]
  public void AnOperationOutsideTheModel_IsUnsupported(string name, string operation) {
    var result = Prove(name);
    Assert.That(result, Is.EqualTo(new ProofResult.Unsupported("exhaustive", operation)));
    var diagnostic = ProofDiagnostics.For(name, _prover.Id, result)!;
    Assert.That((diagnostic.Code, diagnostic.ExitCode, diagnostic.Message), Is.EqualTo(("PLY0062", (ExitCode?)ExitCode.UnsupportedConstruct, $"proof of {name}: operation '{operation}' is not modelled by prover 'exhaustive'")));
  }

  [Test]
  public void AFloatingPointInputNoInstructionReads_IsUnsupported()
    => Assert.That(_prover.Prove(new("r", "%r = Add.u8 %x, 1", "%r = Add.u8 %x, 1", [new("x", "u8"), new("y", "f64")]), ProofBudget.Default),
      Is.EqualTo(new ProofResult.Unsupported("exhaustive", "f64")));

  /// <summary>Test design 7: three inputs of 8 bits, exactly the budget.</summary>
  [Test, Category("Slow")]
  public void TwentyFourInputBits_AreOnTheBudget_AndEnumerated()
    => Assert.That(Prove("add-associative"), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 16_777_216)));

  /// <summary>Test design 7 and 8 at a small budget: on the budget is enumerated, one bit over is not.</summary>
  [Test]
  public void TheBudgetIsAnInclusiveUpperBound() {
    Assert.That(_prover.Prove(Fixture("add-associative"), new ProofBudget(MaxInputBits: 23)), Is.EqualTo(new ProofResult.BudgetExceeded("exhaustive-8bit", 24) { Budget = 23 }));
    Assert.That(_prover.Prove(Fixture("double-u4"), new ProofBudget(MaxInputBits: 4)), Is.EqualTo(new ProofResult.Proved("exhaustive-4bit", 16)));
    Assert.That(_prover.Prove(Fixture("double-u4"), new ProofBudget(MaxInputBits: 3)), Is.EqualTo(new ProofResult.BudgetExceeded("exhaustive-4bit", 4) { Budget = 3 }));
  }

  /// <summary>Test design 12: n = 7 is compared, n = 8 makes the strict shift of the before side invalid.</summary>
  [Test]
  public void AStrictShiftByTheWidth_IsExcludedAsInvalid_ButOneBelowIsCompared() {
    Assert.That(Prove("strict-shift-at-width"), Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256)));
    var unbounded = _prover.Prove(new("r", "%r = Shl.u8[strict] %x, %n", "%r = Shl.u8[unbounded] %x, %n", [new("x", "u8"), new("n", "u8")]), ProofBudget.Default);
    Assert.That(unbounded, Is.EqualTo(new ProofResult.Proved("exhaustive-8bit", 256 * 8)));
  }

  /// <summary>Test design 13.</summary>
  [Test]
  public void AnAfterSideInvalidWhereTheBeforeSideIsValid_IsACounterexample() {
    var result = (ProofResult.Refuted)Prove("after-invalid");
    Assert.That(result.AfterFault, Does.Contain("shift amount of at least 8"));
    Assert.That(ProofDiagnostics.For("after-invalid", _prover.Id, result)!.Message, Does.EndWith("before 0x00, after invalid (Shl.u8[strict]: shift amount of at least 8)"));
  }

  /// <summary>Test design 17: the sides define the result with different types.</summary>
  [Test]
  public void SidesOfDifferentResultTypes_AreRefusedByTheObligation() {
    var exception = Assert.Throws<ArgumentException>(() => _ = new ProofObligation("r", "%r = Add.u8 %x, 1", "%y = ZeroExtend.u16 %x; %r = Add.u16 %y, 1", [new("x", "u8")]));
    Assert.That(exception!.Message, Does.Contain("u8").And.Contain("u16"));
  }

  [Test]
  public void ASideReadingAnUndeclaredInput_IsRefusedByTheObligation()
    => Assert.That(() => new ProofObligation("r", "%r = Add.u8 %x, %y", "%r = Add.u8 %x, 1", [new("x", "u8")]), Throws.ArgumentException.With.Message.Contain("%y"));

  [TestCase("%r = Add.u8 %x, 1", "%s = Add.u8 %x, 1", "does not define '%r'")]
  [TestCase("%r = Add.u8 %x, 1; %r = Add.u8 %x, 2", "%r = Add.u8 %x, 1", "defines '%r' twice")]
  [TestCase("", "%r = Add.u8 %x, 1", "has no instruction")]
  public void AMalformedSide_IsRefusedByTheObligation(string before, string after, string message)
    => Assert.That(() => new ProofObligation("r", before, after, [new("x", "u8")]), Throws.ArgumentException.With.Message.Contain(message));

  [Test]
  public void APreconditionThatIsNoTruthValue_IsRefused()
    => Assert.That(() => new ProofObligation("r", "%r = Add.u8 %x, 1", "%r = Add.u8 %x, 1", [new("x", "u8")], ["%p = Add.u8 %x, 1"], "r"), Throws.ArgumentException.With.Message.Contain("not a bool"));

  [Test]
  public void AnInputNamedTwice_IsRefused()
    => Assert.That(() => new ProofObligation("r", "%r = Add.u8 %x, 1", "%r = Add.u8 %x, 1", [new("x", "u8"), new("x", "u8")]), Throws.ArgumentException.With.Message.Contain("named twice"));

  [Test]
  public void MissingParts_AreRefused() {
    Assert.That(() => new ProofObligation(" ", "%r = Add.u8 %x, 1", "%r = Add.u8 %x, 1", [new("x", "u8")]), Throws.ArgumentException);
    Assert.That(() => new ProofObligation("r", null!, "%r = Add.u8 %x, 1", [new("x", "u8")]), Throws.ArgumentNullException);
    Assert.That(() => _prover.Prove(null!, ProofBudget.Default), Throws.ArgumentNullException);
    Assert.That(() => _prover.Prove(Fixture("call"), null!), Throws.ArgumentNullException);
  }

  /// <summary>Test design 18: the parser's complaint travels with the verdict.</summary>
  [Test]
  public void AnUnknownOperation_IsUnsupported_WithTheParsersComplaint() {
    var result = (ProofResult.Unsupported)Prove("unknown-opcode");
    Assert.That((result.Operation, result.Detail), Is.EqualTo(("Frobnicate.u8", "unknown operation 'Frobnicate'")));
  }

  [Test]
  public void AWidthGenericObligation_SaysSo() {
    Assert.That(Fixture("rotate-from-shifts").IsWidthGeneric, Is.True);
    Assert.That(Fixture("double-u4").IsWidthGeneric, Is.False);
  }

  [Test]
  public void TheProver_IsNamedExhaustive() => Assert.That(_prover.Id, Is.EqualTo("exhaustive"));

}
