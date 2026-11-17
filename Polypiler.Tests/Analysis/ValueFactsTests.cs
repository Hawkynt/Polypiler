using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis;
using Polypiler.Core.Analysis.Constant;
using Polypiler.Core.IR;
using static Polypiler.Tests.Analysis.AnalysisIr;

namespace Polypiler.Tests.Analysis;

/// <summary>
/// The fact facade (#88): one place to ask, Unknown by default, facts per program point, the PLY0300 to
/// PLY0302 diagnostics; the rows of the issue's test design are named in each test.
/// </summary>
[TestFixture]
public sealed class ValueFactsTests {

  /// <summary><c>a = 2 + 3; b = a * a; return b</c>.</summary>
  private static IrFunction Squared() => Function([X],
    new IrDeclare(A, Op(IrBinaryOp.Add, K(2), K(3))),
    new IrDeclare(B, Op(IrBinaryOp.Multiply, R(A), R(A))),
    new IrReturn(R(B)));

  private static ValueFacts Constant(IrFunction function) => new ValueFacts(function).Add(new ConstantAnalysis(function));

  /// <summary>Test design 1: constant folding through the facade, with the statements it rests on.</summary>
  [Test]
  public void ARegisteredDomain_Answers() {
    var function = Squared();
    var facts = Constant(function);
    var point = new ProgramPoint(function.Body, 2);
    Assert.That(facts.Get<ConstantFact>(B, point), Is.EqualTo(ConstantFact.Of(25)));
    Assert.That(facts.Get<ConstantFact>(B, point).ToString(), Is.EqualTo("constant 25"));
    Assert.That(facts.Explain<ConstantFact>(R(B), point).Because, Is.EqualTo(function.Body.Statements.Take(2)));
    Assert.That(facts.Describe(R(B), point), Is.EqualTo("constant: constant 25"));
    Assert.That(facts.Has<ConstantFact>(), Is.True);
  }

  /// <summary>Test design 3: no provider at all answers Unknown everywhere and decides nothing.</summary>
  [Test]
  public void AMissingDomain_YieldsUnknown() {
    var function = Squared();
    var facts = new ValueFacts(function);
    var point = ProgramPoint.End(function.Body);
    Assert.That(facts.Get<ConstantFact>(B, point), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(facts.Get<TestRange>(B, point), Is.EqualTo(TestRange.Unknown));
    Assert.That(facts.Has<TestRange>(), Is.False);
    Assert.That(facts.Decide(Op(IrBinaryOp.Less, R(A), K(10)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.Describe(R(A), point), Is.Empty);
    Assert.That(facts.Explain<ConstantFact>(R(A), point), Is.EqualTo(new FactExplanation("constant", [], false)).Using<FactExplanation>((x, y) => x.Domain == y.Domain && x.Because.Count == y.Because.Count && x.Assumed == y.Assumed));
    Assert.That(facts.Queries.Select(query => query.Fact), Is.EqualTo(new[] { "unknown", "unknown" }));
  }

  /// <summary>Test design 9: Require reports the missing domain with PLY0300, and answers when a provider is there.</summary>
  [Test]
  public void Require_ReportsTheMissingDomain() {
    var function = Squared();
    var facts = new ValueFacts(function);
    var point = new ProgramPoint(function.Body, 2);
    var thrown = Assert.Throws<FactException>(() => facts.Require<TestRange>(R(A), point))!;
    Assert.That(thrown.Diagnostic.Code, Is.EqualTo("PLY0300"));
    Assert.That(thrown.Diagnostic.Message, Is.EqualTo("no analysis provides facts of domain 'known-bits'"));
    Assert.That(facts.Add(new ConstantAnalysis(function)).Require<ConstantFact>(R(A), point), Is.EqualTo(ConstantFact.Of(5)));
  }

  /// <summary>A domain takes one provider.</summary>
  [Test]
  public void ASecondProvider_IsRefused()
    => Assert.That(() => Constant(Squared()).Add(new ConstantAnalysis(Squared())), Throws.InvalidOperationException.With.Message.Contains("'constant'"));

  /// <summary>Test design 2: the edges of <c>x == 7</c> - the constant on the true edge, Unknown on the false one.</summary>
  [Test]
  public void FactsArePerProgramPoint() {
    var then = Block(new IrAssign(Y, Op(IrBinaryOp.Add, R(X), K(1))));
    var otherwise = Block(new IrAssign(Y, K(8)));
    var function = Function([X], new IrDeclare(Y, K(0)), new IrIf(Op(IrBinaryOp.Equal, R(X), K(7)), then, otherwise), new IrReturn(R(Y)));
    var facts = Constant(function);
    Assert.That(facts.Get<ConstantFact>(X, ProgramPoint.Entry(then)), Is.EqualTo(ConstantFact.Of(7)));
    Assert.That(facts.Get<ConstantFact>(X, ProgramPoint.Entry(otherwise)), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(facts.Get<ConstantFact>(Y, new ProgramPoint(function.Body, 2)), Is.EqualTo(ConstantFact.Of(8)));
    Assert.That(facts.Explain<ConstantFact>(R(X), ProgramPoint.Entry(then)).Because, Is.EqualTo(new[] { function.Body.Statements[1] }));
  }

  /// <summary>Test design 2, the other refinements: a truth variable, its negation, conjunction, disjunction and an inequality's false edge.</summary>
  [TestCase("c", true, 1, 0L)]
  [TestCase("!c", false, 1, 0L)]
  [TestCase("c && x == 3", true, 3, null)]
  [TestCase("!c || x != 3", false, 3, null)]
  [TestCase("x != 3", false, 3, null)]
  [TestCase("3 == x", true, 3, null)]
  public void ConditionsRefineTheirEdges(string condition, bool edge, long expected, long? other) {
    IrExpr Parse(string text) => text switch {
      "c" => R(C),
      "!c" => new IrUnary(IrUnaryOp.LogicalNot, R(C)),
      "x == 3" => Op(IrBinaryOp.Equal, R(X), K(3)),
      "3 == x" => Op(IrBinaryOp.Equal, K(3), R(X)),
      "x != 3" => Op(IrBinaryOp.NotEqual, R(X), K(3)),
      "c && x == 3" => Op(IrBinaryOp.LogicalAnd, R(C), Parse("x == 3")),
      _ => Op(IrBinaryOp.LogicalOr, Parse("!c"), Parse("x != 3"))
    };

    var then = Block();
    var otherwise = Block();
    var function = Function([X, C], new IrIf(Parse(condition), then, otherwise), new IrReturn(R(X)));
    var facts = Constant(function);
    var asked = condition.Contains('x', StringComparison.Ordinal) ? X : C;
    var point = ProgramPoint.Entry(edge ? then : otherwise);
    Assert.That(facts.Get<ConstantFact>(asked, point), Is.EqualTo(ConstantFact.Of(expected)));
    Assert.That(facts.Get<ConstantFact>(asked, ProgramPoint.Entry(edge ? otherwise : then)), Is.EqualTo(other is { } value ? ConstantFact.Of(value) : ConstantFact.Unknown));
  }

  /// <summary>Equivalence: an equality with nothing constant, a float and a comparison other than equality refine nothing.</summary>
  [Test]
  public void OtherConditions_RefineNothing() {
    var f = new IrLocal("d", IrType.F64);
    var then = Block();
    var function = Function([X, Y, f],
      new IrIf(Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Equal, R(X), R(Y)),
        Op(IrBinaryOp.LogicalAnd, Op(IrBinaryOp.Equal, R(f), new IrConst(IrType.F64, 0)), Op(IrBinaryOp.Less, R(X), K(3)))), then, null),
      new IrReturn(R(X)));
    var facts = Constant(function);
    Assert.That(facts.Get<ConstantFact>(X, ProgramPoint.Entry(then)), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(facts.Get<ConstantFact>(f, ProgramPoint.Entry(then)), Is.EqualTo(ConstantFact.Unknown));
  }

  /// <summary>Test design 5: a branch decided false, the code after a return and a loop that never runs are Unreachable.</summary>
  [Test]
  public void UnreachablePoints_AreUnreachable() {
    var never = Block(new IrAssign(Y, K(1)));
    var loop = Block(new IrAssign(Y, K(2)));
    var function = Function([X],
      new IrDeclare(Y, K(0)),
      new IrIf(Op(IrBinaryOp.Equal, K(1), K(2)), never, null),
      new IrWhile(Op(IrBinaryOp.LogicalOr, new IrConst(IrType.Bool, 0), new IrConst(IrType.Bool, 0)), loop),
      new IrReturn(R(Y)),
      new IrAssign(Y, K(3)));
    var facts = Constant(function);
    Assert.That(facts.Get<ConstantFact>(Y, ProgramPoint.Entry(never)), Is.EqualTo(ConstantFact.Unreachable));
    Assert.That(facts.Get<ConstantFact>(Y, ProgramPoint.End(loop)), Is.EqualTo(ConstantFact.Unreachable));
    Assert.That(facts.Get<ConstantFact>(Y, new ProgramPoint(function.Body, 4)), Is.EqualTo(ConstantFact.Unreachable));
    Assert.That(facts.Get<ConstantFact>(Y, new ProgramPoint(function.Body, 3)), Is.EqualTo(ConstantFact.Of(0)));
    Assert.That(facts.Decide(Op(IrBinaryOp.Equal, R(Y), K(0)), new ProgramPoint(function.Body, 4)), Is.EqualTo(Truth.Unknown));
    Assert.That(facts.Explain<ConstantFact>(R(Y), new ProgramPoint(function.Body, 4)).Because, Is.Empty);
  }

  /// <summary>Test design 4 and 12: a value defined mid-block may be asked about after its definition only.</summary>
  [Test]
  public void AValue_IsAskedAboutOnlyWhereItIsDefined() {
    var function = Squared();
    var facts = Constant(function);
    Assert.That(facts.Get<ConstantFact>(A, new ProgramPoint(function.Body, 1)), Is.EqualTo(ConstantFact.Of(5)));
    Assert.That(facts.Get<ConstantFact>(X, ProgramPoint.Entry(function.Body)), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(() => facts.Get<ConstantFact>(B, new ProgramPoint(function.Body, 1)), Throws.ArgumentException.With.Message.Contains("'b' is not defined at block 0, statement 1"));
    Assert.That(() => facts.Get<ConstantFact>(A, ProgramPoint.Entry(function.Body)), Throws.ArgumentException);
    Assert.That(() => facts.Get<ConstantFact>(A, ProgramPoint.Entry(Block())), Throws.ArgumentException.With.Message.Contains("not in a block of 'f'"));
    Assert.That(() => facts.Get<ConstantFact>(A, default), Throws.ArgumentException);
    Assert.That(() => facts.Get<ConstantFact>((IrExpr)null!, ProgramPoint.Entry(function.Body)), Throws.ArgumentNullException);
  }

  /// <summary>The acceptance scenario on the tree: a variable assigned in one branch is not defined in the other.</summary>
  [Test]
  public void AVariableOfOneBranch_IsNotDefinedInTheOther() {
    var v = new IrLocal("v", IrType.U32);
    var a = Block(new IrDeclare(v, Op(IrBinaryOp.Add, R(X), K(1))));
    var b = Block();
    var function = Function([X, C], new IrIf(R(C), a, b), new IrReturn(K(0)));
    var facts = Constant(function);
    Assert.That(() => facts.Get<ConstantFact>(v, ProgramPoint.Entry(b)), Throws.ArgumentException.With.Message.Contains("'v' is not defined at block 2, end"));
    Assert.That(facts.Get<ConstantFact>(v, ProgramPoint.End(a)), Is.EqualTo(ConstantFact.Unknown));
  }

  /// <summary>Test design 6: a provider that gives up yields Unknown; one out of its budget is reported once with PLY0301.</summary>
  [Test]
  public void AProviderThatGivesUp_YieldsUnknown() {
    var function = Squared();
    var point = ProgramPoint.End(function.Body);
    var unavailable = new ValueFacts(function).Add(new FixedProvider(_ => throw new FactUnavailableException("no")));
    Assert.That(unavailable.Get<ConstantFact>(B, point), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(unavailable.Decide(Op(IrBinaryOp.Equal, R(B), K(1)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(unavailable.Explain<ConstantFact>(R(B), point).Because, Is.Empty);
    Assert.That(unavailable.Diagnostics, Is.Empty);

    var exhausted = new ValueFacts(function).Add(new ConstantAnalysis(function, 1));
    Assert.That(exhausted.Get<ConstantFact>(B, point), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(exhausted.Get<ConstantFact>(A, point), Is.EqualTo(ConstantFact.Unknown));
    Assert.That(exhausted.Diagnostics.Select(diagnostic => diagnostic.ToString()),
      Is.EqualTo(new[] { "info PLY0301: analysis 'constant' stopped at its budget in 'f'; affected facts are Unknown" }));
  }

  /// <summary>Test design 7 and 10: one decider, none, two agreeing and two contradicting.</summary>
  [Test]
  public void Decide_AsksEveryDecider() {
    var function = Squared();
    var point = new ProgramPoint(function.Body, 2);
    var comparison = Op(IrBinaryOp.Equal, R(B), K(25));
    Assert.That(Constant(function).Decide(comparison, point), Is.EqualTo(Truth.True));
    Assert.That(Constant(function).Decide(Op(IrBinaryOp.Equal, R(B), K(24)), point), Is.EqualTo(Truth.False));
    Assert.That(Constant(function).Decide(Op(IrBinaryOp.Equal, R(X), K(24)), point), Is.EqualTo(Truth.Unknown));
    Assert.That(Constant(function).Add(new FixedRange(Truth.True)).Decide(comparison, point), Is.EqualTo(Truth.True));
    Assert.That(Constant(function).Add(new FixedRange(Truth.Unknown)).Decide(comparison, point), Is.EqualTo(Truth.True));
    Assert.That(new ValueFacts(function).Add(new FixedProvider(_ => ConstantFact.Unknown)).Decide(comparison, point), Is.EqualTo(Truth.Unknown));
    var thrown = Assert.Throws<FactException>(() => Constant(function).Add(new FixedRange(Truth.False)).Decide(comparison, point))!;
    Assert.That(thrown.Diagnostic.ToString(), Is.EqualTo("error PLY0302: unsound fact: constant decides True and known-bits decides False for Equal(b, 25) at block 0, statement 2"));
  }

  /// <summary>Test design 8: the description lists the domains by name, whatever the order of registration.</summary>
  [Test]
  public void Describe_IsOrderedByDomainName() {
    var function = Squared();
    var point = new ProgramPoint(function.Body, 2);
    var one = new ValueFacts(function).Add(new FixedRange(Truth.Unknown)).Add(new ConstantAnalysis(function));
    var other = new ValueFacts(function).Add(new ConstantAnalysis(function)).Add(new FixedRange(Truth.Unknown));
    Assert.That(one.Describe(R(B), point), Is.EqualTo("constant: constant 25\nknown-bits: unknown"));
    Assert.That(other.Describe(R(B), point), Is.EqualTo(one.Describe(R(B), point)));
  }

  /// <summary>The points of a function name their block by its number in pre-order.</summary>
  [Test]
  public void Points_AreDescribedByBlockNumber() {
    var inner = Block(new IrReturn(K(1)));
    var function = Function([C], new IrIf(R(C), inner, null), new IrReturn(K(0)));
    var layout = new FunctionLayout(function);
    Assert.That(layout.Describe(ProgramPoint.End(inner)), Is.EqualTo("block 1, end"));
    Assert.That(layout.Describe(new ProgramPoint(function.Body, 1)), Is.EqualTo("block 0, statement 1"));
    Assert.That(layout.Contains(inner), Is.True);
    Assert.That(layout.Contains(Block()), Is.False);
    Assert.That(() => new FunctionLayout(null!), Throws.ArgumentNullException);
  }

  /// <summary>A block node that occurs in two places names no single point.</summary>
  [Test]
  public void ABlockInTwoPlaces_NamesNoPoint() {
    var shared = Block(new IrAssign(Y, K(1)));
    var function = Function([C], new IrDeclare(Y, K(0)), new IrIf(R(C), shared, shared), new IrReturn(R(Y)));
    Assert.That(() => Constant(function).Get<ConstantFact>(Y, ProgramPoint.Entry(shared)), Throws.ArgumentException.With.Message.Contains("several places"));
  }

  /// <summary>The variables a statement makes for its blocks are defined in them: loop elements, a caught exception, a resource, a binding.</summary>
  [Test]
  public void BoundVariables_AreDefinedInTheirBlocks() {
    var element = new IrLocal("e", IrType.I32);
    var key = new IrLocal("k", IrType.I32);
    var item = new IrLocal("s", IrType.I32);
    var caught = new IrLocal("ex", IrType.String);
    var resource = new IrLocal("r", IrType.String);
    var bound = new IrLocal("n", IrType.I32);
    var list = new IrLocal("list", IrType.List(IrType.I32));
    var (each, sequence, handler, used, branch, plain) = (Block(), Block(), Block(), Block(), Block(), Block());
    var function = Function([list, C],
      new IrForEachCollection(R(list), EnumerationMode.Checked, element, key, each),
      new IrForEachSequence(item, R(list), sequence),
      new IrTry(Block(), [new IrCatch(IrExceptionClass.Exception, caught, null, handler)], null),
      new IrUsing(resource, new IrNull(IrType.String), new IrMethod("Dispose", "Dispose", false), used),
      new IrIf(new IrBind(bound, new IrConst(IrType.I32, 1)), branch, null),
      new IrBlock([new IrIf(R(C), plain, null)]),
      new IrReturn(K(0)));
    var layout = new FunctionLayout(function);
    Assert.That(layout.Defined(element, ProgramPoint.Entry(each)) && layout.Defined(key, ProgramPoint.Entry(each)), Is.True);
    Assert.That(layout.Defined(item, ProgramPoint.Entry(sequence)), Is.True);
    Assert.That(layout.Defined(caught, ProgramPoint.Entry(handler)), Is.True);
    Assert.That(layout.Defined(resource, ProgramPoint.Entry(used)), Is.True);
    Assert.That(layout.Defined(bound, ProgramPoint.Entry(branch)), Is.True);
    Assert.That(layout.Defined(bound, new ProgramPoint(function.Body, 5)), Is.True);
    Assert.That(layout.Defined(element, ProgramPoint.Entry(plain)), Is.False);
    Assert.That(layout.Defined(caught, ProgramPoint.Entry(each)), Is.False);
    Assert.That(layout.Defined(list, ProgramPoint.Entry(plain)), Is.True);
  }

  /// <summary>A point is the place before a statement or the end of its block, compared by the identity of the block.</summary>
  [Test]
  public void ProgramPoints_CompareByBlockIdentity() {
    IrStmt[] statements = [new IrBreak()];
    var one = new IrBlock(statements);
    var two = new IrBlock(statements);
    Assert.That(one, Is.EqualTo(two));
    Assert.That(ProgramPoint.Entry(one), Is.Not.EqualTo(ProgramPoint.Entry(two)));
    Assert.That(ProgramPoint.Entry(one), Is.EqualTo(new ProgramPoint(one, 0)));
    Assert.That(ProgramPoint.Entry(one).GetHashCode(), Is.EqualTo(new ProgramPoint(one, 0).GetHashCode()));
    Assert.That(default(ProgramPoint).GetHashCode(), Is.EqualTo(new ProgramPoint().GetHashCode()));
    Assert.That(ProgramPoint.End(one).IsEnd, Is.True);
    Assert.That(ProgramPoint.Entry(one).IsEnd, Is.False);
    Assert.That(ProgramPoint.End(one).ToString(), Is.EqualTo("end of 1"));
    Assert.That(ProgramPoint.Entry(one).ToString(), Is.EqualTo("statement 0 of 1"));
    Assert.That(default(ProgramPoint).ToString(), Is.EqualTo("no point"));
    Assert.That(() => new ProgramPoint(null!, 0), Throws.ArgumentNullException);
    Assert.That(() => ProgramPoint.End(null!), Throws.ArgumentNullException);
    Assert.That(() => new ProgramPoint(one, -1), Throws.InstanceOf<ArgumentOutOfRangeException>());
    Assert.That(() => new ProgramPoint(one, 2), Throws.InstanceOf<ArgumentOutOfRangeException>());
  }

  /// <summary>The lattice of the constant domain.</summary>
  [Test]
  public void TheConstantDomain_IsALattice() {
    var domain = ConstantDomain.Instance;
    var (five, six) = (ConstantFact.Of(5), ConstantFact.Of(6));
    Assert.That(domain.Join(five, five), Is.EqualTo(five));
    Assert.That(domain.Join(five, six), Is.EqualTo(domain.Unknown));
    Assert.That(domain.Join(domain.Unreachable, six), Is.EqualTo(six));
    Assert.That(domain.Join(six, domain.Unreachable), Is.EqualTo(six));
    Assert.That(domain.Meet(domain.Unknown, six), Is.EqualTo(six));
    Assert.That(domain.Meet(six, domain.Unknown), Is.EqualTo(six));
    Assert.That(domain.Meet(five, six), Is.EqualTo(domain.Unreachable));
    Assert.That(domain.Widen(five, six), Is.EqualTo(domain.Unknown));
    Assert.That(domain.Implies(five, domain.Unknown) && domain.Implies(domain.Unreachable, five) && !domain.Implies(five, six), Is.True);
    Assert.That(domain.Contains(five, new IrConst(IrType.U32, 5)) && !domain.Contains(five, new IrConst(IrType.U32, 6)), Is.True);
    Assert.That(domain.Contains(domain.Unknown, 1) && !domain.Contains(domain.Unreachable, new IrConst(IrType.U32, 5)) && !domain.Contains(five, "5"), Is.True);
    Assert.That(domain.Describe(domain.Unknown) + "," + domain.Describe(domain.Unreachable), Is.EqualTo("unknown,unreachable"));
    Assert.That(ConstantFact.Domain, Is.SameAs(domain));
  }

  /// <summary>The diagnostics carry their registered codes; a fact exception needs a diagnostic.</summary>
  [Test]
  public void Diagnostics_AreRegistered() {
    Assert.That(new[] { AnalysisDiagnostics.MissingDomain.Code, AnalysisDiagnostics.Budget.Code, AnalysisDiagnostics.Unsound.Code }, Is.EqualTo(new[] { "PLY0300", "PLY0301", "PLY0302" }));
    Assert.That(() => new FactException(null!), Throws.ArgumentNullException);
    Assert.That(AnalysisDiagnostics.Describe(new IrUnary(IrUnaryOp.Negate, new IrConvert(IrType.I32, R(X), IrConversion.Wrap))), Is.EqualTo("Negate(IrConvert(x))"));
    var budget = new FactBudgetException("constant", "f");
    Assert.That((budget.Domain, budget.Function, budget.Message), Is.EqualTo(("constant", "f", "analysis 'constant' stopped at its budget in 'f'")));
  }

  /// <summary>A test-only domain named <c>known-bits</c>: no analysis on main provides it.</summary>
  private readonly record struct TestRange(bool Known) : IFact<TestRange> {

    public static TestRange Unknown => default;

    public static IFactDomain<TestRange> Domain { get; } = new TestDomain();

  }

  private sealed class TestDomain : IFactDomain<TestRange> {
    public string Name => "known-bits";
    public TestRange Unknown => default;
    public TestRange Unreachable => new(true);
    public TestRange Join(TestRange left, TestRange right) => left;
    public TestRange Meet(TestRange left, TestRange right) => left;
    public TestRange Widen(TestRange previous, TestRange next) => next;
    public bool Implies(TestRange stronger, TestRange weaker) => true;
    public bool Contains(TestRange fact, object value) => true;
    public string Describe(TestRange fact) => fact.Known ? "known" : "unknown";
  }

  /// <summary>A provider of the test domain that decides every comparison the same way.</summary>
  private sealed class FixedRange(Truth truth) : IFactProvider<TestRange>, IComparisonDecider {
    public TestRange At(IrExpr value, ProgramPoint point) => TestRange.Unknown;
    public FactExplanation Explain(IrExpr value, ProgramPoint point) => new("known-bits", [], false);
    public Truth Decide(IrExpr comparison, ProgramPoint point) => truth;
  }

  /// <summary>A constant provider that answers what it is told.</summary>
  internal sealed class FixedProvider(Func<IrExpr, ConstantFact> answer) : IFactProvider<ConstantFact> {
    public ConstantFact At(IrExpr value, ProgramPoint point) => answer(value);
    public FactExplanation Explain(IrExpr value, ProgramPoint point) => throw new FactUnavailableException("no explanation");
  }

}
