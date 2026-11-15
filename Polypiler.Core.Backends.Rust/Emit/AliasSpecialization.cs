using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// Calls that pass one array variable to two parameters, as an operation in place does
/// (<c>Multiply(p, p, z)</c>): safe Rust cannot borrow one buffer twice where one borrow writes it.
/// The call goes to a copy of the callee whose second parameter is the first one, every use of it
/// named by the first: the same array under one name, which is what the call meant. A callee that
/// assigns either parameter keeps both; the call is then left to the aliasing refusal. A callee no
/// call reaches any more and nobody outside the module sees is dropped.
/// </summary>
internal static class AliasSpecialization {

  /// <summary>The module with every call that passes one array twice going to a copy of its callee made for it.</summary>
  public static IrModule Run(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    var functions = module.Functions.ToList();
    var copies = new Dictionary<string, IrFunction>(StringComparer.Ordinal);
    var changed = false;
    for (var round = 0; round < 8; ++round) {
      var byName = functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      var facts = ArrayOwnership.Of(module with { Functions = functions });
      var next = functions.Select(function => Rewritten(function, byName, copies, facts)).ToList();
      next.AddRange(copies.Values.Where(copy => !next.Any(function => function.Name == copy.Name) && !functions.Any(function => function.Name == copy.Name)));
      if (next.SequenceEqual(functions, ReferenceEqualityComparer.Instance))
        break;

      functions = next;
      changed = true;
    }

    if (!changed)
      return module;

    // A private callee only the copies are called in place of is gone.
    var called = functions.SelectMany(function => ArrayOwnership.Statements(function.Body).SelectMany(IrTree.Nodes)).OfType<IrCall>().Select(call => call.Function).ToHashSet(StringComparer.Ordinal);
    var kept = functions.Where(function => function.IsPublic || called.Contains(function.Name) || module.Implementations.Any(pair => pair.Function == function.Name) || !copies.Values.Any(copy => copy.Name.StartsWith(function.Name + "Aliased", StringComparison.Ordinal))).ToList();
    return module with { Functions = kept };
  }

  /// <summary>A function with its calls that pass one array twice going to the copies made for them.</summary>
  private static IrFunction Rewritten(IrFunction function, Dictionary<string, IrFunction> byName, Dictionary<string, IrFunction> copies, IReadOnlyDictionary<string, FunctionArrays> facts) {
    var body = Rewrite(function.Body, expression => {
      if (expression is not IrCall call || !byName.TryGetValue(call.Function, out var callee) || Merged(call, callee, facts[callee.Name]) is not { Count: > 0 } merged)
        return expression;

      var name = $"{callee.Name}Aliased{string.Concat(merged.Select(pair => pair.Kept.ToString(CultureInfo.InvariantCulture) + pair.Dropped.ToString(CultureInfo.InvariantCulture)))}";
      if (!copies.ContainsKey(name))
        copies[name] = Copy(callee, name, merged);

      var dropped = merged.Select(pair => pair.Dropped).ToHashSet();
      return call with { Function = name, Arguments = [.. call.Arguments.Where((_, index) => !dropped.Contains(index))] };
    });
    return ReferenceEquals(body, function.Body) ? function : function with { Body = body };
  }

  /// <summary>
  /// The pairs of parameters a call passes one array variable to where the callee writes one of the
  /// two, the later one dropped; none where the callee assigns one of them.
  /// </summary>
  private static List<(int Kept, int Dropped)> Merged(IrCall call, IrFunction callee, FunctionArrays facts) {
    var pairs = new List<(int, int)>();
    var dropped = new HashSet<int>();
    for (var later = 1; later < call.Arguments.Count; ++later) {
      if (call.Arguments[later] is not IrLocalRef { Type.IsArray: true } second)
        continue;

      for (var earlier = 0; earlier < later; ++earlier) {
        if (dropped.Contains(earlier) || call.Arguments[earlier] is not IrLocalRef first || first.Local != second.Local
          || !facts.MutableParameters.Contains(earlier) && !facts.MutableParameters.Contains(later))
          continue;

        if (Assigned(callee, callee.Parameters[earlier]) || Assigned(callee, callee.Parameters[later]))
          return [];

        pairs.Add((earlier, later));
        dropped.Add(later);
        break;
      }
    }

    return pairs;
  }

  private static bool Assigned(IrFunction function, IrLocal parameter)
    => ArrayOwnership.Statements(function.Body).Any(statement => statement is IrAssign assign && assign.Local == parameter);

  /// <summary>The copy of a function with the dropped parameters gone and every use of one named by the parameter it was merged into.</summary>
  private static IrFunction Copy(IrFunction callee, string name, List<(int Kept, int Dropped)> merged) {
    var renamed = merged.ToDictionary(pair => callee.Parameters[pair.Dropped], pair => callee.Parameters[pair.Kept]);
    var body = Rewrite(callee.Body, expression => expression is IrLocalRef reference && renamed.TryGetValue(reference.Local, out var kept) ? new IrLocalRef(kept) : expression);
    return callee with {
      Name = name,
      IsPublic = false,
      Parameters = [.. callee.Parameters.Where(parameter => !renamed.ContainsKey(parameter))],
      Body = body
    };
  }

  /// <summary>A block with every expression in it rewritten bottom-up; the block itself where nothing changed.</summary>
  internal static IrBlock Rewrite(IrBlock block, Func<IrExpr, IrExpr> rewrite) {
    var statements = block.Statements.Select(statement => Rewrite(statement, rewrite)).ToList();
    return statements.SequenceEqual(block.Statements, ReferenceEqualityComparer.Instance) ? block : new IrBlock(statements);
  }

  private static IrStmt Rewrite(IrStmt statement, Func<IrExpr, IrExpr> rewrite) {
    if (statement is IrBlock block)
      return Rewrite(block, rewrite);

    var expressions = IrTree.Expressions(statement);
    var blocks = IrTree.Blocks(statement);
    var newExpressions = expressions.Select(expression => Rewrite(expression, rewrite)).ToList();
    var newBlocks = blocks.Select(inner => Rewrite(inner, rewrite)).ToList();
    return newExpressions.SequenceEqual(expressions, ReferenceEqualityComparer.Instance) && newBlocks.SequenceEqual(blocks, ReferenceEqualityComparer.Instance)
      ? statement
      : IrTree.With(statement, newExpressions, newBlocks);
  }

  private static IrExpr Rewrite(IrExpr expression, Func<IrExpr, IrExpr> rewrite) {
    var operands = IrTree.Operands(expression);
    var newOperands = operands.Select(operand => Rewrite(operand, rewrite)).ToList();
    var rebuilt = newOperands.SequenceEqual(operands, ReferenceEqualityComparer.Instance) ? expression : IrTree.With(expression, newOperands);
    return rewrite(rebuilt);
  }

}
