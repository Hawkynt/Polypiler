using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

/// <summary>
/// <c>Err</c> of Visual Basic .NET: <c>Err.Number</c> a module variable of the IR, set where an error
/// is trapped from the class of the exception as the runtime maps it (the table of the dialect
/// <c>vbnet</c> in <c>spec/languages/visualbasic/vbnet-errors.yaml</c>), and by a <c>Catch</c> as the
/// compiler's code sets it; <c>Err.Clear</c> and <c>Err.Raise</c> of a number the IR has a class of.
/// </summary>
internal sealed partial class VbNetLowerer {

  /// <summary>The exception classes an error trap tells apart, with the <c>Err.Number</c> of each; the last catches every other.</summary>
  private static readonly (IrClassType Class, int Number)[] _errors = [
    (IrExceptionClass.DivideByZeroException, 11),
    (IrExceptionClass.OverflowException, 6),
    (IrExceptionClass.IndexOutOfRangeException, 9),
    (IrExceptionClass.InvalidCastException, 13),
    (IrExceptionClass.NullReferenceException, 91),
    (IrExceptionClass.Exception, 5)
  ];

  private IrGlobal? _err;

  /// <summary>The module variable that is <c>Err.Number</c>.</summary>
  private IrGlobal Err => this._err ??= new IrGlobal("ErrNumber", IrType.I32, new IrConst(IrType.I32, 0));

  private IrStmt SetErr(int number) => new IrGlobalAssign(this.Err, new IrConst(IrType.I32, number));

  private static IrStmt Throw(IrClassType type) => new IrThrow(new IrNew(IrType.Object(type), IrExceptionClass.Constructor(type, 0), []));

  /// <summary>Whether an operation is the call of <c>Err</c>.</summary>
  private static bool IsErr(IOperation? operation)
    => operation is IInvocationOperation { TargetMethod: { Name: "Err", ContainingType: { Name: "Information", ContainingNamespace.Name: "VisualBasic" } } };

  /// <summary>Whether the file calls <c>Err</c>, so that a <c>Catch</c> sets <c>Err.Number</c>.</summary>
  private bool ReadsErr => tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
    .Any(name => name.Identifier.ValueText == "Err" && this._model.GetSymbolInfo(name).Symbol is IMethodSymbol { ContainingType.Name: "Information" });

  /// <summary>The exception class <c>Err.Raise</c> throws for an error number, or <see langword="null"/> for a number the IR has no class of.</summary>
  private static IrClassType? ClassOf(object? number) => number switch {
    5 => IrExceptionClass.ArgumentException,
    6 => IrExceptionClass.OverflowException,
    9 => IrExceptionClass.IndexOutOfRangeException,
    11 => IrExceptionClass.DivideByZeroException,
    13 => IrExceptionClass.InvalidCastException,
    91 => IrExceptionClass.NullReferenceException,
    _ => null
  };

  /// <summary>The <c>Err.Number</c> of an exception of a class, or <see langword="null"/> where a class below it has another.</summary>
  private static int? NumberOf(IrClassType type) {
    if (type != IrExceptionClass.Exception && _errors.Where(error => error.Class == type).Select(error => (int?)error.Number).FirstOrDefault() is { } exact)
      return exact;
    return _errors.Any(error => error.Class != type && error.Class.DerivesFrom(type)) ? null : 5;
  }

  /// <summary><c>Err.Number</c>, or <see langword="null"/> for any other operation.</summary>
  private IrExpr? ErrValue(IOperation operation)
    => operation is IPropertyReferenceOperation { Property.Name: "Number" } number && IsErr(number.Instance) ? new IrGlobalRef(this.Err) : null;

  /// <summary><c>Err.Clear()</c> or <c>Err.Raise(n)</c> as a statement, or <see langword="null"/> for any other call.</summary>
  private IrStmt? ErrCall(IInvocationOperation call) {
    if (!IsErr(call.Instance))
      return null;

    var given = call.Arguments.Where(argument => argument.ArgumentKind != ArgumentKind.DefaultValue).ToList();
    switch (call.TargetMethod.Name, given.Count) {
      case ("Clear", 0):
        return this.SetErr(0);
      case ("Raise", 1) when ClassOf(given[0].Value.ConstantValue.Value) is { } type:
        return Throw(type);
      default:
        this.Refuse(call.Syntax, $"'{call.Syntax}': of Err only Number, Clear and Raise of the numbers 5, 6, 9, 11, 13 and 91, which the IR's exception classes stand for");
        return new IrBlock([]);
    }
  }

  /// <summary>The body of a <c>Catch</c>, which sets <c>Err.Number</c> first where the file reads it.</summary>
  private IrBlock CatchBody(ICatchClauseOperation clause, IrClassType type) {
    var body = this.Body(clause.Handler);
    if (!this._readsErr)
      return body;

    if (NumberOf(type) is not { } number) {
      this.Refuse(clause.Syntax, $"a Catch of {type.Name} in a file that reads Err, whose number depends on the class of the exception");
      return body;
    }

    // The compiler's code clears Err again where the Catch ends.
    List<IrStmt> statements = [this.SetErr(number), .. body.Statements];
    if (Completes(new IrBlock(statements)))
      statements.Add(this.SetErr(0));
    return new IrBlock(statements);
  }

}
