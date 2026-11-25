using System.Collections.Generic;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Jvm;

public static partial class JvmEmitter {

  private sealed partial class ModuleEmitter {

    /// <summary>Validates all types, constructs, and intrinsics in the module without emitting bytecode.</summary>
    public IReadOnlyList<Diagnostic> CheckOnly() {
      var problems = new List<Diagnostic>();
      foreach (var typeDecl in this._module.Types)
        if (typeDecl is not IrEnumType)
          problems.Add(UnsupportedType.At($"declared type '{typeDecl.Name}' ({typeDecl.GetType().Name}) is not supported by the JVM backend"));

      if (this._module.ForeignFunctions.Count > 0)
        problems.Add(UnsupportedConstruct.At("foreign function declarations are not supported by the JVM backend"));

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
        case IrExprStmt exprStmt:
          CheckExpr(exprStmt.Expression, problems);
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
        case IrBreak or IrContinue or IrRethrow:
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
            if (!IrExceptionClass.IsLibrary(clause.Class))
              problems.Add(UnsupportedType.At($"catch exception class '{clause.Class.Name}' is not supported by the JVM backend"));
            if (clause.Filter is { } filter)
              CheckExpr(filter, problems);
            CheckStmt(clause.Body, problems);
          }

          if (attempt.Finally is { } cleanup)
            CheckStmt(cleanup, problems);
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
        case IrNew created when created.Type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls):
          foreach (var arg in created.Arguments)
            CheckExpr(arg, problems);
          break;
        case IrFieldLoad load when load.Target.Type.ClassType is { } cls && IrExceptionClass.IsLibrary(cls)
            && (load.Field == IrExceptionClass.Message || load.Field == IrExceptionClass.InnerException):
          CheckExpr(load.Target, problems);
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
