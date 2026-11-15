using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// The calls of <see cref="IrAsyncOp.WhenAll"/> whose order would be observable: one of the functions
  /// they call writes more than its own locals (<see cref="IrAsyncFunctions.Timed"/>).
  /// </summary>
  private static void Timed(IrModule module, List<Diagnostic> diagnostics) {
    foreach (var (function, _, callee) in IrAsyncFunctions.Timed(module))
      diagnostics.Add(Error(function.Name, $"WhenAll of '{callee}', which writes more than its own locals, whose order depends on timing"));
  }

  private sealed partial class FunctionCheck {

    /// <summary>The type a return of the body being checked gives: the result of an async function, the return type of any other function or lambda.</summary>
    private IrType Returned => this._returnType.IsAsync && this.InFunctionBody ? this._returnType.Element! : this._returnType;

    /// <summary>
    /// An await - in the body of an async function itself, of the call of an async function of the
    /// module or of an async value the IR makes - or an async value the IR makes: its operands fit it
    /// (<see cref="IrAsyncFunctions.Signature"/>) and the operands of a <see cref="IrAsyncOp.WhenAll"/>
    /// are calls of async functions.
    /// </summary>
    private void AsyncExpression(IrExpr expression) {
      if (expression is IrAwait awaited) {
        this.Expression(awaited.Operand);
        this.Require(function.IsAsync && this.InFunctionBody, this.InFunctionBody ? "an await outside an async function" : "an await inside a lambda");
        this.Require(awaited.Operand is IrCall { Type.IsAsync: true } or IrAsync, $"an await of a {awaited.Operand.GetType().Name}, which is neither the call of an async function nor an async value of the IR");
        this.Require(awaited.Type == (awaited.Operand.Type.IsAsync ? awaited.Operand.Type.Element : IrType.Void), $"an await of {awaited.Operand.Type} has the type {awaited.Type}");
        return;
      }

      var operation = (IrAsync)expression;
      foreach (var argument in operation.Arguments)
        this.Expression(argument);

      var (type, problem) = IrAsyncFunctions.Signature(operation.Op, [.. operation.Arguments.Select(argument => argument.Type)], operation.Type.Element?.Element);
      if (type is null) {
        this.Require(false, problem!);
        return;
      }

      this.Require(operation.Type == type, $"{operation.Op} has the type {operation.Type}, expected {type}");
      if (operation.Op == IrAsyncOp.WhenAll)
        this.Require(operation.Arguments.All(argument => argument is IrCall), "WhenAll of an async value that is not the call of an async function");
    }

    /// <summary>
    /// Where async values stand: each is the operand of an <see cref="IrAwait"/>, or of a
    /// <see cref="IrAsyncOp.WhenAll"/> that is - awaited where it is created, the subset whose order of
    /// effects .NET and JavaScript agree on.
    /// </summary>
    private void Awaited() {
      var nodes = function.Body.Statements.SelectMany(IrTree.Nodes).ToList();
      var awaited = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);
      foreach (var node in nodes.OfType<IrAwait>())
        awaited.Add(node.Operand);

      foreach (var operation in nodes.OfType<IrAsync>().Where(operation => operation.Op == IrAsyncOp.WhenAll && awaited.Contains(operation)))
        awaited.UnionWith(operation.Arguments);

      if (nodes.Any(node => node.Type.IsAsync && !awaited.Contains(node)))
        this.Require(false, IrAsyncFunctions.NotAwaited);
    }

  }

}
