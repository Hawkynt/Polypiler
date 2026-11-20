using System;
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

/// <summary>Arithmetic recognition contracts on executable source imported into the current tree IR.</summary>
[Binding, Scope(Feature = "Proved wrapping absolute value recognition")]
public sealed class ArithmeticRecognitionSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;

  /// <summary>Imports one named fixture function, retaining the actual frontend conversions.</summary>
  [Given("^the arithmetic recognition fixture \"([^\"]+)\" function \"([^\"]+)\"$")]
  public void Fixture(string file, string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", file);
    var module = PrototypeFixture.Import(file, File.ReadAllText(path));
    this._before = module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  /// <summary>Runs only the selected rule with the stated proof budget.</summary>
  [When("^only arithmetic rule (O[0-9]+) runs with proof budget ([0-9]+) seconds$")]
  public void Run(string id, int seconds) {
    this._after = Optimizer.Run(this._before, new OptimizerOptions {
      Prover = new SmtRecognitionProver() { Budget = new(SolverTimeout: TimeSpan.FromSeconds(seconds)) },
      Disabled = RuleTable.Default.Ids.Where(other => other != id).ToHashSet(), Verify = true
    });
  }

  /// <summary>Disables the selected rule while all unrelated rules remain excluded from this fixture.</summary>
  [When("^arithmetic rule (O[0-9]+) is disabled$")]
  public void Disabled(string id) {
    Assert.That(RuleTable.Default.Ids, Does.Contain(id));
    this._after = Optimizer.Run(this._before, new OptimizerOptions {
      Prover = new SmtRecognitionProver(), Disabled = RuleTable.Default.Ids.ToHashSet(), Verify = true
    });
  }

  /// <summary>The committed rewrite contains the intrinsic and its proof evidence.</summary>
  [Then("^the arithmetic result contains intrinsic \"([^\"]+)\" and an SMT proof$")]
  public void Intrinsic(string operation) {
    Assert.That(IrText.Of(this._after.Module), Does.Contain(operation + "<"));
    Assert.That(this._after.Applications, Has.Count.EqualTo(1));
    Assert.That(this._after.Notes.Any(note => note.EndsWith("[proof: smt-bitvector]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  /// <summary>A refused or disabled candidate preserves the entire imported IR.</summary>
  [Then("^the arithmetic IR is unchanged$")]
  public void Unchanged() {
    Assert.That(IrText.Of(this._after.Module), Is.EqualTo(IrText.Of(this._before)));
    Assert.That(this._after.Applications, Is.Empty);
  }

  /// <summary>The report contains the specified precondition or proof refusal.</summary>
  [Then("^the arithmetic report contains \"([^\"]+)\"$")]
  public void Report(string text) => Assert.That(this._after.Notes, Does.Contain(text));

  /// <summary>A disabled rule emits no provisional proof or refusal.</summary>
  [Then("^the arithmetic report is empty$")]
  public void NoReport() => Assert.That(this._after.Notes, Is.Empty);

  /// <summary>Both versions produce the specified bits, including the signed minimum.</summary>
  [Then("^both arithmetic versions map ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Value(string input, string output) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.I8 => (object)unchecked((sbyte)bits),
      IrTypeKind.I16 => (object)unchecked((short)bits),
      IrTypeKind.I32 => (object)unchecked((int)bits),
      _ => unchecked((long)bits)
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    var width = function.ReturnType.Bits;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var actual = unchecked((ulong)Convert.ToInt64(after, CultureInfo.InvariantCulture)) & mask;
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }
}
