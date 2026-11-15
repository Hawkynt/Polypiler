using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0005, dead-code removal (prototype): a choice on a constant keeps the branch it takes, a loop
/// whose condition is false from the start goes, and so does every statement after a return, a
/// throw, a break or a continue in its block; a nested block that declares nothing is spliced into
/// the one around it. A variable nothing reads goes with its declaration and
/// its assignments where their values may go unevaluated, as does an empty choice on such a value.
/// A function legalization copied in from the libraries that nothing calls any more goes as well.
/// </summary>
public sealed class O0005DeadCode : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0005";

  /// <inheritdoc/>
  public string Name => "dead-code removal";

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
      var body = run.Body();
      if (!run.Changed)
        return function;

      changed = true;
      return function with { Body = body };
    }).ToList();

    // A function of the libraries nothing calls any more: what is left of it was inlined.
    var copied = module.Implementations.Select(pair => pair.Function).ToHashSet(StringComparer.Ordinal);
    var called = functions.Where(function => !copied.Contains(function.Name)).SelectMany(function => Called(function)).ToHashSet(StringComparer.Ordinal);
    var pending = new Queue<string>(called);
    while (pending.Count > 0) {
      var caller = pending.Dequeue();
      foreach (var callee in functions.Where(function => function.Name == caller).SelectMany(Called))
        if (called.Add(callee))
          pending.Enqueue(callee);
    }

    var unused = copied.Where(name => !called.Contains(name)).ToHashSet(StringComparer.Ordinal);
    if (unused.Count > 0) {
      foreach (var name in unused)
        applied(name, "0");

      return module with {
        Functions = [.. functions.Where(function => !unused.Contains(function.Name))],
        Implementations = [.. module.Implementations.Where(pair => !unused.Contains(pair.Function))]
      };
    }

    return changed ? module with { Functions = functions } : module;
  }

  /// <summary>The names of the functions a function names: by a call, or as a value.</summary>
  private static IEnumerable<string> Called(IrFunction function) => IrPassSupport.Nodes(function.Body).Select(node => node switch {
    IrCall call => call.Function,
    IrFunctionRef reference => reference.Function,
    _ => null
  }).OfType<string>();

  private sealed class Walk(IrFunction function, Action<string, string> applied) {

    private readonly HashSet<IrLocal> _shared = IrPassSupport.Shared(function);
    private readonly HashSet<IrLocal> _parameters = [.. function.Parameters];
    private readonly List<int> _position = [];
    private Dictionary<IrLocal, int> _reads = [];
    private bool _dropped;

    public bool Changed { get; private set; }

    public IrBlock Body() {
      var body = this.Block(function.Body);
      this._reads = IrPassSupport.Reads(body);
      return this.Unread(body);
    }

    private void Applied() {
      this.Changed = true;
      applied(function.Name, this._position.Count == 0 ? "1" : string.Join('.', this._position));
    }

    private IrBlock Block(IrBlock block) {
      var statements = new List<IrStmt>();
      for (var index = 0; index < block.Statements.Count; ++index) {
        this._position.Add(index + 1);
        var statement = block.Statements[index];
        var kept = this.Statement(statement);
        statements.AddRange(kept);
        var leaves = kept.Count > 0 && kept[^1] is IrReturn or IrThrow or IrBreak or IrContinue;
        this._position.RemoveAt(this._position.Count - 1);
        if (leaves && index + 1 < block.Statements.Count) {
          this._position.Add(index + 2);
          this.Applied();
          this._position.RemoveAt(this._position.Count - 1);
          break;
        }
      }

      return new IrBlock(statements);
    }

    private IReadOnlyList<IrStmt> Statement(IrStmt statement) {
      switch (statement) {
        case IrIf { Condition: IrConst condition } conditional:
          this.Applied();
          return Spliced(condition.Value != 0 ? this.Block(conditional.Then) : conditional.Else is null ? null : this.Block(conditional.Else));
        case IrWhile { Condition: IrConst { Value: 0 } }:
        case IrFor { Condition: IrConst { Value: 0 } }:
          this.Applied();
          return [];
        case IrDoWhile { Condition: IrConst { Value: 0 } } loop when !IrPassSupport.Statements(loop.Body).Any(inner => inner is IrBreak or IrContinue):
          this.Applied();
          return Spliced(this.Block(loop.Body));
        case IrBlock inner when !inner.Statements.Any(statement => statement is IrDeclare):
          // A block that declares nothing scopes nothing: its statements stand in the block around it.
          this.Changed = true;
          return this.Block(inner).Statements;
        default:
          return [IrPassSupport.Map(statement, expression => expression, this.Block)];
      }
    }

    /// <summary>The statements of a branch that is kept: in a block of their own where they declare a variable, whose scope must not grow.</summary>
    private static IReadOnlyList<IrStmt> Spliced(IrBlock? block) {
      if (block is null)
        return [];

      return block.Statements.Any(statement => statement is IrDeclare) ? [block] : block.Statements;
    }

    // ---------------------------------------------------------------- unread variables

    private IrBlock Unread(IrBlock block) {
      var statements = new List<IrStmt>();
      foreach (var statement in block.Statements) {
        var mapped = IrPassSupport.Map(statement, expression => expression, this.Unread);
        if (this.Drops(mapped)) {
          // One note per function: the variables are the leftovers of one inlining or one propagation.
          if (!this._dropped)
            applied(function.Name, "1");

          this.Changed = this._dropped = true;
          continue;
        }

        statements.Add(mapped);
      }

      return new IrBlock(statements);
    }

    /// <summary>Whether a statement may go: it writes a variable nothing reads with a value that may go unevaluated, or it is an empty block or an empty choice on such a value.</summary>
    private bool Drops(IrStmt statement) => statement switch {
      IrDeclare declare => this.Unread(declare.Local) && (declare.Initializer is null || IrPassSupport.IsPure(declare.Initializer)),
      IrAssign assign => this.Unread(assign.Local) && IrPassSupport.IsPure(assign.Value),
      IrBlock { Statements.Count: 0 } => true,
      IrIf { Then.Statements.Count: 0, Else: null or { Statements.Count: 0 } } conditional => IrPassSupport.IsPure(conditional.Condition),
      _ => false
    };

    private bool Unread(IrLocal local) => !this._parameters.Contains(local) && !this._shared.Contains(local) && this._reads.GetValueOrDefault(local) == 0;

  }

}
