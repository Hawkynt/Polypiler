using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0006, inlining (prototype): a call of a function legalization copied in from the libraries - a
/// helper or a portable floating-point implementation - becomes the body of the function where the
/// body is no larger than <see cref="Small"/>, or no larger than <see cref="Budget"/> where the call
/// has a constant argument to specialise the copy with or is the only call of the function in the
/// module - a larger body copied to many places costs more than the call -, never calls itself, directly or through others, and
/// has a single way out once every return in a choice is made the end of its branch. The arguments
/// become variables declared in their order, the returns assignments of a result variable, and the
/// call a read of that variable. A call is inlined where its statement evaluates it first: nothing
/// before it in the statement has an effect or can trap, it is not one branch of a choice or the
/// right side of <c>&amp;&amp;</c>, <c>||</c> or <c>??</c>, and it is no condition of a loop. What the
/// copy leaves behind is for constant propagation, folding and dead-code removal to clean up.
/// </summary>
public sealed class O0006Inlining : IIrPass {

  /// <summary>The largest body, in statements and expression nodes, that is inlined where the call has a constant argument or is the only one.</summary>
  public const int Budget = 64;

  /// <summary>The largest body, in statements and expression nodes, that is inlined at every call.</summary>
  public const int Small = 24;

  /// <summary>The most calls inlined into one function in one run, a bound on a body that grows.</summary>
  public const int Limit = 256;

  /// <inheritdoc/>
  public string Id => "O0006";

  /// <inheritdoc/>
  public string Name => "inlining";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    var library = module.Implementations.ToDictionary(pair => pair.Function, pair => pair.Implementation, StringComparer.Ordinal);
    if (library.Count == 0)
      return module;

    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var candidates = library.Keys
      .Where(name => byName.TryGetValue(name, out var function) && Inlinable(function, byName))
      .ToDictionary(name => name, name => byName[name], StringComparer.Ordinal);
    if (candidates.Count == 0)
      return module;

    var calls = module.Functions.SelectMany(function => IrPassSupport.Nodes(function.Body)).OfType<IrCall>()
      .GroupBy(call => call.Function, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    var sizes = candidates.ToDictionary(pair => pair.Key, pair => IrPassSupport.Size(pair.Value.Body), StringComparer.Ordinal);
    bool Worth(IrCall call) => sizes[call.Function] <= Small || calls.GetValueOrDefault(call.Function) == 1 || call.Arguments.Any(argument => argument is IrConst);
    var inlined = new List<string>();
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function))
        return function;

      var run = new Walk(function, candidates, Worth, applied);
      var result = run.Function();
      if (run.Inlined.Count == 0)
        return function;

      changed = true;
      inlined.AddRange(run.Inlined.Select(name => library[name]));
      return result;
    }).ToList();
    if (!changed)
      return module;

    return module with { Functions = functions, Inlined = [.. module.Inlined.Concat(inlined).Distinct(StringComparer.Ordinal)] };
  }

  /// <summary>Whether a function may be inlined: within the budget, no lambda, no call of itself through any chain of calls, and a single way out.</summary>
  private static bool Inlinable(IrFunction function, Dictionary<string, IrFunction> functions)
    => IrPassSupport.Size(function.Body) <= Budget
      && !IrPassSupport.Nodes(function.Body).Any(node => node is IrLambda)
      && !Reaches(function.Name, function, functions)
      && Exit(function.Body.Statements, function.ReturnType == IrType.Void ? null : new IrLocal("$result", function.ReturnType)) is not null;

  /// <summary>Whether a function calls a name, directly or through the functions it calls.</summary>
  private static bool Reaches(string name, IrFunction from, Dictionary<string, IrFunction> functions) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var pending = new Stack<IrFunction>([from]);
    while (pending.Count > 0)
      foreach (var call in IrPassSupport.Nodes(pending.Pop().Body).OfType<IrCall>()) {
        if (call.Function == name)
          return true;

        if (seen.Add(call.Function) && functions.TryGetValue(call.Function, out var callee))
          pending.Push(callee);
      }

    return false;
  }

  /// <summary>
  /// A body with its returns made assignments of the result - none for a function without one - and
  /// the statements after a choice whose branch always returns moved into the other branch; or
  /// <see langword="null"/> where a return stands in a loop, a switch, a try or another statement that
  /// cannot be rearranged so.
  /// </summary>
  internal static IReadOnlyList<IrStmt>? Exit(IReadOnlyList<IrStmt> statements, IrLocal? result) {
    var done = new List<IrStmt>();
    for (var index = 0; index < statements.Count; ++index) {
      var statement = statements[index];
      var rest = statements.Skip(index + 1).ToList();
      switch (statement) {
        case IrReturn returned:
          if (returned.Value is { } value && result is not null)
            done.Add(new IrAssign(result, value));

          return done;
        case IrBlock block when Returns(block):
          return Exit([.. block.Statements, .. rest], result) is { } flat ? [.. done, .. flat] : null;
        case IrIf conditional when Returns(conditional): {
          var otherwise = conditional.Else ?? new IrBlock([]);
          var thenLeaves = Leaves(conditional.Then);
          var elseLeaves = Leaves(otherwise);
          IReadOnlyList<IrStmt>? then;
          IReadOnlyList<IrStmt>? other;
          if (thenLeaves) {
            then = Exit(conditional.Then.Statements, result);
            other = Exit([.. otherwise.Statements, .. elseLeaves ? [] : rest], result);
          } else if (elseLeaves) {
            then = Exit([.. conditional.Then.Statements, .. rest], result);
            other = Exit(otherwise.Statements, result);
          } else {
            return null;
          }

          if (then is null || other is null)
            return null;

          done.Add(new IrIf(conditional.Condition, new IrBlock([.. then]), other.Count == 0 ? null : new IrBlock([.. other])));
          return done;
        }
        default:
          if (Returns(statement))
            return null;

          done.Add(statement);
          break;
      }
    }

    return done;
  }

  private static bool Returns(IrStmt statement) => IrStatements.All(statement).Any(inner => inner is IrReturn);

  /// <summary>Whether a block never runs to its end because of a return or a throw on every way through.</summary>
  private static bool Leaves(IrBlock block) => block.Statements.Count > 0 && block.Statements[^1] switch {
    IrReturn or IrThrow => true,
    IrBlock inner => Leaves(inner),
    IrIf { Else: { } otherwise } conditional => Leaves(conditional.Then) && Leaves(otherwise),
    _ => false
  };

  private sealed class Walk(IrFunction function, Dictionary<string, IrFunction> candidates, Func<IrCall, bool> worth, Action<string, string> applied) {

    private readonly HashSet<string> _taken = [.. function.Parameters.Select(local => local.Name), .. function.Locals.Select(local => local.Name)];
    private readonly List<IrLocal> _locals = [.. function.Locals];
    private readonly List<int> _position = [];

    /// <summary>The functions inlined, by their names in the module, once per call site.</summary>
    public List<string> Inlined { get; } = [];

    public IrFunction Function() {
      var body = this.Block(function.Body);
      return this.Inlined.Count == 0 ? function : function with { Body = body, Locals = this._locals };
    }

    private IrBlock Block(IrBlock block) {
      var statements = new List<IrStmt>();
      var pending = new Queue<IrStmt>(block.Statements);
      var index = 0;
      while (pending.Count > 0) {
        var statement = pending.Dequeue();
        this._position.Add(++index);
        if (this.Inlined.Count < Limit && this.Site(statement) is { } site) {
          var expanded = this.Expand(statement, site);
          applied(function.Name, string.Join('.', this._position));
          this._position.RemoveAt(this._position.Count - 1);
          --index;
          // The copy may call further helpers, and the statement may hold another call: both are looked at again.
          var rest = pending.ToList();
          pending.Clear();
          foreach (var next in expanded.Concat(rest))
            pending.Enqueue(next);

          continue;
        }

        statements.Add(IrPassSupport.Map(statement, expression => expression, this.Block));
        this._position.RemoveAt(this._position.Count - 1);
      }

      return new IrBlock(statements);
    }

    /// <summary>The first call of a statement that is inlined, or <see langword="null"/>.</summary>
    private IrCall? Site(IrStmt statement) {
      if (statement is IrWhile or IrDoWhile or IrFor or IrForEachCollection or IrBlock or IrTry or IrUsing)
        return null;

      foreach (var expression in IrTree.Expressions(statement)) {
        var (call, blocked) = this.First(expression);
        if (call is not null)
          return call;

        if (blocked)
          return null;
      }

      return null;
    }

    /// <summary>
    /// The first call of an expression in the order of evaluation that may be inlined, where everything
    /// evaluated before it may be moved after it; <c>Blocked</c> when something evaluated earlier
    /// cannot be, or the call stands where it may not run.
    /// </summary>
    private (IrCall? Call, bool Blocked) First(IrExpr expression) {
      if (expression is IrCall call && candidates.ContainsKey(call.Function) && call.Function != function.Name && worth(call)) {
        // The arguments stay in their order before the copied body; a call within one is taken first
        // only where the arguments before it may be evaluated after it.
        foreach (var argument in call.Arguments) {
          var (inner, blocked) = this.First(argument);
          if (inner is not null)
            return (inner, false);

          if (blocked)
            break;
        }

        return (call, false);
      }

      var lazy = expression switch {
        IrConditional => 1,
        IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr } => 1,
        IrCoalesce => 1,
        IrLambda => 0,
        _ => int.MaxValue
      };
      var operands = IrTree.Operands(expression);
      for (var index = 0; index < operands.Count && index < lazy; ++index) {
        var (inner, blocked) = this.First(operands[index]);
        if (inner is not null || blocked)
          return (inner, blocked);

        if (!IrPassSupport.IsPure(operands[index]))
          return (null, true);
      }

      return (null, !IrPassSupport.IsPure(expression));
    }

    /// <summary>The statements a call is inlined into: the arguments as variables, the body, and the statement reading the result.</summary>
    private List<IrStmt> Expand(IrStmt statement, IrCall call) {
      var callee = candidates[call.Function];
      this.Inlined.Add(call.Function);
      var renamed = new Dictionary<IrLocal, IrLocal>();
      foreach (var local in callee.Parameters.Concat(callee.Locals).Distinct())
        renamed[local] = this.Local(local.Name, local.Type);

      var result = callee.ReturnType == IrType.Void ? null : this.Local(callee.Name.TrimStart('$', '_') is { Length: > 0 } name ? char.ToLowerInvariant(name[0]) + name[1..] : "result", callee.ReturnType);
      var statements = new List<IrStmt>();
      if (result is not null)
        statements.Add(new IrDeclare(result, null));

      statements.AddRange(callee.Parameters.Select((parameter, index) => new IrDeclare(renamed[parameter], call.Arguments[index])));
      var body = Exit(callee.Body.Statements, result)!;
      statements.AddRange(body.Select(inner => Renamed(inner, renamed)));
      var replaced = IrPassSupport.Map(statement, expression => Replace(expression, call, result), block => block);
      // A call for its effect alone leaves nothing to read.
      if (!(replaced is IrExprStmt { Expression: IrLocalRef } || replaced is IrExprStmt { Expression: IrConst }))
        statements.Add(replaced);

      return statements;
    }

    private IrLocal Local(string name, IrType type) {
      var local = new IrLocal(IrPassSupport.Fresh(name, this._taken), type);
      this._locals.Add(local);
      return local;
    }

    /// <summary>The expression with the one call replaced by a read of the result.</summary>
    private static IrExpr Replace(IrExpr expression, IrCall call, IrLocal? result) {
      if (ReferenceEquals(expression, call))
        return result is null ? new IrConst(IrType.I32, 0) : new IrLocalRef(result);

      var operands = IrTree.Operands(expression);
      if (expression is IrLambda || operands.Count == 0)
        return expression;

      var mapped = operands.Select(operand => Replace(operand, call, result)).ToList();
      return mapped.Where((operand, index) => !ReferenceEquals(operand, operands[index])).Any() ? IrShape.With(expression, mapped) : expression;
    }

    /// <summary>A statement of the callee with its variables renamed into the caller.</summary>
    private static IrStmt Renamed(IrStmt statement, Dictionary<IrLocal, IrLocal> renamed) {
      IrLocal Of(IrLocal local) => renamed.GetValueOrDefault(local, local);
      IrExpr Expression(IrExpr expression) => IrShape.Map(expression, node => node switch {
        IrLocalRef reference when renamed.ContainsKey(reference.Local) => new IrLocalRef(Of(reference.Local)),
        IrBind bind when renamed.ContainsKey(bind.Local) => bind with { Local = Of(bind.Local) },
        _ => null
      });

      var mapped = IrPassSupport.Map(statement, Expression, block => new IrBlock([.. block.Statements.Select(inner => Renamed(inner, renamed))]));
      return mapped switch {
        IrDeclare declare => declare with { Local = Of(declare.Local) },
        IrAssign assign => assign with { Local = Of(assign.Local) },
        IrUsing scope => scope with { Resource = Of(scope.Resource) },
        IrFor loop => loop with { PerIteration = [.. loop.PerIteration.Select(Of)] },
        IrForEachCollection loop => loop with { Element = loop.Element is null ? null : Of(loop.Element), Value = loop.Value is null ? null : Of(loop.Value) },
        IrTry attempt => attempt with { Catches = [.. attempt.Catches.Select(clause => clause with { Local = clause.Local is null ? null : Of(clause.Local) })] },
        _ => mapped
      };
    }

  }

}
