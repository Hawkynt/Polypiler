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

  /// <summary>Whether the file reads <c>Err.Number</c>, so that a <c>Catch</c> sets it.</summary>
  private bool ReadsErr => tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
    .Any(access => access.Name.Identifier.ValueText == "Number" && access.Expression is not null && IsErr(this._model.GetOperation(access.Expression)));

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
      case ("Raise", 1) when given[0].Value.ConstantValue is { HasValue: true, Value: int number }:
        return this.RaiseNumber(new IrConst(IrType.I32, number));
      case ("Raise", 1):
        // A number known only at run time: the exception of its class, or one that carries it.
        var statements = new List<IrStmt>();
        var held = new IrLocalRef(this.Temporary(IrType.I32, statements, this.Value(given[0].Value)));
        IrStmt chain = this.RaiseNumber(held);
        foreach (var number in new[] { 91, 13, 11, 9, 6, 5 })
          chain = new IrIf(new IrBinary(IrBinaryOp.Equal, held, new IrConst(IrType.I32, number)), new IrBlock([Throw(ClassOf(number)!)]), new IrBlock([chain]));
        return new IrBlock([.. statements, chain]);
      default:
        this.Refuse(call.Syntax, $"'{call.Syntax}': of Err only Number, Clear and Raise of a number");
        return new IrBlock([]);
    }
  }

  private IrGlobal? _raised;

  /// <summary>
  /// The number of an error raised that no exception class of the IR stands for, until a trap reads it:
  /// the runtime carries it in the exception, the IR in this module variable beside a plain <c>Exception</c>.
  /// </summary>
  private IrGlobal Raised => this._raised ??= new IrGlobal("ErrRaised", IrType.I32, new IrConst(IrType.I32, 0));

  /// <summary>Whether the file raises errors of any number: <c>Err.Raise</c> or <c>Resume</c>, which raises 20 outside a handler.</summary>
  private bool RaisesAnyNumber => tree.GetRoot().DescendantNodes().Any(node => node is ResumeStatementSyntax
    || node is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Raise" } access && IsErr(this._model.GetOperation(access.Expression)));

  /// <summary>The raise of an error whose number has no exception class: the number kept, a plain <c>Exception</c> thrown.</summary>
  private IrStmt RaiseNumber(IrExpr number) => new IrBlock([new IrGlobalAssign(this.Raised, number), Throw(IrExceptionClass.Exception)]);

  /// <summary>Sets <c>Err.Number</c> from a trapped exception of a class: its number, a plain <c>Exception</c> the number raised with it.</summary>
  private List<IrStmt> SetErrFrom(IrClassType type, int number) {
    if (type != IrExceptionClass.Exception || !this._raisesAny)
      return [this.SetErr(number)];

    var raised = new IrGlobalRef(this.Raised);
    return [
      new IrGlobalAssign(this.Err, new IrConditional(new IrBinary(IrBinaryOp.NotEqual, raised, new IrConst(IrType.I32, 0)), raised, new IrConst(IrType.I32, number))),
      new IrGlobalAssign(this.Raised, new IrConst(IrType.I32, 0))
    ];
  }

  /// <summary>The body of a <c>Catch</c>, which sets <c>Err.Number</c> first where the file reads it.</summary>
  private IrBlock CatchBody(ICatchClauseOperation clause, IrClassType type, IrLocal? caught) {
    var body = this.Body(clause.Handler);
    var forget = type == IrExceptionClass.Exception && this._raisesAny ? new IrGlobalAssign(this.Raised, new IrConst(IrType.I32, 0)) : null;
    if (!this._readsErr)
      return forget is null ? body : new IrBlock([forget, .. body.Statements]);

    // The number of the class, or of the class of the exception caught, tested from the most derived.
    List<IrStmt> statements = NumberOf(type) is { } number ? [this.SetErr(number)] : [
      new IrGlobalAssign(this.Err, _errors.Where(error => error.Class != IrExceptionClass.Exception && error.Class.DerivesFrom(type)).Reverse()
        .Aggregate(forget is null ? new IrConst(IrType.I32, 5) : (IrExpr)new IrConditional(new IrBinary(IrBinaryOp.NotEqual, new IrGlobalRef(this.Raised), new IrConst(IrType.I32, 0)), new IrGlobalRef(this.Raised), new IrConst(IrType.I32, 5)),
          (otherwise, error) => new IrConditional(new IrTypeTest(new IrLocalRef(caught!), IrType.Object(error.Class)), new IrConst(IrType.I32, error.Number), otherwise)))
    ];
    if (forget is not null)
      statements.Add(forget);
    statements.AddRange(body.Statements);
    // The compiler's code clears Err again where the Catch ends.
    if (Completes(new IrBlock(statements)))
      statements.Add(this.SetErr(0));
    return new IrBlock(statements);
  }

}
