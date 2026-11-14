using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Permutation.Model;
using Polypiler.Permutation.Semantics;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Permutation;

/// <summary>
/// The steps of <c>tests/features/permutation/perm-hierarchy.feature</c>: mappings, Boolean transforms
/// and conditions are built in process and classified, narrowed and verified by
/// <c>Polypiler.Permutation.Semantics</c>. BitPerm's duplication mapping is also held to the exported
/// fixture of the same test.
/// </summary>
[Binding, Scope(Feature = "Typed semantic hierarchy of bit transforms")]
public sealed partial class PermHierarchySteps {

  private const string DuplicationFixture = "test-33-duplicate-same-source-to-multiple-destinations";

  private BitMapping? _mapping;
  private BitCondition? _condition;
  private BitBooleanTransform? _transform;
  private BitSemanticClass? _class;
  private bool _narrowed;
  private BitRouting? _routing;
  private string? _reason;
  private IReadOnlyList<Diagnostic> _diagnostics = [];

  private BitMapping Mapping => this._mapping ?? throw new AssertionException("no mapping was given");

  private BitBooleanTransform Transform => this._transform ?? throw new AssertionException("no Boolean transform was given");

  /// <summary>Parses a mapping text.</summary>
  [Given("^the mapping " + StepText.Quoted + "$")]
  public void MappingText(string text) => this._mapping = BitMapping.Parse(text);

  /// <summary>Parses a mapping text and checks it is the mapping BitPerm's duplication test exports.</summary>
  [Given("^BitPerm's duplication mapping " + StepText.Quoted + "$")]
  public void DuplicationMapping(string text) {
    this.MappingText(text);
    var path = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "bitperm", "mappings", DuplicationFixture + ".json");
    using var stream = File.OpenRead(path);
    Assert.That(BitPermState.Read(stream), Is.EqualTo(this.Mapping), $"the mapping of {DuplicationFixture}");
  }

  /// <summary>A mapping and a condition that knows nothing about the input and requires the given outputs.</summary>
  [Given("^the mapping " + StepText.Quoted + @" and a condition whose RequiredOutputMask is 0x([0-9A-Fa-f]+)$")]
  public void MappingWithCondition(string text, string required) {
    this.MappingText(text);
    this._condition = new BitCondition(BigInteger.Zero, BigInteger.Zero, BigInteger.Parse("0" + required, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
  }

  /// <summary>A transform of two outputs.</summary>
  [Given(@"^a Boolean transform of (\d+) inputs whose output 0 is " + StepText.Quoted + " and output 1 is " + StepText.Quoted + "$")]
  public void TwoOutputs(int inputs, string first, string second) => this._transform = BitBooleanTransform.Create(inputs, [ExpressionText.Parse(first), ExpressionText.Parse(second)]);

  /// <summary>A transform whose outputs are listed least significant first.</summary>
  [Given(@"^a Boolean transform of (\d+) inputs whose outputs are (.+)$")]
  public void Outputs(int inputs, string list)
    => this._transform = BitBooleanTransform.Create(inputs, [.. QuotedItem().Matches(list).Select(item => ExpressionText.Parse(item.Groups[1].Value))]);

  /// <summary>Classifies the mapping.</summary>
  [When("^it is classified$")]
  public void Classify() => this._class = BitSemanticsClassifier.Classify(this.Mapping);

  /// <summary>Narrows the transform to a routing.</summary>
  [When("^TryAsRouting is called$")]
  public void TryAsRouting() => this._narrowed = this.Transform.TryAsRouting(out this._routing, out this._reason);

  /// <summary>Verifies the routing of the mapping and the condition.</summary>
  [When("^the routing is verified$")]
  public void Verify() => this._diagnostics = BitSemanticsVerifier.Check(BitRouting.From(this.Mapping, this._condition));

  /// <summary>Checks the class.</summary>
  [Then("^the class is " + StepText.Quoted + "$")]
  public void ClassIs(string expected) => Assert.That(this._class, Is.EqualTo(Enum.Parse<BitSemanticClass>(expected)));

  /// <summary>Checks the explanation.</summary>
  [Then("^the explanation is " + StepText.Quoted + "$")]
  public void ExplanationIs(string expected) => Assert.That(BitSemanticsClassifier.Explain(this.Mapping), Is.EqualTo(expected));

  /// <summary>Declares the mapping a permutation, which must succeed and pass the verifier.</summary>
  [Then("^BitPermutation.From succeeds$")]
  public void FromSucceeds() {
    var permutation = BitPermutation.From(this.Mapping);
    Assert.That(permutation.Mapping, Is.SameAs(this.Mapping));
    Assert.That(BitSemanticsVerifier.Check(permutation), Is.Empty);
  }

  /// <summary>Declares the mapping a permutation, which must be refused with exactly one diagnostic.</summary>
  [Then("^BitPermutation.TryFrom reports " + StepText.Quoted + "$")]
  public void TryFromReports(string expected) {
    var diagnostics = new DiagnosticBag();
    Assert.That(BitPermutation.TryFrom(this.Mapping, diagnostics, out var permutation), Is.False);
    Assert.That(permutation, Is.Null);
    Assert.That(diagnostics.Items.Select(item => $"{item.Code}: {item.Message}"), Is.EqualTo(new[] { expected }));
  }

  /// <summary>Checks a refused narrowing.</summary>
  [Then("^it returns false with reason " + StepText.Quoted + "$")]
  public void ReturnsFalse(string reason) {
    Assert.That(this._narrowed, Is.False);
    Assert.That(this._routing, Is.Null);
    Assert.That(this._reason, Is.EqualTo(reason));
  }

  /// <summary>Checks a successful narrowing.</summary>
  [Then("^it returns the routing " + StepText.Quoted + "$")]
  public void ReturnsRouting(string text) {
    Assert.That(this._narrowed, Is.True, this._reason);
    Assert.That(this._routing!.Mapping, Is.EqualTo(BitMapping.Parse(text)));
  }

  /// <summary>Checks the only diagnostic of the verification.</summary>
  [Then("^the diagnostic is " + StepText.Quoted + "$")]
  public void DiagnosticIs(string expected)
    => Assert.That(this._diagnostics.Select(item => $"{item.Code}: {item.Message}"), Is.EqualTo(new[] { expected }));

  [GeneratedRegex("\"([^\"]*)\"")]
  private static partial Regex QuotedItem();

  /// <summary>
  /// The expression notation of the feature: <c>Input 3</c>, <c>Const 0</c>, <c>Const 1</c>,
  /// <c>Not(e)</c>, <c>And(a, b)</c>, <c>Or(a, b)</c>, <c>Xor(a, b)</c>.
  /// </summary>
  private sealed class ExpressionText(string text) {

    private int _position;

    public static BoolExpr Parse(string text) {
      var reader = new ExpressionText(text);
      var expression = reader.Expression();
      if (reader._position != text.Length)
        throw new FormatException($"unexpected text at {reader._position} of '{text}'");
      return expression;
    }

    private BoolExpr Expression() {
      var name = this.Word();
      switch (name) {
        case "Input":
          return new BoolExpr.Input(this.Number());
        case "Const":
          return new BoolExpr.Const(this.Number() == 1);
      }

      this.Expect('(');
      var left = this.Expression();
      if (name == "Not") {
        this.Expect(')');
        return new BoolExpr.Not(left);
      }

      this.Expect(',');
      var right = this.Expression();
      this.Expect(')');
      return name switch {
        "And" => new BoolExpr.And(left, right),
        "Or" => new BoolExpr.Or(left, right),
        "Xor" => new BoolExpr.Xor(left, right),
        _ => throw new FormatException($"unknown operator '{name}' in '{text}'")
      };
    }

    private void SkipSpaces() {
      while (this._position < text.Length && text[this._position] == ' ')
        ++this._position;
    }

    private string Word() {
      this.SkipSpaces();
      var start = this._position;
      while (this._position < text.Length && char.IsLetter(text[this._position]))
        ++this._position;
      return text[start..this._position];
    }

    private int Number() {
      this.SkipSpaces();
      var start = this._position;
      while (this._position < text.Length && char.IsDigit(text[this._position]))
        ++this._position;
      return int.Parse(text[start..this._position], CultureInfo.InvariantCulture);
    }

    private void Expect(char expected) {
      this.SkipSpaces();
      if (this._position >= text.Length || text[this._position] != expected)
        throw new FormatException($"'{expected}' expected at {this._position} of '{text}'");
      ++this._position;
    }

  }

}
