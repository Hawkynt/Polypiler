using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Proofs;

/// <summary>
/// The SMT bitvector prover (#71) on Z3 and on solver stubs, over the obligations of
/// <c>tests/fixtures/verify/smt/</c>: every scenario and row of the issue's test design. Every
/// counterexample is re-checked here by arithmetic, not trusted from the model.
/// </summary>
[TestFixture]
public sealed class SmtBitvectorProverTests {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/verify/smt";

  private static readonly ProofBudget _fiveSeconds = new(SolverTimeout: TimeSpan.FromSeconds(5));

  /// <summary>Reads a fixture obligation.</summary>
  public static ProofObligation Fixture(string name) => ObligationFile.Load(Path.Combine(PrototypeFixture.RepositoryRoot, Folder, name + ObligationFile.Extension));

  private static ProofResult Prove(string name, params int[] widths)
    => new SmtBitvectorProver(widths.Length == 0 ? SmtBitvectorProver.DefaultWidths : widths) { DumpDirectory = null }.Prove(Fixture(name), _fiveSeconds);

  /// <summary>A solver that answers from a list and records the queries.</summary>
  private sealed class Stub(params SmtAnswer[] answers) : ISmtSolver {

    private int _next;

    public List<string> Queries { get; } = [];

    public string Version => "stub";

    public SmtAnswer Check(string query, IReadOnlyList<string> constants, TimeSpan timeout) {
      this.Queries.Add(query);
      return answers[Math.Min(this._next++, answers.Length - 1)];
    }

  }

  private static readonly Dictionary<string, ulong> _none = [];

  /// <summary>Scenario 1: one unsat query per width.</summary>
  [Test]
  public void AConstantRotation_IsProvedAt16_32And64Bit() {
    var result = Prove("rotate-constant");
    Assert.That(result, Is.EqualTo(new ProofResult.Proved("smt-bitvector", 3)));
    Assert.That(ProofDiagnostics.ReportLine("rotate-constant", result), Is.EqualTo("rotate-constant smt-bitvector proved 3 cases"));
  }

  /// <summary>Scenario 2: the carry of a + b is worth 2^32 / 2.</summary>
  [Test]
  public void TheOverflowingAverage_IsRefutedWithAModelWhoseSumWraps() {
    var result = (ProofResult.Refuted)Prove("average-overflowing-u32", 32);
    var (a, b) = (result.Inputs["a"], result.Inputs["b"]);
    Assert.That((BigInteger)a + b, Is.GreaterThanOrEqualTo(new BigInteger(4294967296)));
    Assert.That(result.BeforeValue - result.AfterValue, Is.EqualTo(0x80000000UL));
    Assert.That(result.BeforeValue, Is.EqualTo((ulong)(((BigInteger)a + b) / 2)));
    Assert.That(result.Width, Is.EqualTo(32));
    var diagnostic = ProofDiagnostics.For("average", "smt", result)!;
    Assert.That((diagnostic.Code, diagnostic.ExitCode), Is.EqualTo(("PLY0056", (ExitCode?)ExitCode.SemanticMismatch)));
    Assert.That(diagnostic.Message, Does.StartWith("proof of average refuted at width 32: a=0x"));
  }

  /// <summary>Scenario 3: at 8 bits the halves cannot overlap, at 16 they can.</summary>
  [Test]
  public void ARewriteRightOnlyAt8Bit_IsRefutedAt16() {
    var result = (ProofResult.Refuted)Prove("mul257-as-or");
    var x = result.Inputs["x"];
    Assert.That(result.Width, Is.EqualTo(16));
    Assert.That(x, Is.LessThan(65536UL));
    Assert.That(x * 257 % 65536, Is.Not.EqualTo(((x << 8) | x) % 65536));
    Assert.That((result.BeforeValue, result.AfterValue), Is.EqualTo((x * 257 % 65536, ((x << 8) | x) % 65536)));
    Assert.That(new ExhaustiveProver().Prove(Fixture("mul257-as-or"), ProofBudget.Default), Is.InstanceOf<ProofResult.Unsupported>(), "257 does not fit the 8 bits the exhaustive prover works at");
  }

  /// <summary>Scenario 4, test design 9: no time, no query, no proof.</summary>
  [Test]
  public void AnExhaustedBudget_IsInconclusive_NeverAProof() {
    var stub = new Stub(new SmtAnswer(SmtStatus.Unsat, _none));
    var result = new SmtBitvectorProver([64], stub).Prove(Fixture("mul-commutes-u64"), new ProofBudget(SolverTimeout: TimeSpan.Zero));
    Assert.That(result, Is.EqualTo(new ProofResult.Inconclusive("smt-bitvector", "timeout")));
    Assert.That(stub.Queries, Is.Empty);
    var diagnostic = ProofDiagnostics.For("mul-commutes-u64", "smt", result)!;
    Assert.That((diagnostic.Code, diagnostic.Severity), Is.EqualTo(("PLY0055", DiagnosticSeverity.Warning)));
    Assert.That(diagnostic.Message, Does.EndWith("rewrite not applied"));
  }

  /// <summary>A budget spent by an earlier width leaves the later ones unasked, and proves nothing.</summary>
  [Test]
  public void ABudgetSpentPartway_IsInconclusive() {
    var slow = new SlowStub(TimeSpan.FromMilliseconds(30));
    var result = new SmtBitvectorProver([16, 32, 64], slow).Prove(Fixture("rotate-constant"), new ProofBudget(SolverTimeout: TimeSpan.FromMilliseconds(20)));
    Assert.That(result, Is.EqualTo(new ProofResult.Inconclusive("smt-bitvector", "timeout")));
    Assert.That(slow.Calls, Is.EqualTo(1));
  }

  private sealed class SlowStub(TimeSpan delay) : ISmtSolver {

    public int Calls { get; private set; }

    public string Version => "slow";

    public SmtAnswer Check(string query, IReadOnlyList<string> constants, TimeSpan timeout) {
      ++this.Calls;
      System.Threading.Thread.Sleep(delay);
      return new(SmtStatus.Unsat, _none);
    }

  }

  /// <summary>Scenario 5, test design 15.</summary>
  [TestCase("float-commutes", "Add.f64")]
  [TestCase("memory-load", "Load")]
  [TestCase("call", "Call")]
  public void AnOperationOutsideTheModel_IsRefused(string name, string operation) {
    var result = Prove(name);
    Assert.That(result, Is.InstanceOf<ProofResult.Unsupported>().With.Property("Operation").EqualTo(operation));
    var diagnostic = ProofDiagnostics.For(name, "smt", result)!;
    Assert.That((diagnostic.Code, diagnostic.ExitCode, diagnostic.Message), Is.EqualTo(("PLY0062", (ExitCode?)ExitCode.UnsupportedConstruct, $"proof of {name}: operation '{operation}' is not modelled by prover 'smt'")));
  }

  /// <summary>The known-true identities at 16, 32 and 64 bits (test design 2, 5, 8).</summary>
  [TestCase("rotate-constant")]
  [TestCase("rotate-variable")]
  [TestCase("select-swapped")]
  [TestCase("udiv-self")]
  [TestCase("mul2-is-shl1")]
  [TestCase("subtract-by-complement")]
  [TestCase("de-morgan")]
  [TestCase("xor-twice")]
  public void AKnownTrueIdentity_IsProvedAtEveryWidth(string name)
    => Assert.That(Prove(name), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 3)));

  /// <summary>The known-false identities at 32 bits, each model checked by the interpreter and here.</summary>
  [TestCase("average-overflowing-u32")]
  [TestCase("mul257-as-or")]
  [TestCase("de-morgan-wrong")]
  [TestCase("signed-compare-as-unsigned")]
  [TestCase("rotate-with-arithmetic-shift")]
  [TestCase("after-invalid")]
  public void AKnownFalseIdentity_IsRefutedAt32Bit(string name)
    => Assert.That(Prove(name, 32), Is.InstanceOf<ProofResult.Refuted>().With.Property("Width").EqualTo(32));

  /// <summary>Test design 4.</summary>
  [Test]
  public void SignedAndUnsignedComparisons_AreTold() {
    var signed = (ProofResult.Refuted)Prove("signed-below-zero", 32);
    Assert.That(signed.Inputs["x"], Is.GreaterThanOrEqualTo(0x80000000UL));
    Assert.That(Prove("unsigned-below-zero", 32), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)));
  }

  /// <summary>Test design 6: 31 is compared, 32 is a don't-care.</summary>
  [Test]
  public void AStrictShiftByTheWidth_IsADontCare() => Assert.That(Prove("strict-shift-31", 32), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)));

  /// <summary>Test design 7.</summary>
  [Test]
  public void AnAfterSideInvalidWhereTheBeforeSideIsValid_IsRefuted() {
    var result = (ProofResult.Refuted)Prove("after-invalid", 32);
    Assert.That(result.Inputs["n"], Is.GreaterThanOrEqualTo(32UL));
    Assert.That(result.AfterFault, Does.Contain("shift amount of at least 32"));
  }

  /// <summary>Test design 10: the constant 257 does not fit 8 bits; 16 bits is still asked.</summary>
  [Test]
  public void AConstantThatDoesNotFitAWidth_IsUnsupportedThere_ButTheOtherWidthsAreAsked() {
    var stub = new Stub(new SmtAnswer(SmtStatus.Unsat, _none));
    var result = (ProofResult.Unsupported)new SmtBitvectorProver([16, 8], stub).Prove(Fixture("mul257-self"), _fiveSeconds);
    Assert.That((result.Operation, result.Detail), Is.EqualTo(("Mul.u8", "at width 8: the constant 257 does not fit u8")));
    Assert.That(stub.Queries, Has.Count.EqualTo(1));
    Assert.That(stub.Queries[0], Does.Contain("(_ bv257 16)"));
  }

  /// <summary>Test design 11.</summary>
  [Test]
  public void TheWidths_AreChecked() {
    var stub = new Stub(new SmtAnswer(SmtStatus.Unsat, _none));
    Assert.That(new SmtBitvectorProver([32], stub).Prove(Fixture("rotate-constant"), _fiveSeconds), Is.EqualTo(new ProofResult.Proved("smt-bitvector", 1)));
    Assert.That(stub.Queries, Has.Count.EqualTo(1));
    Assert.That(() => new SmtBitvectorProver([]), Throws.ArgumentException);
    Assert.That(() => new SmtBitvectorProver([0]), Throws.ArgumentException);
    Assert.That(() => new SmtBitvectorProver([65]), Throws.ArgumentException);
    Assert.That(() => new SmtBitvectorProver(null!), Throws.ArgumentNullException);
    Assert.That(new SmtBitvectorProver([64, 16, 16]).Widths, Is.EqualTo(new[] { 16, 64 }));
    Assert.That(new SmtBitvectorProver().Widths, Is.EqualTo(new[] { 16, 32, 64 }));
    Assert.That(new SmtBitvectorProver().Id, Is.EqualTo("smt"));
  }

  /// <summary>Test design 14: unknown is never unsat.</summary>
  [Test]
  public void ASolverAnsweringUnknown_IsInconclusive() {
    var stub = new Stub(new SmtAnswer(SmtStatus.Unknown, _none, "incomplete quantifiers"));
    var result = new SmtBitvectorProver([16, 32], stub).Prove(Fixture("rotate-constant"), _fiveSeconds);
    Assert.That(result, Is.EqualTo(new ProofResult.Inconclusive("smt-bitvector", "solver returned unknown: incomplete quantifiers")));
    Assert.That(ProofDiagnostics.For("rotate-constant", "smt", result)!.Message, Is.EqualTo("proof of rotate-constant inconclusive (solver returned unknown: incomplete quantifiers); rewrite not applied"));
    var timedOut = new SmtBitvectorProver([16], new Stub(new SmtAnswer(SmtStatus.Unknown, _none, "timeout"))).Prove(Fixture("rotate-constant"), _fiveSeconds);
    Assert.That(timedOut, Is.EqualTo(new ProofResult.Inconclusive("smt-bitvector", "timeout")));
  }

  /// <summary>Test design 16: a binding other than the lock's is refused before any query.</summary>
  [Test]
  public void ABindingOfAnotherVersionThanTheLock_IsRefused() {
    var lockText = File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "tooling/toolchains.lock"));
    Assert.That(Z3Solver.Pinned(lockText), Is.EqualTo(Z3Solver.BindingVersion()));
    var altered = lockText.Replace($"version: \"{Z3Solver.Pinned(lockText)}\"\n    kind: nuget\n    requires: dotnet-sdk\n    probe: [Microsoft.Z3]", "version: \"9.9.9\"\n    kind: nuget\n    requires: dotnet-sdk\n    probe: [Microsoft.Z3]", StringComparison.Ordinal);
    var exception = Assert.Throws<ProverUnavailableException>(() => _ = new Z3Solver(altered));
    Assert.That((exception!.Diagnostic.Code, exception.Diagnostic.Message), Is.EqualTo(("PLY0022", $"microsoft-z3 is {Z3Solver.BindingVersion()}, but tooling/toolchains.lock pins 9.9.9")));
  }

  [TestCase("schema: 1\ntoolchains: {}\n")]
  [TestCase("toolchains: [1, 2]\n")]
  [TestCase("{ unclosed\n")]
  [TestCase("")]
  public void ALockWithoutTheBinding_IsRefused(string lockText)
    => Assert.That(() => Z3Solver.Pinned(lockText), Throws.TypeOf<ProverUnavailableException>().With.Property("Diagnostic").Property("Code").EqualTo("PLY0022"));

  [Test]
  public void AMissingNativeLibrary_IsRefused() {
    var lockText = "toolchains:\n  microsoft-z3:\n    version: \"4.11.2\"\n";
    var exception = Assert.Throws<ProverUnavailableException>(() => _ = new Z3Solver(lockText, () => throw new DllNotFoundException("libz3")));
    Assert.That((exception!.Diagnostic.Code, exception.Diagnostic.ExitCode), Is.EqualTo(("PLY0021", (ExitCode?)ExitCode.EnvironmentError)));
    Assert.That(new Z3Solver(lockText, () => "4.11.2").Version, Is.EqualTo("z3 4.11.2"));
    Assert.That(new Z3Solver().Version, Is.EqualTo($"z3 {Z3Solver.BindingVersion()}"));
  }

  /// <summary>Test design 17: a model that does not refute the rewrite is an error, never a refutation.</summary>
  [Test]
  public void AModelThatFailsTheRecheck_IsAnError() {
    var stub = new Stub(new SmtAnswer(SmtStatus.Sat, new Dictionary<string, ulong> { ["x"] = 1 }));
    var exception = Assert.Throws<InvalidOperationException>(() => new SmtBitvectorProver([16], stub).Prove(Fixture("rotate-constant"), _fiveSeconds));
    Assert.That(exception!.Message, Does.Contain("x=0x01").And.Contain("does not refute"));
    var excluded = new Stub(new SmtAnswer(SmtStatus.Sat, new Dictionary<string, ulong> { ["x"] = 1, ["n"] = 0 }));
    Assert.That(() => new SmtBitvectorProver([16], excluded).Prove(Fixture("rotate-variable"), _fiveSeconds), Throws.InvalidOperationException, "n = 0 violates the precondition");
  }

  /// <summary>Test design 18: one file per width with the dump on, none with it off; the query is the stored one.</summary>
  [Test]
  public void TheQueries_AreDumpedOnRequest() {
    var directory = Path.Combine(Path.GetTempPath(), "polypiler-proof-dump-" + Guid.NewGuid().ToString("N"));
    try {
      Assert.That(new SmtBitvectorProver([16, 32]) { DumpDirectory = directory }.Prove(Fixture("rotate-constant"), _fiveSeconds), Is.InstanceOf<ProofResult.Proved>());
      Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal), Is.EqualTo(new[] { "rotate-constant-w16.smt2", "rotate-constant-w32.smt2" }));
      var dumped = File.ReadAllText(Path.Combine(directory, "rotate-constant-w16.smt2"));
      Assert.That(dumped, Does.StartWith("; rule: rotate-constant\n; width: 16\n; verdict: unsat\n; elapsed: "));
      var query = string.Concat(dumped.Split('\n').Where(line => !line.StartsWith(';')).Select(line => line + "\n")).TrimEnd('\n') + "\n";
      Assert.That(query, Is.EqualTo(File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, Folder, "rotate-constant-w16.smt2"))));
      Assert.That(query, Is.EqualTo(SmtEncoder.ToSmtLib(Fixture("rotate-constant"), 16)));

      var quiet = Path.Combine(directory, "quiet");
      new SmtBitvectorProver([16]) { DumpDirectory = null }.Prove(Fixture("rotate-constant"), _fiveSeconds);
      Assert.That(Directory.Exists(quiet), Is.False);

      new SmtBitvectorProver([16], new Stub(new SmtAnswer(SmtStatus.Unknown, _none, "incomplete"))) { DumpDirectory = directory }.Prove(new("odd rule/name", "%r = Add.u<w> %x, 1", "%r = Add.u<w> %x, 1", [new("x", "u<w>")]), _fiveSeconds);
      Assert.That(File.ReadAllText(Path.Combine(directory, "odd_rule_name-w16.smt2")), Does.Contain("; verdict: unknown (incomplete)"));
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, true);
    }
  }

  [Test]
  public void TheDumpDirectory_DefaultsToTheEnvironment() {
    var before = Environment.GetEnvironmentVariable(SmtBitvectorProver.DumpVariable);
    try {
      Environment.SetEnvironmentVariable(SmtBitvectorProver.DumpVariable, "dump-here");
      Assert.That(new SmtBitvectorProver().DumpDirectory, Is.EqualTo("dump-here"));
      Environment.SetEnvironmentVariable(SmtBitvectorProver.DumpVariable, null);
      Assert.That(new SmtBitvectorProver().DumpDirectory, Is.Null);
    } finally {
      Environment.SetEnvironmentVariable(SmtBitvectorProver.DumpVariable, before);
    }
  }

  [Test]
  public void NullArguments_AreRefused() {
    var prover = new SmtBitvectorProver([16], new Stub(new SmtAnswer(SmtStatus.Unsat, _none)));
    Assert.That(() => prover.Prove(null!, _fiveSeconds), Throws.ArgumentNullException);
    Assert.That(() => prover.Prove(Fixture("rotate-constant"), null!), Throws.ArgumentNullException);
    Assert.That(() => new Z3Solver(null!), Throws.ArgumentNullException);
    Assert.That(() => new Z3Solver("", null!), Throws.ArgumentNullException);
    Assert.That(() => new Z3Solver().Check(null!, [], TimeSpan.FromSeconds(1)), Throws.ArgumentNullException);
    Assert.That(() => new Z3Solver().Check("", null!, TimeSpan.FromSeconds(1)), Throws.ArgumentNullException);
  }

}
