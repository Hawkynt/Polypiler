using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Recognize;

/// <summary>
/// What a rule may know about the target before the backend runs: the answers of the backend's
/// capability table to the questions a rule asks.
/// </summary>
/// <param name="RemainderFromQuotient">
/// Whether the target computes <c>a - (a / b) * b</c> from a quotient it already has faster than a
/// second division for <c>a % b</c>.
/// </param>
public sealed record RuleTarget(bool RemainderFromQuotient) {

  /// <summary>A target that has said nothing: every question is answered no.</summary>
  public static RuleTarget Neutral { get; } = new(false);

}

/// <summary>A block around the statements a rule is asked about.</summary>
/// <param name="Statements">The statements of the enclosing block.</param>
/// <param name="Index">The position of the statement that holds the inner block.</param>
public sealed record RuleFrame(IReadOnlyList<IrStmt> Statements, int Index) {

  /// <summary>The statement that holds the inner block.</summary>
  public IrStmt Owner => this.Statements[this.Index];

}

/// <summary>
/// The function a rule works in and the facts about its variables that every rule may rely on: which
/// variables never change after their declaration and what they hold, and - for the statements a
/// rule is asked about - the blocks around them.
/// </summary>
public sealed class RuleContext {

  private readonly Dictionary<IrLocal, int> _writes = [];
  private readonly Dictionary<IrLocal, IrExpr> _initializers = [];
  private readonly HashSet<IrLocal> _parameters;
  private readonly List<RuleFrame> _frames = [];

  /// <summary>Collects the facts of a function.</summary>
  /// <param name="function">The function.</param>
  /// <param name="target">What the target says about the costs a rule asks for.</param>
  public RuleContext(IrFunction function, RuleTarget target) {
    this.Function = function;
    this.Target = target;
    this._parameters = [.. function.Parameters];
    foreach (var statement in IrStatements.All(function.Body)) {
      switch (statement) {
        case IrAssign assign:
          this.Count(assign.Local);
          break;
        case IrDeclare declare:
          this.Count(declare.Local);
          if (declare.Initializer is null)
            this.Count(declare.Local);
          else
            this._initializers[declare.Local] = declare.Initializer;

          break;
      }
    }

    foreach (var step in IrStatements.AllExpressions(function.Body).SelectMany(IrShape.Nodes).OfType<IrStep>())
      if (step.Target is IrLocalRef reference)
        this.Count(reference.Local);
  }

  /// <summary>The function.</summary>
  public IrFunction Function { get; }

  /// <summary>What the target says about costs.</summary>
  public RuleTarget Target { get; }

  /// <summary>The blocks around the statements a rule is asked about, the outermost first; empty for the body of the function.</summary>
  public IReadOnlyList<RuleFrame> Frames => this._frames;

  private void Count(IrLocal local) => this._writes[local] = this._writes.GetValueOrDefault(local) + 1;

  /// <summary>
  /// Whether a variable holds one value wherever it can be read: a parameter that is never written,
  /// or a local whose declaration gives it its value and that nothing writes afterwards.
  /// </summary>
  /// <param name="local">The variable.</param>
  /// <returns><see langword="true"/> when every read sees the value it started with.</returns>
  public bool IsStable(IrLocal local)
    => this._writes.GetValueOrDefault(local) == (this._parameters.Contains(local) ? 0 : 1) && (this._parameters.Contains(local) || this._initializers.ContainsKey(local));

  /// <summary>
  /// What a stable local holds, as an expression that may stand for every read of it: its
  /// initializer, when that has no effect, cannot trap and reads only stable variables. A
  /// parameter has no definition; it is a value of its own.
  /// </summary>
  /// <param name="local">The variable.</param>
  /// <returns>The expression, or <see langword="null"/>.</returns>
  public IrExpr? Definition(IrLocal local)
    => this.IsStable(local) && this._initializers.TryGetValue(local, out var value) && IrShape.IsTrapFree(value)
      && IrShape.Locals(value).All(this.IsStable) ? value : null;

  /// <summary>Enters a block that a statement holds; the pipeline calls this as it descends.</summary>
  /// <param name="frame">The enclosing statements and the position of the statement that holds the block.</param>
  public void Enter(RuleFrame frame) => this._frames.Add(frame);

  /// <summary>Leaves the innermost block entered.</summary>
  public void Leave() => this._frames.RemoveAt(this._frames.Count - 1);

  /// <summary>
  /// Whether no statement can read a variable once the statements of the current block from a
  /// position on have run - so that what it holds afterwards does not matter. Conservative: any
  /// mention later on, or an enclosing loop, makes it live.
  /// </summary>
  /// <param name="local">The variable.</param>
  /// <param name="statements">The statements of the current block.</param>
  /// <param name="from">The position of the first statement that runs afterwards.</param>
  /// <returns><see langword="true"/> when the value is dead.</returns>
  public bool IsDeadAfter(IrLocal local, IReadOnlyList<IrStmt> statements, int from) {
    for (var level = this._frames.Count; ; --level) {
      if (statements.Skip(from).Any(statement => IrStatements.Mentions(statement, local)))
        return false;

      if (statements.Take(from).Any(statement => statement is IrDeclare declare && declare.Local == local))
        return true;

      if (level == 0)
        return true;

      var frame = this._frames[level - 1];
      if (frame.Owner is IrWhile or IrDoWhile or IrFor)
        return false;

      statements = frame.Statements;
      from = frame.Index + 1;
    }
  }

}
