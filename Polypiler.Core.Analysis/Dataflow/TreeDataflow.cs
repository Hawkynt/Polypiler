using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Analysis.Dataflow;

/// <summary>
/// A forward analysis over the structured IR of one function: it carries a <see cref="FactState{TFact}"/>
/// through the statements, joins where branches meet, iterates each loop to a fixpoint - widening after
/// <see cref="WidenAfter"/> passes - and records the state at every point of every block of the function,
/// <see langword="null"/> for a point no execution reaches. A subclass says what an expression's fact is,
/// how a branch condition refines the state and when a condition is decided.
/// </summary>
/// <remarks>
/// Soundness rules of the walk: a variable written inside an expression (a step or a binding) is Unknown
/// after the statement and makes the expression's own value Unknown; a variable a lambda captures or whose
/// address is taken is Unknown everywhere; a statement the walk does not model forgets every variable it
/// may write, before each of its blocks and after it; a catch block and a finally block start from the
/// state before the try with every variable the try may write forgotten, and a break or continue that
/// leaves a try with a finally block forgets what that block writes.
/// </remarks>
/// <typeparam name="TFact">The fact type.</typeparam>
public abstract class TreeDataflow<TFact> where TFact : IFact<TFact> {

  /// <summary>The number of passes over a loop before its head is widened.</summary>
  public const int WidenAfter = 3;

  private readonly Dictionary<ProgramPoint, FactState<TFact>?> _points = [];
  private readonly List<Context> _contexts = [];
  private readonly IReadOnlySet<IrLocal> _escaping;
  private long _budget;
  private bool _done;

  /// <summary>Prepares the analysis of a function; it runs on the first query.</summary>
  /// <param name="function">The function.</param>
  /// <param name="budget">The most statements the walk may visit, counting every pass over a loop.</param>
  protected TreeDataflow(IrFunction function, long budget) {
    ArgumentNullException.ThrowIfNull(function);
    this.Function = function;
    this.Layout = new FunctionLayout(function);
    this._budget = budget;
    this._escaping = Escaping(function);
  }

  /// <summary>The function analysed.</summary>
  public IrFunction Function { get; }

  /// <summary>The layout of the function.</summary>
  public FunctionLayout Layout { get; }

  /// <summary>The domain of the facts.</summary>
  protected static IFactDomain<TFact> Domain => TFact.Domain;

  /// <summary>
  /// The point the expression being evaluated is evaluated at: the point of the statement the walk is in,
  /// or of the query; <see langword="null"/> for the condition of a loop, which runs at the loop's head,
  /// a place that is no point of a block.
  /// </summary>
  protected ProgramPoint? Current { get; private set; }

  /// <summary>The state at a point of the function; <see langword="null"/> for a point no execution reaches.</summary>
  /// <param name="point">The point.</param>
  /// <returns>The state.</returns>
  /// <exception cref="ArgumentException">The point is not in the function.</exception>
  /// <exception cref="FactBudgetException">The walk ran out of its budget.</exception>
  public FactState<TFact>? StateAt(ProgramPoint point) {
    this.Run();
    return this._points.TryGetValue(point, out var state) ? state : throw new ArgumentException($"The point is not in a block of '{this.Function.Name}'.", nameof(point));
  }

  /// <summary>The fact of a value at a point: the domain's Unreachable where no execution comes.</summary>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The fact.</returns>
  public TFact At(IrExpr value, ProgramPoint point) {
    this.Layout.Check(value, point);
    var state = this.StateAt(point);
    this.Current = point;
    return state is not null ? this.Evaluate(value, state) : Domain.Unreachable;
  }

  /// <summary>The statements the fact of a value at a point rests on: those of every variable it reads.</summary>
  /// <param name="value">The value.</param>
  /// <param name="point">The point.</param>
  /// <returns>The explanation.</returns>
  public FactExplanation Explain(IrExpr value, ProgramPoint point) {
    this.Layout.Check(value, point);
    var state = this.StateAt(point);
    var seen = new HashSet<IrStmt>(ReferenceEqualityComparer.Instance);
    IReadOnlyList<IrStmt> because = state is null ? [] : [.. IrTree.Nodes(value).OfType<IrLocalRef>().SelectMany(reference => state.Because(reference.Local)).Where(seen.Add)];
    return new FactExplanation(Domain.Name, because, false);
  }

  /// <summary>The fact of an expression in a reachable state; the expression writes no variable.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="state">The state.</param>
  /// <returns>The fact.</returns>
  protected abstract TFact Evaluate(IrExpr expression, FactState<TFact> state);

  /// <summary>Whether a condition is decided in a state; by default never.</summary>
  /// <param name="condition">The condition, which writes no variable.</param>
  /// <param name="state">The state.</param>
  /// <returns>The answer.</returns>
  protected virtual Truth Test(IrExpr condition, FactState<TFact> state) => Truth.Unknown;

  /// <summary>The state on the edge where a condition had an outcome; by default the state itself.</summary>
  /// <param name="condition">The condition, which writes no variable.</param>
  /// <param name="outcome">The outcome of the edge.</param>
  /// <param name="state">The state before the edge.</param>
  /// <param name="branch">The statement that branches, for the explanation.</param>
  /// <returns>The refined state.</returns>
  protected virtual FactState<TFact> Refine(IrExpr condition, bool outcome, FactState<TFact> state, IrStmt branch) => state;

  /// <summary>The fact of a parameter at the entry of the function; by default the domain's Unknown.</summary>
  /// <param name="parameter">The parameter.</param>
  /// <returns>The fact.</returns>
  protected virtual TFact Parameter(IrLocal parameter) => Domain.Unknown;

  /// <summary>The variables an expression writes: the targets of its steps and its bindings.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>The variables, empty for an expression that writes none.</returns>
  public static IReadOnlyList<IrLocal> Writes(IrExpr expression) => [.. IrTree.Nodes(expression).Select(node => node switch {
    IrStep { Target: IrLocalRef target } => target.Local,
    IrBind bind => bind.Local,
    _ => null
  }).OfType<IrLocal>()];

  private void Run() {
    if (this._done)
      return;

    var state = new FactState<TFact>(this._escaping);
    foreach (var parameter in this.Function.Parameters)
      state = state.With(parameter, this.Parameter(parameter), []);

    this.Block(this.Function.Body, state);
    this._done = true;
  }

  private FactState<TFact>? Block(IrBlock block, FactState<TFact>? state) {
    for (var index = 0; index < block.Statements.Count; ++index) {
      var point = new ProgramPoint(block, index);
      this._points[point] = state;
      this.Current = point;
      state = this.Statement(block.Statements[index], state);
    }

    this._points[ProgramPoint.End(block)] = state;
    return state;
  }

  private FactState<TFact>? Statement(IrStmt statement, FactState<TFact>? state) {
    if (--this._budget < 0)
      throw new FactBudgetException(Domain.Name, this.Function.Name);

    if (state is null) {
      foreach (var inner in IrTree.Blocks(statement))
        this.Block(inner, null);

      return null;
    }

    switch (statement) {
      case IrBlock block:
        return this.Block(block, state);
      case IrDeclare { Initializer: null } declare:
        return state.Without(declare.Local);
      case IrDeclare declare:
        return this.Assign(declare.Local, declare.Initializer, state, statement);
      case IrAssign assign:
        return this.Assign(assign.Local, assign.Value, state, statement);
      case IrIf conditional:
        return this.If(conditional, state);
      case IrWhile loop:
        return this.Loop(loop, loop.Condition, loop.Body, null, state);
      case IrFor loop:
        return this.Loop(loop, loop.Condition, loop.Body, loop.Step, state);
      case IrDoWhile loop:
        return this.DoWhile(loop, state);
      case IrForEachCollection loop:
        return this.ForEach(loop, [loop.Element, loop.Value], loop.Body, this.After(loop.Collection, state));
      case IrForEachSequence loop:
        return this.ForEach(loop, [loop.Element], loop.Body, this.After(loop.Sequence, state));
      case IrSwitch choice:
        return this.Switch(choice, this.After(choice.Value, state));
      case IrUsing scope:
        return this.Block(scope.Body, this.Assign(scope.Resource, scope.Initializer, state, statement));
      case IrTry attempt:
        return this.Try(attempt, state);
      case IrBreak:
        this.Leave(state, context => context.Kind != ContextKind.Finally, (context, left) => context.Breaks = FactState<TFact>.Join(context.Breaks, left));
        return null;
      case IrContinue:
        this.Leave(state, context => context.Kind == ContextKind.Loop, (context, left) => context.Continues = FactState<TFact>.Join(context.Continues, left));
        return null;
      case IrReturn or IrThrow or IrRethrow or IrYieldBreak:
        return null;
      default:
        return this.Unmodelled(statement, state);
    }
  }

  /// <summary>A statement the walk does not model: every variable it may write is forgotten before each of its blocks and after it.</summary>
  private FactState<TFact> Unmodelled(IrStmt statement, FactState<TFact> state) {
    var forgotten = state.Without(Assigned(statement));
    foreach (var inner in IrTree.Blocks(statement))
      this.Block(inner, forgotten);

    return forgotten;
  }

  private FactState<TFact> Assign(IrLocal local, IrExpr value, FactState<TFact> state, IrStmt statement) {
    var writes = Writes(value);
    if (writes.Count > 0)
      return state.Without(writes).Without(local);

    var because = IrTree.Nodes(value).OfType<IrLocalRef>().SelectMany(reference => state.Because(reference.Local)).Append(statement);
    return state.With(local, this.Evaluate(value, state), because);
  }

  /// <summary>The state after an expression has been evaluated for its side effects.</summary>
  private FactState<TFact> After(IrExpr expression, FactState<TFact> state) => state.Without(Writes(expression));

  /// <summary>The states on both edges of a condition: unreachable where it is decided the other way.</summary>
  private (FactState<TFact>? WhenTrue, FactState<TFact>? WhenFalse) Branch(IrExpr condition, FactState<TFact> state, IrStmt branch) {
    if (Writes(condition).Count > 0) {
      var after = this.After(condition, state);
      return (after, after);
    }

    var truth = this.Test(condition, state);
    return (truth == Truth.False ? null : this.Refine(condition, true, state, branch), truth == Truth.True ? null : this.Refine(condition, false, state, branch));
  }

  private FactState<TFact>? If(IrIf conditional, FactState<TFact> state) {
    var (whenTrue, whenFalse) = this.Branch(conditional.Condition, state, conditional);
    var then = this.Block(conditional.Then, whenTrue);
    var otherwise = conditional.Else is { } alternative ? this.Block(alternative, whenFalse) : whenFalse;
    return FactState<TFact>.Join(then, otherwise);
  }

  /// <summary>A loop that tests before each pass, with the step of an <see cref="IrFor"/> after each pass.</summary>
  private FactState<TFact>? Loop(IrStmt loop, IrExpr condition, IrBlock body, IrBlock? step, FactState<TFact> entry) {
    FactState<TFact>? head = entry;
    for (var pass = 0; ; ++pass) {
      this.Current = null;
      var (whenTrue, whenFalse) = this.Branch(condition, head!, loop);
      var context = this.Enter(ContextKind.Loop);
      var ended = this.Block(body, whenTrue);
      this.Exit();
      var back = FactState<TFact>.Join(ended, context.Continues);
      if (step is not null)
        back = this.Block(step, back);

      var next = this.Next(head, FactState<TFact>.Join(entry, back), pass);
      if (FactState<TFact>.Same(next, head))
        return FactState<TFact>.Join(whenFalse, context.Breaks);

      head = next;
    }
  }

  private FactState<TFact>? DoWhile(IrDoWhile loop, FactState<TFact> entry) {
    FactState<TFact>? head = entry;
    for (var pass = 0; ; ++pass) {
      var context = this.Enter(ContextKind.Loop);
      var ended = this.Block(loop.Body, head);
      this.Exit();
      var tested = FactState<TFact>.Join(ended, context.Continues);
      this.Current = null;
      var (whenTrue, whenFalse) = tested is null ? (null, null) : this.Branch(loop.Condition, tested, loop);
      var next = this.Next(head, FactState<TFact>.Join(entry, whenTrue), pass);
      if (FactState<TFact>.Same(next, head))
        return FactState<TFact>.Join(whenFalse, context.Breaks);

      head = next;
    }
  }

  private FactState<TFact>? ForEach(IrStmt loop, IReadOnlyList<IrLocal?> elements, IrBlock body, FactState<TFact> entry) {
    FactState<TFact>? head = entry;
    for (var pass = 0; ; ++pass) {
      var context = this.Enter(ContextKind.Loop);
      var ended = this.Block(body, head!.Without(elements.OfType<IrLocal>()));
      this.Exit();
      var next = this.Next(head, FactState<TFact>.Join(entry, FactState<TFact>.Join(ended, context.Continues)), pass);
      if (FactState<TFact>.Same(next, head))
        return FactState<TFact>.Join(head, context.Breaks);

      head = next;
    }
  }

  /// <summary>The next state of a loop head: widened after <see cref="WidenAfter"/> passes.</summary>
  private FactState<TFact>? Next(FactState<TFact>? head, FactState<TFact>? joined, int pass)
    => pass < WidenAfter ? joined : FactState<TFact>.Widen(head, joined);

  private FactState<TFact>? Switch(IrSwitch choice, FactState<TFact> state) {
    var context = this.Enter(ContextKind.Switch);
    FactState<TFact>? ended = choice.Sections.Any(section => section.IsDefault) ? null : state;
    foreach (var section in choice.Sections)
      ended = FactState<TFact>.Join(ended, this.Block(section.Body, state));

    this.Exit();
    return FactState<TFact>.Join(ended, context.Breaks);
  }

  private FactState<TFact>? Try(IrTry attempt, FactState<TFact> state) {
    var everything = Assigned(attempt);
    if (attempt.Finally is { } final)
      this.Enter(ContextKind.Finally).Forgets = Assigned(final);

    var ended = this.Block(attempt.Body, state);
    var caught = state.Without(Assigned(attempt.Body));
    foreach (var clause in attempt.Catches) {
      var entered = clause.Local is { } local ? caught.Without(local) : caught;
      if (clause.Filter is { } filter)
        entered = this.After(filter, entered);

      ended = FactState<TFact>.Join(ended, this.Block(clause.Body, entered));
    }

    if (attempt.Finally is null)
      return ended;

    this.Exit();
    var finished = this.Block(attempt.Finally, state.Without(everything));
    return ended is null ? null : finished;
  }

  private Context Enter(ContextKind kind) {
    var context = new Context(kind);
    this._contexts.Add(context);
    return context;
  }

  private void Exit() => this._contexts.RemoveAt(this._contexts.Count - 1);

  /// <summary>Leaves through the contexts up to the innermost target, forgetting what each finally block on the way writes.</summary>
  private void Leave(FactState<TFact> state, Func<Context, bool> target, Action<Context, FactState<TFact>> arrive) {
    for (var index = this._contexts.Count - 1; index >= 0; --index) {
      var context = this._contexts[index];
      if (context.Kind == ContextKind.Finally)
        state = state.Without(context.Forgets);

      if (target(context)) {
        arrive(context, state);
        return;
      }
    }
  }

  /// <summary>Every variable a statement may write, in any of its parts.</summary>
  public static IReadOnlyList<IrLocal> Assigned(IrStmt statement) {
    var found = new List<IrLocal>();
    void Walk(IrStmt current) {
      switch (current) {
        case IrDeclare declare:
          found.Add(declare.Local);
          break;
        case IrAssign assign:
          found.Add(assign.Local);
          break;
        case IrForEachCollection loop:
          found.AddRange(new[] { loop.Element, loop.Value }.OfType<IrLocal>());
          break;
        case IrForEachSequence loop:
          found.Add(loop.Element);
          break;
        case IrUsing scope:
          found.Add(scope.Resource);
          break;
        case IrTry attempt:
          found.AddRange(attempt.Catches.Select(clause => clause.Local).OfType<IrLocal>());
          break;
      }

      foreach (var expression in IrTree.Expressions(current))
        found.AddRange(Writes(expression));

      foreach (var inner in IrTree.Blocks(current))
        if (ReferenceEquals(inner, current))
          foreach (var nested in inner.Statements)
            Walk(nested);
        else
          Walk(inner);
    }

    Walk(statement);
    return found;
  }

  /// <summary>The variables of a function that change behind the walk's back: captured by a lambda, or with their address taken.</summary>
  private static HashSet<IrLocal> Escaping(IrFunction function) {
    var escaping = new HashSet<IrLocal>();
    foreach (var node in IrTree.Nodes(function.Body)) {
      if (node is IrLambda lambda)
        escaping.UnionWith(lambda.Captures);

      if (node is IrIntrinsic { Op: IrIntrinsicOp.AddressOf, Arguments: [IrLocalRef target] })
        escaping.Add(target.Local);
    }

    return escaping;
  }

  private enum ContextKind { Loop, Switch, Finally }

  /// <summary>A loop, switch or finally block the walk is inside: the states its breaks and continues leave with, and what a finally block writes.</summary>
  private sealed class Context(ContextKind kind) {

    public ContextKind Kind { get; } = kind;

    public FactState<TFact>? Breaks { get; set; }

    public FactState<TFact>? Continues { get; set; }

    public IReadOnlyList<IrLocal> Forgets { get; set; } = [];

  }

}
