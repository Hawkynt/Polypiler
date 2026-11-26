using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    /// <summary>Validates all types, constructs, and intrinsics in the module without emitting bytecode.</summary>
    public IReadOnlyList<Diagnostic> CheckOnly() {
      var problems = new List<Diagnostic>();
      foreach (var typeDecl in this._module.Types) {
        if (typeDecl is IrStructType st) {
          foreach (var field in st.Fields)
            CheckType(field.Type, $"field '{field.Name}' of struct '{st.Name}'", problems);
        } else if (typeDecl is IrClassType cls) {
          foreach (var field in cls.Fields)
            CheckType(field.Type, $"field '{field.Name}' of class '{cls.Name}'", problems);
        } else if (typeDecl is not (IrEnumType or IrInterfaceType)) {
          problems.Add(UnsupportedType.At($"declared type '{typeDecl.Name}' ({typeDecl.GetType().Name}) is not supported by the JVM backend"));
        }
      }

      foreach (var foreign in this._module.ForeignFunctions) {
        CheckType(foreign.ReturnType, $"return type of foreign function '{foreign.Name}'", problems);
        foreach (var param in foreign.Parameters)
          CheckType(param.Type, $"parameter '{param.Name}' of foreign function '{foreign.Name}'", problems);
      }

      foreach (var constant in this._module.Constants)
        CheckType(constant.Type, $"constant '{constant.Name}'", problems);

      foreach (var global in this._module.Globals) {
        CheckType(global.Type, $"global '{global.Name}'", problems);
        if (global.Initializer is { } init)
          CheckExpr(init, problems);
      }

      foreach (var function in this._module.Functions) {
        CheckType(function.ReturnType, $"return type of '{function.Name}'", problems);
        foreach (var param in function.Parameters)
          CheckType(param.Type, $"parameter '{param.Name}' of '{function.Name}'", problems);
        foreach (var local in function.Locals)
          CheckType(local.Type, $"local '{local.Name}' of '{function.Name}'", problems);

        CheckStmt(function.Body, problems);
      }

      return problems;
    }

    private static void CheckType(IrType type, string context, List<Diagnostic> problems) {
      if (!JvmTypeMapper.IsSupportedType(type))
        problems.Add(UnsupportedType.At($"type '{type}' in {context} is not supported by the JVM backend"));
    }

    private static void CheckStmt(IrStmt stmt, List<Diagnostic> problems) {
      switch (stmt) {
        case IrBlock block:
          foreach (var child in block.Statements)
            CheckStmt(child, problems);
          break;
        case IrDeclare decl:
          CheckType(decl.Local.Type, $"local '{decl.Local.Name}'", problems);
          if (decl.Initializer is { } init)
            CheckExpr(init, problems);
          break;
        case IrAssign assign:
          CheckExpr(assign.Value, problems);
          break;
        case IrGlobalAssign assign:
          CheckExpr(assign.Value, problems);
          break;
        case IrStore store:
          CheckExpr(store.Array, problems);
          CheckExpr(store.Index, problems);
          CheckExpr(store.Value, problems);
          break;
        case IrFieldStore store:
          CheckExpr(store.Target, problems);
          CheckExpr(store.Value, problems);
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
          if (conditional.Else is { } otherwise)
            CheckStmt(otherwise, problems);
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
        case IrSwitch choice:
          CheckExpr(choice.Value, problems);
          foreach (var section in choice.Sections)
            CheckStmt(section.Body, problems);
          break;
        case IrBreak or IrContinue or IrRethrow or IrYieldBreak:
          break;
        case IrReturn ret:
          if (ret.Value is { } val)
            CheckExpr(val, problems);
          break;
        case IrThrow thrown:
          CheckExpr(thrown.Exception, problems);
          break;
        case IrTry attempt:
          CheckStmt(attempt.Body, problems);
          foreach (var clause in attempt.Catches) {
            if (clause.Filter is { } filter)
              CheckExpr(filter, problems);
            CheckStmt(clause.Body, problems);
          }

          if (attempt.Finally is { } cleanup)
            CheckStmt(cleanup, problems);
          break;
        case IrUsing scope:
          CheckType(scope.Resource.Type, $"using resource '{scope.Resource.Name}'", problems);
          CheckExpr(scope.Initializer, problems);
          CheckStmt(scope.Body, problems);
          break;
        case IrForEachCollection loop:
          CheckExpr(loop.Collection, problems);
          CheckStmt(loop.Body, problems);
          break;
        case IrYield produced:
          CheckExpr(produced.Value, problems);
          break;
        case IrYieldFrom delegated:
          CheckExpr(delegated.Sequence, problems);
          break;
        case IrForEachSequence loop:
          CheckType(loop.Element.Type, $"sequence loop variable '{loop.Element.Name}'", problems);
          CheckExpr(loop.Sequence, problems);
          CheckStmt(loop.Body, problems);
          break;
        default:
          problems.Add(UnsupportedConstruct.At($"statement '{stmt.GetType().Name}' is not supported by the JVM backend"));
          break;
      }
    }

    private static void CheckExpr(IrExpr expr, List<Diagnostic> problems) {
      switch (expr) {
        case IrConst or IrStrConst or IrConstRef or IrLocalRef or IrGlobalRef:
          CheckType(expr.Type, "expression", problems);
          break;
        case IrNull:
          CheckType(expr.Type, "null literal", problems);
          break;
        case IrUnary unary:
          CheckExpr(unary.Operand, problems);
          break;
        case IrBinary binary:
          CheckExpr(binary.Left, problems);
          CheckExpr(binary.Right, problems);
          break;
        case IrConvert conv:
          CheckType(conv.Type, "conversion target", problems);
          CheckExpr(conv.Operand, problems);
          break;
        case IrConditional choice:
          CheckExpr(choice.Condition, problems);
          CheckExpr(choice.WhenTrue, problems);
          CheckExpr(choice.WhenFalse, problems);
          break;
        case IrArrayLoad load:
          CheckExpr(load.Array, problems);
          CheckExpr(load.Index, problems);
          break;
        case IrArrayLength length:
          CheckExpr(length.Array, problems);
          break;
        case IrNewArray create:
          CheckType(create.Type, "array allocation", problems);
          CheckExpr(create.Length, problems);
          break;
        case IrCall call:
          foreach (var arg in call.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrForeignCall foreignCall:
          CheckType(foreignCall.Type, $"foreign call '{foreignCall.Function}'", problems);
          foreach (var arg in foreignCall.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrStep step:
          CheckExpr(step.Target, problems);
          break;
        case IrBind bind:
          CheckExpr(bind.Value, problems);
          break;
        case IrIsNull isNull:
          CheckExpr(isNull.Value, problems);
          break;
        case IrNone none:
          CheckType(none.Type, "none literal", problems);
          break;
        case IrSome some:
          CheckType(some.Type, "optional value", problems);
          CheckExpr(some.Value, problems);
          break;
        case IrIsNone isNone:
          CheckExpr(isNone.Value, problems);
          break;
        case IrUnwrap unwrap:
          CheckExpr(unwrap.Value, problems);
          break;
        case IrCoalesce coalesce:
          CheckType(coalesce.Value.Type, "coalesce operand", problems);
          CheckExpr(coalesce.Value, problems);
          CheckExpr(coalesce.Fallback, problems);
          break;
        case IrStructNew create:
          CheckType(create.Type, "struct construction", problems);
          foreach (var arg in create.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrStructDefault def:
          CheckType(def.Type, "struct default", problems);
          break;
        case IrStructEquals eq:
          CheckExpr(eq.Left, problems);
          CheckExpr(eq.Right, problems);
          break;
        case IrNew created:
          CheckType(created.Type, "object allocation", problems);
          foreach (var arg in created.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrMethodCall call:
          CheckType(call.Type, $"method call '{call.Method.Name}'", problems);
          if (call.Target is { } target)
            CheckExpr(target, problems);
          foreach (var arg in call.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrFieldLoad load:
          CheckExpr(load.Target, problems);
          break;
        case IrReferenceEquals eq:
          CheckExpr(eq.Left, problems);
          CheckExpr(eq.Right, problems);
          break;
        case IrTypeTest test:
          CheckType(test.Target, "type test target", problems);
          CheckExpr(test.Value, problems);
          break;
        case IrCast cast:
          CheckType(cast.Type, "cast target", problems);
          CheckExpr(cast.Value, problems);
          break;
        case IrAsCast cast:
          CheckType(cast.Type, "as-cast target", problems);
          CheckExpr(cast.Value, problems);
          break;
        case IrGenericEquals eq:
          CheckExpr(eq.Left, problems);
          CheckExpr(eq.Right, problems);
          break;
        case IrGenericCompare cmp:
          CheckExpr(cmp.Left, problems);
          CheckExpr(cmp.Right, problems);
          break;
        case IrLambda lambda:
          CheckType(lambda.Type, "lambda", problems);
          foreach (var param in lambda.Parameters)
            CheckType(param.Type, $"lambda parameter '{param.Name}'", problems);
          foreach (var local in lambda.Locals)
            CheckType(local.Type, $"lambda local '{local.Name}'", problems);
          CheckStmt(lambda.Body, problems);
          break;
        case IrFunctionRef funcRef:
          CheckType(funcRef.Type, $"function reference '{funcRef.Function}'", problems);
          break;
        case IrBoundMethod bound:
          CheckType(bound.Type, $"bound method '{bound.Method.Name}'", problems);
          if (bound.Receiver is { } recv)
            CheckExpr(recv, problems);
          break;
        case IrInvoke invoke:
          CheckExpr(invoke.Callee, problems);
          foreach (var arg in invoke.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrCollection col:
          CheckType(col.Type, $"collection operation '{col.Op}'", problems);
          foreach (var arg in col.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrArraySequence seq:
          CheckType(seq.Type, "array sequence", problems);
          CheckExpr(seq.Array, problems);
          break;
        case IrAsync asyncOp:
          CheckType(asyncOp.Type, $"async operation '{asyncOp.Op}'", problems);
          foreach (var arg in asyncOp.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrAwait awaited:
          CheckExpr(awaited.Operand, problems);
          break;
        case IrThrowExpression thrown:
          CheckExpr(thrown.Exception, problems);
          break;
        case IrIntrinsic intrinsic:
          foreach (var arg in intrinsic.Arguments)
            CheckExpr(arg, problems);
          if (!IsSupportedIntrinsic(intrinsic))
            problems.Add(UnsupportedIntrinsic.At($"intrinsic '{intrinsic.Op}' is not supported by the JVM backend"));
          break;
        default:
          problems.Add(UnsupportedConstruct.At($"expression '{expr.GetType().Name}' is not supported by the JVM backend"));
          break;
      }
    }

  }

}
