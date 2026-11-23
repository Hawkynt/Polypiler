using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Analysis.Constant;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Scev;

/// <summary>
/// Scalar evolution of the loops of a function in the structured tree IR (#350): which variables step by
/// a loop-invariant amount in every pass, the closed form <see cref="Evolution.AddRec"/> of affine values
/// of them, and how often each loop's body runs (<see cref="TripCount"/>).
/// <para>
/// An induction variable of a loop is a variable the loop writes exactly once, by <c>v = v ± s</c> or
/// <c>v++</c>/<c>v--</c> with an invariant <c>s</c>, as a statement directly in its body - which no
/// <c>continue</c> before it can skip - or in the step of an <see cref="IrFor"/>, which runs after every
/// pass. Its value where a pass starts is <c>{start, +, ±s}</c>. Sums, differences, negations, products
/// with an invariant and left shifts by a constant of such values stay recurrences; a truncation keeps
/// one modulo the narrower width; an extension keeps one only with the matching no-wrap flag and a
/// constant step; everything else is <see cref="Evolution.Unknown"/>. All arithmetic is modulo 2^w.
/// </para>
/// <para>
/// The trip count comes from the test of a <see cref="IrWhile"/> or <see cref="IrFor"/>: a comparison of a
/// recurrence of the loop with an invariant, or a conjunction of such comparisons. The counts are those
/// of the wrapping arithmetic: a count is given only when no value the test reads can wrap before the
/// loop ends, and an <c>!=</c> test only when the distance is a multiple of the step. The start and the
/// bound come from the constant and range facts of the facade at the point before the loop. A body that
/// can leave the loop otherwise - <c>break</c>, <c>return</c>, <c>throw</c>, <c>yield</c> - makes an exact
/// count a bound. No-wrap flags come only from such a count or from a checked step, which traps rather
/// than wraps on every execution that continues - never from the source language.
/// </para>
/// <para>
/// Converted from PB-Compiler's <c>IrScalarEvolution</c> (LGPL-3.0-or-later, same owner; sourcing rung 1),
/// which recognizes the header-phi recurrences of its SSA IR and simulates constant trip counts; this
/// version works on the structured loops, adds affine values, symbolic and bounded counts and no-wrap
/// flags, and computes counts in closed form.
/// </para>
/// </summary>
public sealed class ScalarEvolution {

  private readonly ValueFacts _facts;
  private readonly Dictionary<IrStmt, LoopInfo> _loops = new(ReferenceEqualityComparer.Instance);
  private readonly List<IrStmt> _order = [];
  private readonly Dictionary<IrBlock, Place> _places = new(ReferenceEqualityComparer.Instance);
  private readonly IReadOnlySet<IrLocal> _captured;
  private readonly Dictionary<IrStmt, TripCount> _counts = new(ReferenceEqualityComparer.Instance);
  private readonly HashSet<IrStmt> _counting = new(ReferenceEqualityComparer.Instance);

  /// <summary>Analyses a function with its own constant and range facts.</summary>
  /// <param name="function">The function.</param>
  public ScalarEvolution(IrFunction function)
    : this(new ValueFacts(function).Add(new ConstantAnalysis(function)).Add(new RangeAnalysis(function))) { }

  /// <summary>Analyses the function of a facade, asking it for the constant and range facts it has providers for.</summary>
  /// <param name="facts">The facade.</param>
  public ScalarEvolution(ValueFacts facts) {
    ArgumentNullException.ThrowIfNull(facts);
    this._facts = facts;
    this._captured = IrClosures.Captured(facts.Function.Body);
    this.Visit(facts.Function.Body, null);
  }

  /// <summary>The loops of the function - <see cref="IrWhile"/>, <see cref="IrDoWhile"/> and <see cref="IrFor"/> - outer before inner, in order.</summary>
  public IReadOnlyList<IrStmt> Loops => this._order;

  /// <summary>The loop that directly encloses a loop, or <see langword="null"/> for an outermost one.</summary>
  /// <param name="loop">The loop.</param>
  /// <returns>The enclosing loop.</returns>
  public IrStmt? Parent(IrStmt loop) => this.Info(loop).Parent;

  /// <summary>The induction variables of a loop, in the order of their steps.</summary>
  /// <param name="loop">The loop.</param>
  /// <returns>The variables.</returns>
  public IReadOnlyList<IrLocal> InductionVariables(IrStmt loop) => [.. this.Info(loop).Inductions.Keys];

  /// <summary>The evolution of a value read where a pass of a loop starts, before its test.</summary>
  /// <param name="value">An integer expression over the variables in scope at the loop.</param>
  /// <param name="loop">The loop.</param>
  /// <returns>The evolution.</returns>
  public Evolution Of(IrExpr value, IrStmt loop) {
    ArgumentNullException.ThrowIfNull(value);
    var info = this.Info(loop);
    return this.Flagged(this.Evaluate(value, info, Moment.Start), info);
  }

  /// <summary>The evolution of a value read at a point inside a loop's body or step, after the steps that ran before it in the pass.</summary>
  /// <param name="value">An integer expression over the variables defined at the point.</param>
  /// <param name="point">The point.</param>
  /// <param name="loop">The loop, which holds the point.</param>
  /// <returns>The evolution.</returns>
  /// <exception cref="ArgumentException">The point lies outside the loop.</exception>
  public Evolution At(IrExpr value, ProgramPoint point, IrStmt loop) {
    ArgumentNullException.ThrowIfNull(value);
    var info = this.Info(loop);
    return this.Flagged(this.Evaluate(value, info, this.Locate(info, point)), info);
  }

  /// <summary>How often the body of a loop runs.</summary>
  /// <param name="loop">The loop.</param>
  /// <returns>The trip count.</returns>
  public TripCount TripCountOf(IrStmt loop) {
    var info = this.Info(loop);
    if (this._counts.TryGetValue(loop, out var known))
      return known;

    // A count that asks for itself, through the flags of its own test, is unknown.
    if (!this._counting.Add(loop))
      return TripCount.Unknown;

    try {
      var count = info.Statement switch {
        IrWhile whileLoop => this.Count(whileLoop.Condition, info),
        IrFor forLoop => this.Count(forLoop.Condition, info),
        _ => TripCount.Unknown
      };
      if (info.Escapes)
        count = count switch {
          TripCount.Exact exact => new TripCount.Max(exact.Count),
          TripCount.Symbolic symbolic => new TripCount.Max(this.Bound(symbolic, info)),
          _ => count
        };

      return this._counts[loop] = count;
    } finally {
      this._counting.Remove(loop);
    }
  }

  // The structure of the function

  /// <summary>Where a block sits: the statement that holds it, the block of that statement, and its position there.</summary>
  private sealed record Place(IrStmt Owner, IrBlock Block, int Index);

  /// <summary>
  /// An induction variable: what each pass adds, where its step is - at a position of the body, or of the
  /// step of an <see cref="IrFor"/> - and whether the step traps rather than wraps.
  /// </summary>
  private sealed record Induction(IrExpr Step, bool Subtracts, int? Position, int? StepPosition, bool Checked);

  /// <summary>A variable the body gives its value once per pass, directly in the body at a position.</summary>
  private sealed record Definition(IrExpr Value, int Position);

  /// <summary>
  /// Where in a pass a value is read: before the statement at <see cref="Body"/> of the body, or - with
  /// <see cref="Body"/> past the body - before the statement at <see cref="Step"/> of an <see cref="IrFor"/>'s step.
  /// </summary>
  private readonly record struct Moment(int Body, int Step) {

    /// <summary>Where a pass starts, before the loop's test.</summary>
    public static Moment Start => new(0, -1);

    /// <summary>Whether the moment lies in the step, after the whole body.</summary>
    public bool InStep => this.Body == int.MaxValue;

  }

  /// <summary>What the analysis knows of one loop.</summary>
  private sealed class LoopInfo(IrStmt statement, IrBlock block, int index, IrStmt? parent) {
    public IrStmt Statement { get; } = statement;
    public IrBlock Block { get; } = block;
    public int Index { get; } = index;
    public IrStmt? Parent { get; } = parent;
    public ProgramPoint Entry => new(this.Block, this.Index);
    public IrBlock Body => this.Statement switch { IrWhile loop => loop.Body, IrDoWhile loop => loop.Body, _ => ((IrFor)this.Statement).Body };
    public HashSet<IrLocal> Written { get; } = [];
    public Dictionary<IrLocal, Induction> Inductions { get; } = [];
    public Dictionary<IrLocal, Evolution> Starts { get; } = [];
    public Dictionary<IrLocal, Definition> Definitions { get; } = [];
    public int FirstContinue { get; set; } = int.MaxValue;
    public bool Escapes { get; set; }
    public bool Prepared { get; set; }
  }

  private void Visit(IrBlock block, IrStmt? loop) {
    for (var index = 0; index < block.Statements.Count; ++index) {
      var statement = block.Statements[index];
      var inner = loop;
      if (statement is IrWhile or IrDoWhile or IrFor) {
        this._loops[statement] = new LoopInfo(statement, block, index, loop);
        this._order.Add(statement);
        inner = statement;
      }

      foreach (var child in IrTree.Blocks(statement)) {
        if (ReferenceEquals(child, statement))
          continue;

        this._places[child] = new Place(statement, block, index);
        this.Visit(child, inner);
      }

      if (statement is IrBlock nested) {
        this._places[nested] = new Place(statement, block, index);
        this.Visit(nested, inner);
      }
    }
  }

  private LoopInfo Info(IrStmt loop) {
    ArgumentNullException.ThrowIfNull(loop);
    if (!this._loops.TryGetValue(loop, out var info))
      throw new ArgumentException("The statement is no loop of the function.", nameof(loop));

    if (!info.Prepared)
      this.Prepare(info);

    return info;
  }

  /// <summary>Collects what a loop writes, how it can be left, and its induction variables.</summary>
  private void Prepare(LoopInfo info) {
    info.Prepared = true;
    var counts = new Dictionary<IrLocal, int>();
    void Write(IrLocal? local) {
      if (local is null)
        return;

      info.Written.Add(local);
      counts[local] = counts.GetValueOrDefault(local) + 1;
    }

    foreach (var expression in IrTree.Expressions(info.Statement))
      this.Writes(expression, Write);
    foreach (var block in IrTree.Blocks(info.Statement))
      this.Walk(block, Write, info, 0);

    // A variable a closure captures may change whenever the closure runs.
    foreach (var captured in this._captured)
      info.Written.Add(captured);

    // The written set is complete only now; whether a step is invariant depends on it.
    var candidates = new List<(IrLocal? Local, IrStmt Statement, int? Position)>();
    for (var position = 0; position < info.Body.Statements.Count; ++position)
      candidates.Add((Target(info.Body.Statements[position]), info.Body.Statements[position], position));
    if (info.Statement is IrFor loop)
      candidates.AddRange(loop.Step.Statements.Select(statement => (Target(statement), statement, (int?)null)));

    for (var position = 0; position < info.Body.Statements.Count && info.FirstContinue == int.MaxValue; ++position)
      if (Continues([info.Body.Statements[position]]))
        info.FirstContinue = position;

    var stepIndex = 0;
    foreach (var (local, statement, position) in candidates) {
      var atStep = position is null ? stepIndex++ : (int?)null;
      if (local is null || counts.GetValueOrDefault(local) != 1 || this._captured.Contains(local) || !local.Type.IsInteger)
        continue;

      // A step a continue can skip is no step of every pass; a definition it can skip is still one where it ran.
      if (Update(statement, local) is var (step, subtracts, trap) && this.IsInvariant(step, info)) {
        if (position is not { } at || at < info.FirstContinue)
          info.Inductions[local] = new Induction(step, subtracts, position, atStep, trap);
      }
      // What is no step is an assignment: a statement v++ always is one.
      else if (position is { } defined)
        info.Definitions[local] = new Definition(((IrAssign)statement).Value, defined);
    }

    // A declaration in the body is a definition too: a fresh variable every pass.
    for (var position = 0; position < info.Body.Statements.Count; ++position)
      if (info.Body.Statements[position] is IrDeclare { Initializer: { } initial } declare && counts.GetValueOrDefault(declare.Local) == 1
        && !this._captured.Contains(declare.Local) && declare.Local.Type.IsInteger)
        info.Definitions[declare.Local] = new Definition(initial, position);
  }

  /// <summary>The variable a statement steps, if it is <c>v = …</c> or a statement of <c>v++</c>.</summary>
  private static IrLocal? Target(IrStmt statement) => statement switch {
    IrAssign assign => assign.Local,
    IrExprStmt { Expression: IrStep { Target: IrLocalRef reference } } => reference.Local,
    _ => null
  };

  /// <summary><c>v = v ± s</c>, <c>v = s + v</c> or <c>v++</c>/<c>v--</c>: the step, whether it is subtracted, and whether it traps on overflow.</summary>
  private static (IrExpr Step, bool Subtracts, bool Checked)? Update(IrStmt statement, IrLocal local) {
    static bool Self(IrExpr expression, IrLocal local) => expression is IrLocalRef reference && reference.Local == local;
    switch (statement) {
      case IrExprStmt { Expression: IrStep step }:
        return (new IrConst(local.Type, 1), !step.Increment, step.Trap);
      case IrAssign { Value: IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.AddChecked } add }:
        var isChecked = add.Op == IrBinaryOp.AddChecked;
        return Self(add.Left, local) ? (add.Right, false, isChecked) : Self(add.Right, local) ? (add.Left, false, isChecked) : null;
      case IrAssign { Value: IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked } subtract } when Self(subtract.Left, local):
        return (subtract.Right, true, subtract.Op == IrBinaryOp.SubtractChecked);
      default:
        return null;
    }
  }

  /// <summary>Whether a statement among some, outside the loops nested in them, continues the loop that holds them.</summary>
  private static bool Continues(IEnumerable<IrStmt> statements) => statements.Any(statement => statement switch {
    IrContinue => true,
    IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence => false,
    _ => IrTree.Blocks(statement).Any(block => !ReferenceEquals(block, statement) && Continues(block.Statements)) || statement is IrBlock nested && Continues(nested.Statements)
  });

  /// <summary>Records every write of a block and whether something in it leaves the loop other than by its test.</summary>
  private void Walk(IrBlock block, Action<IrLocal?> write, LoopInfo info, int depth) {
    foreach (var statement in block.Statements) {
      switch (statement) {
        case IrAssign assign:
          write(assign.Local);
          break;
        case IrDeclare declare:
          write(declare.Local);
          break;
        case IrFieldStore store:
          write(Root(store.Target));
          break;
        case IrUsing scope:
          write(scope.Resource);
          break;
        case IrForEachSequence each:
          write(each.Element);
          break;
        case IrForEachCollection each:
          write(each.Element);
          write(each.Value);
          break;
        case IrTry attempt:
          foreach (var clause in attempt.Catches)
            write(clause.Local);
          break;
        case IrBreak when depth == 0:
        case IrReturn or IrThrow or IrRethrow or IrYield or IrYieldBreak or IrYieldFrom:
          info.Escapes = true;
          break;
      }

      foreach (var expression in IrTree.Expressions(statement))
        this.Writes(expression, write);

      // A break inside a nested loop or switch leaves that one, not this loop.
      var nested = statement is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrForEachSequence or IrSwitch ? depth + 1 : depth;
      foreach (var child in IrTree.Blocks(statement))
        if (!ReferenceEquals(child, statement))
          this.Walk(child, write, info, nested);
      if (statement is IrBlock inner)
        this.Walk(inner, write, info, depth);
    }
  }

  private void Writes(IrExpr expression, Action<IrLocal?> write) {
    foreach (var node in IrTree.Nodes(expression))
      switch (node) {
        case IrStep step:
          write(Root(step.Target));
          break;
        case IrBind bind:
          write(bind.Local);
          break;
      }
  }

  /// <summary>The variable a place is part of: itself, or the one whose field holds it; <see langword="null"/> for an array element.</summary>
  private static IrLocal? Root(IrExpr place) => place switch {
    IrLocalRef reference => reference.Local,
    IrFieldLoad load => Root(load.Target),
    _ => null
  };

  /// <summary>Whether an expression is the same in every pass: trap-free and reading only variables the loop does not write.</summary>
  private bool IsInvariant(IrExpr expression, LoopInfo info) => IrTree.Nodes(expression).All(node => node switch {
    IrConst => true,
    // Captured variables count as written: a closure may change them whenever it runs.
    IrLocalRef reference => !info.Written.Contains(reference.Local),
    IrUnary unary => unary.Op is IrUnaryOp.Negate or IrUnaryOp.BitNot,
    IrBinary binary => !IrOperators.Traps(binary.Op) && binary.Op is not (IrBinaryOp.Divide or IrBinaryOp.Remainder),
    IrConvert conversion => conversion.Contract == IrConversion.Wrap && conversion.Operand.Type.IsInteger && conversion.Type.IsInteger,
    _ => false
  });

  /// <summary>Where in a pass a point inside a loop lies: the position of the body's - or the step's - statement that holds it.</summary>
  private Moment Locate(LoopInfo info, ProgramPoint point) {
    var (block, index) = (point.Block, point.Index);
    while (true) {
      if (ReferenceEquals(block, info.Body))
        return new Moment(index, -1);
      if (info.Statement is IrFor loop && ReferenceEquals(block, loop.Step))
        return new Moment(int.MaxValue, index);
      if (!this._places.TryGetValue(block, out var place))
        throw new ArgumentException("The point lies outside the loop.", nameof(point));

      (block, index) = (place.Block, place.Index);
    }
  }

  /// <summary>Whether an induction variable has taken this pass's step at a moment.</summary>
  private static bool Stepped(Induction induction, Moment moment)
    => induction.Position is { } at ? at < moment.Body : moment.InStep && induction.StepPosition < moment.Step;

  /// <summary>Whether a definition has run in this pass at a moment: before it in the body, and in the step only when no <c>continue</c> can skip it.</summary>
  private static bool Defined(Definition definition, Moment moment, LoopInfo info)
    => definition.Position < moment.Body && (!moment.InStep || definition.Position < info.FirstContinue);

  // Evolutions

  private Evolution Evaluate(IrExpr expression, LoopInfo info, Moment moment) {
    if (!expression.Type.IsInteger)
      return Evolution.Unknown;

    switch (expression) {
      case IrConst constant:
        return new Evolution.Invariant(constant);
      case IrLocalRef reference when info.Inductions.TryGetValue(reference.Local, out var induction):
        var recurrence = this.Recurrence(reference.Local, induction, info);
        return Stepped(induction, moment) ? Add(recurrence, recurrence.Step) : recurrence;
      case IrLocalRef reference when info.Definitions.TryGetValue(reference.Local, out var definition):
        return Defined(definition, moment, info) ? this.Evaluate(definition.Value, info, new Moment(definition.Position, -1)) : Evolution.Unknown;
      case IrLocalRef reference:
        return this.IsInvariant(reference, info) ? new Evolution.Invariant(reference) : Evolution.Unknown;
      case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.AddChecked or IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked } sum when sum.Left.Type == sum.Type && sum.Right.Type == sum.Type:
        var (left, right) = (this.Evaluate(sum.Left, info, moment), this.Evaluate(sum.Right, info, moment));
        return sum.Op is IrBinaryOp.Add or IrBinaryOp.AddChecked ? Add(left, right) : Add(left, Negate(right));
      case IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked } product when product.Left.Type == product.Type && product.Right.Type == product.Type:
        var (first, second) = (this.Evaluate(product.Left, info, moment), this.Evaluate(product.Right, info, moment));
        return first is Evolution.Invariant factor ? Scale(second, factor.Value) : second is Evolution.Invariant other ? Scale(first, other.Value) : Evolution.Unknown;
      case IrBinary { Op: IrBinaryOp.ShiftLeft, Right: IrConst { Type.IsInteger: true } count } shift when count.Value >= 0 && count.Value < shift.Type.Bits:
        return Scale(this.Evaluate(shift.Left, info, moment), Constant(shift.Type, Int128.One << (int)count.Value));
      case IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.NegateChecked } negation:
        return Negate(this.Evaluate(negation.Operand, info, moment));
      case IrConvert { Contract: IrConversion.Wrap, Operand.Type.IsInteger: true } conversion:
        // An extension needs the operand's no-wrap flags, which its trip count proves.
        return Convert(this.Flagged(this.Evaluate(conversion.Operand, info, moment), info), conversion.Operand.Type, conversion.Type);
      default:
        return this.IsInvariant(expression, info) ? new Evolution.Invariant(expression) : Evolution.Unknown;
    }
  }

  /// <summary>The recurrence of an induction variable: its start where the loop is entered and its step, with the flags a checked step proves.</summary>
  private Evolution.AddRec Recurrence(IrLocal local, Induction induction, LoopInfo info) {
    if (!info.Starts.TryGetValue(local, out var start))
      info.Starts[local] = start = this.Start(local, info);

    var step = new Evolution.Invariant(induction.Subtracts ? Fold(IrBinaryOp.Subtract, Constant(local.Type, 0), induction.Step) : induction.Step);
    return new Evolution.AddRec(start, step, info.Statement, local.Type,
      induction.Checked && !local.Type.IsSigned, induction.Checked && local.Type.IsSigned);
  }

  /// <summary>
  /// The value an induction variable enters the loop with: a constant the facts know; else, in a nested
  /// loop, the evolution in the enclosing loop of what the statement right before the loop gives it;
  /// else the variable itself, read where the loop is entered.
  /// </summary>
  private Evolution Start(IrLocal local, LoopInfo info) {
    if (this.Fact<ConstantFact>(new IrLocalRef(local), info.Entry) is { IsConstant: true } constant)
      return new Evolution.Invariant(new IrConst(local.Type, constant.Value));

    if (info.Parent is { } parent && info.Index > 0
      && info.Block.Statements[info.Index - 1] switch { IrDeclare { Initializer: { } value } declare when declare.Local == local => value, IrAssign assign when assign.Local == local => assign.Value, _ => null } is { } initial) {
      var outer = this.Info(parent);
      if (this.Evaluate(initial, outer, this.Locate(outer, new ProgramPoint(info.Block, info.Index - 1))) is var evolution && evolution != Evolution.Unknown)
        return evolution;
    }

    return new Evolution.Invariant(new IrLocalRef(local));
  }

  /// <summary>A recurrence of the loop with constant start and step, with the no-wrap flags its trip count proves.</summary>
  private Evolution Flagged(Evolution evolution, LoopInfo info) {
    if (evolution is not Evolution.AddRec { Start: Evolution.Invariant start, Step: Evolution.Invariant step } recurrence
      || !ReferenceEquals(recurrence.Loop, info.Statement) || !start.IsConstant(out _) || !step.IsConstant(out _)
      || this.Passes(info) is not { } passes)
      return evolution;

    // The values the test reads run from the start to start + passes·step, monotonically; both ends decide.
    var type = recurrence.Type;
    var delta = Signed(step, type);
    var unsigned = Within(Unsigned(start, type) + passes * delta, type, false);
    var signed = Within(Signed(start, type) + passes * delta, type, true);
    return recurrence with { NoUnsignedWrap = recurrence.NoUnsignedWrap || unsigned, NoSignedWrap = recurrence.NoSignedWrap || signed };
  }

  /// <summary>The most passes that start and are then ended by the test: the bound of the trip count, if it has one.</summary>
  private BigInteger? Passes(LoopInfo info) => this.TripCountOf(info.Statement) switch {
    TripCount.Exact exact => exact.Count,
    TripCount.Max max => max.Bound,
    TripCount.Symbolic symbolic => this.Bound(symbolic, info),
    _ => null
  };

  /// <summary>
  /// The largest value a symbolic count can take, from the range of its value where the loop is entered -
  /// at most its type's maximum; no pass at all where no execution reaches the loop.
  /// </summary>
  private BigInteger Bound(TripCount.Symbolic symbolic, LoopInfo info)
    => this.Range(symbolic.Value, info.Entry) is { IsEmpty: false } range ? BigInteger.Max(0, (BigInteger)range.Max + symbolic.Offset) : 0;

  // The operands of the IR's arithmetic share its type, so the evolutions combined here do too.

  private static Evolution Add(Evolution left, Evolution right) => (left, right) switch {
    (Evolution.Invariant a, Evolution.Invariant b) => new Evolution.Invariant(Fold(IrBinaryOp.Add, a.Value, b.Value)),
    (Evolution.AddRec a, Evolution.Invariant) => a with { Start = Add(a.Start, right), NoUnsignedWrap = false, NoSignedWrap = false },
    (Evolution.Invariant, Evolution.AddRec b) => b with { Start = Add(left, b.Start), NoUnsignedWrap = false, NoSignedWrap = false },
    (Evolution.AddRec a, Evolution.AddRec b) when ReferenceEquals(a.Loop, b.Loop)
      => a with { Start = Add(a.Start, b.Start), Step = Add(a.Step, b.Step), NoUnsignedWrap = false, NoSignedWrap = false },
    _ => Evolution.Unknown
  };

  private static Evolution Negate(Evolution evolution) => evolution switch {
    Evolution.Invariant value => new Evolution.Invariant(Fold(IrBinaryOp.Subtract, Constant(value.Value.Type, 0), value.Value)),
    Evolution.AddRec recurrence => recurrence with { Start = Negate(recurrence.Start), Step = Negate(recurrence.Step), NoUnsignedWrap = false, NoSignedWrap = false },
    _ => Evolution.Unknown
  };

  private static Evolution Scale(Evolution evolution, IrExpr factor) => evolution switch {
    Evolution.Invariant value => new Evolution.Invariant(Fold(IrBinaryOp.Multiply, value.Value, factor)),
    Evolution.AddRec recurrence
      => recurrence with { Start = Scale(recurrence.Start, factor), Step = Scale(recurrence.Step, factor), NoUnsignedWrap = false, NoSignedWrap = false },
    _ => Evolution.Unknown
  };

  /// <summary>
  /// An evolution converted to another integer type: an invariant always; a recurrence modulo a narrower
  /// width, as it is at the same width, and through an extension only when its flag proves the extended
  /// values follow the extended recurrence - then its constant step is extended as the signed amount it adds.
  /// </summary>
  private static Evolution Convert(Evolution evolution, IrType from, IrType to) {
    switch (evolution) {
      case Evolution.Invariant value:
        return new Evolution.Invariant(value.Value is IrConst constant ? Constant(to, IrOperators.Number(from, constant.Value)) : new IrConvert(to, value.Value));
      case Evolution.AddRec recurrence when to.Bits <= from.Bits:
        var same = to.Bits == from.Bits;
        return recurrence with {
          Start = Convert(recurrence.Start, from, to),
          Step = Convert(recurrence.Step, from, to),
          Type = to,
          NoUnsignedWrap = same && recurrence.NoUnsignedWrap,
          NoSignedWrap = same && recurrence.NoSignedWrap
        };
      case Evolution.AddRec { Step: Evolution.Invariant step } recurrence when step.IsConstant(out _) && (from.IsSigned ? recurrence.NoSignedWrap : recurrence.NoUnsignedWrap):
        var delta = Signed(step, from);
        return recurrence with {
          Start = Convert(recurrence.Start, from, to),
          Step = new Evolution.Invariant(Constant(to, (Int128)delta)),
          Type = to,
          NoUnsignedWrap = !from.IsSigned,
          NoSignedWrap = true
        };
      default:
        return Evolution.Unknown;
    }
  }

  // Trip counts

  /// <summary>The count of a loop test: a comparison, or the smallest of a conjunction's.</summary>
  private TripCount Count(IrExpr condition, LoopInfo info) {
    switch (condition) {
      case IrBinary { Op: IrBinaryOp.LogicalAnd } conjunction:
        var (left, right) = (this.Count(conjunction.Left, info), this.Count(conjunction.Right, info));
        return (left, right) switch {
          (TripCount.Exact a, TripCount.Exact b) => new TripCount.Exact(BigInteger.Min(a.Count, b.Count)),
          _ when (Upper(left), Upper(right)) is var (a, b) && (a ?? b) is not null => new TripCount.Max(a is { } x && b is { } y ? BigInteger.Min(x, y) : (a ?? b)!.Value),
          _ => TripCount.Unknown
        };
      case IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual or IrBinaryOp.NotEqual, Left.Type.IsInteger: true } comparison
        when comparison.Left.Type == comparison.Right.Type:
        return this.Compare(comparison, info);
      case IrUnary { Op: IrUnaryOp.LogicalNot, Operand: IrBinary { Op: >= IrBinaryOp.Equal and <= IrBinaryOp.GreaterOrEqual } negated }:
        // The loop runs while the comparison fails: the negated comparison is the test.
        return Negated(negated.Op) is { } op ? this.Count(negated with { Op = op }, info) : TripCount.Unknown;
      default:
        return TripCount.Unknown;
    }

    BigInteger? Upper(TripCount count) => count switch {
      TripCount.Exact exact => exact.Count,
      TripCount.Max max => max.Bound,
      TripCount.Symbolic symbolic => this.Bound(symbolic, info),
      _ => null
    };
  }

  private TripCount Compare(IrBinary comparison, LoopInfo info) {
    var (left, right) = (this.Evaluate(comparison.Left, info, Moment.Start), this.Evaluate(comparison.Right, info, Moment.Start));
    var (op, recurrence, bound, counted) = (left, right) switch {
      (Evolution.AddRec a, Evolution.Invariant b) when ReferenceEquals(a.Loop, info.Statement) => (comparison.Op, a, b, comparison.Left),
      (Evolution.Invariant a, Evolution.AddRec b) when ReferenceEquals(b.Loop, info.Statement) => (Mirror(comparison.Op), b, a, comparison.Right),
      _ => (comparison.Op, null, null, null)
    };
    if (recurrence is not { Step: Evolution.Invariant step } || bound is null || !step.IsConstant(out _))
      return TripCount.Unknown;

    var type = comparison.Left.Type;
    var delta = Signed(step, type);
    if (delta.IsZero)
      return TripCount.Unknown;

    var whole = ValueRange.Of(type);
    var starts = recurrence.Start is Evolution.Invariant start ? this.Range(start.Value, info.Entry)
      : counted is IrLocalRef reference ? this.Range(reference, info.Entry) : whole;
    var bounds = this.Range(bound.Value, info.Entry);
    if (starts.IsEmpty || bounds.IsEmpty)
      return new TripCount.Exact(0);

    // Exact for a known start and bound; else symbolic in the bound; else the worst case of the ranges.
    if (starts.Min == starts.Max && bounds.Min == bounds.Max)
      return Passes(op, starts.Min, delta, bounds.Min, type) is { } exact ? new TripCount.Exact(exact) : TripCount.Unknown;

    if (starts.Min == starts.Max && delta.IsOne && bound.Value is not IrConst) {
      var first = (BigInteger)starts.Min;
      var symbolic = op switch {
        IrBinaryOp.Less or IrBinaryOp.NotEqual when (BigInteger)bounds.Min >= first => new TripCount.Symbolic(bound.Value, -first),
        IrBinaryOp.LessOrEqual when (BigInteger)bounds.Min >= first - 1 && bounds.Max < whole.Max => new TripCount.Symbolic(bound.Value, 1 - first),
        _ => null
      };
      if (symbolic is not null)
        return symbolic;
    }

    // A step towards the bound passes it once: the count grows with the distance, whose worst case the
    // ranges give. A step away from it never ends the loop once it runs, unless the test fails at once
    // for every start and bound the ranges allow.
    var towards = (op, delta.Sign) is (IrBinaryOp.Less or IrBinaryOp.LessOrEqual, > 0) or (IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual, < 0);
    if (!towards)
      return op switch {
        IrBinaryOp.Less when starts.Min >= bounds.Max => new TripCount.Exact(0),
        IrBinaryOp.LessOrEqual when starts.Min > bounds.Max => new TripCount.Exact(0),
        IrBinaryOp.Greater when starts.Max <= bounds.Min => new TripCount.Exact(0),
        IrBinaryOp.GreaterOrEqual when starts.Max < bounds.Min => new TripCount.Exact(0),
        _ => TripCount.Unknown
      };

    var worst = delta.Sign > 0 ? Passes(op, starts.Min, delta, bounds.Max, type) : Passes(op, starts.Max, delta, bounds.Min, type);
    return worst is { } most ? new TripCount.Max(most) : TripCount.Unknown;
  }

  /// <summary>
  /// The passes of <c>while (v op bound)</c> with <c>v = start + k·delta</c>, in mathematical integers,
  /// provided that every value the test reads - the last one included - lies within the type; otherwise,
  /// and where the loop does not end, <see langword="null"/>.
  /// </summary>
  private static BigInteger? Passes(IrBinaryOp op, Int128 start, BigInteger delta, Int128 bound, IrType type) {
    var (a, b) = ((BigInteger)start, (BigInteger)bound);
    BigInteger? count = (op, delta.Sign) switch {
      (IrBinaryOp.Less, > 0) => a >= b ? 0 : Ceiling(b - a, delta),
      (IrBinaryOp.LessOrEqual, > 0) => a > b ? 0 : BigInteger.Divide(b - a, delta) + 1,
      (IrBinaryOp.Greater, < 0) => a <= b ? 0 : Ceiling(a - b, -delta),
      (IrBinaryOp.GreaterOrEqual, < 0) => a < b ? 0 : BigInteger.Divide(a - b, -delta) + 1,
      (IrBinaryOp.Less, < 0) when a >= b => 0,
      (IrBinaryOp.LessOrEqual, < 0) when a > b => 0,
      (IrBinaryOp.Greater, > 0) when a <= b => 0,
      (IrBinaryOp.GreaterOrEqual, > 0) when a < b => 0,
      (IrBinaryOp.NotEqual, _) when a == b => 0,
      (IrBinaryOp.NotEqual, _) when BigInteger.Remainder(b - a, delta).IsZero && BigInteger.Divide(b - a, delta).Sign > 0 => BigInteger.Divide(b - a, delta),
      _ => null
    };
    return count is { } passes && Within(a + passes * delta, type, type.IsSigned) ? passes : null;
  }

  private static IrBinaryOp? Negated(IrBinaryOp op) => op switch {
    IrBinaryOp.Less => IrBinaryOp.GreaterOrEqual,
    IrBinaryOp.LessOrEqual => IrBinaryOp.Greater,
    IrBinaryOp.Greater => IrBinaryOp.LessOrEqual,
    IrBinaryOp.GreaterOrEqual => IrBinaryOp.Less,
    IrBinaryOp.Equal => IrBinaryOp.NotEqual,
    _ => null
  };

  private static BigInteger Ceiling(BigInteger dividend, BigInteger divisor) => BigInteger.Divide(dividend + divisor - 1, divisor);

  private static IrBinaryOp Mirror(IrBinaryOp op) => op switch {
    IrBinaryOp.Less => IrBinaryOp.Greater,
    IrBinaryOp.LessOrEqual => IrBinaryOp.GreaterOrEqual,
    IrBinaryOp.Greater => IrBinaryOp.Less,
    IrBinaryOp.GreaterOrEqual => IrBinaryOp.LessOrEqual,
    _ => op
  };

  // Facts and numbers

  /// <summary>The range of a value at a point: from the facade's range and constant facts, the whole type where they say nothing.</summary>
  private ValueRange Range(IrExpr value, ProgramPoint point) {
    var whole = ValueRange.Of(value.Type);
    if (value is IrConst { Type.IsInteger: true } constant)
      return ValueRange.Of(IrOperators.Number(constant.Type, constant.Value));

    if (this.Fact<ConstantFact>(value, point) is { IsConstant: true } known)
      return ValueRange.Of(IrOperators.Number(value.Type, known.Value));

    var range = this.Fact<ValueRange>(value, point) ?? ValueRange.Unknown;
    return range.IsEmpty ? range : RangeDomain.Instance.Meet(range, whole);
  }

  private TFact? Fact<TFact>(IrExpr value, ProgramPoint point) where TFact : struct, IFact<TFact> {
    if (!this._facts.Has<TFact>())
      return null;

    try {
      return this._facts.Get<TFact>(value, point);
    } catch (ArgumentException) {
      return null;
    }
  }

  private static bool Within(BigInteger value, IrType type, bool signed)
    => signed ? value >= -(BigInteger.One << (type.Bits - 1)) && value < BigInteger.One << (type.Bits - 1) : value >= 0 && value < BigInteger.One << type.Bits;

  /// <summary>The bits of a constant invariant read as an unsigned number of the type's width.</summary>
  private static BigInteger Unsigned(Evolution.Invariant value, IrType type) => (BigInteger)(ulong)((IrConst)value.Value).Value & ((BigInteger.One << type.Bits) - 1);

  /// <summary>The bits of a constant invariant read as a signed number of the type's width: for a step, the amount it adds.</summary>
  private static BigInteger Signed(Evolution.Invariant value, IrType type) {
    var bits = Unsigned(value, type);
    return bits >= BigInteger.One << (type.Bits - 1) ? bits - (BigInteger.One << type.Bits) : bits;
  }

  /// <summary>A constant of a type, the number taken modulo 2^w.</summary>
  private static IrConst Constant(IrType type, Int128 number) {
    var bits = unchecked((long)(ulong)(UInt128)(number & (Int128)ulong.MaxValue));
    return new IrConst(type, IrOperators.Wrap(type, bits));
  }

  /// <summary>An operation of two invariants, folded when both are constants.</summary>
  private static IrExpr Fold(IrBinaryOp op, IrExpr left, IrExpr right) {
    if (left is IrConst { Type.IsInteger: true } a && right is IrConst { Type.IsInteger: true } b) {
      var (x, y) = (IrOperators.Number(a.Type, a.Value), IrOperators.Number(b.Type, b.Value));
      return Constant(left.Type, op switch {
        IrBinaryOp.Add => x + y,
        IrBinaryOp.Subtract => x - y,
        _ => unchecked(x * y)
      });
    }

    // Adding 0 or multiplying by 1 needs no operation: x reads better than 0 + x or 1 * x.
    return (op, left, right) switch {
      (IrBinaryOp.Add, IrConst { Value: 0 }, _) => right,
      (IrBinaryOp.Add, _, IrConst { Value: 0 }) => left,
      (IrBinaryOp.Multiply, IrConst { Value: 1 }, _) => right,
      (IrBinaryOp.Multiply, _, IrConst { Value: 1 }) => left,
      _ => new IrBinary(op, left, right)
    };
  }

}
