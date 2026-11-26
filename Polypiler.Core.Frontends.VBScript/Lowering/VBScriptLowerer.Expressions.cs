using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.VBScript.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VBScript.Lowering;

internal sealed partial class VBScriptLowerer {

  private IrExpr LowerExpr(VbsExpr expr, ProcContext ctx) {
    switch (expr) {
      case VbsIntLit i:
        return i.Value is >= int.MinValue and <= int.MaxValue
          ? new IrConst(IrType.I32, i.Value)
          : new IrConst(IrType.I64, i.Value);
      case VbsFloatLit f:
        return new IrConst(IrType.F64, IrOperators.Bits(IrType.F64, f.Value));
      case VbsStringLit s:
        return new IrStrConst(s.Value);
      case VbsBoolLit b:
        return new IrConst(IrType.Bool, b.Value ? 1 : 0);
      case VbsEmptyLit:
        return new IrConst(IrType.I32, 0);
      case VbsNullLit or VbsNothingLit:
        return new IrNull(IrType.String);
      case VbsMeExpr me:
        if (ctx.Receiver is not null)
          return new IrLocalRef(ctx.Receiver);
        this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, me.Line, me.Column, "Me");
        return new IrConst(IrType.I32, 0);
      case VbsNewExpr ne:
        if (this._classes.TryGetValue(ne.ClassName, out var ci))
          return new IrNew(IrType.Object(ci.IrClass), ci.DefaultCtor, []);
        this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, ne.Line, ne.Column, ne.ClassName);
        return new IrConst(IrType.I32, 0);
      case VbsIdentExpr id:
        return this.LowerIdent(id, ctx);
      case VbsUnaryExpr u:
        return this.LowerUnary(u, ctx);
      case VbsBinaryExpr b:
        return this.LowerBinary(b, ctx);
      case VbsMemberExpr m:
        return this.LowerMember(m, ctx);
      default: {
        var c = (VbsCallOrIndexExpr)expr;
        return this.LowerCallOrIndex(c.Target, c.Arguments, c.Line, c.Column, ctx);
      }
    }
  }

  private IrExpr LowerIdent(VbsIdentExpr id, ProcContext ctx) {
    if (ctx.ResultLocal is not null && id.Name.Equals(ctx.Proc.Decl.Name, StringComparison.OrdinalIgnoreCase))
      return new IrLocalRef(ctx.ResultLocal);

    if (ctx.LocalConsts.TryGetValue(id.Name, out var lc))
      return lc;

    if (ctx.Locals.TryGetValue(id.Name, out var local))
      return new IrLocalRef(local);

    if (ctx.Proc.Owner is not null && ctx.Receiver is not null) {
      if (ctx.Proc.Owner.Fields.TryGetValue(id.Name, out var field))
        return new IrFieldLoad(new IrLocalRef(ctx.Receiver), field);
      if (ctx.Proc.Owner.Getters.TryGetValue(id.Name, out var getter))
        return new IrMethodCall(getter.ReturnType!, getter.Method!, new IrLocalRef(ctx.Receiver), []);
      if (ctx.Proc.Owner.Methods.TryGetValue(id.Name, out var method) && method.Decl.Parameters.Count == 0)
        return new IrMethodCall(method.ReturnType!, method.Method!, new IrLocalRef(ctx.Receiver), []);
    }

    if (this._moduleConsts.TryGetValue(id.Name, out var mc))
      return new IrConstRef(mc);

    if (this._globals.TryGetValue(id.Name, out var global))
      return new IrGlobalRef(global);

    // Zero-arg function call without parentheses (VBScript allows `x = Foo`).
    if (this._procedures.TryGetValue(id.Name, out var callee) && callee.Decl.Parameters.Count == 0)
      return new IrCall(callee.IrName, callee.ReturnType!, []);

    if (this._program.OptionExplicit) {
      this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, id.Line, id.Column, id.Name);
      return new IrConst(IrType.I32, 0);
    }

    return new IrConst(IrType.I32, 0);
  }

  private IrExpr LowerUnary(VbsUnaryExpr u, ProcContext ctx) {
    var operand = this.LowerExpr(u.Operand, ctx);
    if (u.Op == VbsUnaryOp.Plus)
      return operand;
    if (u.Op == VbsUnaryOp.Negate)
      return operand.Type.IsFloat || operand.Type is { Kind: IrTypeKind.I32 or IrTypeKind.I64 }
        ? new IrUnary(IrUnaryOp.Negate, operand)
        : new IrUnary(IrUnaryOp.Negate, this.Coerce(operand, IrType.I32));
    if (operand.Type == IrType.Bool)
      return new IrUnary(IrUnaryOp.LogicalNot, operand);
    var intOp = operand.Type.IsWideInteger ? operand : this.Coerce(operand, IrType.I32);
    return new IrUnary(IrUnaryOp.BitNot, intOp);
  }

  private IrExpr LowerBinary(VbsBinaryExpr b, ProcContext ctx) {
    // Handle `x Is Nothing` / `Nothing Is x` / `x Is y`.
    if (b.Op == VbsBinaryOp.Is) {
      if (b.Right is VbsNothingLit) {
        var lhs = this.LowerExpr(b.Left, ctx);
        return lhs.Type.IsNullable ? new IrIsNull(lhs) : new IrConst(IrType.Bool, 0);
      }
      if (b.Left is VbsNothingLit) {
        var rhs = this.LowerExpr(b.Right, ctx);
        return rhs.Type.IsNullable ? new IrIsNull(rhs) : new IrConst(IrType.Bool, 0);
      }
      var leftObj = this.LowerExpr(b.Left, ctx);
      var rightObj = this.LowerExpr(b.Right, ctx);
      return new IrReferenceEquals(leftObj, rightObj);
    }

    var left = this.LowerExpr(b.Left, ctx);
    var right = this.LowerExpr(b.Right, ctx);

    switch (b.Op) {
      case VbsBinaryOp.Concat:
        return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [this.ToStringExpr(left), this.ToStringExpr(right)]);
      case VbsBinaryOp.Power:
        return new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F64, [this.Coerce(left, IrType.F64), this.Coerce(right, IrType.F64)]);
      case VbsBinaryOp.Divide:
        return new IrBinary(IrBinaryOp.Divide, this.Coerce(left, IrType.F64), this.Coerce(right, IrType.F64));
      case VbsBinaryOp.IntDivide:
        return new IrBinary(IrBinaryOp.Divide, this.Coerce(left, IrType.I32), this.Coerce(right, IrType.I32));
      case VbsBinaryOp.Mod:
        return new IrBinary(IrBinaryOp.Remainder, this.Coerce(left, IrType.I32), this.Coerce(right, IrType.I32));
      case VbsBinaryOp.Add when left.Type == IrType.String && right.Type == IrType.String:
        return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right]);
      case VbsBinaryOp.Add or VbsBinaryOp.Subtract or VbsBinaryOp.Multiply: {
        var common = CommonNumericType(left.Type, right.Type);
        var irOp = b.Op switch {
          VbsBinaryOp.Add => IrBinaryOp.Add,
          VbsBinaryOp.Subtract => IrBinaryOp.Subtract,
          _ => IrBinaryOp.Multiply
        };
        return new IrBinary(irOp, this.Coerce(left, common), this.Coerce(right, common));
      }
      case VbsBinaryOp.Equal or VbsBinaryOp.NotEqual:
        return this.EmitEquality(left, right, negate: b.Op == VbsBinaryOp.NotEqual);
      case VbsBinaryOp.Less or VbsBinaryOp.LessEqual or VbsBinaryOp.Greater or VbsBinaryOp.GreaterEqual: {
        var cmpOp = b.Op switch {
          VbsBinaryOp.Less => IrBinaryOp.Less,
          VbsBinaryOp.LessEqual => IrBinaryOp.LessOrEqual,
          VbsBinaryOp.Greater => IrBinaryOp.Greater,
          _ => IrBinaryOp.GreaterOrEqual
        };
        if (left.Type == IrType.String || right.Type == IrType.String) {
          var cmp = new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [this.ToStringExpr(left), this.ToStringExpr(right)]);
          return new IrBinary(cmpOp, cmp, new IrConst(IrType.I32, 0));
        }
        var common = CommonNumericType(left.Type, right.Type);
        return new IrBinary(cmpOp, this.Coerce(left, common), this.Coerce(right, common));
      }
      default:
        return this.EmitLogicalOrBitwise(b.Op, left, right);
    }
  }

  private IrExpr EmitEquality(IrExpr left, IrExpr right, bool negate) {
    if (left.Type == IrType.String || right.Type == IrType.String) {
      IrExpr eq = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [this.ToStringExpr(left), this.ToStringExpr(right)]);
      return negate ? new IrUnary(IrUnaryOp.LogicalNot, eq) : eq;
    }

    if (left.Type == IrType.Bool && right.Type == IrType.Bool)
      return new IrBinary(negate ? IrBinaryOp.NotEqual : IrBinaryOp.Equal, left, right);

    var common = CommonNumericType(left.Type, right.Type);
    return new IrBinary(negate ? IrBinaryOp.NotEqual : IrBinaryOp.Equal, this.Coerce(left, common), this.Coerce(right, common));
  }

  private IrExpr EmitLogicalOrBitwise(VbsBinaryOp op, IrExpr left, IrExpr right) {
    // VBScript: bitwise on integers, logical on booleans.
    if (left.Type == IrType.Bool && right.Type == IrType.Bool) {
      return op switch {
        VbsBinaryOp.And => new IrBinary(IrBinaryOp.LogicalAnd, left, right),
        VbsBinaryOp.Or => new IrBinary(IrBinaryOp.LogicalOr, left, right),
        _ => new IrBinary(IrBinaryOp.NotEqual, left, right)
      };
    }

    var intType = left.Type == IrType.I64 || right.Type == IrType.I64 ? IrType.I64 : IrType.I32;
    var bitOp = op switch {
      VbsBinaryOp.And => IrBinaryOp.And,
      VbsBinaryOp.Or => IrBinaryOp.Or,
      _ => IrBinaryOp.Xor
    };
    return new IrBinary(bitOp, this.Coerce(left, intType), this.Coerce(right, intType));
  }

  private IrExpr LowerMember(VbsMemberExpr m, ProcContext ctx) {
    if (m.Target is VbsIdentExpr { Name: var objName } && objName.Equals("Err", StringComparison.OrdinalIgnoreCase)) {
      var (errNum, errDesc) = this.EnsureErrGlobals();
      if (m.Member.Equals("Number", StringComparison.OrdinalIgnoreCase))
        return new IrGlobalRef(errNum);
      if (m.Member.Equals("Description", StringComparison.OrdinalIgnoreCase))
        return new IrGlobalRef(errDesc);
      this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, m.Line, m.Column, $"Err.{m.Member}");
      return new IrConst(IrType.I32, 0);
    }

    var recv = this.LowerExpr(m.Target, ctx);
    if (recv.Type.ClassType is { } cls && this._classes.TryGetValue(cls.Name, out var ci)) {
      if (ci.Fields.TryGetValue(m.Member, out var field))
        return new IrFieldLoad(recv, field);
      if (ci.Getters.TryGetValue(m.Member, out var getter))
        return new IrMethodCall(getter.ReturnType!, getter.Method!, recv, []);
      if (ci.Methods.TryGetValue(m.Member, out var method) && method.Decl.Parameters.Count == 0)
        return new IrMethodCall(method.ReturnType!, method.Method!, recv, []);
    }

    this.AddDiagnostic(VBScriptFrontend.UnresolvedSymbol, m.Line, m.Column, m.Member);
    return new IrConst(IrType.I32, 0);
  }

  private IrExpr LowerCallOrIndex(VbsExpr target, IReadOnlyList<VbsExpr> args, int line, int column, ProcContext ctx) {
    if (target is VbsIdentExpr id) {
      // 1. Built-in VBScript function?
      if (this.TryLowerBuiltin(id.Name, args, line, column, ctx, out var builtinExpr))
        return builtinExpr;

      // 2. Local / field / global array indexing?
      if (this.TryResolveArrayVar(id.Name, ctx, out var arrExpr)) {
        if (args.Count != 1) {
          this.AddDiagnostic(VBScriptFrontend.UnsupportedConstruct, line, column, "multi-dimensional array access");
          return new IrConst(IrType.I32, 0);
        }
        return new IrArrayLoad(arrExpr, this.Coerce(this.LowerExpr(args[0], ctx), IrType.I32));
      }

      // 3. Method on Me inside a Class?
      if (ctx.Proc.Owner is not null && ctx.Receiver is not null && ctx.Proc.Owner.Methods.TryGetValue(id.Name, out var ownMethod))
        return this.EmitMethodCall(ownMethod, new IrLocalRef(ctx.Receiver), args, line, column, ctx);

      // 4. Module-level Function or Sub?
      if (this._procedures.TryGetValue(id.Name, out var callee)) {
        var loweredArgs = this.LowerCallArguments(callee, args, line, column, ctx);
        return new IrCall(callee.IrName, callee.ReturnType!, loweredArgs);
      }

      this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, line, column, id.Name);
      return new IrConst(IrType.I32, 0);
    }

    if (target is VbsMemberExpr mem) {
      // Unsupported host objects like WScript.Echo
      if (mem.Target is VbsIdentExpr { Name: var hostName }
          && !ctx.Locals.ContainsKey(hostName)
          && !this._globals.ContainsKey(hostName)
          && (ctx.Proc.Owner is null || !ctx.Proc.Owner.Fields.ContainsKey(hostName))) {
        this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, mem.Line, mem.Column, $"{hostName}.{mem.Member}");
        return new IrConst(IrType.I32, 0);
      }

      var recv = this.LowerExpr(mem.Target, ctx);
      if (recv.Type.ClassType is { } cls && this._classes.TryGetValue(cls.Name, out var ci)) {
        if (ci.Methods.TryGetValue(mem.Member, out var method))
          return this.EmitMethodCall(method, recv, args, line, column, ctx);
        if (ci.Fields.TryGetValue(mem.Member, out var field) && field.Type.IsArray && args.Count == 1)
          return new IrArrayLoad(new IrFieldLoad(recv, field), this.Coerce(this.LowerExpr(args[0], ctx), IrType.I32));
      }

      this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, mem.Line, mem.Column, mem.Member);
      return new IrConst(IrType.I32, 0);
    }

    this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, line, column, "indirect call");
    return new IrConst(IrType.I32, 0);
  }

  private IrExpr EmitMethodCall(ProcInfo method, IrExpr receiver, IReadOnlyList<VbsExpr> args, int line, int column, ProcContext ctx) {
    var loweredArgs = this.LowerCallArguments(method, args, line, column, ctx);
    return new IrMethodCall(method.ReturnType!, method.Method!, receiver, loweredArgs);
  }

  private List<IrExpr> LowerCallArguments(ProcInfo callee, IReadOnlyList<VbsExpr> args, int line, int column, ProcContext ctx) {
    if (args.Count != callee.ParamTypes.Length)
      this.AddDiagnostic(VBScriptFrontend.UnsupportedCall, line, column, $"{callee.Decl.Name} (expected {callee.ParamTypes.Length} args, got {args.Count})");

    var result = new List<IrExpr>(callee.ParamTypes.Length);
    for (var i = 0; i < callee.ParamTypes.Length; ++i) {
      var arg = i < args.Count ? this.LowerExpr(args[i], ctx) : this.DefaultValueOf(callee.ParamTypes[i]);
      result.Add(this.Coerce(arg, callee.ParamTypes[i]));
    }
    return result;
  }

  private bool TryResolveArrayVar(string name, ProcContext ctx, out IrExpr expr) {
    if (ctx.Locals.TryGetValue(name, out var local) && local.Type.IsArray) {
      expr = new IrLocalRef(local);
      return true;
    }

    if (ctx.Proc.Owner is not null && ctx.Receiver is not null
        && ctx.Proc.Owner.Fields.TryGetValue(name, out var field) && field.Type.IsArray) {
      expr = new IrFieldLoad(new IrLocalRef(ctx.Receiver), field);
      return true;
    }

    if (this._globals.TryGetValue(name, out var global) && global.Type.IsArray) {
      expr = new IrGlobalRef(global);
      return true;
    }

    expr = null!;
    return false;
  }

  private IrExpr Coerce(IrExpr expr, IrType target) {
    if (expr.Type == target)
      return expr;

    if (target == IrType.String)
      return this.ToStringExpr(expr);

    if (target == IrType.Bool)
      return this.ToBoolExpr(expr);

    if (target.IsObject && expr is IrNull)
      return new IrNull(target);

    if (target.IsOptional && expr.Type == target.Underlying)
      return new IrSome(expr);

    if (target.IsNumeric && expr.Type == IrType.Bool) {
      // VBScript: True -> -1, False -> 0
      var negOne = target.IsFloat
        ? new IrConst(target, IrOperators.Bits(target, -1.0))
        : new IrConst(target, -1);
      return new IrConditional(expr, negOne, this.DefaultValueOf(target));
    }

    if (target.IsNumeric && expr.Type.IsNumeric) {
      var contract = target.IsFloat ? IrConversion.Wrap : expr.Type.IsFloat ? IrConversion.Saturate : IrConversion.Wrap;
      return new IrConvert(target, expr, contract);
    }

    if (target.IsNumeric && expr.Type == IrType.String)
      return new IrIntrinsic(IrIntrinsicOp.ParseDotNet, target, [expr]);

    return expr;
  }

  private IrExpr ToStringExpr(IrExpr expr) {
    if (expr.Type == IrType.String)
      return expr;
    if (expr.Type == IrType.Bool)
      return new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [expr]);
    if (expr.Type.IsNumeric)
      return new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [expr]);
    return new IrStrConst("");
  }

  private IrExpr ToBoolExpr(IrExpr expr) {
    if (expr.Type == IrType.Bool)
      return expr;
    if (expr.Type.IsNumeric)
      return new IrBinary(IrBinaryOp.NotEqual, expr, this.DefaultValueOf(expr.Type));
    if (expr.Type == IrType.String)
      return new IrBinary(IrBinaryOp.NotEqual, new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [expr]), new IrConst(IrType.I32, 0));
    if (expr.Type.IsNullable)
      return new IrUnary(IrUnaryOp.LogicalNot, new IrIsNull(expr));
    return new IrConst(IrType.Bool, 0);
  }

  private static IrType CommonNumericType(IrType left, IrType right) {
    if (left == IrType.F64 || right == IrType.F64)
      return IrType.F64;
    if (left == IrType.F32 || right == IrType.F32)
      return IrType.F32;
    if (left == IrType.I64 || right == IrType.I64)
      return IrType.I64;
    return IrType.I32;
  }

}
