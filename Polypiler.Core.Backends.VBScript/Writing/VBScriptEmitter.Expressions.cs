using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VBScript;

public static partial class VBScriptEmitter {

  private sealed partial class Writer {

    private string EmitExpr(IrExpr expression, StringBuilder pre, int indent) {
      if (!this.CheckType(expression.Type, this._currentFunction?.Name ?? this._module.Name))
        return "0";

      return expression switch {
        IrConst c => FormatScalarLiteral(c.Type, c.Value),
        IrStrConst s => VBScriptNaming.StringLiteral(s.Value),
        IrConstRef cr => this._constNames[cr.Constant],
        IrLocalRef lr => this.LocalName(lr.Local),
        IrGlobalRef gr => this._globalNames[gr.Global],
        IrUnary u => this.EmitUnary(u, pre, indent),
        IrBinary b => this.EmitBinary(b, pre, indent),
        IrConvert conv => this.EmitConvert(conv, pre, indent),
        IrConditional cond => this.EmitConditional(cond, pre, indent),
        IrCall call => this.EmitCall(call, pre, indent),
        IrStep step => this.EmitStep(step, pre, indent),
        IrBind bind => this.EmitBind(bind, pre, indent),
        IrArrayLoad load => this.EmitArrayLoad(load, pre, indent),
        IrArrayLength len => $"(UBound({this.EmitExpr(len.Array, pre, indent)}) + 1)",
        IrNewArray newArr => this.EmitNewArray(newArr, pre, indent),
        IrStructNew sn => this.EmitStructNew(sn, pre, indent),
        IrStructDefault sd => $"New {this._typeNames[sd.Type.StructType!]}",
        IrFieldLoad fl => this.EmitFieldLoad(fl, pre, indent),
        IrStructEquals se => this.EmitStructEquals(se, pre, indent),
        IrNew objNew => this.EmitObjectNew(objNew, pre, indent),
        IrNull n => NeedsSet(n.Type) ? "Nothing" : "Null",
        IrMethodCall mc => this.EmitMethodCallCore(mc, pre, indent, isStatement: false),
        IrReferenceEquals re => this.EmitReferenceEquals(re, pre, indent),
        IrNone none => NeedsSet(none.Type) ? "Nothing" : "Null",
        IrSome some => this.EmitExpr(some.Value, pre, indent),
        IrIsNone isNone => this.EmitIsAbsent(isNone.Value, pre, indent),
        IrIsNull isNull => this.EmitIsAbsent(isNull.Value, pre, indent),
        IrUnwrap unwrap => this.EmitUnwrap(unwrap, pre, indent),
        IrCoalesce coalesce => this.EmitCoalesce(coalesce, pre, indent),
        IrThrowExpression te => this.EmitThrowExpr(te, pre, indent),
        IrTypeTest tt => this.EmitTypeTest(tt, pre, indent),
        IrCast cast => this.EmitCast(cast, pre, indent),
        IrAsCast asCast => this.EmitAsCast(asCast, pre, indent),
        IrGenericEquals ge => this.EmitGenericEquals(ge, pre, indent),
        IrGenericCompare gc => this.EmitGenericCompare(gc, pre, indent),
        IrIntrinsic intrinsic => this.EmitIntrinsic(intrinsic, pre, indent),
        _ => this.ReportConstruct(expression.GetType().Name)
      };
    }

    private string CopyIfStruct(IrType type, string expr, StringBuilder pre, int indent) {
      if (!type.IsStruct || expr.StartsWith("New ", StringComparison.Ordinal))
        return expr;
      var tmp = this.NewTemp("ply_s");
      pre.Append(Indent(indent)).Append("Set ").Append(tmp).Append(" = (").Append(expr).Append(").ply_clone()\n");
      return tmp;
    }

    private string EmitUnary(IrUnary unary, StringBuilder pre, int indent) {
      var operand = this.EmitExpr(unary.Operand, pre, indent);
      var type = unary.Type.Integral;
      return unary.Op switch {
        IrUnaryOp.LogicalNot => $"(Not ({operand}))",
        IrUnaryOp.BitNot when type == IrType.I32 => $"{this.Use(VBScriptHelpers.Not32)}({operand})",
        IrUnaryOp.BitNot when type == IrType.U32 => $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.Not32)}({operand}))",
        IrUnaryOp.BitNot when type == IrType.I64 => $"{this.Use(VBScriptHelpers.Not64)}({operand})",
        IrUnaryOp.BitNot => $"{this.Use(VBScriptHelpers.U64)}({this.Use(VBScriptHelpers.Not64)}({operand}))",
        IrUnaryOp.Negate when type.IsFloat => $"(-CDbl({operand}))",
        IrUnaryOp.Negate => this.WrapInteger(type, $"(-CDbl({operand}))"),
        _ => this.WrapInteger(type, $"{this.Use(VBScriptHelpers.CheckRange)}(-CDbl({operand}), {MinDouble(type)}, {MaxDouble(type)})")
      };
    }

    private string EmitBinary(IrBinary binary, StringBuilder pre, int indent) {
      if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr)
        return this.EmitShortCircuit(binary, pre, indent);

      var left = this.EmitExpr(binary.Left, pre, indent);
      var rightPre = new StringBuilder();
      var right = this.EmitExpr(binary.Right, rightPre, indent);
      if (rightPre.Length > 0) {
        var lhs = this.NewTemp("ply_l");
        pre.Append(Indent(indent)).Append(AssignPrefix(binary.Left.Type)).Append(lhs).Append(" = ").Append(left).Append('\n');
        pre.Append(rightPre);
        left = lhs;
      }

      if (TryComparisonOp(binary.Op, out var cmp))
        return $"(({left}) {cmp} ({right}))";

      var type = binary.Type.Integral;
      if (type.IsFloat)
        return binary.Op switch {
          IrBinaryOp.Add => $"(CDbl({left}) + CDbl({right}))",
          IrBinaryOp.Subtract => $"(CDbl({left}) - CDbl({right}))",
          IrBinaryOp.Multiply => $"(CDbl({left}) * CDbl({right}))",
          IrBinaryOp.Divide => $"(CDbl({left}) / CDbl({right}))",
          _ => $"{this.Use(VBScriptHelpers.FloatRem)}({left}, {right})"
        };

      if (type.IsWord) {
        var signed = type.IsSigned;
        return binary.Op switch {
          IrBinaryOp.Add => this.WrapInteger(type, $"CDbl({left}) + CDbl({right})"),
          IrBinaryOp.Subtract => this.WrapInteger(type, $"CDbl({left}) - CDbl({right})"),
          IrBinaryOp.Multiply => signed
            ? $"{this.Use(VBScriptHelpers.Mul32)}({left}, {right})"
            : $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.Mul32)}({left}, {right}))",
          IrBinaryOp.Divide => $"{this.Use(signed ? VBScriptHelpers.Div32 : VBScriptHelpers.UDiv32)}({left}, {right})",
          IrBinaryOp.Remainder => $"{this.Use(signed ? VBScriptHelpers.Rem32 : VBScriptHelpers.URem32)}({left}, {right})",
          IrBinaryOp.And => signed ? $"{this.Use(VBScriptHelpers.And32)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.And32)}({left}, {right}))",
          IrBinaryOp.Or => signed ? $"{this.Use(VBScriptHelpers.Or32)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.Or32)}({left}, {right}))",
          IrBinaryOp.Xor => signed ? $"{this.Use(VBScriptHelpers.Xor32)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.Xor32)}({left}, {right}))",
          IrBinaryOp.ShiftLeft => signed ? $"{this.Use(VBScriptHelpers.Shl32)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U32)}({this.Use(VBScriptHelpers.Shl32)}({left}, {right}))",
          IrBinaryOp.ShiftRight when signed => $"{this.Use(VBScriptHelpers.Shr32)}({left}, {right})",
          IrBinaryOp.ShiftRight => $"{this.Use(VBScriptHelpers.UShr32)}({left}, {right})",
          IrBinaryOp.ShiftRightLogical => signed ? $"{this.Use(VBScriptHelpers.I32)}({this.Use(VBScriptHelpers.UShr32)}({left}, {right}))" : $"{this.Use(VBScriptHelpers.UShr32)}({left}, {right})",
          _ => this.EmitCheckedBinary(binary.Op, type, left, right)
        };
      }

      // 64-bit integers via Double helpers.
      var signed64 = type.IsSigned;
      return binary.Op switch {
        IrBinaryOp.Add => this.WrapInteger(type, $"CDbl({left}) + CDbl({right})"),
        IrBinaryOp.Subtract => this.WrapInteger(type, $"CDbl({left}) - CDbl({right})"),
        IrBinaryOp.Multiply => this.WrapInteger(type, $"CDbl({left}) * CDbl({right})"),
        IrBinaryOp.Divide => $"{this.Use(VBScriptHelpers.Div64)}({left}, {right})",
        IrBinaryOp.Remainder => $"{this.Use(VBScriptHelpers.Rem64)}({left}, {right})",
        IrBinaryOp.And => signed64 ? $"{this.Use(VBScriptHelpers.And64)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U64)}({this.Use(VBScriptHelpers.And64)}({left}, {right}))",
        IrBinaryOp.Or => signed64 ? $"{this.Use(VBScriptHelpers.Or64)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U64)}({this.Use(VBScriptHelpers.Or64)}({left}, {right}))",
        IrBinaryOp.Xor => signed64 ? $"{this.Use(VBScriptHelpers.Xor64)}({left}, {right})" : $"{this.Use(VBScriptHelpers.U64)}({this.Use(VBScriptHelpers.Xor64)}({left}, {right}))",
        IrBinaryOp.ShiftLeft => signed64 ? $"{this.Use(VBScriptHelpers.I64)}({this.Use(VBScriptHelpers.Shl64)}({left}, {right}))" : $"{this.Use(VBScriptHelpers.Shl64)}({left}, {right})",
        IrBinaryOp.ShiftRight when signed64 => $"{this.Use(VBScriptHelpers.Shr64)}({left}, {right})",
        IrBinaryOp.ShiftRight => $"{this.Use(VBScriptHelpers.UShr64)}({left}, {right})",
        IrBinaryOp.ShiftRightLogical => signed64 ? $"{this.Use(VBScriptHelpers.I64)}({this.Use(VBScriptHelpers.UShr64)}({left}, {right}))" : $"{this.Use(VBScriptHelpers.UShr64)}({left}, {right})",
        _ => this.EmitCheckedBinary(binary.Op, type, left, right)
      };
    }

    private string EmitCheckedBinary(IrBinaryOp op, IrType type, string left, string right) {
      var tok = op == IrBinaryOp.AddChecked ? "+" : op == IrBinaryOp.SubtractChecked ? "-" : "*";
      var checkedExpr = $"{this.Use(VBScriptHelpers.CheckRange)}(CDbl({left}) {tok} CDbl({right}), {MinDouble(type)}, {MaxDouble(type)})";
      return this.WrapInteger(type, checkedExpr);
    }

    private string EmitShortCircuit(IrBinary binary, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_b");
      var left = this.EmitExpr(binary.Left, pre, indent);
      pre.Append(pad).Append(tmp).Append(" = CBool(").Append(left).Append(")\n");
      pre.Append(pad).Append(binary.Op == IrBinaryOp.LogicalAnd ? "If " : "If Not ").Append(tmp).Append(" Then\n");
      var right = this.EmitExpr(binary.Right, pre, indent + 1);
      pre.Append(Indent(indent + 1)).Append(tmp).Append(" = CBool(").Append(right).Append(")\n");
      pre.Append(pad).Append("End If\n");
      return tmp;
    }

    private string EmitConditional(IrConditional cond, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var condExpr = this.EmitExpr(cond.Condition, pre, indent);
      var tmp = this.NewTemp("ply_c");
      var pfx = AssignPrefix(cond.Type);
      pre.Append(pad).Append("If ").Append(condExpr).Append(" Then\n");
      var tVal = this.EmitExpr(cond.WhenTrue, pre, indent + 1);
      pre.Append(Indent(indent + 1)).Append(pfx).Append(tmp).Append(" = ").Append(tVal).Append('\n');
      pre.Append(pad).Append("Else\n");
      var fVal = this.EmitExpr(cond.WhenFalse, pre, indent + 1);
      pre.Append(Indent(indent + 1)).Append(pfx).Append(tmp).Append(" = ").Append(fVal).Append('\n');
      pre.Append(pad).Append("End If\n");
      return tmp;
    }

    private string EmitConvert(IrConvert convert, StringBuilder pre, int indent) {
      var operand = this.EmitExpr(convert.Operand, pre, indent);
      var target = convert.Type.Integral;
      var source = convert.Operand.Type.Integral;
      if (target == source)
        return operand;

      if (target.IsFloat)
        return $"CDbl({operand})";

      if (source.IsFloat) {
        var doTrap = convert.Contract == IrConversion.Trap ? "True" : "False";
        var conv = $"{this.Use(VBScriptHelpers.FloatToInt)}({operand}, {MinDouble(target)}, {MaxDouble(target)}, {doTrap})";
        return this.WrapInteger(target, conv);
      }

      if (convert.Contract == IrConversion.Trap)
        return this.WrapInteger(target, $"{this.Use(VBScriptHelpers.CheckRange)}({operand}, {MinDouble(target)}, {MaxDouble(target)})");

      return this.WrapInteger(target, operand);
    }

    private string WrapInteger(IrType type, string expr) => type.Kind switch {
      IrTypeKind.I8 => $"{this.Use(VBScriptHelpers.I8)}({expr})",
      IrTypeKind.U8 => $"{this.Use(VBScriptHelpers.U8)}({expr})",
      IrTypeKind.I16 => $"{this.Use(VBScriptHelpers.I16)}({expr})",
      IrTypeKind.U16 => $"{this.Use(VBScriptHelpers.U16)}({expr})",
      IrTypeKind.I32 => $"{this.Use(VBScriptHelpers.I32)}({expr})",
      IrTypeKind.U32 => $"{this.Use(VBScriptHelpers.U32)}({expr})",
      IrTypeKind.I64 => $"{this.Use(VBScriptHelpers.I64)}({expr})",
      _ => $"{this.Use(VBScriptHelpers.U64)}({expr})"
    };

    private string EmitCall(IrCall call, StringBuilder pre, int indent) {
      var args = this.EmitCallArguments(call.Arguments, pre, indent);
      return $"{this.ResolveFunctionName(call.Function)}({args})";
    }

    private string EmitCallArguments(IReadOnlyList<IrExpr> arguments, StringBuilder pre, int indent)
      => string.Join(", ", arguments.Select(a => this.CopyIfStruct(a.Type, this.EmitExpr(a, pre, indent), pre, indent)));

    private string ResolveFunctionName(string irName) => this._functionNames[irName];

    private string EmitStep(IrStep step, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var type = step.Type.Integral;
      var delta = step.Increment ? "+ 1" : "- 1";
      var oldTmp = this.NewTemp("ply_old");
      var newTmp = this.NewTemp("ply_new");

      string ComputeNew(string cur) {
        var raw = $"CDbl({cur}) {delta}";
        if (type.IsFloat)
          return raw;
        if (step.Trap)
          raw = $"{this.Use(VBScriptHelpers.CheckRange)}({raw}, {MinDouble(type)}, {MaxDouble(type)})";
        return this.WrapInteger(type, raw);
      }

      string place;
      if (step.Target is IrLocalRef localRef) {
        place = this.LocalName(localRef.Local);
      } else if (step.Target is IrGlobalRef globalRef) {
        place = this._globalNames[globalRef.Global];
      } else {
        var load = (IrArrayLoad)step.Target;
        var arr = this.EmitExpr(load.Array, pre, indent);
        var idx = this.EmitExpr(load.Index, pre, indent);
        pre.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.CheckIdx)).Append('(').Append(arr).Append(", ").Append(idx).Append(")\n");
        place = $"{arr}({idx})";
      }

      pre.Append(pad).Append(oldTmp).Append(" = ").Append(place).Append('\n');
      pre.Append(pad).Append(newTmp).Append(" = ").Append(ComputeNew(oldTmp)).Append('\n');
      pre.Append(pad).Append(place).Append(" = ").Append(newTmp).Append('\n');
      return step.Postfix ? oldTmp : newTmp;
    }

    private string EmitBind(IrBind bind, StringBuilder pre, int indent) {
      var name = this.LocalName(bind.Local);
      var val = this.CopyIfStruct(bind.Local.Type, this.EmitExpr(bind.Value, pre, indent), pre, indent);
      pre.Append(Indent(indent)).Append(AssignPrefix(bind.Local.Type)).Append(name).Append(" = ").Append(val).Append('\n');
      return name;
    }

    private string EmitArrayLoad(IrArrayLoad load, StringBuilder pre, int indent) {
      var arr = this.EmitExpr(load.Array, pre, indent);
      var idx = this.EmitExpr(load.Index, pre, indent);
      pre.Append(Indent(indent)).Append("Call ").Append(this.Use(VBScriptHelpers.CheckIdx)).Append('(').Append(arr).Append(", ").Append(idx).Append(")\n");
      return this.CopyIfStruct(load.Type, $"{arr}({idx})", pre, indent);
    }

    private string EmitNewArray(IrNewArray newArr, StringBuilder pre, int indent) {
      var len = this.EmitExpr(newArr.Length, pre, indent);
      var elem = newArr.Type.Element!;
      if (!NeedsSet(elem))
        return $"{this.Use(VBScriptHelpers.NewArr)}({len}, {this.DefaultValue(elem)})";

      var pad = Indent(indent);
      var lenTmp = this.NewTemp("ply_n");
      var arrTmp = this.NewTemp("ply_a");
      var idxTmp = this.NewTemp("ply_i");
      pre.Append(pad).Append(lenTmp).Append(" = ").Append(len).Append('\n');
      pre.Append(pad).Append("If ").Append(lenTmp).Append(" < 0 Then Err.Raise vbObjectError + 1002, \"OverflowException\", \"Arithmetic operation resulted in an overflow.\"\n");
      pre.Append(pad).Append("If ").Append(lenTmp).Append(" = 0 Then\n");
      pre.Append(Indent(indent + 1)).Append(arrTmp).Append(" = Array()\n");
      pre.Append(pad).Append("Else\n");
      pre.Append(Indent(indent + 1)).Append("ReDim ").Append(arrTmp).Append('(').Append(lenTmp).Append(" - 1)\n");
      pre.Append(Indent(indent + 1)).Append("For ").Append(idxTmp).Append(" = 0 To ").Append(lenTmp).Append(" - 1\n");
      pre.Append(Indent(indent + 2)).Append("Set ").Append(arrTmp).Append('(').Append(idxTmp).Append(") = ").Append(this.DefaultValue(elem)).Append('\n');
      pre.Append(Indent(indent + 1)).Append("Next\n");
      pre.Append(pad).Append("End If\n");
      return arrTmp;
    }

    private string EmitStructNew(IrStructNew sn, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var st = sn.Type.StructType!;
      var tmp = this.NewTemp("ply_s");
      var fields = this._fieldNames[st];
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = New ").Append(this._typeNames[st]).Append('\n');
      for (var i = 0; i < st.Fields.Count; ++i) {
        var field = st.Fields[i];
        var val = this.CopyIfStruct(field.Type, this.EmitExpr(sn.Arguments[i], pre, indent), pre, indent);
        pre.Append(pad).Append(AssignPrefix(field.Type)).Append(tmp).Append('.').Append(fields[field]).Append(" = ").Append(val).Append('\n');
      }
      return tmp;
    }

    private string EmitPlace(IrExpr target, StringBuilder pre, int indent) {
      if (target is IrArrayLoad load) {
        var arr = this.EmitExpr(load.Array, pre, indent);
        var idx = this.EmitExpr(load.Index, pre, indent);
        pre.Append(Indent(indent)).Append("Call ").Append(this.Use(VBScriptHelpers.CheckIdx)).Append('(').Append(arr).Append(", ").Append(idx).Append(")\n");
        return $"{arr}({idx})";
      }
      if (target is IrFieldLoad fl) {
        var owner = this.EmitPlace(fl.Target, pre, indent);
        return $"{owner}.{this.FieldName(fl.Target.Type, fl.Field)}";
      }
      var expr = this.EmitExpr(target, pre, indent);
      if (target.Type.IsObject) {
        var tmp = this.NewTemp("ply_o");
        pre.Append(Indent(indent)).Append("Set ").Append(tmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(expr).Append(")\n");
        return tmp;
      }
      return expr;
    }

    private string EmitFieldLoad(IrFieldLoad load, StringBuilder pre, int indent) {
      var target = this.EmitPlace(load.Target, pre, indent);
      var access = $"{target}.{this.FieldName(load.Target.Type, load.Field)}";
      return this.CopyIfStruct(load.Type, access, pre, indent);
    }

    private string EmitStructEquals(IrStructEquals eq, StringBuilder pre, int indent) {
      var left = this.EmitExpr(eq.Left, pre, indent);
      var right = this.EmitExpr(eq.Right, pre, indent);
      if (eq.Left.Type.IsObject) {
        var pad = Indent(indent);
        var l = this.NewTemp("ply_l");
        var r = this.NewTemp("ply_r");
        var res = this.NewTemp("ply_eq");
        pre.Append(pad).Append("Set ").Append(l).Append(" = ").Append(left).Append('\n');
        pre.Append(pad).Append("Set ").Append(r).Append(" = ").Append(right).Append('\n');
        pre.Append(pad).Append("If ").Append(l).Append(" Is Nothing And ").Append(r).Append(" Is Nothing Then\n");
        pre.Append(Indent(indent + 1)).Append(res).Append(" = True\n");
        pre.Append(pad).Append("ElseIf ").Append(l).Append(" Is Nothing Or ").Append(r).Append(" Is Nothing Then\n");
        pre.Append(Indent(indent + 1)).Append(res).Append(" = False\n");
        pre.Append(pad).Append("Else\n");
        pre.Append(Indent(indent + 1)).Append(res).Append(" = ").Append(l).Append(".ply_equals(").Append(r).Append(")\n");
        pre.Append(pad).Append("End If\n");
        return res;
      }
      return $"({left}).ply_equals({right})";
    }

    private string EmitObjectNew(IrNew objNew, StringBuilder pre, int indent) {
      var cls = objNew.Type.ClassType!;
      if (IrExceptionClass.IsLibrary(cls)) {
        this.Use(VBScriptHelpers.ErrSupport);
        var msg = objNew.Arguments.Count > 0
          ? this.EmitExpr(objNew.Arguments[0], pre, indent)
          : VBScriptNaming.StringLiteral(IrExceptionClass.DefaultMessage(cls));
        var inner = objNew.Arguments.Count > 1
          ? this.EmitExpr(objNew.Arguments[1], pre, indent)
          : "Nothing";
        return $"ply_make_err({VBScriptNaming.StringLiteral(cls.Name)}, {msg}, {inner})";
      }

      var factory = this._ctorFactories[objNew.Constructor];
      var args = this.EmitCallArguments(objNew.Arguments, pre, indent);
      return $"{factory}({args})";
    }

    private string EmitMethodCallCore(IrMethodCall call, StringBuilder pre, int indent, bool isStatement) {
      var evalArgs = call.Arguments.Select(a => this.CopyIfStruct(a.Type, this.EmitExpr(a, pre, indent), pre, indent)).ToList();
      if (call.Target is null)
        return $"{this.ResolveFunctionName(call.Method.Function)}({string.Join(", ", evalArgs)})";

      var recv = this.EmitExpr(call.Target, pre, indent);
      var tmp = this.NewTemp("ply_r");
      pre.Append(Indent(indent)).Append("Set ").Append(tmp).Append(" = ").Append(this.Use(VBScriptHelpers.CheckNull)).Append('(').Append(recv).Append(")\n");

      if (call.Kind == IrMethodCallKind.Base) {
        var all = string.Join(", ", evalArgs.Prepend(tmp));
        return $"{this.ResolveFunctionName(call.Method.Function)}({all})";
      }

      var slot = this._methodSlotNames[call.Method.Root];
      _ = isStatement;
      return $"{tmp}.{slot}({string.Join(", ", evalArgs)})";
    }

    private string EmitReferenceEquals(IrReferenceEquals refEq, StringBuilder pre, int indent) {
      var left = this.EmitExpr(refEq.Left, pre, indent);
      var right = this.EmitExpr(refEq.Right, pre, indent);
      if (NeedsSet(refEq.Left.Type))
        return $"(({left}) Is ({right}))";

      var pad = Indent(indent);
      var l = this.NewTemp("ply_l");
      var r = this.NewTemp("ply_r");
      var res = this.NewTemp("ply_re");
      pre.Append(pad).Append(l).Append(" = ").Append(left).Append('\n');
      pre.Append(pad).Append(r).Append(" = ").Append(right).Append('\n');
      pre.Append(pad).Append("If IsNull(").Append(l).Append(") And IsNull(").Append(r).Append(") Then\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = True\n");
      pre.Append(pad).Append("ElseIf IsNull(").Append(l).Append(") Or IsNull(").Append(r).Append(") Then\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = False\n");
      pre.Append(pad).Append("Else\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = (StrComp(").Append(l).Append(", ").Append(r).Append(", 0) = 0)\n");
      pre.Append(pad).Append("End If\n");
      return res;
    }

    private string EmitIsAbsent(IrExpr value, StringBuilder pre, int indent) {
      var expr = this.EmitExpr(value, pre, indent);
      return NeedsSet(value.Type) ? $"(({expr}) Is Nothing)" : $"IsNull({expr})";
    }

    private string EmitUnwrap(IrUnwrap unwrap, StringBuilder pre, int indent) {
      var val = this.EmitExpr(unwrap.Value, pre, indent);
      if (unwrap.IsTested)
        return val;
      return NeedsSet(unwrap.Type)
        ? $"{this.Use(VBScriptHelpers.UnwrapObj)}({val})"
        : $"{this.Use(VBScriptHelpers.Unwrap)}({val})";
    }

    private string EmitCoalesce(IrCoalesce coalesce, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_q");
      var isObj = NeedsSet(coalesce.Value.Type);
      var pfx = isObj ? "Set " : "";
      var val = this.EmitExpr(coalesce.Value, pre, indent);
      pre.Append(pad).Append(pfx).Append(tmp).Append(" = ").Append(val).Append('\n');
      pre.Append(pad).Append("If ").Append(isObj ? $"{tmp} Is Nothing" : $"IsNull({tmp})").Append(" Then\n");
      var fb = this.EmitExpr(coalesce.Fallback, pre, indent + 1);
      pre.Append(Indent(indent + 1)).Append(pfx).Append(tmp).Append(" = ").Append(fb).Append('\n');
      pre.Append(pad).Append("End If\n");
      return tmp;
    }

    private string EmitThrowExpr(IrThrowExpression te, StringBuilder pre, int indent) {
      this.Use(VBScriptHelpers.ErrSupport);
      var ex = this.EmitExpr(te.Exception, pre, indent);
      pre.Append(Indent(indent)).Append("Call ply_throw(").Append(ex).Append(")\n");
      return this.DefaultValue(te.Type);
    }

    private string EmitTypeTest(IrTypeTest tt, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_tt");
      var res = this.NewTemp("ply_is");
      var val = this.EmitExpr(tt.Value, pre, indent);
      var targetName = tt.Target.Declaration!.Name;
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = ").Append(val).Append('\n');
      pre.Append(pad).Append("If ").Append(tmp).Append(" Is Nothing Then ").Append(res).Append(" = False Else ").Append(res).Append(" = ").Append(tmp).Append(".ply_is(").Append(VBScriptNaming.StringLiteral(targetName)).Append(")\n");
      return res;
    }

    private string EmitAsCast(IrAsCast asCast, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_ac");
      var val = this.EmitExpr(asCast.Value, pre, indent);
      var targetName = asCast.Type.Declaration!.Name;
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = ").Append(val).Append('\n');
      pre.Append(pad).Append("If Not (").Append(tmp).Append(" Is Nothing) Then If Not ").Append(tmp).Append(".ply_is(").Append(VBScriptNaming.StringLiteral(targetName)).Append(") Then Set ").Append(tmp).Append(" = Nothing\n");
      return tmp;
    }

    private string EmitCast(IrCast cast, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var tmp = this.NewTemp("ply_cc");
      var val = this.EmitExpr(cast.Value, pre, indent);
      var targetName = cast.Type.Declaration!.Name;
      pre.Append(pad).Append("Set ").Append(tmp).Append(" = ").Append(val).Append('\n');
      pre.Append(pad).Append("If Not (").Append(tmp).Append(" Is Nothing) Then If Not ").Append(tmp).Append(".ply_is(").Append(VBScriptNaming.StringLiteral(targetName)).Append(") Then Err.Raise vbObjectError + 1006, \"InvalidCastException\", \"Specified cast is not valid.\"\n");
      return tmp;
    }

    private string EmitGenericEquals(IrGenericEquals eq, StringBuilder pre, int indent) {
      if (eq.Left.Type.IsStruct)
        return this.EmitStructEquals(new IrStructEquals(eq.Left, eq.Right), pre, indent);
      var l = this.EmitExpr(eq.Left, pre, indent);
      var r = this.EmitExpr(eq.Right, pre, indent);
      if (eq.Left.Type == IrType.String)
        return $"(StrComp({l}, {r}, 0) = 0)";
      return $"(({l}) = ({r}))";
    }

    private string EmitGenericCompare(IrGenericCompare cmp, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var l = this.EmitExpr(cmp.Left, pre, indent);
      var r = this.EmitExpr(cmp.Right, pre, indent);
      if (cmp.Left.Type == IrType.String)
        return $"StrComp({l}, {r}, 0)";
      var lv = this.NewTemp("ply_cl");
      var rv = this.NewTemp("ply_cr");
      var res = this.NewTemp("ply_cm");
      pre.Append(pad).Append(lv).Append(" = ").Append(l).Append('\n');
      pre.Append(pad).Append(rv).Append(" = ").Append(r).Append('\n');
      pre.Append(pad).Append("If ").Append(lv).Append(" < ").Append(rv).Append(" Then\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = -1\n");
      pre.Append(pad).Append("ElseIf ").Append(lv).Append(" > ").Append(rv).Append(" Then\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = 1\n");
      pre.Append(pad).Append("Else\n");
      pre.Append(Indent(indent + 1)).Append(res).Append(" = 0\n");
      pre.Append(pad).Append("End If\n");
      return res;
    }

    private void EmitVoidIntrinsic(IrIntrinsic intrinsic, StringBuilder pre, int indent) {
      var pad = Indent(indent);
      var args = intrinsic.Arguments.Select(a => this.EmitExpr(a, pre, indent)).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayCopy:
          pre.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.ArrCopy)).Append('(').AppendJoin(", ", args).Append(")\n");
          break;
        case IrIntrinsicOp.ArrayFill:
          pre.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.ArrFill)).Append('(').Append(args[0]).Append(", ").Append(args[1]).Append(")\n");
          break;
        case IrIntrinsicOp.ArrayClear:
          pre.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.ArrFill)).Append('(').Append(args[0]).Append(", ").Append(this.DefaultValue(intrinsic.Arguments[0].Type.Element!)).Append(")\n");
          break;
        case IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig: {
          var valType = intrinsic.Arguments[2].Type;
          if (!valType.IsInteger || valType.IsLong) {
            this.ReportIntrinsic(intrinsic.Op.ToString());
            return;
          }
          var little = intrinsic.Op == IrIntrinsicOp.StoreLittle ? "True" : "False";
          pre.Append(pad).Append("Call ").Append(this.Use(VBScriptHelpers.StoreEndian)).Append('(')
            .Append(args[0]).Append(", ").Append(args[1]).Append(", ").Append(args[2]).Append(", ")
            .Append(valType.Size.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(little).Append(")\n");
          break;
        }
      }
    }

    private string EmitIntrinsic(IrIntrinsic intrinsic, StringBuilder pre, int indent) {
      var args = intrinsic.Arguments.Select(a => this.EmitExpr(a, pre, indent)).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ArrayOf:
          return $"Array({string.Join(", ", args)})";

        case IrIntrinsicOp.Abs: {
          var t = intrinsic.Type.Integral;
          if (t.IsFloat)
            return $"Abs(CDbl({args[0]}))";
          if (!t.IsSigned)
            return args[0];
          return this.WrapInteger(t, $"{this.Use(VBScriptHelpers.AbsSigned)}({args[0]}, {MinDouble(t)})");
        }

        case IrIntrinsicOp.Min:
          return $"{this.Use(VBScriptHelpers.Min)}({args[0]}, {args[1]})";

        case IrIntrinsicOp.Max:
          return $"{this.Use(VBScriptHelpers.Max)}({args[0]}, {args[1]})";

        case IrIntrinsicOp.Sign:
          return $"CLng(Sgn({args[0]}))";

        case IrIntrinsicOp.Sqrt:
          return $"Sqr(CDbl({args[0]}))";

        case IrIntrinsicOp.Floor:
          return $"Int(CDbl({args[0]}))";

        case IrIntrinsicOp.Ceiling:
          return $"(-Int(-CDbl({args[0]})))";

        case IrIntrinsicOp.Truncate:
          return $"Fix(CDbl({args[0]}))";

        case IrIntrinsicOp.RoundEven:
          return $"Round(CDbl({args[0]}))";

        case IrIntrinsicOp.RoundAway:
          return $"{this.Use(VBScriptHelpers.RoundAway)}({args[0]})";

        case IrIntrinsicOp.RoundHalfUp:
          return $"{this.Use(VBScriptHelpers.RoundHalfUp)}({args[0]})";

        case IrIntrinsicOp.CopySign:
          return $"{this.Use(VBScriptHelpers.CopySign)}({args[0]}, {args[1]})";

        case IrIntrinsicOp.ToInt32:
          return $"{this.Use(VBScriptHelpers.I32)}(Fix(CDbl({args[0]})))";

        case IrIntrinsicOp.ToUint32:
          return $"{this.Use(VBScriptHelpers.U32)}(Fix(CDbl({args[0]})))";

        case IrIntrinsicOp.RotateLeft when intrinsic.Type.Bits == 32:
          return $"{this.Use(VBScriptHelpers.Rotl32)}({args[0]}, {args[1]}, {(intrinsic.Type.IsSigned ? "True" : "False")})";

        case IrIntrinsicOp.RotateRight when intrinsic.Type.Bits == 32:
          return $"{this.Use(VBScriptHelpers.Rotr32)}({args[0]}, {args[1]}, {(intrinsic.Type.IsSigned ? "True" : "False")})";

        case IrIntrinsicOp.PopCount when intrinsic.Arguments[0].Type.Bits <= 32: {
          var bits = intrinsic.Arguments[0].Type.Bits;
          var masked = bits < 32 ? this.WrapInteger(bits == 8 ? IrType.U8 : IrType.U16, args[0]) : args[0];
          return $"{this.Use(VBScriptHelpers.PopCnt32)}({masked})";
        }

        case IrIntrinsicOp.LeadingZeroCount when intrinsic.Arguments[0].Type.Bits <= 32:
          return $"{this.Use(VBScriptHelpers.Clz32)}({args[0]}, {intrinsic.Arguments[0].Type.Bits.ToString(CultureInfo.InvariantCulture)})";

        case IrIntrinsicOp.TrailingZeroCount when intrinsic.Arguments[0].Type.Bits <= 32:
          return $"{this.Use(VBScriptHelpers.Ctz32)}({args[0]}, {intrinsic.Arguments[0].Type.Bits.ToString(CultureInfo.InvariantCulture)})";

        case IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig: {
          var t = intrinsic.Type;
          if (!t.IsInteger || t.IsLong)
            return this.ReportIntrinsic(intrinsic.Op.ToString());
          var little = intrinsic.Op == IrIntrinsicOp.LoadLittle ? "True" : "False";
          var signed = t.IsSigned ? "True" : "False";
          return $"{this.Use(VBScriptHelpers.LoadEndian)}({args[0]}, {args[1]}, {t.Size.ToString(CultureInfo.InvariantCulture)}, {little}, {signed})";
        }

        case >= IrIntrinsicOp.StrLength and <= IrIntrinsicOp.StrPadEnd:
          return this.EmitStringIntrinsic(intrinsic, args);

        case >= IrIntrinsicOp.EnumToString and <= IrIntrinsicOp.EnumValues:
          return this.EmitEnumIntrinsic(intrinsic, args, pre, indent);

        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma:
          return $"CStr({args[0]})";

        case IrIntrinsicOp.FormatBoolDotNet:
          return $"CStr({args[0]})";

        case IrIntrinsicOp.FormatBoolEcma:
          return $"LCase(CStr({args[0]}))";

        default:
          return this.ReportIntrinsic(intrinsic.Op.ToString());
      }
    }

    private string EmitStringIntrinsic(IrIntrinsic intrinsic, string[] args) {
      string Needle(int index) => intrinsic.Arguments[index].Type == IrType.String ? args[index] : $"ChrW({args[index]})";
      return intrinsic.Op switch {
        IrIntrinsicOp.StrLength => $"Len({args[0]})",
        IrIntrinsicOp.StrCodeUnitAt => $"{this.Use(VBScriptHelpers.StrUnit)}({args[0]}, {args[1]})",
        IrIntrinsicOp.StrConcat => "(" + string.Join(" & ", args) + ")",
        IrIntrinsicOp.StrFromCodeUnit => $"ChrW({args[0]})",
        IrIntrinsicOp.StrFromCodeUnits => args.Length == 1
          ? $"{this.Use(VBScriptHelpers.StrFromUnits)}({args[0]}, 0, UBound({args[0]}) + 1)"
          : $"{this.Use(VBScriptHelpers.StrFromUnits)}({args[0]}, {args[1]}, {args[2]})",
        IrIntrinsicOp.StrToCodeUnits => $"{this.Use(VBScriptHelpers.StrToUnits)}({args[0]})",
        IrIntrinsicOp.StrRepeat => $"{this.Use(VBScriptHelpers.StrRepeat)}({Needle(0)}, {args[1]})",
        IrIntrinsicOp.StrSubstring => args.Length == 2
          ? $"{this.Use(VBScriptHelpers.StrSub)}({args[0]}, {args[1]}, Len({args[0]}) - ({args[1]}))"
          : $"{this.Use(VBScriptHelpers.StrSub)}({args[0]}, {args[1]}, {args[2]})",
        IrIntrinsicOp.StrEquals => $"(StrComp({args[0]}, {args[1]}, 0) = 0)",
        IrIntrinsicOp.StrCompareOrdinal => $"StrComp({args[0]}, {args[1]}, 0)",
        IrIntrinsicOp.StrIndexOf => $"{this.Use(VBScriptHelpers.StrIndexOf)}({args[0]}, {Needle(1)}, {(args.Length == 2 ? "0" : args[2])})",
        IrIntrinsicOp.StrLastIndexOf => $"{this.Use(VBScriptHelpers.StrLastIndexOf)}({args[0]}, {Needle(1)}, {(args.Length == 2 ? $"Len({args[0]})" : args[2])})",
        IrIntrinsicOp.StrStartsWith => $"{this.Use(VBScriptHelpers.StrStarts)}({args[0]}, {Needle(1)})",
        IrIntrinsicOp.StrEndsWith => $"{this.Use(VBScriptHelpers.StrEnds)}({args[0]}, {Needle(1)})",
        IrIntrinsicOp.StrContains => $"{this.Use(VBScriptHelpers.StrContains)}({args[0]}, {Needle(1)})",
        IrIntrinsicOp.StrReplaceAll => $"{this.Use(VBScriptHelpers.StrReplace)}({args[0]}, {Needle(1)}, {Needle(2)})",
        IrIntrinsicOp.StrReplaceFirst => $"{this.Use(VBScriptHelpers.StrReplaceFirst)}({args[0]}, {args[1]}, {args[2]})",
        IrIntrinsicOp.StrSplit => $"{this.Use(VBScriptHelpers.StrSplit)}({args[0]}, {Needle(1)}, {args[2]})",
        IrIntrinsicOp.StrJoin => $"Join({args[1]}, {args[0]})",
        IrIntrinsicOp.StrTrim => $"{this.Use(VBScriptHelpers.StrTrim)}({args[0]}, 0)",
        IrIntrinsicOp.StrTrimStart => $"{this.Use(VBScriptHelpers.StrTrim)}({args[0]}, 1)",
        IrIntrinsicOp.StrTrimEnd => $"{this.Use(VBScriptHelpers.StrTrim)}({args[0]}, 2)",
        IrIntrinsicOp.StrPadStart => $"{this.Use(VBScriptHelpers.StrPad)}({args[0]}, {args[1]}, {args[2]}, True)",
        _ => $"{this.Use(VBScriptHelpers.StrPad)}({args[0]}, {args[1]}, {args[2]}, False)"
      };
    }

    private string EmitEnumIntrinsic(IrIntrinsic intrinsic, string[] args, StringBuilder pre, int indent) {
      var enumType = (intrinsic.Arguments[0].Type.EnumType ?? intrinsic.Type.EnumType)!;
      var pad = Indent(indent);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.EnumHasFlag:
          return $"({this.Use(VBScriptHelpers.And32)}({args[0]}, {args[1]}) = {this.Use(VBScriptHelpers.I32)}({args[1]}))";
        case IrIntrinsicOp.EnumNames:
          return $"Array({string.Join(", ", enumType.Ordered.Select(m => VBScriptNaming.StringLiteral(m.Name)))})";
        case IrIntrinsicOp.EnumValues:
          return $"Array({string.Join(", ", enumType.Ordered.Select(m => FormatIntLiteral(enumType.Underlying, m.Value)))})";
        case IrIntrinsicOp.EnumIsDefined: {
          var v = this.NewTemp("ply_ev");
          var res = this.NewTemp("ply_ed");
          pre.Append(pad).Append(v).Append(" = ").Append(args[0]).Append('\n');
          var checks = enumType.Members.Select(m => $"{v} = {FormatIntLiteral(enumType.Underlying, m.Value)}");
          pre.Append(pad).Append(res).Append(" = (").AppendJoin(" Or ", checks).Append(")\n");
          return res;
        }
        case IrIntrinsicOp.EnumToString: {
          var v = this.NewTemp("ply_ev");
          var res = this.NewTemp("ply_es");
          pre.Append(pad).Append(v).Append(" = ").Append(args[0]).Append('\n');
          pre.Append(pad).Append("Select Case ").Append(v).Append('\n');
          foreach (var m in enumType.Members)
            pre.Append(Indent(indent + 1)).Append("Case ").Append(FormatIntLiteral(enumType.Underlying, m.Value))
              .Append(" : ").Append(res).Append(" = ").Append(VBScriptNaming.StringLiteral(m.Name)).Append('\n');
          pre.Append(Indent(indent + 1)).Append("Case Else : ").Append(res).Append(" = CStr(").Append(v).Append(")\n");
          pre.Append(pad).Append("End Select\n");
          return res;
        }
        default: {
          var s = this.NewTemp("ply_ep");
          var res = this.NewTemp("ply_er");
          pre.Append(pad).Append(s).Append(" = ").Append(args[0]).Append('\n');
          pre.Append(pad).Append("Select Case ").Append(s).Append('\n');
          foreach (var m in enumType.Members)
            pre.Append(Indent(indent + 1)).Append("Case ").Append(VBScriptNaming.StringLiteral(m.Name))
              .Append(" : ").Append(res).Append(" = ").Append(FormatIntLiteral(enumType.Underlying, m.Value)).Append('\n');
          pre.Append(Indent(indent + 1)).Append("Case Else : Err.Raise vbObjectError + 1007, \"FormatException\", \"Unknown enum value.\"\n");
          pre.Append(pad).Append("End Select\n");
          return res;
        }
      }
    }

    private static bool TryComparisonOp(IrBinaryOp op, out string token) {
      token = op switch {
        IrBinaryOp.Equal => "=",
        IrBinaryOp.NotEqual => "<>",
        IrBinaryOp.Less => "<",
        IrBinaryOp.LessOrEqual => "<=",
        IrBinaryOp.Greater => ">",
        IrBinaryOp.GreaterOrEqual => ">=",
        _ => ""
      };
      return token.Length > 0;
    }

    private static string MinDouble(IrType type)
      => $"{IrOperators.Minimum(type).ToString(CultureInfo.InvariantCulture)}.0";

    private static string MaxDouble(IrType type)
      => $"{IrOperators.Maximum(type).ToString(CultureInfo.InvariantCulture)}.0";

  }

}
