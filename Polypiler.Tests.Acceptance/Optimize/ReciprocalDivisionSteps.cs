using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;
using Polypiler.Core.Verification.Proofs;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Optimize;

/// <summary>Reciprocal division contracts on executable source imported into the current tree IR.</summary>
[Binding, Scope(Feature = "Recovered division and remainder by a constant")]
public sealed class ReciprocalDivisionSteps {

  private IrModule _before = null!;
  private OptimizationResult _after = null!;

  /// <summary>Imports one named fixture function.</summary>
  [Given("^the reciprocal division fixture function \"([^\"]+)\"$")]
  public void Fixture(string function) {
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "recognize", "o0126", "divide.cs");
    var module = PrototypeFixture.Import("divide.cs", File.ReadAllText(path));
    this._before = module with { Functions = [module.Functions.Single(candidate => candidate.Name == function)] };
    Assert.That(IrVerifier.Verify(this._before), Is.Empty);
  }

  /// <summary>Runs only O0126 for a target that states no cost preference.</summary>
  [When("^only rule O0126 runs$")]
  public void Run() => this.Optimize(disabled: false);

  /// <summary>Runs with every rule disabled, O0126 among them.</summary>
  [When("^rule O0126 is disabled$")]
  public void Disabled() => this.Optimize(disabled: true);

  private void Optimize(bool disabled) => this._after = Optimizer.Run(this._before, new OptimizerOptions {
    Prover = new SmtRecognitionProver(), Target = RuleTarget.Neutral, Verify = true,
    Disabled = RuleTable.Default.Ids.Where(id => disabled || id != "O0126").ToHashSet()
  });

  /// <summary>The result is the operation by the constant, accepted on its checked certificate.</summary>
  [Then("^the result is a (division|remainder) by (\\d+) on a validated certificate$")]
  public void Divides(string operation, long divisor) {
    var op = operation == "division" ? IrBinaryOp.Divide : IrBinaryOp.Remainder;
    Assert.That(IrTree.Nodes(this._after.Module.Functions[0].Body).OfType<IrBinary>().Any(node => node.Op == op && node.Right is IrConst { Value: var d } && d == divisor), Is.True,
      IrText.Of(this._after.Module));
    Assert.That(this._after.Notes.Any(note => note.StartsWith("O0126 applied: ", StringComparison.Ordinal) && note.EndsWith("[proof: validated-rule]", StringComparison.Ordinal)), Is.True);
    Assert.That(IrVerifier.Verify(this._after.Module), Is.Empty);
  }

  /// <summary>A refused or disabled candidate preserves the entire imported IR.</summary>
  [Then("^the divided IR is unchanged$")]
  public void Unchanged() {
    Assert.That(IrText.Of(this._after.Module), Is.EqualTo(IrText.Of(this._before)));
    Assert.That(this._after.Applications, Is.Empty);
  }

  /// <summary>The report contains the refusal.</summary>
  [Then("^the division report contains \"([^\"]+)\"$")]
  public void Report(string text) => Assert.That(this._after.Notes, Does.Contain(text));

  /// <summary>A disabled rule reports nothing.</summary>
  [Then("^the division report is empty$")]
  public void Empty() => Assert.That(this._after.Notes, Is.Empty);

  /// <summary>Both versions produce the stated bits for one operand.</summary>
  [Then("^both divided versions map ([0-9A-Fa-f]+) to ([0-9A-Fa-f]+)$")]
  public void Value(string input, string output) {
    var function = this._before.Functions.Single();
    var bits = ulong.Parse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    object argument = function.Parameters[0].Type.Kind switch {
      IrTypeKind.U16 => unchecked((ushort)bits),
      IrTypeKind.I32 => unchecked((int)bits),
      IrTypeKind.U32 => unchecked((uint)bits),
      IrTypeKind.I64 => unchecked((long)bits),
      _ => bits
    };
    var before = IrInterpreter.Call(this._before, function.Name, [argument]);
    var after = IrInterpreter.Call(this._after.Module, function.Name, [argument]);
    Assert.That(after, Is.EqualTo(before));
    var width = function.ReturnType.Bits;
    var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    var actual = (after is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(after, CultureInfo.InvariantCulture))) & mask;
    Assert.That(actual, Is.EqualTo(ulong.Parse(output, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
  }
}
