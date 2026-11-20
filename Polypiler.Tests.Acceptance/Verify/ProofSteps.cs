using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Verify;

/// <summary>
/// The steps of <c>tests/features/infra/ver-exhaustive.feature</c> (#70) and
/// <c>tests/features/infra/ver-smt.feature</c> (#71): an obligation built from the scenario's inputs,
/// preconditions and sides, proved by the real provers - Z3 included - and the verdict, its
/// diagnostic and every counterexample checked by arithmetic in the step.
/// </summary>
[Binding]
[Scope(Feature = "Exhaustive small-width equivalence prover")]
[Scope(Feature = "SMT bitvector equivalence prover on Z3")]
public sealed class ProofSteps {

  private const string Rule = "scenario";

  private IReadOnlyList<ProofInput> _inputs = [];
  private readonly List<string> _preconditions = [];
  private string _before = "";
  private string _after = "";
  private ProofResult? _result;
  private string _prover = "";

  private ProofResult Result => this._result ?? throw new InvalidOperationException("no prover ran");

  private ProofObligation Obligation => new(Rule, this._before, this._after, this._inputs, this._preconditions, "r");

  private Diagnostic Diagnostic => ProofDiagnostics.For(Rule, this._prover, this.Result) ?? throw new AssertionException("the verdict raises no diagnostic");

  private static ulong Hex(string text) => ulong.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

  [Given("^the inputs " + StepText.Quoted + "$")]
  public void Inputs(string inputs) => this._inputs = ObligationFile.Inputs(inputs);

  [Given("^the precondition " + StepText.Quoted + "$")]
  public void Precondition(string fragment) => this._preconditions.Add(fragment);

  [Given("^the before IR " + StepText.Quoted + "$")]
  public void Before(string fragment) => this._before = fragment;

  [Given("^the after IR " + StepText.Quoted + "$")]
  public void After(string fragment) => this._after = fragment;

  [When("^the exhaustive prover runs with the default budget$")]
  public void Exhaustive() {
    var prover = new ExhaustiveProver();
    this._prover = prover.Id;
    this._result = prover.Prove(this.Obligation, ProofBudget.Default);
  }

  [When("^the SMT prover runs with a ([0-9]+) s budget$")]
  public void SmtDefault(int seconds) => this.Smt("16, 32 and 64", seconds);

  [When("^the SMT prover runs at widths? ([0-9, and]+) with a ([0-9]+) s budget$")]
  public void Smt(string widths, int seconds) {
    var list = widths.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Where(part => part != "and").Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToList();
    var prover = new SmtBitvectorProver(list) { DumpDirectory = null };
    this._prover = prover.Id;
    this._result = prover.Prove(this.Obligation, new ProofBudget(SolverTimeout: TimeSpan.FromSeconds(seconds)));
  }

  [Then("^the result is Proved with proof class " + StepText.Quoted + "$")]
  public void Proved(string proofClass) {
    Assert.That(this.Result, Is.InstanceOf<ProofResult.Proved>());
    Assert.That(this.Result.ProofClass, Is.EqualTo(proofClass));
    Assert.That(ProofDiagnostics.For(Rule, this._prover, this.Result), Is.Null);
  }

  [Then("^([0-9]+) input combinations were evaluated$")]
  public void Evaluated(long count) => Assert.That(((ProofResult.Proved)this.Result).Evaluated, Is.EqualTo(count));

  [Then("^three queries were answered \"unsat\"$")]
  public void ThreeUnsat() => Assert.That(((ProofResult.Proved)this.Result).Evaluated, Is.EqualTo(3));

  [Then("^the result is Refuted with ((?:[a-z]+ = 0x[0-9A-F]+, )+)before (0x[0-9A-F]+) and after (0x[0-9A-F]+)$")]
  public void RefutedWith(string inputs, string before, string after) {
    var refuted = (ProofResult.Refuted)this.Result;
    var expected = inputs.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(" = ")).ToDictionary(pair => pair[0], pair => Hex(pair[1]));
    Assert.That(refuted.Inputs, Is.EqualTo(expected));
    Assert.That((refuted.BeforeValue, refuted.AfterValue), Is.EqualTo((Hex(before), Hex(after))));
  }

  [Then("^the result is Refuted$")]
  public void Refuted() => Assert.That(this.Result, Is.InstanceOf<ProofResult.Refuted>());

  [Then("^the result is Refuted at width ([0-9]+)$")]
  public void RefutedAt(int width) => Assert.That(((ProofResult.Refuted)this.Result).Width, Is.EqualTo(width));

  [Then("^the reported inputs satisfy a \\+ b >= 4294967296$")]
  public void SumWraps() {
    var inputs = ((ProofResult.Refuted)this.Result).Inputs;
    Assert.That((BigInteger)inputs["a"] + inputs["b"], Is.GreaterThanOrEqualTo(new BigInteger(4294967296)));
  }

  [Then("^the reported before value minus the reported after value is (0x[0-9A-F]+)$")]
  public void Difference(string difference) {
    var refuted = (ProofResult.Refuted)this.Result;
    Assert.That(refuted.BeforeValue - refuted.AfterValue, Is.EqualTo(Hex(difference)));
  }

  [Then("^substituting the reported x gives different values for \"x \\* 257 mod 65536\" and \"\\(x << 8 \\| x\\) mod 65536\"$")]
  public void Substituted() {
    var x = ((ProofResult.Refuted)this.Result).Inputs["x"];
    Assert.That(x * 257 % 65536, Is.Not.EqualTo(((x << 8) | x) % 65536));
  }

  [Then("^the result is BudgetExceeded with ([0-9]+) input bits$")]
  public void BudgetExceeded(int bits) => Assert.That(((ProofResult.BudgetExceeded)this.Result).InputBits, Is.EqualTo(bits));

  [Then("^the result is Inconclusive with reason " + StepText.Quoted + "$")]
  public void Inconclusive(string reason) => Assert.That(((ProofResult.Inconclusive)this.Result).Reason, Is.EqualTo(reason));

  [Then("^the result is Unsupported$")]
  public void Unsupported() => Assert.That(this.Result, Is.InstanceOf<ProofResult.Unsupported>());

  [Then("^diagnostic (PLY[0-9]{4}) is reported$")]
  public void Reported(string code) => Assert.That((this.Diagnostic.Code, this.Diagnostic.Severity), Is.EqualTo((code, DiagnosticSeverity.Error)));

  [Then("^diagnostic (PLY[0-9]{4}) is reported with mapped exit code ([0-9]+)$")]
  public void ReportedWithExit(string code, int exit) {
    this.Reported(code);
    Assert.That((int?)this.Diagnostic.ExitCode, Is.EqualTo(exit));
  }

  [Then("^warning (PLY[0-9]{4}) is reported$")]
  public void Warned(string code) => Assert.That((this.Diagnostic.Code, this.Diagnostic.Severity, this.Diagnostic.ExitCode), Is.EqualTo((code, DiagnosticSeverity.Warning, (ExitCode?)null)));

  [Then("^warning (PLY[0-9]{4}) is reported with " + StepText.Quoted + "$")]
  public void WarnedWith(string code, string text) {
    this.Warned(code);
    Assert.That(this.Diagnostic.Message, Does.Contain(text));
  }

  [Then("^no result of class " + StepText.Quoted + " is recorded$")]
  public void NothingRecorded(string proofClass)
    => Assert.That(this.Result is ProofResult.Proved proved && proved.ProofClass == proofClass, Is.False);

}
