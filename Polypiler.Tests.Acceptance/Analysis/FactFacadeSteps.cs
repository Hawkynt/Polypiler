using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Checking;
using Polypiler.Core.Analysis.Constant;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Reqnroll;

namespace Polypiler.Tests.Acceptance.Analysis;

/// <summary>The steps of <c>tests/features/intrinsics/ana-facade.feature</c>: the fact facade over C# methods imported into the IR.</summary>
[Binding, Scope(Feature = "Fact query facade")]
public sealed class FactFacadeSteps {

  private IrModule? _module;
  private ValueFacts? _facts;
  private string? _fact;
  private string? _then;
  private string? _else;
  private string? _failure;

  private IrFunction Function => this._module!.Functions[0];

  private ValueFacts Facts => this._facts ??= new ValueFacts(this.Function);

  private IrLocal Local(string name) => this.Function.Parameters.Concat(this.Function.Locals).First(local => local.Name == name);

  private ProgramPoint BeforeReturn => new(this.Function.Body, this.Function.Body.Statements.Count - 1);

  private IrIf Branch => this.Function.Body.Statements.OfType<IrIf>().Single();

  [Given("^the C# method \"(.*)\"$")]
  public void Method(string method) => this._module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));

  [Given("^the constant domain is registered$")]
  public void ConstantRegistered() => this.Facts.Add(new ConstantAnalysis(this.Function));

  [Given("^no provider is registered$")]
  public void NoProvider() => Assert.That(this.Facts.Has<ConstantFact>(), Is.False);

  [Given("^a deliberately wrong provider that claims \"(.*)\" is constant (\\d+) before the return$")]
  public void WrongProvider(string name, long value) {
    this.Facts.Add(new Wrong(ConstantFact.Of(value)));
    this.Facts.Get<ConstantFact>(this.Local(name), this.BeforeReturn);
  }

  [When("^the constant fact of \"(.*)\" is queried before the return$")]
  public void QueriedBeforeReturn(string name) => this._fact = this.Facts.Get<ConstantFact>(this.Local(name), this.BeforeReturn).ToString();

  [When("^the constant fact of \"(.*)\" is queried at the entry of the then branch and of the else branch$")]
  public void QueriedOnBothEdges(string name) {
    this._then = this.Facts.Get<ConstantFact>(this.Local(name), ProgramPoint.Entry(this.Branch.Then)).ToString();
    this._else = this.Facts.Get<ConstantFact>(this.Local(name), ProgramPoint.Entry(this.Branch.Else!)).ToString();
  }

  [When("^the constant fact of \"(.*)\" is queried at the entry of the else branch$")]
  public void QueriedOnElse(string name)
    => this._failure = Assert.Throws<ArgumentException>(() => this.Facts.Get<ConstantFact>(this.Local(name), ProgramPoint.Entry(this.Branch.Else!)))!.Message;

  [When("^Require is called for the known-bits fact of \"(.*)\" before the return$")]
  public void Required(string name)
    => this._failure = Assert.Throws<FactException>(() => this.Facts.Require<KnownBits>(new IrLocalRef(this.Local(name)), this.BeforeReturn))!.Diagnostic.ToString();

  [When("^FactChecker runs the function with x = (\\d+)$")]
  public void Checked(uint x) => this._failure = string.Join('\n', FactChecker.Run(this._module!, this.Facts, [[x]]));

  [Then("^the fact is \"(.*)\"$")]
  public void FactIs(string fact) => Assert.That(this._fact, Is.EqualTo(fact));

  [Then("^the fact at the then branch is \"(.*)\"$")]
  public void ThenIs(string fact) => Assert.That(this._then, Is.EqualTo(fact));

  [Then("^the fact at the else branch is \"(.*)\"$")]
  public void ElseIs(string fact) => Assert.That(this._else, Is.EqualTo(fact));

  [Then("^Decide for \"(\\w+) < (\\d+)\" before the return is Unknown$")]
  public void DecideUnknown(string name, uint bound) {
    var comparison = new IrBinary(IrBinaryOp.Less, new IrLocalRef(this.Local(name)), new IrConst(IrType.U32, bound));
    Assert.That(this.Facts.Decide(comparison, this.BeforeReturn), Is.EqualTo(Truth.Unknown));
  }

  [Then("^it fails with \"(.*)\"$")]
  public void FailsWith(string text) => Assert.That(this._failure, Does.Contain(text));

  /// <summary>A domain no analysis provides.</summary>
  private readonly record struct KnownBits(long Ones) : IFact<KnownBits> {

    public static IFactDomain<KnownBits> Domain { get; } = new KnownBitsDomain();

  }

  private sealed class KnownBitsDomain : IFactDomain<KnownBits> {
    public string Name => "known-bits";
    public KnownBits Unknown => default;
    public KnownBits Unreachable => new(-1);
    public KnownBits Join(KnownBits left, KnownBits right) => new(left.Ones & right.Ones);
    public KnownBits Meet(KnownBits left, KnownBits right) => new(left.Ones | right.Ones);
    public KnownBits Widen(KnownBits previous, KnownBits next) => this.Join(previous, next);
    public bool Implies(KnownBits stronger, KnownBits weaker) => (stronger.Ones & weaker.Ones) == weaker.Ones;
    public bool Contains(KnownBits fact, object value) => value is IrConst constant && (constant.Value & fact.Ones) == fact.Ones;
    public string Describe(KnownBits fact) => "ones " + fact.Ones.ToString(CultureInfo.InvariantCulture);
  }

  /// <summary>A provider that claims one constant for every value.</summary>
  private sealed class Wrong(ConstantFact claim) : IFactProvider<ConstantFact> {
    public ConstantFact At(IrExpr value, ProgramPoint point) => claim;
    public FactExplanation Explain(IrExpr value, ProgramPoint point) => new("constant", [], false);
  }

}
