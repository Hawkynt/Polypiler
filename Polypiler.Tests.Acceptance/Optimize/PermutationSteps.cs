using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>The permutation work orders on executable source fixtures imported into the tree IR.</summary>
[Binding, Scope(Feature = "Proved bit reversal recognition"), Scope(Feature = "Proved byte swap recognition")]
public sealed class PermutationSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;

  [Given("^the permutation fixture \"([^\"]+)\" function \"([^\"]+)\"$")]
  public void Fixture(string file, string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", file);
    var module = PrototypeFixture.Import(file, File.ReadAllText(path));
    this._before = module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  [When("^only permutation rule (O[0-9]+) runs with proof budget ([0-9]+) seconds$")]
  public void Run(string id, int seconds) {
    this._after = Optimizer.Run(this._before, new OptimizerOptions {
      Prover = new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.FromSeconds(seconds)) },
      Disabled = RuleTable.Default.Ids.Where(other => other != id).ToHashSet(),
      Verify = true
    });
  }

  [When("^permutation rule (O[0-9]+) is disabled$")]
  public void Disabled(string id) => this._after = Optimizer.Run(this._before, new OptimizerOptions {
    Prover = new SmtRecognitionProver(), Disabled = new HashSet<string>(RuleTable.Default.Ids), Verify = true
  });

  [Then("^the permutation result contains intrinsic \"([^\"]+)\" and an SMT proof$")]
  public void Intrinsic(string operation) {
    Assert.That(IrText.Of(this._after.Module), Does.Contain(operation + "<"));
    Assert.That(this._after.Applications, Has.Count.EqualTo(1));
    Assert.That(this._after.Notes.Any(note => note.EndsWith("[proof: smt-bitvector]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  [Then("^the permutation IR is unchanged$")]
  public void Unchanged() {
    Assert.That(IrText.Of(this._after.Module), Is.EqualTo(IrText.Of(this._before)));
    Assert.That(this._after.Applications, Is.Empty);
  }

  [Then("^the permutation report contains \"([^\"]+)\"$")]
  public void Report(string text) => Assert.That(this._after.Notes, Does.Contain(text));

  [Then("^the permutation report is empty$")]
  public void NoReport() => Assert.That(this._after.Notes, Is.Empty);

  [Then("^both permutation versions map ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Value(string input, string output) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.U64 => bits,
      IrTypeKind.I32 => unchecked((int)bits),
      _ => (uint)bits
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    var actual = after is int signed ? (ulong)unchecked((uint)signed) : Convert.ToUInt64(after, CultureInfo.InvariantCulture);
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }
}
