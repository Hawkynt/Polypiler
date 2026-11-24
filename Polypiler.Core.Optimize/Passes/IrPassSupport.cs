using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Polypiler.Core.IR;
using Polypiler.Core.Recognize;

namespace Polypiler.Core.Optimize.Passes;

/// <summary>What the passes over a legalized module share: rebuilding statements, the variables a lambda shares, fresh names and the value of a constant expression.</summary>
internal static class IrPassSupport {

  /// <summary>A statement with its own expressions and its blocks mapped, in the order <see cref="IrTree.Expressions"/> and <see cref="IrTree.Blocks"/> list them.</summary>
  public static IrStmt Map(IrStmt statement, Func<IrExpr, IrExpr> expression, Func<IrBlock, IrBlock> block) => statement switch {
    IrBlock inner => block(inner),
    IrDeclare declare => declare with { Initializer = declare.Initializer is null ? null : expression(declare.Initializer) },
    IrAssign assign => assign with { Value = expression(assign.Value) },
    IrGlobalAssign assign => assign with { Value = expression(assign.Value) },
    IrStore store => new IrStore(expression(store.Array), expression(store.Index), expression(store.Value)),
    IrFieldStore store => store with { Target = expression(store.Target), Value = expression(store.Value) },
    IrExprStmt statementExpression => new IrExprStmt(expression(statementExpression.Expression)),
    IrWriteLine write => write with { Value = expression(write.Value) },
    IrIf conditional => new IrIf(expression(conditional.Condition), block(conditional.Then), conditional.Else is null ? null : block(conditional.Else)),
    IrWhile loop => new IrWhile(expression(loop.Condition), block(loop.Body)),
    IrDoWhile loop => new IrDoWhile(block(loop.Body), expression(loop.Condition)),
    IrFor loop => new IrFor(expression(loop.Condition), block(loop.Body), block(loop.Step)) { PerIteration = loop.PerIteration },
    IrSwitch choice => new IrSwitch(expression(choice.Value), [.. choice.Sections.Select(section => section with { Body = block(section.Body) })]),
    IrReturn result => new IrReturn(result.Value is null ? null : expression(result.Value)),
    IrThrow thrown => new IrThrow(expression(thrown.Exception)),
    IrTry attempt => new IrTry(block(attempt.Body),
      [.. attempt.Catches.Select(clause => clause with { Filter = clause.Filter is null ? null : expression(clause.Filter), Body = block(clause.Body) })],
      attempt.Finally is null ? null : block(attempt.Finally)),
    IrUsing scope => scope with { Initializer = expression(scope.Initializer), Body = block(scope.Body) },
    IrForEachCollection loop => loop with { Collection = expression(loop.Collection), Body = block(loop.Body) },
    IrYield produced => new IrYield(expression(produced.Value)),
    IrYieldFrom delegated => new IrYieldFrom(expression(delegated.Sequence)),
    IrForEachSequence loop => loop with { Sequence = expression(loop.Sequence), Body = block(loop.Body) },
    _ => statement
  };

  /// <summary>Every statement of a block, the statements of the blocks within included, in pre-order.</summary>
  public static IEnumerable<IrStmt> Statements(IrBlock block) => block.Statements.SelectMany(IrStatements.All);

  /// <summary>Every expression node of a function, those in the bodies of lambdas included.</summary>
  public static IEnumerable<IrExpr> Nodes(IrBlock body) => body.Statements.SelectMany(IrTree.Nodes);

  /// <summary>
  /// The variables a lambda of a function names: a value they hold may be read or written where the
  /// lambda runs, so no pass assumes what they hold, nor removes or renames them.
  /// </summary>
  public static HashSet<IrLocal> Shared(IrFunction function) {
    var shared = new HashSet<IrLocal>();
    foreach (var lambda in Nodes(function.Body).OfType<IrLambda>()) {
      shared.UnionWith(lambda.Body.Statements.SelectMany(IrTree.Nodes).OfType<IrLocalRef>().Select(reference => reference.Local));
      foreach (var statement in lambda.Body.Statements)
        shared.UnionWith(IrStatements.Writes(statement));
    }

    return shared;
  }

  /// <summary>How often a function reads each variable, its lambdas included.</summary>
  public static Dictionary<IrLocal, int> Reads(IrBlock body) {
    var reads = new Dictionary<IrLocal, int>();
    foreach (var reference in Nodes(body).OfType<IrLocalRef>())
      reads[reference.Local] = reads.GetValueOrDefault(reference.Local) + 1;

    return reads;
  }

  /// <summary>The variables a function writes other than by its declaration of them: assignments, steps and bindings.</summary>
  public static Dictionary<IrLocal, int> Writes(IrBlock body) {
    var writes = new Dictionary<IrLocal, int>();
    foreach (var statement in Statements(body))
      if (statement is IrAssign assign)
        writes[assign.Local] = writes.GetValueOrDefault(assign.Local) + 1;
      else if (statement is IrUsing scope)
        writes[scope.Resource] = writes.GetValueOrDefault(scope.Resource) + 1;
      else if (statement is IrForEachSequence loop)
        writes[loop.Element] = writes.GetValueOrDefault(loop.Element) + 1;
      else if (statement is IrFieldStore { Target: var target } && IrStatements.Root(target) is { } root)
        writes[root] = writes.GetValueOrDefault(root) + 1;

    foreach (var node in Nodes(body))
      if (node is IrStep { Target: IrLocalRef { Local: var stepped } })
        writes[stepped] = writes.GetValueOrDefault(stepped) + 1;
      else if (node is IrBind bind)
        writes[bind.Local] = writes.GetValueOrDefault(bind.Local) + 1;

    return writes;
  }

  /// <summary>How many statements of a function declare each variable.</summary>
  public static Dictionary<IrLocal, int> Declarations(IrBlock body) {
    var declarations = new Dictionary<IrLocal, int>();
    foreach (var declare in Statements(body).OfType<IrDeclare>())
      declarations[declare.Local] = declarations.GetValueOrDefault(declare.Local) + 1;

    return declarations;
  }

  /// <summary>
  /// Whether an expression may be evaluated anywhere, any number of times or not at all: it has no
  /// effect and never traps (<see cref="IrShape.IsSideEffectFree"/>, <see cref="IrShape.IsTrapFree"/>).
  /// </summary>
  public static bool IsPure(IrExpr expression) {
    // Node by node and remembered per node: the passes ask it of an expression and of each of its parts.
    if (_purity.TryGetValue(expression, out var known))
      return known.Value;

    var pure = IrShape.IsSideEffectFreeNode(expression) && IrShape.IsTrapFreeNode(expression) && IrShape.Operands(expression).All(IsPure);
    _purity.AddOrUpdate(expression, new StrongBox<bool>(pure));
    return pure;
  }

  private static readonly ConditionalWeakTable<IrExpr, StrongBox<bool>> _purity = [];

  /// <summary>A name not yet taken, which is then taken: the name itself, or it with the smallest number from 2 on.</summary>
  public static string Fresh(string name, HashSet<string> taken) {
    var candidate = name;
    for (var number = 2; !taken.Add(candidate); ++number)
      candidate = $"{name}{number}";

    return candidate;
  }

  /// <summary>Whether a block never runs to its end: its last statement returns, throws, breaks or continues, or is a choice or a block of which that holds for every way through.</summary>
  public static bool Leaves(IrBlock block) => block.Statements.Count > 0 && block.Statements[^1] switch {
    IrReturn or IrThrow or IrBreak or IrContinue => true,
    IrBlock inner => Leaves(inner),
    IrIf { Else: { } otherwise } conditional => Leaves(conditional.Then) && Leaves(otherwise),
    _ => false
  };

  /// <summary>The size of a block as the cost rules count it: every statement and every expression node.</summary>
  public static int Size(IrBlock block) => Statements(block).Count() + Nodes(block).Count();

  /// <summary>
  /// The value of an expression whose operands are constants, as the interpreter computes it; or
  /// <see langword="null"/> when it is no integer or truth value, or when it traps: a trap stays where
  /// the program has it.
  /// </summary>
  public static IrConst? Evaluate(IrExpr expression) {
    if (!(expression.Type.IsInteger || expression.Type == IrType.Bool))
      return null;

    var function = new IrFunction("value", true, expression.Type, [], [], new IrBlock([new IrReturn(expression)]));
    try {
      return Constant(expression.Type, IrInterpreter.Call(new IrModule("constant", [function]), function.Name, [])!);
    } catch (IrException) {
      return null;
    }
  }

  /// <summary>A value as the interpreter gives it back across its boundary, as a constant of its type.</summary>
  internal static IrConst Constant(IrType type, object value) => new(type, value switch {
    bool truth => truth ? 1 : 0,
    ulong word => unchecked((long)word),
    _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
  });

}
