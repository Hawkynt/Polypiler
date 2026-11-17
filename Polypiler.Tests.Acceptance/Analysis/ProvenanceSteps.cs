using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Provenance;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Analysis;

/// <summary>The steps of <c>tests/features/intrinsics/ana-provenance.feature</c>: provenance of the pointers of C# methods imported into the IR.</summary>
[Binding, Scope(Feature = "Pointer provenance")]
public sealed class ProvenanceSteps {

  private IrFunction? _function;
  private ValueFacts? _facts;

  private ProgramPoint BeforeReturn => new(this._function!.Body, this._function.Body.Statements.Count - 1);

  private IrExpr Value(string name) => new IrLocalRef(this._function!.Parameters.Concat(this._function.Locals).First(local => local.Name == name));

  [Given("^the analysed C# method \"(.*)\"$")]
  public void Method(string method) => this._function = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method)).Functions[0];

  [When("^the provenance analysis runs$")]
  public void Runs() => this._facts = new ValueFacts(this._function!).Add(new ProvenanceAnalysis(this._function!));

  [Then("^the provenance of \"(.*)\" is \"(.*)\"$")]
  public void ProvenanceIs(string name, string expected) => Assert.That(this._facts!.ProvenanceAt(this.Value(name), this.BeforeReturn).ToString(), Is.EqualTo(expected));

  [Then("^IsInBounds of \"(.*)\" for (\\d+) bytes is (True|False|Unknown)$")]
  public void InBounds(string name, long bytes, string expected)
    => Assert.That(this._facts!.IsInBounds(this.Value(name), bytes, this.BeforeReturn), Is.EqualTo(Enum.Parse<Truth>(expected)));

}
