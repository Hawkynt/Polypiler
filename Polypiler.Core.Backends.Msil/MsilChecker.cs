using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Msil;

/// <summary>Validates that an <see cref="IrModule"/> stays within the subset supported by the MSIL backend.</summary>
internal static class MsilChecker {

  /// <summary>Checks every declaration, global, constant and function of <paramref name="module"/>.</summary>
  public static IReadOnlyList<Diagnostic> Check(IrModule module) {
    var problems = new List<Diagnostic>();

    foreach (var type in module.Types) {
      if (type is IrStructType structure) {
        foreach (var field in structure.Fields)
          CheckType(field.Type, problems);
      } else if (type is not IrEnumType) {
        problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedType, null, type.Name));
      }
    }

    if (module.ForeignFunctions.Count > 0)
      problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, "foreign functions"));

    foreach (var constant in module.Constants)
      CheckType(constant.Type, problems);

    foreach (var global in module.Globals) {
      CheckType(global.Type, problems);
      if (global.Initializer is not null)
        CheckExpr(global.Initializer, problems);
    }

    foreach (var function in module.Functions) {
      CheckType(function.ReturnType, problems);
      foreach (var parameter in function.Parameters)
        CheckType(parameter.Type, problems);
      foreach (var local in function.Locals)
        CheckType(local.Type, problems);
      CheckStmt(function.Body, problems);
    }

    return [.. problems.Distinct()];
  }

  /// <summary>Whether a type has a direct MSIL representation in this backend.</summary>
  internal static bool IsSupportedType(IrType type) => type.Kind switch {
    IrTypeKind.Void or IrTypeKind.Bool or IrTypeKind.I8 or IrTypeKind.U8
      or IrTypeKind.I16 or IrTypeKind.U16 or IrTypeKind.I32 or IrTypeKind.U32
      or IrTypeKind.I64 or IrTypeKind.U64 or IrTypeKind.F32 or IrTypeKind.F64
      or IrTypeKind.String or IrTypeKind.Struct => true,
    IrTypeKind.Array => IsSupportedType(type.Element!),
    IrTypeKind.Optional => IsSupportedType(type.Underlying!),
    IrTypeKind.Object => type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls),
    _ => false
  };

  /// <summary>Whether an intrinsic operation is lowered natively by the MSIL backend.</summary>
  internal static bool IsSupportedIntrinsic(IrIntrinsic intrinsic) => intrinsic.Op switch {
    IrIntrinsicOp.ArrayOf or IrIntrinsicOp.ArrayCopy or IrIntrinsicOp.ArrayClear or IrIntrinsicOp.ArrayFill => true,
    IrIntrinsicOp.Abs or IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Sign => true,
    IrIntrinsicOp.Sqrt or IrIntrinsicOp.Floor or IrIntrinsicOp.Ceiling or IrIntrinsicOp.Truncate
      or IrIntrinsicOp.RoundEven or IrIntrinsicOp.RoundAway or IrIntrinsicOp.CopySign
      or IrIntrinsicOp.IsNaN or IrIntrinsicOp.IsInfinity or IrIntrinsicOp.IsFinite
      or IrIntrinsicOp.IsNormal or IrIntrinsicOp.IsSubnormal or IrIntrinsicOp.IsNegative
      or IrIntrinsicOp.FloatBits or IrIntrinsicOp.BitsFloat => true,
    IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight or IrIntrinsicOp.LeadingZeroCount
      or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.PopCount => intrinsic.Arguments[0].Type.Bits >= 32,
    IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig
      or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => intrinsic.Arguments[0].Type.IsArray,
    IrIntrinsicOp.StrLength or IrIntrinsicOp.StrCodeUnitAt or IrIntrinsicOp.StrConcat
      or IrIntrinsicOp.StrEquals or IrIntrinsicOp.StrCompareOrdinal
      or IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith or IrIntrinsicOp.StrContains
      or IrIntrinsicOp.StrIndexOf or IrIntrinsicOp.StrSubstring or IrIntrinsicOp.StrJoin => true,
    _ => false
  };

  private static void CheckType(IrType type, List<Diagnostic> problems) {
    if (!IsSupportedType(type))
      problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedType, null, type.ToString()));
  }

  private static void CheckStmt(IrStmt stmt, List<Diagnostic> problems) {
    switch (stmt) {
      case IrBlock block:
        foreach (var child in block.Statements)
          CheckStmt(child, problems);
        break;
      case IrDeclare declare:
        CheckType(declare.Local.Type, problems);
        if (declare.Initializer is not null)
          CheckExpr(declare.Initializer, problems);
        break;
      case IrAssign assign:
        CheckExpr(assign.Value, problems);
        break;
      case IrGlobalAssign globalAssign:
        CheckExpr(globalAssign.Value, problems);
        break;
      case IrStore store:
        CheckExpr(store.Array, problems);
        CheckExpr(store.Index, problems);
        CheckExpr(store.Value, problems);
        break;
      case IrFieldStore fieldStore:
        CheckExpr(fieldStore.Target, problems);
        CheckExpr(fieldStore.Value, problems);
        break;
      case IrExprStmt exprStmt:
        CheckExpr(exprStmt.Expression, problems);
        break;
      case IrIf conditional:
        CheckExpr(conditional.Condition, problems);
        CheckStmt(conditional.Then, problems);
        if (conditional.Else is not null)
          CheckStmt(conditional.Else, problems);
        break;
      case IrWhile loop:
        CheckExpr(loop.Condition, problems);
        CheckStmt(loop.Body, problems);
        break;
      case IrDoWhile loop:
        CheckStmt(loop.Body, problems);
        CheckExpr(loop.Condition, problems);
        break;
      case IrFor loop:
        if (loop.PerIteration.Count > 0)
          problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, "per-iteration loop bindings"));
        CheckExpr(loop.Condition, problems);
        CheckStmt(loop.Body, problems);
        CheckStmt(loop.Step, problems);
        break;
      case IrSwitch selection:
        CheckExpr(selection.Value, problems);
        foreach (var section in selection.Sections)
          CheckStmt(section.Body, problems);
        break;
      case IrBreak or IrContinue or IrRethrow:
        break;
      case IrReturn ret:
        if (ret.Value is not null)
          CheckExpr(ret.Value, problems);
        break;
      case IrThrow throwing:
        CheckExpr(throwing.Exception, problems);
        break;
      case IrTry trying:
        CheckStmt(trying.Body, problems);
        foreach (var handler in trying.Catches) {
          if (!IrExceptionClass.IsLibrary(handler.Class))
            problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedType, null, handler.Class.Name));
          if (handler.Filter is not null)
            CheckExpr(handler.Filter, problems);
          CheckStmt(handler.Body, problems);
        }
        if (trying.Finally is not null)
          CheckStmt(trying.Finally, problems);
        break;
      default:
        problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, stmt.GetType().Name));
        break;
    }
  }

  private static void CheckExpr(IrExpr expr, List<Diagnostic> problems) {
    CheckType(expr.Type, problems);
    switch (expr) {
      case IrConst or IrStrConst or IrConstRef or IrLocalRef or IrGlobalRef
        or IrStructDefault or IrNone or IrNull:
        break;
      case IrUnary unary:
        CheckExpr(unary.Operand, problems);
        break;
      case IrBinary binary:
        CheckExpr(binary.Left, problems);
        CheckExpr(binary.Right, problems);
        break;
      case IrConvert convert:
        CheckExpr(convert.Operand, problems);
        break;
      case IrConditional conditional:
        CheckExpr(conditional.Condition, problems);
        CheckExpr(conditional.WhenTrue, problems);
        CheckExpr(conditional.WhenFalse, problems);
        break;
      case IrArrayLoad load:
        CheckExpr(load.Array, problems);
        CheckExpr(load.Index, problems);
        break;
      case IrArrayLength length:
        CheckExpr(length.Array, problems);
        break;
      case IrNewArray create:
        CheckExpr(create.Length, problems);
        break;
      case IrCall call:
        foreach (var arg in call.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrStep step:
        CheckExpr(step.Target, problems);
        break;
      case IrStructNew create:
        foreach (var arg in create.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrFieldLoad fieldLoad:
        CheckExpr(fieldLoad.Target, problems);
        break;
      case IrStructEquals equals:
        CheckExpr(equals.Left, problems);
        CheckExpr(equals.Right, problems);
        break;
      case IrSome some:
        CheckExpr(some.Value, problems);
        break;
      case IrIsNone isNone:
        CheckExpr(isNone.Value, problems);
        break;
      case IrUnwrap unwrap:
        CheckExpr(unwrap.Value, problems);
        break;
      case IrCoalesce coalesce:
        CheckExpr(coalesce.Value, problems);
        CheckExpr(coalesce.Fallback, problems);
        break;
      case IrIsNull isNull:
        CheckExpr(isNull.Value, problems);
        break;
      case IrBind bind:
        CheckExpr(bind.Value, problems);
        break;
      case IrThrowExpression throwExpr:
        CheckExpr(throwExpr.Exception, problems);
        break;
      case IrNew create:
        if (create.Type.ClassType is not { } cls || !IrExceptionClass.IsLibrary(cls))
          problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, $"new {create.Type}"));
        foreach (var arg in create.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrIntrinsic intrinsic:
        if (!IsSupportedIntrinsic(intrinsic))
          problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedIntrinsic, null, intrinsic.Op.ToString()));
        foreach (var arg in intrinsic.Arguments)
          CheckExpr(arg, problems);
        break;
      default:
        problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, expr.GetType().Name));
        break;
    }
  }

}
