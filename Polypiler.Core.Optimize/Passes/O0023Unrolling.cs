using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0023, loop unrolling (prototype: full unrolling only): a loop that counts a variable from a
/// constant by a constant step while it compares with a constant - <c>for (i = c; i &lt; n; i += k)</c>
/// as the frontends make it, a <c>while</c> whose body ends with the step - and runs at most
/// <see cref="Limit"/> times becomes that many copies of its body, each a block of its own whose
/// variables are new ones and in which the counter is the constant it holds in that pass; the counter
/// is assigned the value it ends with after the last copy. The counter is declared or assigned the
/// constant directly before the loop; nothing else writes it, nothing in the body breaks or continues,
/// no lambda names a variable of the loop, and the copies stay within <see cref="Budget"/>. A loop that
/// does not run at all leaves only that assignment.
/// </summary>
public sealed class O0023Unrolling : IIrPass {

  /// <summary>The most passes of a loop that is unrolled.</summary>
  public const int Limit = 8;

  /// <summary>The largest size, in statements and expression nodes, of all copies together.</summary>
  public const int Budget = 512;

  /// <inheritdoc/>
  public string Id => "O0023";

  /// <inheritdoc/>
  public string Name => "loop unrolling";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function))
        return function;

      var run = new Walk(function, applied);
      var result = run.Function();
      changed |= run.Changed;
      return run.Changed ? result : function;
    }).ToList();
    return changed ? module with { Functions = functions } : module;
  }

  /// <summary>A loop that counts: its counter, the constant it starts from, the comparison and the step.</summary>
  internal sealed record Counted(IrLocal Counter, IrConst Start, IrBinaryOp Compare, IrConst Bound, bool CounterLeft, IrBlock Body, IReadOnlyList<IrStmt> Step);

  /// <summary>The loop a statement is where the statement before it sets its counter to a constant; or <see langword="null"/>.</summary>
  internal static Counted? Recognize(IrStmt? before, IrStmt loop) {
    var start = before switch {
      IrDeclare { Initializer: IrConst value } declare => (declare.Local, value),
      IrAssign { Value: IrConst value } assign => (assign.Local, value),
      _ => default((IrLocal, IrConst)?)
    };
    if (start is not { } set || !set.Item1.Type.IsInteger)
      return null;

    var (condition, body, step) = loop switch {
      IrWhile { Body.Statements.Count: > 0 } whileLoop => (whileLoop.Condition, new IrBlock([.. whileLoop.Body.Statements.SkipLast(1)]), (IReadOnlyList<IrStmt>)[whileLoop.Body.Statements[^1]]),
      IrFor { PerIteration.Count: 0 } forLoop => (forLoop.Condition, forLoop.Body, forLoop.Step.Statements),
      _ => default((IrExpr, IrBlock, IReadOnlyList<IrStmt>)?)
    } ?? default;
    if (condition is not IrBinary { Op: IrBinaryOp.Less or IrBinaryOp.LessOrEqual or IrBinaryOp.Greater or IrBinaryOp.GreaterOrEqual or IrBinaryOp.NotEqual } comparison)
      return null;

    var counter = set.Item1;
    var (counterLeft, bound) = (comparison.Left, comparison.Right) switch {
      (IrLocalRef { Local: var left }, IrConst right) when left == counter => (true, right),
      (IrConst left, IrLocalRef { Local: var right }) when right == counter => (false, left),
      _ => (false, (IrConst?)null)
    };
    if (bound is null || step.Count == 0 || !step.All(statement => Steps(statement, counter)))
      return null;

    if (IrStatements.Writes(body).Contains(counter) || IrPassSupport.Statements(body).Any(statement => statement is IrBreak or IrContinue))
      return null;

    return new Counted(counter, set.Item2, comparison.Op, bound, counterLeft, body, step);
  }

  /// <summary>Whether a statement of the step adds a constant to the counter: an assignment of it plus or minus a constant, or a step of it.</summary>
  private static bool Steps(IrStmt statement, IrLocal counter) => statement switch {
    IrAssign { Value: IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract, Left: IrLocalRef { Local: var read }, Right: IrConst } } assign => assign.Local == counter && read == counter,
    IrExprStmt { Expression: IrStep { Target: IrLocalRef { Local: var stepped } } } => stepped == counter,
    _ => false
  };

  /// <summary>
  /// The values of the counter of a counted loop, stepped as its type wraps: the one of every pass and,
  /// last, the one it ends with; <see langword="null"/> beyond <see cref="Limit"/> passes or where a step
  /// does not fold.
  /// </summary>
  internal static IReadOnlyList<IrConst>? Values(Counted loop) {
    var value = loop.Start;
    var values = new List<IrConst> { value };
    for (var trips = 0; trips <= Limit; ++trips) {
      var test = loop.CounterLeft ? new IrBinary(loop.Compare, value, loop.Bound) : new IrBinary(loop.Compare, loop.Bound, value);
      if (IrPassSupport.Evaluate(test) is not { } holds)
        return null;

      if (holds.Value == 0)
        return values;

      foreach (var statement in loop.Step) {
        IrExpr next = statement switch {
          IrAssign { Value: IrBinary step } => step with { Left = value },
          IrExprStmt { Expression: IrStep step } => new IrBinary(step.Increment ? IrBinaryOp.Add : IrBinaryOp.Subtract, value, new IrConst(value.Type, 1)),
          _ => value
        };
        if (statement is IrExprStmt { Expression: IrStep { Trap: true } })
          next = new IrBinary(next is IrBinary { Op: IrBinaryOp.Add } ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked, value, new IrConst(value.Type, 1));

        if (IrPassSupport.Evaluate(next) is not { } stepped)
          return null;

        value = stepped;
      }

      values.Add(value);
    }

    return null;
  }

  private sealed class Walk(IrFunction function, Action<string, string> applied) {

    private readonly HashSet<IrLocal> _shared = IrPassSupport.Shared(function);
    private readonly HashSet<string> _taken = [.. function.Parameters.Select(local => local.Name), .. function.Locals.Select(local => local.Name)];
    private readonly List<IrLocal> _locals = [.. function.Locals];
    private readonly List<int> _position = [];

    public bool Changed { get; private set; }

    public IrFunction Function() {
      var body = this.Block(function.Body);
      return function with { Body = body, Locals = this._locals };
    }

    private IrBlock Block(IrBlock block) {
      var statements = new List<IrStmt>();
      for (var index = 0; index < block.Statements.Count; ++index) {
        this._position.Add(index + 1);
        var statement = IrPassSupport.Map(block.Statements[index], expression => expression, this.Block);
        if (this.Unrolled(statements.Count > 0 ? statements[^1] : null, statement) is { } copies) {
          statements.AddRange(copies);
          this.Changed = true;
          applied(function.Name, string.Join('.', this._position));
        } else {
          statements.Add(statement);
        }

        this._position.RemoveAt(this._position.Count - 1);
      }

      return new IrBlock(statements);
    }

    /// <summary>The copies a loop is unrolled into, or <see langword="null"/> to keep it.</summary>
    private IReadOnlyList<IrStmt>? Unrolled(IrStmt? before, IrStmt loop) {
      if (Recognize(before, loop) is not { } counted || this._shared.Contains(counted.Counter)
          || IrPassSupport.Nodes(counted.Body).Any(node => node is IrLambda))
        return null;

      if (Values(counted) is not { } values || (values.Count - 1) * (IrPassSupport.Size(counted.Body) + 2) > Budget)
        return null;

      var copies = new List<IrStmt>();
      for (var trip = 0; trip < values.Count - 1; ++trip)
        copies.Add(this.Fresh(Substituted(counted.Body, counted.Counter, values[trip])));

      copies.Add(new IrAssign(counted.Counter, values[^1]));
      return copies;
    }

    /// <summary>A body in which every read of the counter is the constant it holds in one pass.</summary>
    private static IrBlock Substituted(IrBlock body, IrLocal counter, IrConst value) {
      IrExpr Expression(IrExpr expression) => IrShape.Map(expression, node => node is IrLocalRef reference && reference.Local == counter ? value : null);
      return new IrBlock([.. body.Statements.Select(statement => IrPassSupport.Map(statement, Expression, inner => Substituted(inner, counter, value)))]);
    }

    /// <summary>A copy of a body whose every variable it declares is a new one, so that the copies side by side never declare one twice.</summary>
    private IrBlock Fresh(IrBlock body) {
      var renamed = new Dictionary<IrLocal, IrLocal>();
      foreach (var declare in IrPassSupport.Statements(body).OfType<IrDeclare>())
        if (!renamed.ContainsKey(declare.Local)) {
          var local = new IrLocal(IrPassSupport.Fresh(declare.Local.Name, this._taken), declare.Local.Type);
          this._locals.Add(local);
          renamed[declare.Local] = local;
        }

      return renamed.Count == 0 ? body : Renamed(body, renamed);
    }

    private static IrBlock Renamed(IrBlock block, Dictionary<IrLocal, IrLocal> renamed) {
      IrExpr Expression(IrExpr expression) => IrShape.Map(expression, node => node is IrLocalRef reference && renamed.TryGetValue(reference.Local, out var local) ? new IrLocalRef(local) : null);
      return new IrBlock([.. block.Statements.Select(statement => IrPassSupport.Map(statement, Expression, inner => Renamed(inner, renamed)) switch {
        IrDeclare declare when renamed.TryGetValue(declare.Local, out var local) => declare with { Local = local },
        IrAssign assign when renamed.TryGetValue(assign.Local, out var local) => assign with { Local = local },
        var other => other
      })]);
    }

  }

}
