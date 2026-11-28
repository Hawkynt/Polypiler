using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Specialize;

public static partial class DeDynamicize {

  /// <summary>A call of a function of the module, and the clone it goes to; <see langword="null"/> for the function itself.</summary>
  private sealed record CallSite(string Function, Signature? Target, IReadOnlyList<DynamicFact> Arguments);

  /// <summary>A loop or a switch the walk is in: the states its breaks and, for a loop, its continues leave with.</summary>
  private sealed class Frame(bool loop) {

    public bool IsLoop { get; } = loop;

    public List<DynamicState<IrLocal>> Breaks { get; } = [];

    public List<DynamicState<IrLocal>> Continues { get; } = [];

  }

  /// <summary>
  /// The forward analysis of one function or clone: the state of the variant places before every
  /// statement, at every loop's condition and where each local was assigned or read; and from them the
  /// locals it types, and the function with them typed.
  /// </summary>
  private sealed partial class Walker {

    private readonly Program _program;
    private readonly DynamicFacts _domain;
    private readonly IDynamicSemantics _table;
    private readonly HashSet<IrLocal> _tracked = [];
    private readonly HashSet<IrLocal> _escaping;
    private readonly Dictionary<IrLocal, DynamicFact> _reaching = [];
    private readonly Dictionary<IrLocal, DynamicFact> _assigned = [];
    private readonly HashSet<IrLocal> _impure = [];
    private readonly Dictionary<IrLocal, HashSet<DynamicType>> _values = [];
    private readonly Dictionary<IrLocal, (DynamicFact Fact, string Where)> _first = [];
    private readonly Dictionary<IrLocal, string> _reasons = [];
    private readonly Dictionary<IrStmt, DynamicState<IrLocal>> _before = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrStmt, DynamicState<IrLocal>> _conditions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrStmt, DynamicState<IrLocal>> _heads = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrStmt, (bool Then, bool Else)> _reached = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrCall, List<DynamicFact>> _arguments = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Frame> _frames = new();
    private readonly Stack<List<DynamicState<IrLocal>>> _tries = new();
    private readonly DynamicState<IrLocal> _entry;

    public Walker(Program program, IrFunction function, Signature? signature) {
      this._program = program;
      this._domain = program.Domain;
      this._table = program.Semantics;
      this.Function = function;
      this.Signature = signature;
      this._escaping = Escaping(function.Body);
      var facts = new List<KeyValuePair<IrLocal, DynamicFact>>();
      for (var index = 0; index < function.Parameters.Count; ++index) {
        var parameter = function.Parameters[index];
        if (!parameter.Type.IsVariant)
          continue;
        this._tracked.Add(parameter);
        facts.Add(new(parameter, signature?.Parameters[index] is { } exact ? DynamicFact.Proven(exact) : this._domain.Unknown));
      }

      foreach (var local in function.Locals.Where(local => local.Type.IsVariant && !function.Parameters.Contains(local))) {
        this._tracked.Add(local);
        facts.Add(new(local, DynamicFact.Proven(DynamicType.OfTag(IrVariantTag.Empty))));
      }

      this._entry = new(facts, this._domain.Unknown);
    }

    public IrFunction Function { get; }

    public Signature? Signature { get; }

    /// <summary>The join of every value the function returns; the dynamic fact for a function that does not return a variant.</summary>
    public DynamicFact Returned { get; private set; }

    /// <summary>The calls of functions of the module, and where each goes, after <see cref="Run"/>.</summary>
    public Dictionary<IrCall, CallSite> Calls { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The locals and parameters the walk types: each with its new local and its exact element.</summary>
    private Dictionary<IrLocal, (IrLocal Local, DynamicExact Type)> Retyped { get; } = [];

    /// <summary>The type the function or clone returns.</summary>
    private IrType ReturnType { get; set; } = IrType.Void;

    /// <summary>The locals the body binds, steps, captures or iterates with, or writes in a statement the walk does not model: they stay as they are.</summary>
    private static HashSet<IrLocal> Escaping(IrBlock body) {
      var escaping = new HashSet<IrLocal>();
      foreach (var node in IrTree.Nodes(body)) {
        if (node is IrBind bind)
          escaping.Add(bind.Local);
        else if (node is IrStep { Target: IrLocalRef target })
          escaping.Add(target.Local);
        else if (node is IrLambda lambda)
          escaping.UnionWith(lambda.Captures.Concat(IrTree.Nodes(lambda.Body).OfType<IrLocalRef>().Select(reference => reference.Local)));
      }

      void Visit(IrStmt statement) {
        if (statement is IrFor { PerIteration.Count: > 0 } loop)
          escaping.UnionWith(loop.PerIteration);
        foreach (var block in IrTree.Blocks(statement))
          foreach (var inner in block.Statements)
            Visit(inner);
      }

      Visit(body);
      return escaping;
    }

    public void Run() {
      this.Returned = this._domain.Unreachable;
      this.Block(this.Function.Body, this._entry);
      if (!this.Function.ReturnType.IsVariant)
        this.Returned = DynamicFact.Proven(Program.Exact(this.Function.ReturnType));

      foreach (var (call, arguments) in this._arguments) {
        var (_, clone) = this._program.Call(call.Function, arguments);
        this.Calls[call] = new(call.Function, clone, arguments);
      }

      // A local every read of which sees one exact type is that type; an assignment of another value
      // reaches no read, so it is a dead store, which the rewrite keeps only for its effects. A local
      // no read sees is the one exact type of every value assigned it but the initial Empty.
      foreach (var local in this._tracked.Where(local => !this._escaping.Contains(local) && !this.Function.Parameters.Contains(local) && !this._impure.Contains(local))) {
        var values = this._values.GetValueOrDefault(local, []);
        var fact = this._reaching.TryGetValue(local, out var read) ? read
          : DynamicFact.Proven(values.Where(value => value != Unassigned).Aggregate(DynamicType.Nothing, (joined, value) => DynamicType.Join(joined, value, this._domain.Policy)));
        if (fact is { IsStatic: true, Type.Plain: DynamicExact { BoxTag: not null } exact }
            && values.All(value => value.Plain == exact || DynamicType.Meet(value.Plain, exact, this._domain.Policy) is DynamicNothing))
          this.Retyped[local] = (local with { Type = exact.Type }, exact);
      }

      if (this.Signature is { } signature)
        for (var index = 0; index < this.Function.Parameters.Count; ++index)
          if (signature.Parameters[index] is { } exact)
            this.Retyped[this.Function.Parameters[index]] = (this.Function.Parameters[index] with { Type = exact.Type }, exact);

      this.ReturnType = this.Signature is not null && this.Function.ReturnType.IsVariant && this.Returned is { IsStatic: true, Type.Plain: DynamicExact { BoxTag: not null } result }
        ? result.Type : this.Function.ReturnType;
    }

    /// <summary>What the walk found, for a frontend that writes the typing itself.</summary>
    public DynamicFunctionTyping Typing(string name, bool kept, Dictionary<string, string> names) {
      IReadOnlyDictionary<IrLocal, DynamicType> Plain(DynamicState<IrLocal> state)
        => this._tracked.ToDictionary(local => local, local => state[local].Certainty == Certainty.Dynamic ? DynamicType.Variant : state[local].Type.Plain);
      return new(this.Function.Name, name, this.Signature?.Parameters, kept,
        this.Retyped.ToDictionary(pair => pair.Key, pair => pair.Value.Type),
        this._tracked.ToDictionary(local => local, local => this._escaping.Contains(local) ? DynamicType.Variant : this._assigned.GetValueOrDefault(local, this._domain.Unreachable).Type.Plain),
        this._heads.ToDictionary<KeyValuePair<IrStmt, DynamicState<IrLocal>>, IrStmt, IReadOnlyDictionary<IrLocal, DynamicType>>(pair => pair.Key, pair => Plain(pair.Value), ReferenceEqualityComparer.Instance),
        this.Calls.ToDictionary<KeyValuePair<IrCall, CallSite>, IrCall, string>(pair => pair.Key, pair => pair.Value.Target is { } clone ? names[Program.Key(pair.Value.Function, clone)] : pair.Value.Function, ReferenceEqualityComparer.Instance),
        this.Returned with { Type = this.Returned.Type.Plain }, this.ReturnType);
    }

    /// <summary>The element of a local that holds the language's value of no assignment yet, Visual Basic's <c>Empty</c>.</summary>
    private static readonly DynamicType Unassigned = DynamicType.OfTag(IrVariantTag.Empty);

    #region Facts

    /// <summary>The fact of an expression in a state: of its tag for a variant, of its value for another type.</summary>
    private DynamicFact Eval(IrExpr expression, DynamicState<IrLocal> state) {
      if (this._table.Classify(expression) is { } call)
        return DynamicFact.Proven(this._table.Result(call.Operation, this.Eval(call.Left, state).Type, call.Right is null ? DynamicType.Nothing : this.Eval(call.Right, state).Type));

      switch (expression) {
        case IrVariantOf { Value: { Type.IsInteger: true } payload } made when IrVariants.PayloadType(made.Tag) == payload.Type && Constant(payload) is { } value:
          return DynamicFact.Proven(DynamicType.Exact(payload.Type, made.Tag, ValueRange.Of(value)));
        case IrVariantOf made:
          return DynamicFact.Proven(DynamicType.OfTag(made.Tag));
        case { Type.IsInteger: true } when Constant(expression) is { } value:
          return DynamicFact.Proven(DynamicType.Exact(expression.Type, null, ValueRange.Of(value)));
        case IrLocalRef reference when this._tracked.Contains(reference.Local):
          return state[reference.Local];
        case IrConditional choice:
          return this._domain.Join(this.Eval(choice.WhenTrue, state), this.Eval(choice.WhenFalse, state));
        case IrCall direct when this._program.Functions.ContainsKey(direct.Function) && !this._table.IsHelper(direct.Function):
          return this._program.Call(direct.Function, [.. direct.Arguments.Select(argument => this.Eval(argument, state))], register: false).Result;
        case { Type.IsVariant: true }:
          return this._domain.Unknown;
        default:
          return DynamicFact.Proven(Program.Exact(expression.Type));
      }
    }

    /// <summary>
    /// The value of an integer expression of constants - negated, added, subtracted, multiplied or
    /// converted, as a frontend writes <c>-1</c> - where every step fits its type; <see langword="null"/>
    /// for every other expression.
    /// </summary>
    private static Int128? Constant(IrExpr expression) {
      Int128? value = expression switch {
        IrConst { Type.IsInteger: true } constant => IrOperators.Number(constant.Type, constant.Value),
        IrUnary { Op: IrUnaryOp.Negate or IrUnaryOp.NegateChecked } negation => -Constant(negation.Operand),
        IrConvert { Operand.Type.IsInteger: true } conversion => Constant(conversion.Operand),
        IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.AddChecked } sum => Constant(sum.Left) + Constant(sum.Right),
        IrBinary { Op: IrBinaryOp.Subtract or IrBinaryOp.SubtractChecked } difference => Constant(difference.Left) - Constant(difference.Right),
        IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked } product => Constant(product.Left) * Constant(product.Right),
        _ => null
      };
      return value is { } number && expression.Type.IsInteger && ValueRange.Of(expression.Type).Contains(number) ? number : null;
    }

    /// <summary>Records what an expression reads: the facts of the tracked locals, and the arguments of the calls of the module's functions.</summary>
    private void Reads(IrExpr expression, DynamicState<IrLocal> state) {
      foreach (var node in IrTree.Nodes(expression)) {
        if (node is IrLocalRef reference && this._tracked.Contains(reference.Local))
          this.Reach(reference.Local, state[reference.Local], null);
        else if (node is IrCall call && this._program.Functions.ContainsKey(call.Function) && !this._table.IsHelper(call.Function)) {
          var facts = call.Arguments.Select(argument => this.Eval(argument, state)).ToList();
          this._arguments[call] = this._arguments.TryGetValue(call, out var known) ? [.. known.Zip(facts, this._domain.Join)] : facts;
        }
      }
    }

    /// <summary>
    /// Joins a fact into what a local holds where it is read, or into what it is assigned; keeps the
    /// first assignment and the one whose join made the assigned values a variant.
    /// </summary>
    private void Reach(IrLocal local, DynamicFact fact, IrStmt? assignment) {
      if (assignment is null) {
        this._reaching[local] = this._domain.Join(this._reaching.GetValueOrDefault(local, this._domain.Unreachable), fact);
        return;
      }

      (this._values.TryGetValue(local, out var values) ? values : this._values[local] = []).Add(fact.Type);
      var before = this._assigned.GetValueOrDefault(local, this._domain.Unreachable);
      var after = this._domain.Join(before, fact);
      this._assigned[local] = after;
      var where = Where(assignment);
      if (!this._first.ContainsKey(local) && Present(fact.Type) is not DynamicNothing)
        this._first[local] = (fact, where);
      if (after.Type.Level >= 5 && before.Type.Level < 5 && !this._reasons.ContainsKey(local)) {
        var first = this._first.TryGetValue(local, out var known) ? known : (Fact: fact, Where: where);
        this._reasons[local] = fact.Type is DynamicVariant ? $"assigned a value of any type{where}" : $"assigned {Present(first.Fact.Type)}{first.Where} and {Present(fact.Type)}{where}";
      }
    }

    /// <summary>An element without its missing values, as a reason names it.</summary>
    private static DynamicType Present(DynamicType type) => type is DynamicOptional optional ? optional.Value : type;

    /// <summary>Where a statement came from, as <c> at 12:5</c>; empty where nothing recorded it.</summary>
    private static string Where(IrStmt statement) => statement.Origins.OfType<SourceSpan>().FirstOrDefault() is { } span ? $" at {span.StartLine}:{span.StartColumn}" : "";

    #endregion

    #region Statements

    private DynamicState<IrLocal>? Join(IEnumerable<DynamicState<IrLocal>?> states) {
      DynamicState<IrLocal>? joined = null;
      foreach (var state in states.OfType<DynamicState<IrLocal>>())
        joined = joined is null ? state : joined.Join(state, this._domain);
      return joined;
    }

    private void Record(Dictionary<IrStmt, DynamicState<IrLocal>> table, IrStmt statement, DynamicState<IrLocal> state)
      => table[statement] = table.TryGetValue(statement, out var known) ? known.Join(state, this._domain) : state;

    private DynamicState<IrLocal>? Block(IrBlock block, DynamicState<IrLocal>? state) {
      foreach (var statement in block.Statements) {
        if (state is null)
          return null;
        state = this.Statement(statement, state);
      }

      return state;
    }

    private DynamicState<IrLocal>? Statement(IrStmt statement, DynamicState<IrLocal> state) {
      this.Record(this._before, statement, state);
      if (this._tries.TryPeek(out var attempt))
        attempt.Add(state);
      var after = this.Transfer(statement, state);
      if (after is not null && this._tries.TryPeek(out attempt))
        attempt.Add(after);
      return after;
    }

    private DynamicState<IrLocal>? Transfer(IrStmt statement, DynamicState<IrLocal> state) {
      switch (statement) {
        case IrBlock block:
          return this.Block(block, state);
        case IrAssign assign:
          this.Reads(assign.Value, state);
          return this.Assign(assign.Local, assign.Value, state, assign);
        case IrDeclare declare:
          if (declare.Initializer is null)
            return state;
          if (!IrShape.IsSideEffectFree(declare.Initializer) || !IrShape.IsTrapFree(declare.Initializer))
            this._impure.Add(declare.Local);
          this.Reads(declare.Initializer, state);
          return this.Assign(declare.Local, declare.Initializer, state, declare);
        case IrIf conditional:
          return this.If(conditional, state);
        case IrWhile loop:
          return this.Loop(loop, state, loop.Condition, null, loop.Body, null);
        case IrFor loop:
          return this.Loop(loop, state, loop.Condition, null, loop.Body, loop.Step);
        case IrDoWhile loop:
          return this.Loop(loop, state, null, loop.Condition, loop.Body, null);
        case IrSwitch choice:
          return this.Switch(choice, state);
        case IrBreak:
          this._frames.Peek().Breaks.Add(state);
          return null;
        case IrContinue:
          this._frames.First(frame => frame.IsLoop).Continues.Add(state);
          return null;
        case IrReturn result:
          if (result.Value is { } value) {
            this.Reads(value, state);
            this.Returned = this._domain.Join(this.Returned, this.Eval(value, state));
          }

          return null;
        case IrThrow thrown:
          this.Reads(thrown.Exception, state);
          return null;
        case IrRethrow or IrYieldBreak:
          return null;
        case IrTry attempt:
          return this.Try(attempt, state);
        case IrExprStmt or IrStore or IrFieldStore or IrGlobalAssign or IrWriteLine or IrYield or IrYieldFrom:
          foreach (var expression in IrTree.Expressions(statement))
            this.Reads(expression, state);
          return state;
        default:
          return this.Opaque(statement, state);
      }
    }

    private DynamicState<IrLocal> Assign(IrLocal local, IrExpr value, DynamicState<IrLocal> state, IrStmt statement) {
      if (!this._tracked.Contains(local))
        return state;
      var fact = this.Eval(value, state);
      this.Reach(local, fact, statement);
      return state.With(local, fact);
    }

    private DynamicState<IrLocal>? If(IrIf conditional, DynamicState<IrLocal> state) {
      this.Reads(conditional.Condition, state);
      var then = this.Refine(conditional.Condition, state, true);
      var otherwise = this.Refine(conditional.Condition, state, false);
      var reached = this._reached.GetValueOrDefault(conditional);
      this._reached[conditional] = (reached.Then || then is not null, reached.Else || otherwise is not null);
      var end = then is null ? null : this.Block(conditional.Then, then);
      var other = otherwise is null ? null : conditional.Else is null ? otherwise : this.Block(conditional.Else, otherwise);
      return this.Join([end, other]);
    }

    /// <summary>
    /// A loop: its head joins the state before it with those its passes end in, until they no longer
    /// grow; after <see cref="DeDynamicizeOptions.MostRounds"/> passes every place it changes is dynamic,
    /// and one more pass records the states of that head.
    /// </summary>
    private DynamicState<IrLocal>? Loop(IrStmt loop, DynamicState<IrLocal> entry, IrExpr? test, IrExpr? testAfter, IrBlock body, IrBlock? step) {
      var head = entry;
      DynamicState<IrLocal>? exit;
      for (var round = 0; ; ++round) {
        var frame = new Frame(true);
        this._frames.Push(frame);
        DynamicState<IrLocal>? next;
        if (test is not null) {
          this.Record(this._conditions, loop, head);
          this.Reads(test, head);
          var end = this.Block(body, this.Refine(test, head, true));
          var passed = this.Join([end, .. frame.Continues]);
          next = step is null || passed is null ? passed : this.Block(step, passed);
          exit = this.Join([this.Refine(test, head, false), .. frame.Breaks]);
        } else {
          var end = this.Join([this.Block(body, head), .. frame.Continues]);
          if (end is not null) {
            this.Record(this._conditions, loop, end);
            this.Reads(testAfter!, end);
          }

          next = end is null ? null : this.Refine(testAfter!, end, true);
          exit = this.Join([end is null ? null : this.Refine(testAfter!, end, false), .. frame.Breaks]);
        }

        this._frames.Pop();
        var grown = next is null ? head : head.Join(next, this._domain, widen: true);
        if (grown.SameAs(head)) {
          this.Record(this._heads, loop, head);
          return exit;
        }
        if (round >= this._program.Options.MostRounds) {
          // Out of passes: what the loop changes may hold anything; one more pass records it.
          var dynamic = head.Places.Where(place => grown[place] != head[place]).Aggregate(grown, (known, place) => known.With(place, this._domain.Unknown));
          foreach (var place in head.Places.Where(place => grown[place] != head[place]))
            this.Reach(place, this._domain.Unknown, null);
          if (dynamic.SameAs(head)) {
            this.Record(this._heads, loop, head);
            return exit;
          }
          head = dynamic;
          continue;
        }

        head = grown;
      }
    }

    private DynamicState<IrLocal>? Switch(IrSwitch choice, DynamicState<IrLocal> state) {
      this.Reads(choice.Value, state);
      var tested = TagTested(choice.Value);
      var frame = new Frame(false);
      this._frames.Push(frame);
      var ends = new List<DynamicState<IrLocal>?>();
      var labels = choice.Sections.SelectMany(section => section.Labels).ToList();
      foreach (var section in choice.Sections) {
        var entry = tested is null ? state : this.Join([.. section.Labels.Select(label => this.Narrow(state, tested, label)),
          section.IsDefault ? this.Without(state, tested, labels) : null]);
        ends.Add(entry is null ? null : this.Block(section.Body, entry));
      }

      this._frames.Pop();
      var unmatched = choice.Sections.Any(section => section.IsDefault) ? null : tested is null ? state : this.Without(state, tested, labels);
      return this.Join([.. ends, .. frame.Breaks, unmatched]);
    }

    /// <summary>A try: every catch starts from what holds anywhere in the body; a finally block from what holds where the body or a catch ends or leaves.</summary>
    private DynamicState<IrLocal>? Try(IrTry attempt, DynamicState<IrLocal> state) {
      var inside = new List<DynamicState<IrLocal>> { state };
      this._tries.Push(inside);
      var end = this.Block(attempt.Body, state);
      this._tries.Pop();
      var handler = this.Join(inside)!;
      if (this._tries.TryPeek(out var outer))
        outer.AddRange(inside);
      var ends = new List<DynamicState<IrLocal>?> { end };
      foreach (var clause in attempt.Catches) {
        if (clause.Filter is { } filter)
          this.Reads(filter, handler);
        ends.Add(this.Block(clause.Body, handler));
      }

      var after = this.Join(ends);
      if (attempt.Finally is not { } final)
        return after;
      // The finally block also runs where an exception leaves the try.
      var finished = this.Block(final, this.Join([after, handler])!);
      return after is null ? null : finished;
    }

    /// <summary>A statement the walk does not model: every tracked local it writes may hold anything and is left as it is.</summary>
    private DynamicState<IrLocal> Opaque(IrStmt statement, DynamicState<IrLocal> state) {
      var written = Written(statement).Where(this._tracked.Contains).ToList();
      this._escaping.UnionWith(written);
      var havoc = written.Aggregate(state, (known, local) => known.With(local, this._domain.Unknown));
      foreach (var local in written)
        this.Reach(local, this._domain.Unknown, null);
      foreach (var node in IrTree.Nodes(statement).OfType<IrLocalRef>().Where(reference => this._tracked.Contains(reference.Local)))
        this.Reach(node.Local, havoc[node.Local], null);
      var inner = Statements(statement).ToList();
      if (inner.OfType<IrReturn>().Any(result => result.Value is not null))
        this.Returned = this._domain.Join(this.Returned, this._domain.Unknown);
      if (inner.Any(inside => inside is IrBreak or IrContinue))
        foreach (var frame in this._frames) {
          frame.Breaks.Add(havoc);
          frame.Continues.Add(havoc);
        }

      return havoc;
    }

    /// <summary>Every statement within a statement, itself first.</summary>
    private static IEnumerable<IrStmt> Statements(IrStmt statement) {
      yield return statement;
      foreach (var block in IrTree.Blocks(statement))
        foreach (var inner in block.Statements.SelectMany(Statements))
          yield return inner;
    }

    #endregion

    #region Refinement

    /// <summary>The local a tag test is of, and the tag: <c>IrVariantTagOf(x) == k</c>, in either order and through conversions.</summary>
    private (IrLocal Local, IrVariantTag Tag)? TagTest(IrExpr left, IrExpr right) {
      var (a, b) = (Bare(left), Bare(right));
      if (a is IrConst && b is IrVariantTagOf)
        (a, b) = (b, a);
      return a is IrVariantTagOf { Value: IrLocalRef reference } && this._tracked.Contains(reference.Local) && b is IrConst constant && Enum.IsDefined((IrVariantTag)constant.Value)
        ? (reference.Local, (IrVariantTag)constant.Value)
        : null;
    }

    /// <summary>The local whose tag a switch selects on, or <see langword="null"/>.</summary>
    private IrLocal? TagTested(IrExpr value) => Bare(value) is IrVariantTagOf { Value: IrLocalRef reference } && this._tracked.Contains(reference.Local) ? reference.Local : null;

    private static IrExpr Bare(IrExpr expression) => expression is IrConvert conversion ? Bare(conversion.Operand) : expression;

    /// <summary>The state where a condition has a truth value: narrowed by the tag tests it makes; <see langword="null"/> where it cannot have it.</summary>
    private DynamicState<IrLocal>? Refine(IrExpr condition, DynamicState<IrLocal>? state, bool truth) {
      if (state is null)
        return null;
      switch (condition) {
        case IrUnary { Op: IrUnaryOp.LogicalNot } negation:
          return this.Refine(negation.Operand, state, !truth);
        case IrBinary { Op: IrBinaryOp.LogicalAnd } both:
          return truth ? this.Refine(both.Right, this.Refine(both.Left, state, true), true)
            : this.Join([this.Refine(both.Left, state, false), this.Refine(both.Right, this.Refine(both.Left, state, true), false)]);
        case IrBinary { Op: IrBinaryOp.LogicalOr } either:
          return truth ? this.Join([this.Refine(either.Left, state, true), this.Refine(either.Right, this.Refine(either.Left, state, false), true)])
            : this.Refine(either.Right, this.Refine(either.Left, state, false), false);
        case IrBinary { Op: IrBinaryOp.Equal or IrBinaryOp.NotEqual } test when this.TagTest(test.Left, test.Right) is var (local, tag):
          return (test.Op == IrBinaryOp.Equal) == truth ? this.Narrow(state, local, (long)tag) : this.Without(state, local, [(long)tag]);
        case IrCall when truth && this._table.Classify(condition) is { Operation: DynamicOperation.LoopTest, Left: IrLocalRef { Local: var counter }, Right: { } limit, Step: { } step } && this._tracked.Contains(counter):
          return this.Bounded(state, counter, this.Eval(limit, state).Type, this.Eval(step, state).Type);
        default:
          return state;
      }
    }

    /// <summary>
    /// The state where the test of a counted loop holds: an integer counter has not passed the limit,
    /// so its range ends at the limit's - above for a step proven not negative, below for one proven
    /// negative; a step of unknown sign, or a limit that is no integer of a range, narrows nothing.
    /// </summary>
    private DynamicState<IrLocal> Bounded(DynamicState<IrLocal> state, IrLocal counter, DynamicType limit, DynamicType step) {
      static ValueRange? Bounds(DynamicType type) => type is DynamicExact { Type.IsInteger: true } exact ? exact.Range ?? ValueRange.Of(exact.Type) : null;
      if (Bounds(limit) is not { } end || Bounds(step) is not { } by || (by.Min < 0 && by.Max >= 0))
        return state;
      var bound = by.Min >= 0 ? new ValueRange(Int128.MinValue, end.Max) : new ValueRange(end.Min, Int128.MaxValue);
      DynamicType Narrowed(DynamicType member) => member is DynamicExact { Type.IsInteger: true } exact && Bounds(exact) is { } own
        ? DynamicType.Exact(exact.Type, exact.Tag, new ValueRange(Int128.Max(own.Min, bound.Min), Int128.Min(own.Max, bound.Max)))
        : member;
      var fact = state[counter];
      var narrowed = fact.Type switch {
        DynamicExact => Narrowed(fact.Type),
        DynamicUnion union => DynamicType.Union(this._domain.Policy, [.. union.Members.Select(Narrowed)]),
        _ => fact.Type
      };
      return state.With(counter, fact with { Type = narrowed });
    }

    private DynamicState<IrLocal>? Narrow(DynamicState<IrLocal> state, IrLocal local, long tag) {
      if (!Enum.IsDefined((IrVariantTag)tag) || IrVariants.PayloadType((IrVariantTag)tag) is null && (IrVariantTag)tag is not (IrVariantTag.Empty or IrVariantTag.Null))
        return state;
      var narrowed = DynamicType.Meet(state[local].Type, DynamicType.OfTag((IrVariantTag)tag), this._domain.Policy);
      return narrowed is DynamicNothing ? null : state.With(local, state[local] with { Type = narrowed, Certainty = Certainty.Proven });
    }

    private DynamicState<IrLocal>? Without(DynamicState<IrLocal> state, IrLocal local, IEnumerable<long> tags) {
      var rest = tags.Where(tag => Enum.IsDefined((IrVariantTag)tag)).Aggregate(state[local].Type, (type, tag) => Remove(type, (IrVariantTag)tag));
      return rest is DynamicNothing ? null : state.With(local, state[local] with { Type = rest });
    }

    /// <summary>An element without the values of one tag.</summary>
    private DynamicType Remove(DynamicType type, IrVariantTag tag) => type switch {
      DynamicOptional optional when tag is IrVariantTag.Empty or IrVariantTag.Null
        => DynamicType.Optional(optional.Value, optional.Missing & ~(tag == IrVariantTag.Empty ? DynamicMissing.Empty : DynamicMissing.Null)),
      DynamicOptional optional => DynamicType.Optional(this.Remove(optional.Value, tag), optional.Missing),
      DynamicUnion union => DynamicType.Union(this._domain.Policy, [.. union.Members.Select(member => this.Remove(member, tag))]),
      DynamicExact exact when exact.BoxTag == tag => DynamicType.Nothing,
      _ => type
    };

    #endregion

  }

}
