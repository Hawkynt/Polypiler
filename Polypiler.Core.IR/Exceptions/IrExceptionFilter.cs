using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// When an exception filter may run late. The IR runs a filter in the first pass, before the finally
/// blocks between the throw and the handler (<see cref="IrThrow"/>). A target without filters - C#
/// below 6, JavaScript at every level - can only catch first and test after, when those finally
/// blocks have run; it tests the clauses in order in one catch block, so a clause after the filtered
/// one is still tried. That gives the same result exactly when the filter has no effect, cannot trap
/// (an exception in a filter makes it false) and reads no variable a finally block inside the try writes.
/// </summary>
public static class IrExceptionFilter {

  /// <summary>Whether the filter of a clause gives the same result when it is tested after the exception is caught.</summary>
  /// <param name="attempt">The try.</param>
  /// <param name="clause">One of its catch clauses, with a filter.</param>
  /// <returns><see langword="true"/> when a target may test the filter after catching.</returns>
  public static bool MayRunLate(IrTry attempt, IrCatch clause) {
    if (clause.Filter is not { } filter)
      return true;

    if (!IrTree.Nodes(filter).All(node => Pure(node, clause.Local)))
      return false;

    var written = new HashSet<IrLocal>(FinallyBlocks(attempt.Body).SelectMany(Assigned));
    return !IrTree.Nodes(filter).OfType<IrLocalRef>().Any(reference => written.Contains(reference.Local));
  }

  /// <summary>Whether a node of a filter has no effect and cannot trap: constants, variables, arithmetic that wraps, comparisons, the message of the exception caught, string comparisons.</summary>
  private static bool Pure(IrExpr node, IrLocal? caught) => node switch {
    IrConst or IrStrConst or IrLocalRef or IrConditional => true,
    IrConstRef reference => !reference.Type.IsArray,
    IrUnary unary => unary.Op != IrUnaryOp.NegateChecked,
    IrBinary binary => !IrOperators.Traps(binary.Op) && !(binary.Left.Type.IsInteger && binary.Op is IrBinaryOp.Divide or IrBinaryOp.Remainder),
    IrConvert conversion => conversion.Contract == IrConversion.Wrap && !conversion.Operand.Type.IsFloat,
    // The fields of an exception are read-only: no finally block can change what the filter reads of them.
    IrFieldLoad { Target: IrLocalRef reference } load => reference.Local == caught && IrExceptionClass.Exception.Owns(load.Field),
    IrReferenceEquals or IrIsNull => true,
    IrIntrinsic intrinsic => intrinsic.Op is IrIntrinsicOp.StrEquals or IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith or IrIntrinsicOp.StrContains or IrIntrinsicOp.StrLength,
    _ => false
  };

  /// <summary>Every finally block within a block, those of nested tries included.</summary>
  private static IEnumerable<IrBlock> FinallyBlocks(IrStmt statement) {
    var nested = (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).SelectMany(FinallyBlocks);
    return statement is IrTry { Finally: { } final } ? nested.Prepend(final) : nested;
  }

  /// <summary>Every local a block or a statement in it assigns.</summary>
  private static IEnumerable<IrLocal> Assigned(IrStmt statement) {
    IEnumerable<IrLocal> own = statement switch {
      IrAssign assign => [assign.Local],
      IrDeclare declare => [declare.Local],
      _ => []
    };
    var stepped = IrTree.Expressions(statement).SelectMany(IrTree.Nodes).SelectMany(node => node switch {
      IrStep { Target: IrLocalRef reference } => [reference.Local],
      IrBind bind => [bind.Local],
      _ => (IEnumerable<IrLocal>)[]
    });
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    return own.Concat(stepped).Concat(inner.SelectMany(Assigned));
  }

}
