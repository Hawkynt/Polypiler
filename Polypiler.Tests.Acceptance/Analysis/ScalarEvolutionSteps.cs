using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Scev;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Analysis;

/// <summary>The steps of <c>tests/features/intrinsics/ana-scev.feature</c>: scalar evolution of a C# loop imported into the IR.</summary>
[Binding, Scope(Feature = "Scalar evolution")]
public sealed class ScalarEvolutionSteps {

  private IrFunction? _function;
  private ScalarEvolution? _analysis;

  private IrStmt Loop => this._analysis!.Loops.Single();

  private IrExpr Read(string name) => new IrLocalRef(this._function!.Parameters.Concat(this._function.Locals).First(local => local.Name == name));

  /// <summary>Imports a method with one loop.</summary>
  [Given("^the analysed C# loop \"(.*)\"$")]
  public void Method(string method) => this._function = PrototypeFixture.Import("Loop.cs", PrototypeFixture.Wrap(method)).Functions[0];

  /// <summary>Analyses the method with its own constant and range facts.</summary>
  [When("^scalar evolution runs$")]
  public void Run() => this._analysis = new ScalarEvolution(this._function!);

  /// <summary>The evolution of a variable where a pass starts.</summary>
  [Then("^the evolution of \"([^\"]+)\" is \"([^\"]+)\"$")]
  public void Evolution(string name, string expected) => Assert.That(this._analysis!.Of(this.Read(name), this.Loop).ToString(), Is.EqualTo(expected));

  /// <summary>The evolution of a variable read before a statement of the loop's body.</summary>
  [Then("^the evolution of \"([^\"]+)\" before statement ([0-9]+) of the body is \"([^\"]+)\"$")]
  public void EvolutionAt(string name, int statement, string expected) {
    var body = this.Loop switch { IrWhile loop => loop.Body, IrFor loop => loop.Body, _ => ((IrDoWhile)this.Loop).Body };
    Assert.That(this._analysis!.At(this.Read(name), new ProgramPoint(body, statement), this.Loop).ToString(), Is.EqualTo(expected));
  }

  /// <summary>How often the body runs.</summary>
  [Then("^the trip count of the loop is \"([^\"]+)\"$")]
  public void TripCount(string expected) => Assert.That(this._analysis!.TripCountOf(this.Loop).ToString(), Is.EqualTo(expected));
}
