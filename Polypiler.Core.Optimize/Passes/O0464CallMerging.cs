using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>
/// O0464, merging of identical calls (prototype): within one expression of a statement, a call of a
/// function whose effect summary (<see cref="IrEffects"/>) finds it pure or read-only, with arguments
/// that are pure, is evaluated once when the same call follows it. The first call keeps its place and
/// stores its value in a new variable, declared before the statement; every later one reads the
/// variable. A later call is merged only where the first always runs before it - it is not in a branch
/// of a choice or the right side of <c>&amp;&amp;</c>, <c>||</c> or <c>??</c> the first is outside of - and
/// where nothing evaluated between the two writes what the function or its arguments read. Nothing
/// moves, so no trap and no other effect changes its order: a function that may trap or run forever
/// has done so at the first call already when it does. A function that writes or allocates is never
/// merged, nor is a call through a function value. The summary is ready for loop-invariant code motion
/// (O0012), which is not part of this pass.
/// </summary>
public sealed class O0464CallMerging : IIrPass {

  /// <inheritdoc/>
  public string Id => "O0464";

  /// <inheritdoc/>
  public string Name => "call merging";

  /// <inheritdoc/>
  public IrModule Run(IrModule module, Func<IrFunction, bool> scope, Action<string, string> applied) {
    ArgumentNullException.ThrowIfNull(module);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(applied);
    var effects = IrEffects.Of(module);
    var changed = false;
    var functions = module.Functions.Select(function => {
      if (!scope(function) || !IrPassSupport.Nodes(function.Body).OfType<IrCall>().Skip(1).Any())
        return function;

      var walk = new Walk(function, effects, applied);
      var body = walk.Block(function.Body);
      if (walk.Made.Count == 0)
        return function;

      changed = true;
      return function with { Body = body, Locals = [.. function.Locals, .. walk.Made] };
    }).ToList();
    return changed ? module with { Functions = functions } : module;
  }

  /// <summary>A node of an expression in the order of evaluation: where its subtree starts, and the branches it runs in.</summary>
  /// <param name="Node">The node.</param>
  /// <param name="Start">The position of the first node of its subtree; its own position is where it ends.</param>
  /// <param name="Guards">The branches of choices it runs in, each numbered.</param>
  private sealed record Evaluated(IrExpr Node, int Start, IReadOnlySet<int> Guards);

  /// <summary>The merging in one function.</summary>
  private sealed class Walk(IrFunction function, IrEffectTable effects, Action<string, string> applied) {

    private readonly HashSet<string> _taken = new(function.Parameters.Concat(function.Locals).Select(local => local.Name), StringComparer.Ordinal);
    private readonly List<int> _position = [];

    /// <summary>The variables the merges declared.</summary>
    public List<IrLocal> Made { get; } = [];

    public IrBlock Block(IrBlock block) {
      var statements = new List<IrStmt>();
      var changed = false;
      for (var index = 0; index < block.Statements.Count; ++index) {
        this._position.Add(index + 1);
        var statement = block.Statements[index];
        var declared = new List<IrDeclare>();
        var mapped = Merges(statement)
          ? IrPassSupport.Map(statement, expression => this.Expression(expression, declared), this.Block)
          : IrPassSupport.Map(statement, expression => expression, this.Block);
        if (declared.Count > 0)
          applied(function.Name, string.Join('.', this._position));

        var differs = declared.Count > 0 || !Same(mapped, statement);
        changed |= differs;
        statements.AddRange(declared);
        statements.Add(differs ? mapped : statement);
        this._position.RemoveAt(this._position.Count - 1);
      }

      return changed ? new IrBlock(statements) : block;
    }

    /// <summary>Whether a rebuilt statement is the old one: only its blocks may have been rebuilt, which report themselves.</summary>
    private static bool Same(IrStmt mapped, IrStmt statement)
      => IrTree.Blocks(mapped).Zip(IrTree.Blocks(statement)).All(pair => ReferenceEquals(pair.First, pair.Second));

    /// <summary>Whether the expressions of a statement are evaluated where a declaration before it is in scope and each in one go: not the filters of a try, the resource of a using, a loop over a collection or a sequence, a yield.</summary>
    private static bool Merges(IrStmt statement) => statement is IrDeclare or IrAssign or IrGlobalAssign or IrStore or IrFieldStore or IrExprStmt
      or IrIf or IrWhile or IrDoWhile or IrFor or IrSwitch or IrReturn or IrThrow;

    /// <summary>An expression with every group of identical calls merged, the variables it needs added to the declarations.</summary>
    private IrExpr Expression(IrExpr expression, List<IrDeclare> declared) {
      var order = new List<Evaluated>();
      var guards = 0;
      Order(expression, new HashSet<int>());
      var used = new HashSet<int>();
      var replacements = new Dictionary<int, Func<IrExpr, IrExpr>>();
      for (var first = 0; first < order.Count; ++first) {
        if (used.Contains(first) || order[first].Node is not IrCall call || !this.Mergeable(call))
          continue;

        var reader = effects.Function(call.Function).With(call.Arguments.Select(effects.Expression).Aggregate(IrEffects.None, (all, one) => all.With(one)));
        var between = IrEffects.None;
        var later = new List<int>();
        for (var next = first + 1; next < order.Count; ++next) {
          var candidate = order[next];
          if (candidate.Node is IrCall other && !used.Contains(next) && candidate.Start > first && order[first].Guards.IsSubsetOf(candidate.Guards)
              && other.Function == call.Function && IrShape.Same(other, call) && !between.WritesWhatIsRead(reader)) {
            later.Add(next);
            used.Add(next);
            continue;
          }

          between = between.With(effects.Node(candidate.Node));
        }

        if (later.Count == 0)
          continue;

        var variable = new IrLocal(IrPassSupport.Fresh("$" + Simple(call.Function), this._taken), call.Type);
        this.Made.Add(variable);
        declared.Add(new IrDeclare(variable, call.Type.IsNumeric || call.Type == IrType.Bool ? new IrConst(call.Type, 0) : null));
        // The first call as it is rebuilt, with what was merged inside its arguments.
        replacements[first] = rebuilt => new IrBind(variable, rebuilt);
        foreach (var merged in later)
          replacements[merged] = _ => new IrLocalRef(variable);
      }

      return replacements.Count == 0 ? expression : Rebuild(expression, replacements, new Counter());

      // Every node after its operands, as it is evaluated, with the branches of the choices it is in.
      int Order(IrExpr node, HashSet<int> inside) {
        var start = order.Count;
        var operands = IrTree.Operands(node);
        for (var index = 0; index < operands.Count; ++index) {
          var branch = node switch {
            IrBinary { Op: IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr } when index == 1 => true,
            IrConditional when index > 0 => true,
            IrCoalesce when index == 1 => true,
            _ => false
          };
          Order(operands[index], branch ? [.. inside, ++guards] : inside);
        }

        order.Add(new Evaluated(node, start, inside));
        return start;
      }
    }

    /// <summary>Whether a call may be merged with the same call after it: a function that writes and makes nothing, with arguments that may be evaluated anywhere.</summary>
    private bool Mergeable(IrCall call) => effects.Function(call.Function).IsReadOnly && call.Arguments.All(effects.IsPure);

    /// <summary>The letters, digits and underscores of a name, which every target writes as a name of its own.</summary>
    private static string Simple(string name) => new([.. name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')]);

    private sealed class Counter {
      public int Next { get; set; }
    }

    /// <summary>The expression with the nodes at the positions of the order replaced, the positions counted as <see cref="Expression"/> counts them.</summary>
    private static IrExpr Rebuild(IrExpr node, Dictionary<int, Func<IrExpr, IrExpr>> replacements, Counter counter) {
      var operands = IrTree.Operands(node);
      var mapped = operands.Select(operand => Rebuild(operand, replacements, counter)).ToList();
      var position = counter.Next++;
      var rebuilt = mapped.Where((operand, index) => !ReferenceEquals(operand, operands[index])).Any() ? IrTree.With(node, mapped) : node;
      return replacements.TryGetValue(position, out var replacement) ? replacement(rebuilt) : rebuilt;
    }

  }

}
