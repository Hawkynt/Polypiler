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
      switch (type) {
        case IrStructType structure:
          foreach (var field in structure.Fields)
            CheckType(field.Type, problems);
          break;
        case IrClassType cls:
          foreach (var field in cls.Fields)
            CheckType(field.Type, problems);
          break;
        case IrEnumType or IrInterfaceType:
          break;
        default:
          problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedType, null, type.Name));
          break;
      }
    }

    foreach (var foreign in module.ForeignFunctions) {
      CheckType(foreign.ReturnType, problems);
      foreach (var parameter in foreign.Parameters) {
        CheckType(parameter.Type, problems);
        if (parameter.Marshal is { Kind: IrMarshalKind.ByRef, NativeSize: true })
          problems.Add(Diagnostic.Create(MsilEmitter.UnsupportedConstruct, null, "native-size by-reference foreign parameter"));
      }
    }

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
      or IrTypeKind.String or IrTypeKind.Struct or IrTypeKind.Object => true,
    IrTypeKind.Array or IrTypeKind.List or IrTypeKind.Set or IrTypeKind.Sequence
      or IrTypeKind.Async or IrTypeKind.Span or IrTypeKind.ReadOnlySpan
      or IrTypeKind.Memory or IrTypeKind.ReadOnlyMemory or IrTypeKind.Pointer => IsSupportedType(type.Element!),
    IrTypeKind.Map => IsSupportedType(type.Key!) && IsSupportedType(type.Element!),
    IrTypeKind.Optional => IsSupportedType(type.Underlying!),
    IrTypeKind.Function => IsSupportedType(type.Signature!.Result) && type.Signature.Parameters.All(IsSupportedType),
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
    IrIntrinsicOp.ParallelBitExtract or IrIntrinsicOp.ParallelBitDeposit => true,
    IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig
      or IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig => intrinsic.Arguments[0].Type.IsArray || intrinsic.Arguments[0].Type.IsView,
    IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice or IrIntrinsicOp.ViewLength or IrIntrinsicOp.StackAlloc
      or IrIntrinsicOp.ViewCopy or IrIntrinsicOp.ViewTryCopy or IrIntrinsicOp.ViewFill
      or IrIntrinsicOp.ViewEqual or IrIntrinsicOp.ViewToArray => true,
    _ when IrPointers.IsPointerOperation(intrinsic.Op) => true,
    IrIntrinsicOp.StrLength or IrIntrinsicOp.StrCodeUnitAt or IrIntrinsicOp.StrConcat
      or IrIntrinsicOp.StrEquals or IrIntrinsicOp.StrCompareOrdinal
      or IrIntrinsicOp.StrStartsWith or IrIntrinsicOp.StrEndsWith or IrIntrinsicOp.StrContains
      or IrIntrinsicOp.StrIndexOf or IrIntrinsicOp.StrSubstring or IrIntrinsicOp.StrJoin
      or IrIntrinsicOp.FormatDotNet => true,
    IrIntrinsicOp.FormatEcma => intrinsic.Arguments[0].Type.IsInteger,
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
      case IrWriteLine write:
        CheckExpr(write.Value, problems);
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
        CheckExpr(loop.Condition, problems);
        CheckStmt(loop.Body, problems);
        CheckStmt(loop.Step, problems);
        break;
      case IrSwitch selection:
        CheckExpr(selection.Value, problems);
        foreach (var section in selection.Sections)
          CheckStmt(section.Body, problems);
        break;
      case IrBreak or IrContinue or IrRethrow or IrYieldBreak:
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
          if (handler.Filter is not null)
            CheckExpr(handler.Filter, problems);
          CheckStmt(handler.Body, problems);
        }
        if (trying.Finally is not null)
          CheckStmt(trying.Finally, problems);
        break;
      case IrUsing scope:
        CheckType(scope.Resource.Type, problems);
        CheckExpr(scope.Initializer, problems);
        CheckStmt(scope.Body, problems);
        break;
      case IrForEachCollection fe:
        CheckExpr(fe.Collection, problems);
        if (fe.Element is not null)
          CheckType(fe.Element.Type, problems);
        if (fe.Value is not null)
          CheckType(fe.Value.Type, problems);
        CheckStmt(fe.Body, problems);
        break;
      case IrYield yielded:
        CheckExpr(yielded.Value, problems);
        break;
      case IrYieldFrom delegated:
        CheckExpr(delegated.Sequence, problems);
        break;
      case IrForEachSequence feSeq:
        CheckType(feSeq.Element.Type, problems);
        CheckExpr(feSeq.Sequence, problems);
        CheckStmt(feSeq.Body, problems);
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
        or IrStructDefault or IrNone or IrNull or IrLastError or IrFunctionRef:
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
      case IrForeignCall call:
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
      case IrNew create:
        foreach (var arg in create.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrMethodCall call:
        if (call.Target is not null)
          CheckExpr(call.Target, problems);
        foreach (var arg in call.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrReferenceEquals eq:
        CheckExpr(eq.Left, problems);
        CheckExpr(eq.Right, problems);
        break;
      case IrTypeTest test:
        CheckExpr(test.Value, problems);
        CheckType(test.Target, problems);
        break;
      case IrCast cast:
        CheckExpr(cast.Value, problems);
        break;
      case IrAsCast asCast:
        CheckExpr(asCast.Value, problems);
        break;
      case IrGenericEquals geq:
        CheckExpr(geq.Left, problems);
        CheckExpr(geq.Right, problems);
        break;
      case IrGenericCompare gcmp:
        CheckExpr(gcmp.Left, problems);
        CheckExpr(gcmp.Right, problems);
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
      case IrLambda lambda:
        foreach (var p in lambda.Parameters)
          CheckType(p.Type, problems);
        foreach (var l in lambda.Locals)
          CheckType(l.Type, problems);
        CheckStmt(lambda.Body, problems);
        break;
      case IrBoundMethod bound:
        if (bound.Receiver is not null)
          CheckExpr(bound.Receiver, problems);
        break;
      case IrInvoke invoke:
        CheckExpr(invoke.Callee, problems);
        foreach (var arg in invoke.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrCollection collection:
        foreach (var arg in collection.Arguments)
          CheckExpr(arg, problems);
        break;
      case IrArraySequence arrSeq:
        CheckExpr(arrSeq.Array, problems);
        break;
      case IrAwait awaited:
        CheckExpr(awaited.Operand, problems);
        break;
      case IrAsync asyncOp:
        foreach (var arg in asyncOp.Arguments)
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
