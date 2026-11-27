using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

public static partial class BatchEmitter {

  private sealed partial class UnifiedEmitter {

    private sealed partial class FunctionWriter {

      public int EmitExpr(IrExpr expr, int slot) {
        switch (expr) {
          case IrConst constant:
            this.EmitConst(constant, slot);
            return slot;
          case IrStrConst strConst:
            this.EmitStrConst(strConst, slot);
            return slot;
          case IrConstRef constRef:
            this.EmitVarToSlot(this._owner._constVars[constRef.Constant], slot);
            return slot;
          case IrLocalRef localRef:
            this.EmitReadLocal(localRef.Local, slot);
            return slot;
          case IrGlobalRef globalRef:
            this.EmitVarToSlot(this._owner._globalVars[globalRef.Global], slot);
            return slot;
          case IrNull:
            this.EmitSetSlotLiteral(slot, expr.Type.IsPointer ? "0" : _nullToken);
            return slot;
          case IrNone:
            this.EmitSetSlotLiteral(slot, _noneToken);
            return slot;
          case IrSome some:
            this.EmitExpr(some.Value, slot);
            return slot;
          case IrIsNone isNone:
            this.EmitExpr(isNone.Value, slot);
            this.EmitFrame($"if \"!{this.SlotVar(slot)}!\"==\"{_noneToken}\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            return slot;
          case IrIsNull isNull:
            this.EmitExpr(isNull.Value, slot);
            var nullTok = isNull.Value.Type.IsPointer ? "0" : _nullToken;
            this.EmitFrame($"if \"!{this.SlotVar(slot)}!\"==\"{nullTok}\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            return slot;
          case IrUnwrap unwrap:
            this.EmitExpr(unwrap.Value, slot);
            if (!unwrap.IsTested) {
              this.EmitSlotToVar(slot, "_uw_v");
              var okLbl = this._owner.NewLabel("_uw_ok");
              this.Line($"if not \"!_uw_v!\"==\"{_noneToken}\" goto :{okLbl}");
              this.EmitTrap(IrExceptionClass.InvalidOperationException, "MissingValue");
              this.Mark(okLbl);
            }

            return slot;
          case IrCoalesce coalesce:
            this.EmitCoalesce(coalesce, slot);
            return slot;
          case IrBind bind:
            this.EmitExpr(bind.Value, slot);
            this.EmitWriteLocal(bind.Local, slot, copyStruct: true);
            return slot;
          case IrUnary unary:
            this.EmitUnary(unary, slot);
            return slot;
          case IrBinary binary:
            this.EmitBinary(binary, slot);
            return slot;
          case IrConvert convert:
            this.EmitConvert(convert, slot);
            return slot;
          case IrConditional conditional:
            this.EmitConditional(conditional, slot);
            return slot;
          case IrStep step:
            this.EmitStep(step, slot);
            return slot;
          case IrNewArray newArr:
            this.EmitNewArray(newArr, slot);
            return slot;
          case IrArrayLength arrLen:
            this.EmitArrayLength(arrLen, slot);
            return slot;
          case IrArrayLoad arrLoad:
            this.EmitArrayLoadRaw(arrLoad, slot, copyStruct: true);
            return slot;
          case IrStructNew structNew:
            this.EmitStructNew(structNew, slot);
            return slot;
          case IrStructDefault structDef:
            this.EmitStructDefault(structDef.Type.StructType!, slot);
            return slot;
          case IrFieldLoad fieldLoad:
            this.EmitFieldLoad(fieldLoad, slot);
            return slot;
          case IrStructEquals structEq:
            this.EmitStructEquals(structEq, slot);
            return slot;
          case IrNew creation:
            this.EmitNewObject(creation, slot);
            return slot;
          case IrMethodCall methodCall:
            this.EmitMethodCall(methodCall, slot);
            return slot;
          case IrReferenceEquals refEq:
            this.EmitExpr(refEq.Left, slot);
            this.EmitExpr(refEq.Right, slot + 1);
            this.EmitFrame($"if \"!{this.SlotVar(slot)}!\"==\"!{this.SlotVar(slot + 1)}!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            return slot;
          case IrTypeTest typeTest:
            this.EmitTypeTest(typeTest, slot);
            return slot;
          case IrCast cast:
            this.EmitCast(cast, slot);
            return slot;
          case IrAsCast asCast:
            this.EmitAsCast(asCast, slot);
            return slot;
          case IrCall call:
            this.EmitCall(call, slot);
            return slot;
          case IrLambda lambda:
            this.EmitLambda(lambda, slot);
            return slot;
          case IrFunctionRef fnRef:
            this.EmitFunctionRef(fnRef, slot);
            return slot;
          case IrBoundMethod bound:
            this.EmitBoundMethod(bound, slot);
            return slot;
          case IrInvoke invoke:
            this.EmitInvoke(invoke, slot);
            return slot;
          case IrGenericEquals genEq:
            this.EmitGenericEquals(genEq, slot);
            return slot;
          case IrGenericCompare genCmp:
            this.EmitGenericCompare(genCmp, slot);
            return slot;
          case IrCollection collection:
            this.EmitCollection(collection, slot);
            return slot;
          case IrArraySequence arrSeq:
            this.EmitArraySequence(arrSeq, slot);
            return slot;
          case IrAsync asyncExpr:
            this.EmitAsync(asyncExpr, slot);
            return slot;
          case IrAwait awaitExpr:
            this.EmitExpr(awaitExpr.Operand, slot);
            return slot;
          case IrThrowExpression throwExpr:
            this.EmitThrow(throwExpr.Exception);
            return slot;
          case IrIntrinsic intrinsic:
            this.EmitIntrinsic(intrinsic, slot);
            return slot;
          case IrForeignCall or IrLastError:
            throw new Refusal("foreign native interop call");
          default:
            throw new Refusal(expr.GetType().Name);
        }
      }

      private void EmitConst(IrConst constant, int slot) {
        var formatted = FormatConstValue(constant.Type, constant.Value);
        this.EmitSetSlotLiteral(slot, formatted);
      }

      private void EmitStrConst(IrStrConst strConst, int slot) {
        var escaped = this._owner.EscapeStringLiteral(strConst.Value);
        this.Line("set /a \"_HID+=1\"");
        this.Line($"set \"_S_s!_HID!={escaped}\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=s!_HID!\"");
      }

      private void EmitCoalesce(IrCoalesce coalesce, int slot) {
        this.EmitExpr(coalesce.Value, slot);
        var doneLbl = this._owner.NewLabel("_coa_d");
        this.EmitSlotToVar(slot, "_cv");
        this.Line($"if not \"!_cv!\"==\"{_noneToken}\" goto :{doneLbl}");
        this.EmitExpr(coalesce.Fallback, slot);
        this.Mark(doneLbl);
      }

      private void EmitConditional(IrConditional conditional, int slot) {
        var yes = this._owner.NewLabel("_cnd_t");
        var no = this._owner.NewLabel("_cnd_f");
        var end = this._owner.NewLabel("_cnd_e");
        this.EmitBranch(conditional.Condition, yes, no, slot);
        this.Mark(yes);
        this.EmitExpr(conditional.WhenTrue, slot);
        this.Jump(end);
        this.Mark(no);
        this.EmitExpr(conditional.WhenFalse, slot);
        this.Mark(end);
      }

      private void EmitBranch(IrExpr condition, string yes, string no, int slot) {
        if (condition is IrConst constant && constant.Type == IrType.Bool) {
          this.Jump(constant.Value == 0 ? no : yes);
          return;
        }

        if (condition is IrUnary { Op: IrUnaryOp.LogicalNot } negated) {
          this.EmitBranch(negated.Operand, no, yes, slot);
          return;
        }

        if (condition is IrBinary { Op: IrBinaryOp.LogicalAnd } both) {
          var next = this._owner.NewLabel("_and");
          this.EmitBranch(both.Left, next, no, slot);
          this.Mark(next);
          this.EmitBranch(both.Right, yes, no, slot);
          return;
        }

        if (condition is IrBinary { Op: IrBinaryOp.LogicalOr } either) {
          var next = this._owner.NewLabel("_or");
          this.EmitBranch(either.Left, yes, next, slot);
          this.Mark(next);
          this.EmitBranch(either.Right, yes, no, slot);
          return;
        }

        this.EmitExpr(condition, slot);
        this.EmitSlotToVar(slot, "_br");
        this.Line($"if \"!_br!\"==\"1\" goto :{yes}");
        this.Jump(no);
      }

      private void EmitUnary(IrUnary unary, int slot) {
        this.EmitExpr(unary.Operand, slot);
        var integral = unary.Type.Integral;
        if (unary.Op == IrUnaryOp.LogicalNot) {
          this.EmitFrame($"set /a \"{this.SlotVar(slot)}=1-{this.SlotVar(slot)}\"");
          return;
        }

        if (integral.IsLong) {
          this.EmitSlotToVar(slot, "_u64");
          if (unary.Op == IrUnaryOp.BitNot) {
            this._owner.RequireHelper("bit64");
            this.Line("call :__ply_bit64 not \"!_u64!\" \"0:0\"");
            this.EmitVarToSlot("_RET", slot);
          } else {
            if (unary.Op == IrUnaryOp.NegateChecked) {
              var okLbl = this._owner.NewLabel("_n64_ok");
              this.Line($"if not \"!_u64!\"==\"-2147483648:0\" goto :{okLbl}");
              this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
              this.Mark(okLbl);
            }

            this._owner.RequireHelper("neg64");
            this.Line("call :__ply_neg64 \"!_u64!\"");
            this.EmitVarToSlot("_RET", slot);
          }

          return;
        }

        switch (unary.Op) {
          case IrUnaryOp.Negate:
            this.EmitFrame($"set /a \"{this.SlotVar(slot)}=-{this.SlotVar(slot)}\"");
            break;
          case IrUnaryOp.NegateChecked:
            this.EmitSlotToVar(slot, "_nc");
            var ok = this._owner.NewLabel("_nc_ok");
            this.Line($"if not \"!_nc!\"==\"-2147483648\" goto :{ok}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok);
            this.EmitFrame($"set /a \"{this.SlotVar(slot)}=-{this.SlotVar(slot)}\"");
            break;
          case IrUnaryOp.BitNot:
            this.EmitFrame($"set /a \"{this.SlotVar(slot)}=~{this.SlotVar(slot)}\"");
            break;
        }
      }

      private void EmitBinary(IrBinary binary, int slot) {
        if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr) {
          var yes = this._owner.NewLabel("_lg_t");
          var no = this._owner.NewLabel("_lg_f");
          var end = this._owner.NewLabel("_lg_e");
          this.EmitBranch(binary, yes, no, slot);
          this.Mark(yes);
          this.EmitSetSlotLiteral(slot, "1");
          this.Jump(end);
          this.Mark(no);
          this.EmitSetSlotLiteral(slot, "0");
          this.Mark(end);
          return;
        }

        this.EmitExpr(binary.Left, slot);
        this.EmitExpr(binary.Right, slot + 1);
        var operandType = binary.Left.Type.Integral;

        if (operandType.IsLong) {
          this.EmitBinary64(binary.Op, operandType.IsSigned, slot);
          return;
        }

        var l = this.SlotVar(slot);
        var r = this.SlotVar(slot + 1);
        switch (binary.Op) {
          case IrBinaryOp.Add:
            this.EmitFrame($"set /a \"{l}={l}+{r}\"");
            break;
          case IrBinaryOp.Subtract:
            this.EmitFrame($"set /a \"{l}={l}-{r}\"");
            break;
          case IrBinaryOp.Multiply:
            this.EmitFrame($"set /a \"{l}={l}*{r}\"");
            break;
          case IrBinaryOp.And:
            this.EmitFrame($"set /a \"{l}={l}&{r}\"");
            break;
          case IrBinaryOp.Or:
            this.EmitFrame($"set /a \"{l}={l}|{r}\"");
            break;
          case IrBinaryOp.Xor:
            this.EmitFrame($"set /a \"{l}={l}^{r}\"");
            break;
          case IrBinaryOp.ShiftLeft:
            this.EmitFrame($"set /a \"_sc={r}&31, {l}={l}<<_sc\"");
            break;
          case IrBinaryOp.ShiftRight when operandType.IsSigned:
            this.EmitFrame($"set /a \"_sc={r}&31, _sg=-((({l}>>31)&1)), {l}=(({l}^_sg)>>_sc)^_sg\"");
            break;
          case IrBinaryOp.ShiftRight:
          case IrBinaryOp.ShiftRightLogical:
            var srDone = this._owner.NewLabel("_srl");
            this.EmitFrame($"set /a \"_sc={r}&31\"");
            this.Line($"if !_sc! EQU 0 goto :{srDone}");
            this.EmitFrame($"set /a \"{l}=({l}>>_sc)&(2147483647>>(_sc-1))\"");
            this.Mark(srDone);
            break;
          case IrBinaryOp.AddChecked when operandType.IsSigned:
            this.EmitFrame($"set /a \"_res={l}+{r}, _ov=((({l}^_res)&({r}^_res))>>31)&1, {l}=_res\"");
            this.EmitOverflowTrapIfSet("_ov");
            break;
          case IrBinaryOp.AddChecked:
            this.EmitFrame($"set /a \"_res={l}+{r}, _s1=({l}>>31)&1, _s2=({r}>>31)&1, _ss=(_res>>31)&1, _ov=(_s1&_s2)|((_s1|_s2)&(_ss^1)), {l}=_res\"");
            this.EmitOverflowTrapIfSet("_ov");
            break;
          case IrBinaryOp.SubtractChecked when operandType.IsSigned:
            this.EmitFrame($"set /a \"_res={l}-{r}, _ov=((({l}^{r})&({l}^_res))>>31)&1, {l}=_res\"");
            this.EmitOverflowTrapIfSet("_ov");
            break;
          case IrBinaryOp.SubtractChecked:
            this.EmitFrame($"set /a \"_res={l}-{r}, _s1=({l}>>31)&1, _s2=({r}>>31)&1, _sd=(_res>>31)&1, _ov=((_s1^1)&_s2)|(((_s1^_s2)^1)&_sd), {l}=_res\"");
            this.EmitOverflowTrapIfSet("_ov");
            break;
          case IrBinaryOp.MultiplyChecked when operandType.IsSigned:
            this._owner.RequireHelper("mulchk32");
            this.EmitSlotToVar(slot, "_ml");
            this.EmitSlotToVar(slot + 1, "_mr");
            this.Line("call :__ply_mulchk32 \"!_ml!\" \"!_mr!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.MultiplyChecked:
            this._owner.RequireHelper("mul64");
            this.EmitSlotToVar(slot, "_ml");
            this.EmitSlotToVar(slot + 1, "_mr");
            this.Line("call :__ply_mul64 \"0:!_ml!\" \"0:!_mr!\"");
            this.Line("for /f \"tokens=1,2 delims=:\" %%A in (\"!_RET!\") do (set \"_mh=%%A\" & set \"_mlo=%%B\")");
            var umOk = this._owner.NewLabel("_um_ok");
            this.Line($"if \"!_mh!\"==\"0\" goto :{umOk}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(umOk);
            this.EmitVarToSlot("_mlo", slot);
            break;
          case IrBinaryOp.Divide when operandType.IsSigned:
          case IrBinaryOp.Remainder when operandType.IsSigned:
            this.EmitSignedDivRem32(binary.Op == IrBinaryOp.Remainder, slot);
            break;
          case IrBinaryOp.Divide:
          case IrBinaryOp.Remainder:
            this._owner.RequireHelper("udiv32");
            this.EmitSlotToVar(slot, "_dl");
            this.EmitSlotToVar(slot + 1, "_dr");
            this.Line("call :__ply_udiv32 \"!_dl!\" \"!_dr!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot(binary.Op == IrBinaryOp.Remainder ? "_REM" : "_RET", slot);
            break;
          case IrBinaryOp.Equal:
            this.EmitFrame($"if \"!{l}!\"==\"!{r}!\" (set \"{l}=1\") else (set \"{l}=0\")");
            break;
          case IrBinaryOp.NotEqual:
            this.EmitFrame($"if not \"!{l}!\"==\"!{r}!\" (set \"{l}=1\") else (set \"{l}=0\")");
            break;
          default:
            this.EmitOrderedCompare32(binary.Op, operandType.IsSigned, slot);
            break;
        }
      }

      private void EmitOverflowTrapIfSet(string flagVar) {
        var ok = this._owner.NewLabel("_ov_ok");
        this.Line($"if !{flagVar}! EQU 0 goto :{ok}");
        this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
        this.Mark(ok);
      }

      private void EmitSignedDivRem32(bool isRemainder, int slot) {
        this.EmitSlotToVar(slot, "_da");
        this.EmitSlotToVar(slot + 1, "_db");
        var nz = this._owner.NewLabel("_dv_nz");
        this.Line($"if not \"!_db!\"==\"0\" goto :{nz}");
        this.EmitTrap(IrExceptionClass.DivideByZeroException, "DivideByZero");
        this.Mark(nz);
        var noOv = this._owner.NewLabel("_dv_ok");
        this.Line($"if not \"!_da!\"==\"-2147483648\" goto :{noOv}");
        this.Line($"if not \"!_db!\"==\"-1\" goto :{noOv}");
        this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
        this.Mark(noOv);
        var op = isRemainder ? "%%" : "/";
        this.EmitFrame($"set /a \"{this.SlotVar(slot)}=_da{op}_db\"");
      }

      private void EmitOrderedCompare32(IrBinaryOp op, bool isSigned, int slot) {
        var l = this.SlotVar(slot);
        var r = this.SlotVar(slot + 1);
        if (isSigned) {
          this.EmitFrame($"set /a \"_ca={l}, _cb={r}\"");
        } else {
          this.EmitFrame($"set /a \"_ca={l}^(-2147483647-1), _cb={r}^(-2147483647-1)\"");
        }

        var cmpOp = op switch {
          IrBinaryOp.Less => "LSS",
          IrBinaryOp.LessOrEqual => "LEQ",
          IrBinaryOp.Greater => "GTR",
          _ => "GEQ"
        };
        var signOp = op is IrBinaryOp.Less or IrBinaryOp.LessOrEqual ? "GTR" : "LSS";
        this.Line("set /a \"_csa=(_ca>>31)&1, _csb=(_cb>>31)&1\"");
        var sameSign = this._owner.NewLabel("_cmp_s");
        var cmpDone = this._owner.NewLabel("_cmp_d");
        this.Line($"if !_csa! EQU !_csb! goto :{sameSign}");
        this.EmitFrame($"if !_csa! {signOp} !_csb! (set \"{l}=1\") else (set \"{l}=0\")");
        this.Jump(cmpDone);
        this.Mark(sameSign);
        this.Line("set /a \"_cma=_ca&2147483647, _cmb=_cb&2147483647\"");
        this.EmitFrame($"if !_cma! {cmpOp} !_cmb! (set \"{l}=1\") else (set \"{l}=0\")");
        this.Mark(cmpDone);
      }

      private void EmitBinary64(IrBinaryOp op, bool isSigned, int slot) {
        this.EmitSlotToVar(slot, "_b64_l");
        this.EmitSlotToVar(slot + 1, "_b64_r");
        switch (op) {
          case IrBinaryOp.Add:
            this._owner.RequireHelper("add64");
            this.Line("call :__ply_add64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.Subtract:
            this._owner.RequireHelper("sub64");
            this.Line("call :__ply_sub64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.Multiply:
            this._owner.RequireHelper("mul64");
            this.Line("call :__ply_mul64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.AddChecked:
            this._owner.RequireHelper("add64");
            this.Line("call :__ply_add64 \"!_b64_l!\" \"!_b64_r!\"");
            this.Line("set \"_b64_res=!_RET!\"");
            this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_l!\") do set /a \"_lh=%%A\"");
            this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_r!\") do set /a \"_rh=%%A\"");
            this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_res!\") do set /a \"_sh=%%A\"");
            if (isSigned) {
              this.Line("set /a \"_ov=(((_lh^_sh)&(_rh^_sh))>>31)&1\"");
            } else {
              this._owner.RequireHelper("cmp_u64");
              this.Line("call :__ply_cmp_u64 \"!_b64_res!\" \"!_b64_l!\"");
              this.Line("if !_RET! LSS 0 (set \"_ov=1\") else (set \"_ov=0\")");
            }

            this.EmitOverflowTrapIfSet("_ov");
            this.EmitVarToSlot("_b64_res", slot);
            break;
          case IrBinaryOp.SubtractChecked:
            this._owner.RequireHelper("sub64");
            this.Line("call :__ply_sub64 \"!_b64_l!\" \"!_b64_r!\"");
            this.Line("set \"_b64_res=!_RET!\"");
            if (isSigned) {
              this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_l!\") do set /a \"_lh=%%A\"");
              this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_r!\") do set /a \"_rh=%%A\"");
              this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_b64_res!\") do set /a \"_dh=%%A\"");
              this.Line("set /a \"_ov=(((_lh^_rh)&(_lh^_dh))>>31)&1\"");
            } else {
              this._owner.RequireHelper("cmp_u64");
              this.Line("call :__ply_cmp_u64 \"!_b64_l!\" \"!_b64_r!\"");
              this.Line("if !_RET! LSS 0 (set \"_ov=1\") else (set \"_ov=0\")");
            }

            this.EmitOverflowTrapIfSet("_ov");
            this.EmitVarToSlot("_b64_res", slot);
            break;
          case IrBinaryOp.MultiplyChecked:
            this._owner.RequireHelper("mul64");
            this._owner.RequireHelper(isSigned ? "sdiv64" : "udiv64");
            this.Line("call :__ply_mul64 \"!_b64_l!\" \"!_b64_r!\"");
            this.Line("set \"_b64_res=!_RET!\"");
            var mcOk = this._owner.NewLabel("_mc64_ok");
            this.Line($"if \"!_b64_l!\"==\"0:0\" goto :{mcOk}");
            this.Line($"if \"!_b64_r!\"==\"0:0\" goto :{mcOk}");
            this.Line($"call :__ply_{(isSigned ? "sdiv64" : "udiv64")} \"!_b64_res!\" \"!_b64_l!\"");
            this.EmitCheckUnwind();
            this.Line($"if \"!_RET!\"==\"!_b64_r!\" goto :{mcOk}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(mcOk);
            this.EmitVarToSlot("_b64_res", slot);
            break;
          case IrBinaryOp.Divide:
          case IrBinaryOp.Remainder:
            this._owner.RequireHelper(isSigned ? "sdiv64" : "udiv64");
            this.Line($"call :__ply_{(isSigned ? "sdiv64" : "udiv64")} \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot(op == IrBinaryOp.Remainder ? "_REM" : "_RET", slot);
            break;
          case IrBinaryOp.And:
          case IrBinaryOp.Or:
          case IrBinaryOp.Xor:
            this._owner.RequireHelper("bit64");
            var bname = op == IrBinaryOp.And ? "and" : op == IrBinaryOp.Or ? "or" : "xor";
            this.Line($"call :__ply_bit64 {bname} \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.ShiftLeft:
            this._owner.RequireHelper("shl64");
            this.Line("call :__ply_shl64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.ShiftRight when isSigned:
            this._owner.RequireHelper("shr64");
            this.Line("call :__ply_shr64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.ShiftRight:
          case IrBinaryOp.ShiftRightLogical:
            this._owner.RequireHelper("ushr64");
            this.Line("call :__ply_ushr64 \"!_b64_l!\" \"!_b64_r!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrBinaryOp.Equal:
            this.EmitFrame($"if \"!_b64_l!\"==\"!_b64_r!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            break;
          case IrBinaryOp.NotEqual:
            this.EmitFrame($"if not \"!_b64_l!\"==\"!_b64_r!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            break;
          default:
            this._owner.RequireHelper(isSigned ? "cmp_i64" : "cmp_u64");
            this.Line($"call :__ply_cmp_{(isSigned ? "i64" : "u64")} \"!_b64_l!\" \"!_b64_r!\"");
            var cmpOp = op switch {
              IrBinaryOp.Less => "LSS",
              IrBinaryOp.LessOrEqual => "LEQ",
              IrBinaryOp.Greater => "GTR",
              _ => "GEQ"
            };
            this.EmitFrame($"if !_RET! {cmpOp} 0 (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            break;
        }
      }

      private void EmitConvert(IrConvert convert, int slot) {
        this.EmitExpr(convert.Operand, slot);
        var src = convert.Operand.Type.Integral;
        var dst = convert.Type.Integral;
        if (src == dst)
          return;

        var trap = convert.Contract == IrConversion.Trap;
        if (!src.IsLong && dst.IsLong) {
          this.EmitSlotToVar(slot, "_cv");
          if (src.IsSigned) {
            if (trap && !dst.IsSigned) {
              var ok = this._owner.NewLabel("_cv_ok");
              this.Line($"set /a \"_sg=(_cv>>31)&1\"");
              this.Line($"if !_sg! EQU 0 goto :{ok}");
              this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
              this.Mark(ok);
            }

            this.Line("set /a \"_hi=-((_cv>>31)&1)\"");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=!_hi!:!_cv!\"");
          } else {
            this.EmitFrame($"set \"{this.SlotVar(slot)}=0:!_cv!\"");
          }

          return;
        }

        if (src.IsLong && !dst.IsLong) {
          this.EmitSlotToVar(slot, "_cv");
          this.Line("for /f \"tokens=1,2 delims=:\" %%A in (\"!_cv!\") do (set /a \"_hi=%%A, _lo=%%B\")");
          if (trap) {
            var min = IrOperators.Minimum(dst);
            var max = IrOperators.Maximum(dst);
            this._owner.RequireHelper(src.IsSigned ? "cmp_i64" : "cmp_u64");
            var cmp = src.IsSigned ? "cmp_i64" : "cmp_u64";
            var minTok = FormatConstValue(src, (long)min);
            var maxTok = FormatConstValue(src, (long)max);
            var ok1 = this._owner.NewLabel("_cv1");
            var ok2 = this._owner.NewLabel("_cv2");
            this.Line($"call :__ply_{cmp} \"!_cv!\" \"{minTok}\"");
            this.Line($"if !_RET! GEQ 0 goto :{ok1}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok1);
            this.Line($"call :__ply_{cmp} \"!_cv!\" \"{maxTok}\"");
            this.Line($"if !_RET! LEQ 0 goto :{ok2}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok2);
          }

          this.EmitVarToSlot("_lo", slot);
          this.EmitNarrowMaskSlot(dst, slot);
          return;
        }

        if (src.IsLong && dst.IsLong) {
          if (trap && src.IsSigned != dst.IsSigned) {
            this.EmitSlotToVar(slot, "_cv");
            this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_cv!\") do set /a \"_sg=(%%A>>31)&1\"");
            var ok = this._owner.NewLabel("_cv64");
            this.Line($"if !_sg! EQU 0 goto :{ok}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok);
          }

          return;
        }

        // 32-bit or narrower -> 32-bit or narrower.
        if (trap && !IrOperators.Contains(dst, src)) {
          this.EmitSlotToVar(slot, "_cv");
          if (src == IrType.U32) {
            this._owner.RequireHelper("cmp_u32");
            var maxU = unchecked((int)(long)IrOperators.Maximum(dst));
            var ok = this._owner.NewLabel("_cvu");
            this.Line($"call :__ply_cmp_u32 \"!_cv!\" \"{SafeInt32Literal(maxU)}\"");
            this.Line($"if !_RET! LEQ 0 goto :{ok}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok);
          } else {
            var min = (int)IrOperators.Minimum(dst);
            var max = dst == IrType.U32 ? int.MaxValue : (int)IrOperators.Maximum(dst);
            var ok = this._owner.NewLabel("_cvs");
            if (dst == IrType.U32) {
              this.Line("set /a \"_sg=(_cv>>31)&1\"");
              this.Line($"if !_sg! EQU 0 goto :{ok}");
            } else {
              this.Line($"if !_cv! GEQ {SafeInt32Literal(min)} if !_cv! LEQ {SafeInt32Literal(max)} goto :{ok}");
            }

            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok);
          }
        }

        this.EmitNarrowMaskSlot(dst, slot);
      }

      private void EmitNarrowMaskSlot(IrType integral, int slot) {
        var v = this.SlotVar(slot);
        switch (integral.Kind) {
          case IrTypeKind.U8:
            this.EmitFrame($"set /a \"{v}=({v})&255\"");
            break;
          case IrTypeKind.I8:
            this.EmitFrame($"set /a \"{v}=((({v})&255)^128)-128\"");
            break;
          case IrTypeKind.U16:
            this.EmitFrame($"set /a \"{v}=({v})&65535\"");
            break;
          case IrTypeKind.I16:
            this.EmitFrame($"set /a \"{v}=((({v})&65535)^32768)-32768\"");
            break;
        }
      }

      private void EmitStep(IrStep step, int slot) {
        var isLong = step.Type.Integral.IsLong;
        var op = step.Increment
          ? (step.Trap ? IrBinaryOp.AddChecked : IrBinaryOp.Add)
          : (step.Trap ? IrBinaryOp.SubtractChecked : IrBinaryOp.Subtract);
        var one = new IrConst(step.Type.Integral, 1);

        if (step.Target is IrLocalRef localRef) {
          this.EmitReadLocal(localRef.Local, slot);
          this.EmitCopySlot(slot + 1, slot);
          this.EmitConst(one, slot + 2);
          if (isLong) {
            this.EmitCopySlot(slot + 1, slot);
            this.EmitBinary64(op, step.Type.Integral.IsSigned, slot + 1);
          } else {
            this.EmitStepDelta32(step, slot + 1);
          }

          this.EmitWriteLocal(localRef.Local, slot + 1, copyStruct: false);
          if (!step.Postfix)
            this.EmitCopySlot(slot, slot + 1);
        } else if (step.Target is IrArrayLoad load) {
          this.EmitExpr(load.Array, slot + 3);
          this.EmitExpr(load.Index, slot + 4);
          this.EmitSlotToVar(slot + 3, "_st_a");
          this.EmitSlotToVar(slot + 4, "_st_i");
          this.EmitNullTrap("_st_a");
          this.Line("for %%A in (!_st_a!) do set /a \"_st_len=_AL_%%A\"");
          this.EmitBoundsTrap("_st_i", "_st_len");
          this.EmitFrame($"for %%A in (!_st_a!) do for %%I in (!_st_i!) do set \"{this.SlotVar(slot)}=!_A_%%A_%%I!\"");
          this.EmitCopySlot(slot + 1, slot);
          if (isLong) {
            this.EmitConst(one, slot + 2);
            this.EmitBinary64(op, step.Type.Integral.IsSigned, slot + 1);
          } else {
            this.EmitStepDelta32(step, slot + 1);
          }

          this.EmitSlotToVar(slot + 3, "_st_a");
          this.EmitSlotToVar(slot + 4, "_st_i");
          this.EmitSlotToVar(slot + 1, "_st_v");
          this.Line("for %%A in (!_st_a!) do for %%I in (!_st_i!) do set \"_A_%%A_%%I=!_st_v!\"");
          if (!step.Postfix)
            this.EmitCopySlot(slot, slot + 1);
        }
      }

      private void EmitStepDelta32(IrStep step, int slot) {
        var integral = step.Type.Integral;
        var v = this.SlotVar(slot);
        if (step.Trap) {
          this.EmitSlotToVar(slot, "_sv");
          var limit = step.Increment ? (int)IrOperators.Maximum(integral) : (int)IrOperators.Minimum(integral);
          var ok = this._owner.NewLabel("_stp_ok");
          this.Line($"if not \"!_sv!\"==\"{limit.ToString(CultureInfo.InvariantCulture)}\" goto :{ok}");
          this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
          this.Mark(ok);
        }

        this.EmitFrame($"set /a \"{v}{(step.Increment ? "+=1" : "-=1")}\"");
        this.EmitNarrowMaskSlot(integral, slot);
      }

      private void EmitNewArray(IrNewArray newArr, int slot) {
        if (newArr.Length is IrConst { Value: > CmdArrayStorageBudget })
          throw new Refusal($"array storage exceeding {CmdArrayStorageBudget} elements");
        if (newArr.Length is IrConst { Value: >= 0 } cLen) {
          this._owner._constArraySlots += (int)cLen.Value;
          if (this._owner._constArraySlots > CmdArrayStorageBudget)
            throw new Refusal($"array storage exceeding {CmdArrayStorageBudget} elements");
        }

        this.EmitExpr(newArr.Length, slot);
        this.EmitSlotToVar(slot, "_na_len");
        var ok = this._owner.NewLabel("_na_ok");
        this.Line("set /a \"_na_s=(_na_len>>31)&1\"");
        this.Line($"if !_na_s! EQU 0 goto :{ok}");
        this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
        this.Mark(ok);
        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_na_id=!_HID!\"");
        this.Line("set \"_AL_!_na_id!=!_na_len!\"");
        var elemType = newArr.Type.Element!;
        if (elemType.IsStruct) {
          var initDone = this._owner.NewLabel("_na_dn");
          var initLp = this._owner.NewLabel("_na_lp");
          this.EmitVarToSlot("_na_id", slot);
          this.EmitVarToSlot("_na_len", slot + 1);
          this.EmitSetSlotLiteral(slot + 2, "0");
          this.Mark(initLp);
          this.EmitFrame($"if !{this.SlotVar(slot + 2)}! GEQ !{this.SlotVar(slot + 1)}! goto :{initDone}");
          this.EmitStructDefault(elemType.StructType!, slot + 3);
          this.EmitFrame($"for %%A in (!{this.SlotVar(slot)}!) do for %%I in (!{this.SlotVar(slot + 2)}!) do set \"_A_%%A_%%I=!{this.SlotVar(slot + 3)}!\"");
          this.EmitFrame($"set /a \"{this.SlotVar(slot + 2)}+=1\"");
          this.Jump(initLp);
          this.Mark(initDone);
        } else {
          var defTok = elemType.IsNullable ? _nullToken : elemType.Integral.IsLong ? "0:0" : "0";
          this.Line("set /a \"_na_lim=_na_len-1\"");
          this.Line($"if !_na_lim! GEQ 0 for /l %%I in (0,1,!_na_lim!) do set \"_A_!_na_id!_%%I={defTok}\"");
          this.EmitVarToSlot("_na_id", slot);
        }
      }

      private void EmitArrayLength(IrArrayLength arrLen, int slot) {
        this.EmitExpr(arrLen.Array, slot);
        this.EmitSlotToVar(slot, "_al_id");
        this.EmitNullTrap("_al_id");
        this.EmitFrame($"for %%A in (!_al_id!) do set \"{this.SlotVar(slot)}=!_AL_%%A!\"");
      }

      private void EmitArrayLoadRaw(IrArrayLoad load, int slot, bool copyStruct) {
        this.EmitExpr(load.Array, slot);
        this.EmitExpr(load.Index, slot + 1);
        this.EmitSlotToVar(slot, "_la_id");
        this.EmitSlotToVar(slot + 1, "_la_idx");

        if (load.Array.Type.IsView) {
          this.Line("for %%V in (!_la_id!) do (set \"_la_arr=!_V_%%V_arr!\" & set /a \"_la_off=_V_%%V_off, _la_len=_V_%%V_len\")");
          this.EmitBoundsTrap("_la_idx", "_la_len");
          this.Line("set /a \"_la_real=_la_off+_la_idx\"");
          this.EmitFrame($"for %%A in (!_la_arr!) do for %%I in (!_la_real!) do set \"{this.SlotVar(slot)}=!_A_%%A_%%I!\"");
        } else {
          this.EmitNullTrap("_la_id");
          this.Line("for %%A in (!_la_id!) do set /a \"_la_len=_AL_%%A\"");
          this.EmitBoundsTrap("_la_idx", "_la_len");
          this.EmitFrame($"for %%A in (!_la_id!) do for %%I in (!_la_idx!) do set \"{this.SlotVar(slot)}=!_A_%%A_%%I!\"");
        }

        if (copyStruct && load.Type.IsStruct) {
          var sid = this._owner._structIds[load.Type.StructType!];
          this._owner._neededStructCopies.Add(load.Type.StructType!);
          this.EmitSlotToVar(slot, "_cs_in");
          this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
          this.EmitVarToSlot("_RET", slot);
        }
      }

      private void EmitNullTrap(string idVar) {
        var ok = this._owner.NewLabel("_nn_ok");
        this.Line($"if not \"!{idVar}!\"==\"{_nullToken}\" goto :{ok}");
        this.EmitTrap(IrExceptionClass.NullReferenceException, "NullReference");
        this.Mark(ok);
      }

      private void EmitBoundsTrap(string idxVar, string lenVar) {
        var ok = this._owner.NewLabel("_bd_ok");
        var bad = this._owner.NewLabel("_bd_bad");
        this.Line($"set /a \"_bs=({idxVar}>>31)&1\"");
        this.Line($"if !_bs! NEQ 0 goto :{bad}");
        this.Line($"if !{idxVar}! LSS !{lenVar}! goto :{ok}");
        this.Mark(bad);
        this.EmitTrap(IrExceptionClass.IndexOutOfRangeException, "IndexOutOfRange");
        this.Mark(ok);
      }

      private void EmitStructNew(IrStructNew structNew, int slot) {
        var st = structNew.Type.StructType!;
        for (var i = 0; i < structNew.Arguments.Count; ++i)
          this.EmitExpr(structNew.Arguments[i], slot + 1 + i);

        this.Line("set /a \"_HID+=1\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        for (var i = 0; i < st.Fields.Count; ++i) {
          var field = st.Fields[i];
          var fk = this._owner._fieldKeys[field];
          if (field.Type.IsStruct) {
            var sid = this._owner._structIds[field.Type.StructType!];
            this._owner._neededStructCopies.Add(field.Type.StructType!);
            this.EmitSlotToVar(slot + 1 + i, "_cs_in");
            this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}=!_RET!\"");
          } else {
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}=!{this.SlotVar(slot + 1 + i)}!\"");
          }
        }
      }

      private void EmitStructDefault(IrStructType st, int slot) {
        this.Line("set /a \"_HID+=1\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        foreach (var field in st.Fields) {
          var fk = this._owner._fieldKeys[field];
          if (field.Type.IsStruct) {
            this.EmitStructDefault(field.Type.StructType!, slot + 1);
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}=!{this.SlotVar(slot + 1)}!\"");
          } else {
            var defTok = field.Type.IsOptional ? _noneToken : field.Type.IsNullable ? _nullToken : field.Type.Integral.IsLong ? "0:0" : "0";
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}={defTok}\"");
          }
        }
      }

      private void EmitFieldLoad(IrFieldLoad fieldLoad, int slot) {
        if (fieldLoad.Target.Type.IsObject) {
          this.EmitExpr(fieldLoad.Target, slot);
          this.EmitSlotToVar(slot, "_fl_o");
          this.EmitNullTrap("_fl_o");
        } else {
          this.EmitPlaceHandle(fieldLoad.Target, slot);
          this.EmitSlotToVar(slot, "_fl_o");
        }

        var fk = this._owner._fieldKeys[fieldLoad.Field];
        this.EmitFrame($"for %%O in (!_fl_o!) do set \"{this.SlotVar(slot)}=!_O_%%O_{fk}!\"");
        if (fieldLoad.Type.IsStruct) {
          var sid = this._owner._structIds[fieldLoad.Type.StructType!];
          this._owner._neededStructCopies.Add(fieldLoad.Type.StructType!);
          this.EmitSlotToVar(slot, "_cs_in");
          this.Line($"call :__ply_copy_s{sid} \"!_cs_in!\"");
          this.EmitVarToSlot("_RET", slot);
        }
      }

      private void EmitStructEquals(IrStructEquals eq, int slot) {
        this.EmitExpr(eq.Left, slot);
        this.EmitExpr(eq.Right, slot + 1);
        this.EmitSlotToVar(slot, "_eq_a");
        this.EmitSlotToVar(slot + 1, "_eq_b");
        if (eq.Left.Type.StructType is { } st) {
          this._owner._neededStructEqs.Add(st);
          this.Line($"call :__ply_eq_s{this._owner._structIds[st]} \"!_eq_a!\" \"!_eq_b!\"");
        } else {
          var cl = eq.Left.Type.ClassType!;
          this._owner._neededRecordEqs.Add(cl);
          this.Line($"call :__ply_req_c{this._owner._classIds[cl]} \"!_eq_a!\" \"!_eq_b!\"");
        }

        this.EmitVarToSlot("_RET", slot);
      }

      private void EmitNewObject(IrNew creation, int slot) {
        var cl = creation.Type.ClassType!;
        for (var i = 0; i < creation.Arguments.Count; ++i)
          this.EmitExpr(creation.Arguments[i], slot + 1 + i);

        this.Line("set /a \"_HID+=1\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O___t={this._owner._classIds[cl]}\"");
        foreach (var field in cl.AllFields) {
          var fk = this._owner._fieldKeys[field];
          if (field.Type.IsStruct) {
            this.EmitStructDefault(field.Type.StructType!, slot + 1 + creation.Arguments.Count);
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}=!{this.SlotVar(slot + 1 + creation.Arguments.Count)}!\"");
          } else {
            var defTok = field.Type.IsOptional ? _noneToken : field.Type.IsNullable ? _nullToken : field.Type.Integral.IsLong ? "0:0" : "0";
            this.EmitFrame($"for %%O in (!{this.SlotVar(slot)}!) do set \"_O_%%O_{fk}={defTok}\"");
          }
        }

        if (IrExceptionClass.IsLibrary(creation.Constructor)) {
          this.EmitLibraryExceptionCtorInit(cl, slot, creation.Arguments.Count);
          return;
        }

        var args = new List<string>(1 + creation.Arguments.Count);
        this.EmitSlotToVar(slot, "_ca0");
        args.Add("\"!_ca0!\"");
        for (var i = 0; i < creation.Arguments.Count; ++i) {
          var av = "_ca" + (i + 1).ToString(CultureInfo.InvariantCulture);
          this.EmitSlotToVar(slot + 1 + i, av);
          args.Add($"\"!{av}!\"");
        }

        this.Line($"call :{this._owner._functionLabels[creation.Constructor.Function]} {string.Join(" ", args)}");
        this.EmitCheckUnwind();
      }

      private void EmitLibraryExceptionCtorInit(IrClassType ownerClass, int objSlot, int argCount) {
        this.EmitSlotToVar(objSlot, "_eo");
        if (argCount >= 1) {
          this.EmitSlotToVar(objSlot + 1, "_em");
          this.Line("for %%O in (!_eo!) do set \"_O_%%O_f_Message=!_em!\"");
        } else {
          var defMsg = this._owner.EscapeStringLiteral(IrExceptionClass.DefaultMessage(ownerClass));
          this.Line("set /a \"_HID+=1\"");
          this.Line($"set \"_S_s!_HID!={defMsg}\"");
          this.Line("for %%O in (!_eo!) do set \"_O_%%O_f_Message=s!_HID!\"");
        }

        if (argCount >= 2) {
          this.EmitSlotToVar(objSlot + 2, "_ei");
          this.Line("for %%O in (!_eo!) do set \"_O_%%O_f_InnerException=!_ei!\"");
        } else {
          this.Line($"for %%O in (!_eo!) do set \"_O_%%O_f_InnerException={_nullToken}\"");
        }
      }

      private void EmitMethodCall(IrMethodCall call, int slot) {
        var args = new List<string>();
        var baseArgSlot = slot;
        if (call.Target is { } target) {
          this.EmitExpr(target, slot);
          this.EmitSlotToVar(slot, "_mc_r");
          this.EmitNullTrap("_mc_r");
          baseArgSlot = slot + 1;
        }

        for (var i = 0; i < call.Arguments.Count; ++i)
          this.EmitExpr(call.Arguments[i], baseArgSlot + i);

        var idx = 0;
        if (call.Target is not null) {
          this.EmitSlotToVar(slot, "_ca0");
          args.Add("\"!_ca0!\"");
          idx = 1;
        }

        for (var i = 0; i < call.Arguments.Count; ++i) {
          var av = "_ca" + (idx + i).ToString(CultureInfo.InvariantCulture);
          this.EmitSlotToVar(baseArgSlot + i, av);
          args.Add($"\"!{av}!\"");
        }

        string targetLabel;
        if (call.Kind == IrMethodCallKind.Virtual) {
          var root = call.Method.Root;
          this._owner._neededVirtualDispatch.Add(root);
          targetLabel = "__ply_v_" + this._owner._virtualSlotIds[root].ToString(CultureInfo.InvariantCulture);
        } else if (call.Kind == IrMethodCallKind.Interface) {
          this._owner._neededInterfaceDispatch.Add(call.Method);
          targetLabel = "__ply_i_" + this._owner._interfaceMethodIds[call.Method].ToString(CultureInfo.InvariantCulture);
        } else {
          targetLabel = this._owner._functionLabels[call.Method.Function];
        }

        var suffix = args.Count == 0 ? "" : " " + string.Join(" ", args);
        this.Line($"call :{targetLabel}{suffix}");
        this.EmitCheckUnwind();
        if (call.Type != IrType.Void)
          this.EmitVarToSlot("_RET", slot);
      }

      private void EmitTypeTest(IrTypeTest test, int slot) {
        this.EmitExpr(test.Value, slot);
        this.EmitSlotToVar(slot, "_tt_o");
        var tid = test.Target.ClassType is { } cl ? this._owner._classIds[cl] : this._owner._interfaceIds[test.Target.InterfaceType!];
        var notNull = this._owner.NewLabel("_tt_nn");
        var done = this._owner.NewLabel("_tt_dn");
        this.Line($"if not \"!_tt_o!\"==\"{_nullToken}\" goto :{notNull}");
        this.EmitSetSlotLiteral(slot, "0");
        this.Jump(done);
        this.Mark(notNull);
        this.Line("for %%O in (!_tt_o!) do set \"_tt_t=!_O_%%O___t!\"");
        this.EmitFrame($"if defined _ISA_!_tt_t!_{tid} (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
        this.Mark(done);
      }

      private void EmitCast(IrCast cast, int slot) {
        this.EmitExpr(cast.Value, slot);
        this.EmitSlotToVar(slot, "_ct_o");
        var tid = cast.Type.ClassType is { } cl ? this._owner._classIds[cl] : this._owner._interfaceIds[cast.Type.InterfaceType!];
        var ok = this._owner.NewLabel("_ct_ok");
        this.Line($"if \"!_ct_o!\"==\"{_nullToken}\" goto :{ok}");
        this.Line("for %%O in (!_ct_o!) do set \"_ct_t=!_O_%%O___t!\"");
        this.Line($"if defined _ISA_!_ct_t!_{tid} goto :{ok}");
        this.EmitTrap(IrExceptionClass.InvalidCastException, "InvalidCast");
        this.Mark(ok);
      }

      private void EmitAsCast(IrAsCast cast, int slot) {
        this.EmitExpr(cast.Value, slot);
        this.EmitSlotToVar(slot, "_ac_o");
        var tid = cast.Type.ClassType is { } cl ? this._owner._classIds[cl] : this._owner._interfaceIds[cast.Type.InterfaceType!];
        var done = this._owner.NewLabel("_ac_dn");
        this.Line($"if \"!_ac_o!\"==\"{_nullToken}\" goto :{done}");
        this.Line("for %%O in (!_ac_o!) do set \"_ac_t=!_O_%%O___t!\"");
        this.Line($"if defined _ISA_!_ac_t!_{tid} goto :{done}");
        this.EmitSetSlotLiteral(slot, _nullToken);
        this.Mark(done);
      }

      private void EmitCall(IrCall call, int slot) {
        for (var i = 0; i < call.Arguments.Count; ++i)
          this.EmitExpr(call.Arguments[i], slot + i);

        // Library exception base constructor call from derived exception class constructor.
        if (IrExceptionClass.ConstructorOf(call.Function) is { } libCtor) {
          var owner = IrExceptionClass.All.First(t => t.Constructors.Contains(libCtor));
          this.EmitLibraryExceptionCtorInit(owner, slot, call.Arguments.Count - 1);
          return;
        }

        // Iterator function call creates a restartable sequence descriptor without running the body.
        if (this._owner._functionsByName.TryGetValue(call.Function, out var calleeFn) && IrSequences.IsIterator(calleeFn)) {
          this.Line("set /a \"_HID+=1\"");
          this.Line("set \"_sq_id=!_HID!\"");
          this.Line($"set \"_O_!_sq_id!___seq={this._owner._functionLabels[call.Function]}\"");
          for (var i = 0; i < call.Arguments.Count; ++i) {
            this.EmitSlotToVar(slot + i, "_sq_a");
            this.Line($"set \"_O_!_sq_id!_a{i}=!_sq_a!\"");
          }

          this.EmitVarToSlot("_sq_id", slot);
          return;
        }

        var args = new List<string>(call.Arguments.Count);
        for (var i = 0; i < call.Arguments.Count; ++i) {
          var av = "_ca" + i.ToString(CultureInfo.InvariantCulture);
          this.EmitSlotToVar(slot + i, av);
          args.Add($"\"!{av}!\"");
        }

        var suffix = args.Count == 0 ? "" : " " + string.Join(" ", args);
        this.Line($"call :{this._owner._functionLabels[call.Function]}{suffix}");
        this.EmitCheckUnwind();
        if (call.Type != IrType.Void && !call.Type.IsAsync)
          this.EmitVarToSlot("_RET", slot);
        else if (call.Type.IsAsync && call.Type.Element != IrType.Void)
          this.EmitVarToSlot("_RET", slot);
      }

      private void EmitLambda(IrLambda lambda, int slot) {
        var lamId = this._owner._nextLambdaId++;
        var lamLabel = "lam_" + lamId.ToString(CultureInfo.InvariantCulture);
        var lamFn = new IrFunction(lamLabel, false, lambda.ReturnType, lambda.Parameters, lambda.Locals, lambda.Body);
        var lamWriter = new FunctionWriter(this._owner, lamFn, false, false, lambda.Captures, lamLabel);
        lamWriter.EmitFunction();
        this._owner._extraSubroutines.AddRange(lamWriter.Lines);

        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_cl_id=!_HID!\"");
        this.Line($"set \"_O_!_cl_id!___fn={lamLabel}\"");
        this.Line("set \"_O_!_cl_id!___kind=1\"");
        for (var i = 0; i < lambda.Captures.Count; ++i)
          this.EmitFrame($"set \"_O_!_cl_id!_c{i}=!{this.LocalVar(lambda.Captures[i])}!\"");

        this.EmitVarToSlot("_cl_id", slot);
      }

      private void EmitFunctionRef(IrFunctionRef fnRef, int slot) {
        this.Line("set /a \"_HID+=1\"");
        this.Line($"set \"_O_!_HID!___fn={this._owner._functionLabels[fnRef.Function]}\"");
        this.Line("set \"_O_!_HID!___kind=0\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitBoundMethod(IrBoundMethod bound, int slot) {
        string label;
        if (bound.Kind == IrMethodCallKind.Virtual) {
          var root = bound.Method.Root;
          this._owner._neededVirtualDispatch.Add(root);
          label = "__ply_v_" + this._owner._virtualSlotIds[root].ToString(CultureInfo.InvariantCulture);
        } else if (bound.Kind == IrMethodCallKind.Interface) {
          this._owner._neededInterfaceDispatch.Add(bound.Method);
          label = "__ply_i_" + this._owner._interfaceMethodIds[bound.Method].ToString(CultureInfo.InvariantCulture);
        } else {
          label = this._owner._functionLabels[bound.Method.Function];
        }

        if (bound.Receiver is { } recv) {
          this.EmitExpr(recv, slot);
          this.EmitSlotToVar(slot, "_bm_r");
          this.EmitNullTrap("_bm_r");
          this.Line("set /a \"_HID+=1\"");
          this.Line($"set \"_O_!_HID!___fn={label}\"");
          this.Line("set \"_O_!_HID!___kind=2\"");
          this.Line("set \"_O_!_HID!___recv=!_bm_r!\"");
          this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        } else {
          this.Line("set /a \"_HID+=1\"");
          this.Line($"set \"_O_!_HID!___fn={label}\"");
          this.Line("set \"_O_!_HID!___kind=0\"");
          this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        }
      }

      private void EmitInvoke(IrInvoke invoke, int slot) {
        this.EmitExpr(invoke.Callee, slot);
        for (var i = 0; i < invoke.Arguments.Count; ++i)
          this.EmitExpr(invoke.Arguments[i], slot + 1 + i);

        this.EmitSlotToVar(slot, "_iv_c");
        this.EmitNullTrap("_iv_c");
        var argRefs = new List<string>(invoke.Arguments.Count);
        for (var i = 0; i < invoke.Arguments.Count; ++i) {
          var av = "_ca" + i.ToString(CultureInfo.InvariantCulture);
          this.EmitSlotToVar(slot + 1 + i, av);
          argRefs.Add($"\"!{av}!\"");
        }

        var argsSuffix = argRefs.Count == 0 ? "" : " " + string.Join(" ", argRefs);
        this.Line("for %%C in (!_iv_c!) do (set \"_iv_fn=!_O_%%C___fn!\" & set \"_iv_k=!_O_%%C___kind!\" & set \"_iv_r=!_O_%%C___recv!\")");
        this.Line($"if \"!_iv_k!\"==\"1\" (call :!_iv_fn! \"!_iv_c!\"{argsSuffix}) else if \"!_iv_k!\"==\"2\" (call :!_iv_fn! \"!_iv_r!\"{argsSuffix}) else (call :!_iv_fn!{argsSuffix})");
        this.EmitCheckUnwind();
        if (invoke.Type != IrType.Void)
          this.EmitVarToSlot("_RET", slot);
      }

      private void EmitGenericEquals(IrGenericEquals eq, int slot) {
        if (eq.Left.Type.ClassType is { IsRecord: true }) {
          this.EmitStructEquals(new IrStructEquals(eq.Left, eq.Right), slot);
          return;
        }

        this.EmitExpr(eq.Left, slot);
        this.EmitExpr(eq.Right, slot + 1);
        this.EmitFrame($"if \"!{this.SlotVar(slot)}!\"==\"!{this.SlotVar(slot + 1)}!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
      }

      private void EmitGenericCompare(IrGenericCompare cmp, int slot) {
        this.EmitExpr(cmp.Left, slot);
        this.EmitExpr(cmp.Right, slot + 1);
        var type = cmp.Left.Type.Integral;
        if (type == IrType.Bool || (type.IsInteger && type.Bits < 32)) {
          this.EmitFrame($"set /a \"{this.SlotVar(slot)}={this.SlotVar(slot)}-{this.SlotVar(slot + 1)}\"");
          return;
        }

        if (type.IsLong) {
          this._owner.RequireHelper(type.IsSigned ? "cmp_i64" : "cmp_u64");
          this.EmitSlotToVar(slot, "_gc_a");
          this.EmitSlotToVar(slot + 1, "_gc_b");
          this.Line($"call :__ply_cmp_{(type.IsSigned ? "i64" : "u64")} \"!_gc_a!\" \"!_gc_b!\"");
          this.EmitVarToSlot("_RET", slot);
          return;
        }

        if (!type.IsSigned) {
          this._owner.RequireHelper("cmp_u32");
          this.EmitSlotToVar(slot, "_gc_a");
          this.EmitSlotToVar(slot + 1, "_gc_b");
          this.Line("call :__ply_cmp_u32 \"!_gc_a!\" \"!_gc_b!\"");
          this.EmitVarToSlot("_RET", slot);
          return;
        }

        // Signed i32: -1, 0, 1 using sign+magnitude comparison.
        this.EmitFrame($"set /a \"_ca={this.SlotVar(slot)}, _cb={this.SlotVar(slot + 1)}\"");
        this.Line("set /a \"_csa=(_ca>>31)&1, _csb=(_cb>>31)&1\"");
        var same = this._owner.NewLabel("_gc_s");
        var done = this._owner.NewLabel("_gc_d");
        this.Line($"if !_csa! EQU !_csb! goto :{same}");
        this.EmitFrame($"if !_csa! GTR !_csb! (set \"{this.SlotVar(slot)}=-1\") else (set \"{this.SlotVar(slot)}=1\")");
        this.Jump(done);
        this.Mark(same);
        this.Line("set /a \"_cma=_ca&2147483647, _cmb=_cb&2147483647\"");
        this.EmitFrame($"if !_cma! LSS !_cmb! (set \"{this.SlotVar(slot)}=-1\") else if !_cma! GTR !_cmb! (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
        this.Mark(done);
      }

      private void EmitCollection(IrCollection col, int slot) {
        for (var i = 0; i < col.Arguments.Count; ++i)
          this.EmitExpr(col.Arguments[i], slot + i);

        var colType = IrCollections.Constructs(col.Op) ? col.Type : col.Arguments[0].Type;
        var isStrKey = colType.Kind == IrTypeKind.Map ? colType.Key!.IsString : colType.Element!.IsString;
        var strFlag = isStrKey ? "1" : "0";

        switch (col.Op) {
          case IrCollectionOp.ListNew:
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_L_!_HID!_len=0\" & set \"_L_!_HID!_ver=0\"");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
            break;
          case IrCollectionOp.ListOf:
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_lo_id=!_HID!\"");
            this.Line($"set \"_L_!_lo_id!_len={col.Arguments.Count.ToString(CultureInfo.InvariantCulture)}\" & set \"_L_!_lo_id!_ver=0\"");
            for (var i = 0; i < col.Arguments.Count; ++i)
              this.EmitFrame($"set \"_L_!_lo_id!_{i}=!{this.SlotVar(slot + i)}!\"");
            this.EmitVarToSlot("_lo_id", slot);
            break;
          case IrCollectionOp.ListFromArray:
            this.EmitSlotToVar(slot, "_fa_a");
            this.EmitNullTrap("_fa_a");
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_fa_l=!_HID!\"");
            this.Line("for %%A in (!_fa_a!) do set /a \"_fa_n=_AL_%%A\"");
            this.Line("set \"_L_!_fa_l!_len=!_fa_n!\" & set \"_L_!_fa_l!_ver=0\"");
            this.Line("set /a \"_fa_lim=_fa_n-1\"");
            this.Line("if !_fa_lim! GEQ 0 for /l %%I in (0,1,!_fa_lim!) do for %%A in (!_fa_a!) do set \"_L_!_fa_l!_%%I=!_A_%%A_%%I!\"");
            this.EmitVarToSlot("_fa_l", slot);
            break;
          case IrCollectionOp.ListCount:
            this.EmitSlotToVar(slot, "_lc_id");
            this.EmitFrame($"for %%L in (!_lc_id!) do set \"{this.SlotVar(slot)}=!_L_%%L_len!\"");
            break;
          case IrCollectionOp.ListGet:
            this.EmitSlotToVar(slot, "_lg_id");
            this.EmitSlotToVar(slot + 1, "_lg_i");
            this.Line("for %%L in (!_lg_id!) do set /a \"_lg_len=_L_%%L_len\"");
            this.EmitArgOutOfRangeTrap("_lg_i", "_lg_len");
            this.EmitFrame($"for %%L in (!_lg_id!) do for %%I in (!_lg_i!) do set \"{this.SlotVar(slot)}=!_L_%%L_%%I!\"");
            break;
          case IrCollectionOp.ListSet:
            this.EmitSlotToVar(slot, "_ls_id");
            this.EmitSlotToVar(slot + 1, "_ls_i");
            this.EmitSlotToVar(slot + 2, "_ls_v");
            this.Line("for %%L in (!_ls_id!) do set /a \"_ls_len=_L_%%L_len\"");
            this.EmitArgOutOfRangeTrap("_ls_i", "_ls_len");
            this.Line("for %%L in (!_ls_id!) do (for %%I in (!_ls_i!) do set \"_L_%%L_%%I=!_ls_v!\" & set /a \"_L_%%L_ver+=1\")");
            break;
          case IrCollectionOp.ListAdd:
            this.EmitSlotToVar(slot, "_la_id");
            this.EmitSlotToVar(slot + 1, "_la_v");
            this.Line("for %%L in (!_la_id!) do (set /a \"_la_i=_L_%%L_len\" & for %%I in (!_la_i!) do set \"_L_%%L_%%I=!_la_v!\" & set /a \"_L_%%L_len+=1, _L_%%L_ver+=1\")");
            break;
          case IrCollectionOp.ListInsert:
            this.EmitSlotToVar(slot, "_li_id");
            this.EmitSlotToVar(slot + 1, "_li_i");
            this.EmitSlotToVar(slot + 2, "_li_v");
            this.Line("for %%L in (!_li_id!) do set /a \"_li_len=_L_%%L_len, _li_hi=_li_len+1\"");
            this.EmitArgOutOfRangeTrap("_li_i", "_li_hi");
            this.Line("if !_li_len! GTR !_li_i! for /l %%I in (!_li_len!,-1,!_li_i!) do (if %%I GTR !_li_i! (set /a \"_p=%%I-1\" & for %%L in (!_li_id!) do for %%P in (!_p!) do set \"_L_%%L_%%I=!_L_%%L_%%P!\"))");
            this.Line("for %%L in (!_li_id!) do (for %%I in (!_li_i!) do set \"_L_%%L_%%I=!_li_v!\" & set /a \"_L_%%L_len+=1, _L_%%L_ver+=1\")");
            break;
          case IrCollectionOp.ListRemoveAt:
            this.EmitSlotToVar(slot, "_lr_id");
            this.EmitSlotToVar(slot + 1, "_lr_i");
            this.Line("for %%L in (!_lr_id!) do set /a \"_lr_len=_L_%%L_len\"");
            this.EmitArgOutOfRangeTrap("_lr_i", "_lr_len");
            this.Line("set /a \"_lr_lim=_lr_len-2\"");
            this.Line("if !_lr_lim! GEQ !_lr_i! for /l %%I in (!_lr_i!,1,!_lr_lim!) do (set /a \"_n=%%I+1\" & for %%L in (!_lr_id!) do for %%N in (!_n!) do set \"_L_%%L_%%I=!_L_%%L_%%N!\")");
            this.Line("for %%L in (!_lr_id!) do set /a \"_L_%%L_len-=1, _L_%%L_ver+=1\"");
            break;
          case IrCollectionOp.ListRemoveLast:
            this.EmitSlotToVar(slot, "_lp_id");
            this.Line("for %%L in (!_lp_id!) do set /a \"_lp_len=_L_%%L_len\"");
            var absTok = col.Type.IsOptional ? _noneToken : _nullToken;
            this.EmitFrame($"if !_lp_len! EQU 0 (set \"{this.SlotVar(slot)}={absTok}\") else (set /a \"_lp_i=_lp_len-1\" & for %%L in (!_lp_id!) do (for %%I in (!_lp_i!) do set \"{this.SlotVar(slot)}=!_L_%%L_%%I!\" & set /a \"_L_%%L_len-=1, _L_%%L_ver+=1\"))");
            break;
          case IrCollectionOp.ListClear:
            this.EmitSlotToVar(slot, "_lc_id");
            this.Line("for %%L in (!_lc_id!) do (set \"_L_%%L_len=0\" & set /a \"_L_%%L_ver+=1\")");
            break;
          case IrCollectionOp.ListContains:
          case IrCollectionOp.ListIncludesSameValueZero:
            this._owner.RequireHelper("list_ops");
            this.EmitSlotToVar(slot, "_lf_a");
            this.EmitSlotToVar(slot + 1, "_lf_b");
            this.Line($"call :__ply_list_find \"!_lf_a!\" \"!_lf_b!\" \"{strFlag}\"");
            this.EmitFrame($"if \"!_RET!\"==\"-1\" (set \"{this.SlotVar(slot)}=0\") else (set \"{this.SlotVar(slot)}=1\")");
            break;
          case IrCollectionOp.ListIndexOfEquals:
          case IrCollectionOp.ListIndexOfStrict:
            this._owner.RequireHelper("list_ops");
            this.EmitSlotToVar(slot, "_lf_a");
            this.EmitSlotToVar(slot + 1, "_lf_b");
            this.Line($"call :__ply_list_find \"!_lf_a!\" \"!_lf_b!\" \"{strFlag}\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrCollectionOp.ListCopyToArray:
            this.EmitSlotToVar(slot, "_ca_l");
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_ca_a=!_HID!\"");
            this.Line("for %%L in (!_ca_l!) do set /a \"_ca_n=_L_%%L_len\"");
            this.Line("set \"_AL_!_ca_a!=!_ca_n!\"");
            this.Line("set /a \"_ca_lim=_ca_n-1\"");
            this.Line("if !_ca_lim! GEQ 0 for /l %%I in (0,1,!_ca_lim!) do for %%L in (!_ca_l!) do set \"_A_!_ca_a!_%%I=!_L_%%L_%%I!\"");
            this.EmitVarToSlot("_ca_a", slot);
            break;
          case IrCollectionOp.MapNew:
          case IrCollectionOp.SetNew:
            var ord = colType.Order == MapOrder.SlotReuse ? "1" : "0";
            this.Line("set /a \"_HID+=1\"");
            this.Line($"set \"_M_!_HID!_cap=0\" & set \"_M_!_HID!_cnt=0\" & set \"_M_!_HID!_ver=0\" & set \"_M_!_HID!_ord={ord}\" & set \"_M_!_HID!_fn=0\"");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
            break;
          case IrCollectionOp.MapCount:
          case IrCollectionOp.SetCount:
            this.EmitSlotToVar(slot, "_mc_id");
            this.EmitFrame($"for %%M in (!_mc_id!) do set \"{this.SlotVar(slot)}=!_M_%%M_cnt!\"");
            break;
          case IrCollectionOp.MapGet:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mg_id");
            this.EmitSlotToVar(slot + 1, "_mg_k");
            this.Line($"call :__ply_map_find \"!_mg_id!\" \"!_mg_k!\" \"{strFlag}\"");
            var mgOk = this._owner.NewLabel("_mg_ok");
            this.Line($"if not \"!_RET!\"==\"-1\" goto :{mgOk}");
            this.EmitTrap(IrExceptionClass.KeyNotFoundException, "KeyNotFound");
            this.Mark(mgOk);
            this.EmitFrame($"for %%M in (!_mg_id!) do for %%I in (!_RET!) do set \"{this.SlotVar(slot)}=!_M_%%M_v_%%I!\"");
            break;
          case IrCollectionOp.MapTryGet:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mt_id");
            this.EmitSlotToVar(slot + 1, "_mt_k");
            this.Line($"call :__ply_map_find \"!_mt_id!\" \"!_mt_k!\" \"{strFlag}\"");
            var mtAbs = col.Type.IsOptional ? _noneToken : _nullToken;
            this.EmitFrame($"if \"!_RET!\"==\"-1\" (set \"{this.SlotVar(slot)}={mtAbs}\") else (for %%M in (!_mt_id!) do for %%I in (!_RET!) do set \"{this.SlotVar(slot)}=!_M_%%M_v_%%I!\")");
            break;
          case IrCollectionOp.MapContainsKey:
          case IrCollectionOp.SetContains:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mk_id");
            this.EmitSlotToVar(slot + 1, "_mk_k");
            this.Line($"call :__ply_map_find \"!_mk_id!\" \"!_mk_k!\" \"{strFlag}\"");
            this.EmitFrame($"if \"!_RET!\"==\"-1\" (set \"{this.SlotVar(slot)}=0\") else (set \"{this.SlotVar(slot)}=1\")");
            break;
          case IrCollectionOp.MapPut:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mp_id");
            this.EmitSlotToVar(slot + 1, "_mp_k");
            this.EmitSlotToVar(slot + 2, "_mp_v");
            this.Line($"call :__ply_map_find \"!_mp_id!\" \"!_mp_k!\" \"{strFlag}\"");
            var mpNew = this._owner.NewLabel("_mp_n");
            var mpDone = this._owner.NewLabel("_mp_d");
            this.Line($"if \"!_RET!\"==\"-1\" goto :{mpNew}");
            this.Line("for %%M in (!_mp_id!) do for %%I in (!_RET!) do set \"_M_%%M_v_%%I=!_mp_v!\"");
            this.Jump(mpDone);
            this.Mark(mpNew);
            this.Line("call :__ply_map_alloc \"!_mp_id!\"");
            this.Line("for %%M in (!_mp_id!) do (for %%I in (!_RET!) do (set \"_M_%%M_u_%%I=1\" & set \"_M_%%M_k_%%I=!_mp_k!\" & set \"_M_%%M_v_%%I=!_mp_v!\") & set /a \"_M_%%M_cnt+=1, _M_%%M_ver+=1\")");
            this.Mark(mpDone);
            break;
          case IrCollectionOp.MapAdd:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_ma_id");
            this.EmitSlotToVar(slot + 1, "_ma_k");
            this.EmitSlotToVar(slot + 2, "_ma_v");
            this.Line($"call :__ply_map_find \"!_ma_id!\" \"!_ma_k!\" \"{strFlag}\"");
            var maOk = this._owner.NewLabel("_ma_ok");
            this.Line($"if \"!_RET!\"==\"-1\" goto :{maOk}");
            this.EmitTrap(IrExceptionClass.ArgumentException, "DuplicateKey");
            this.Mark(maOk);
            this.Line("call :__ply_map_alloc \"!_ma_id!\"");
            this.Line("for %%M in (!_ma_id!) do (for %%I in (!_RET!) do (set \"_M_%%M_u_%%I=1\" & set \"_M_%%M_k_%%I=!_ma_k!\" & set \"_M_%%M_v_%%I=!_ma_v!\") & set /a \"_M_%%M_cnt+=1, _M_%%M_ver+=1\")");
            break;
          case IrCollectionOp.MapTryAdd:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mta_id");
            this.EmitSlotToVar(slot + 1, "_mta_k");
            this.EmitSlotToVar(slot + 2, "_mta_v");
            this.Line($"call :__ply_map_find \"!_mta_id!\" \"!_mta_k!\" \"{strFlag}\"");
            var mtaYes = this._owner.NewLabel("_mta_y");
            var mtaDn = this._owner.NewLabel("_mta_d");
            this.Line($"if \"!_RET!\"==\"-1\" goto :{mtaYes}");
            this.EmitSetSlotLiteral(slot, "0");
            this.Jump(mtaDn);
            this.Mark(mtaYes);
            this.Line("call :__ply_map_alloc \"!_mta_id!\"");
            this.Line("for %%M in (!_mta_id!) do (for %%I in (!_RET!) do (set \"_M_%%M_u_%%I=1\" & set \"_M_%%M_k_%%I=!_mta_k!\" & set \"_M_%%M_v_%%I=!_mta_v!\") & set /a \"_M_%%M_cnt+=1, _M_%%M_ver+=1\")");
            this.EmitSetSlotLiteral(slot, "1");
            this.Mark(mtaDn);
            break;
          case IrCollectionOp.SetAdd:
            this._owner.RequireHelper("set_ops");
            this.EmitSlotToVar(slot, "_sa_id");
            this.EmitSlotToVar(slot + 1, "_sa_k");
            this.Line($"call :__ply_map_find \"!_sa_id!\" \"!_sa_k!\" \"{strFlag}\"");
            var saYes = this._owner.NewLabel("_sa_y");
            var saDn = this._owner.NewLabel("_sa_d");
            this.Line($"if \"!_RET!\"==\"-1\" goto :{saYes}");
            this.EmitSetSlotLiteral(slot, "0");
            this.Jump(saDn);
            this.Mark(saYes);
            this.Line("call :__ply_map_alloc \"!_sa_id!\"");
            this.Line("for %%M in (!_sa_id!) do (for %%I in (!_RET!) do (set \"_M_%%M_u_%%I=1\" & set \"_M_%%M_k_%%I=!_sa_k!\") & set /a \"_M_%%M_cnt+=1, _M_%%M_ver+=1\")");
            this.EmitSetSlotLiteral(slot, "1");
            this.Mark(saDn);
            break;
          case IrCollectionOp.MapRemove:
          case IrCollectionOp.SetRemove:
            this._owner.RequireHelper("map_ops");
            this.EmitSlotToVar(slot, "_mr_a");
            this.EmitSlotToVar(slot + 1, "_mr_k");
            this.Line($"call :__ply_map_rem \"!_mr_a!\" \"!_mr_k!\" \"{strFlag}\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrCollectionOp.MapClear:
          case IrCollectionOp.SetClear:
            this.EmitSlotToVar(slot, "_mc_id");
            this.Line("for %%M in (!_mc_id!) do (set \"_M_%%M_cap=0\" & set \"_M_%%M_cnt=0\" & set \"_M_%%M_fn=0\")");
            break;
        }
      }

      private void EmitArgOutOfRangeTrap(string idxVar, string lenVar) {
        var ok = this._owner.NewLabel("_ao_ok");
        var bad = this._owner.NewLabel("_ao_bad");
        this.Line($"set /a \"_bs=({idxVar}>>31)&1\"");
        this.Line($"if !_bs! NEQ 0 goto :{bad}");
        this.Line($"if !{idxVar}! LSS !{lenVar}! goto :{ok}");
        this.Mark(bad);
        this.EmitTrap(IrExceptionClass.ArgumentOutOfRangeException, "ArgumentOutOfRange");
        this.Mark(ok);
      }

      private void EmitArraySequence(IrArraySequence arrSeq, int slot) {
        this._owner.RequireHelper("arrseq");
        this.EmitExpr(arrSeq.Array, slot);
        this.EmitSlotToVar(slot, "_as_a");
        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_O_!_HID!___seq=__ply_arrseq\"");
        this.Line("set \"_O_!_HID!_a0=!_as_a!\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitAsync(IrAsync asyncExpr, int slot) {
        switch (asyncExpr.Op) {
          case IrAsyncOp.Completed:
            this.EmitSetSlotLiteral(slot, "0");
            break;
          case IrAsyncOp.FromResult:
            this.EmitExpr(asyncExpr.Arguments[0], slot);
            break;
          case IrAsyncOp.WhenAll:
            this.EmitWhenAll(asyncExpr, slot);
            break;
        }
      }

      private static bool CanRunInParallelWorker(IrExpr arg)
        => arg is IrCall call && call.Arguments.Count <= 4
          && call.Arguments.All(a => a.Type.Integral.IsInteger || a.Type == IrType.Bool)
          && call.Type.Element is { } res && (res == IrType.Void || res.Integral.IsInteger || res == IrType.Bool);

      private void EmitWhenAll(IrAsync whenAll, int slot) {
        var count = whenAll.Arguments.Count;
        var isVoid = whenAll.Type.Element == IrType.Void;
        if (count >= 2 && whenAll.Arguments.All(CanRunInParallelWorker)) {
          // Real parallel multi-process execution via start "" /B cmd /c + file-I/O barrier!
          this._owner._needsWorker = true;
          this.Line("set /a \"_WID+=1\"");
          this.Line("set \"_wdir=%TEMP%\\ply_w_!RANDOM!_!_WID!\"");
          this.Line("md \"!_wdir!\"");

          // Evaluate all call arguments left-to-right first, then spawn parallel workers.
          var spawned = new List<(string Label, List<int> ArgSlots)>(count);
          var nextArgSlot = slot + 1;
          for (var i = 0; i < count; ++i) {
            var call = (IrCall)whenAll.Arguments[i];
            var argSlots = new List<int>(call.Arguments.Count);
            foreach (var a in call.Arguments) {
              this.EmitExpr(a, nextArgSlot);
              argSlots.Add(nextArgSlot++);
            }

            spawned.Add((this._owner._functionLabels[call.Function], argSlots));
          }

          for (var i = 0; i < count; ++i) {
            var (label, argSlots) = spawned[i];
            var argTokens = new List<string>(argSlots.Count);
            for (var j = 0; j < argSlots.Count; ++j) {
              var av = $"_wa{j}";
              this.EmitSlotToVar(argSlots[j], av);
              argTokens.Add($"\"!{av}!\"");
            }

            var argSuffix = argTokens.Count == 0 ? "" : " " + string.Join(" ", argTokens);
            this.Line($"start \"\" /B cmd /c \"\"%~f0\" __ply_worker \"!_wdir!\\t{i}.res\" \"!_wdir!\\t{i}.err\" \"!_wdir!\\t{i}.done\" {label}{argSuffix}\"");
          }

          // Pure cmd.exe barrier loop waiting for all workers to write their .done sentinel files.
          var waitLbl = this._owner.NewLabel("_wa_w");
          this.Mark(waitLbl);
          for (var i = 0; i < count; ++i)
            this.Line($"if not exist \"!_wdir!\\t{i}.done\" goto :{waitLbl}");

          // Check errors in argument order first (Task.WhenAll semantics).
          for (var i = 0; i < count; ++i) {
            var noErr = this._owner.NewLabel("_wa_ok");
            this.Line($"if not exist \"!_wdir!\\t{i}.err\" goto :{noErr}");
            this.Line($"set /p _werr=<\"!_wdir!\\t{i}.err\"");
            this.Line("rd /s /q \"!_wdir!\"");
            this._owner.RequireHelper("trap");
            this.Line("for /f \"tokens=1,2 delims=:\" %%A in (\"!_werr!\") do call :__ply_trap %%A %%B");
            this.Jump(this.CurrentUnwindLabel());
            this.Mark(noErr);
          }

          if (!isVoid) {
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_wa_arr=!_HID!\"");
            this.Line($"set \"_AL_!_wa_arr!={count.ToString(CultureInfo.InvariantCulture)}\"");
            for (var i = 0; i < count; ++i) {
              this.Line("set \"_wr=\"");
              this.Line($"set /p _wr=<\"!_wdir!\\t{i}.res\"");
              this.Line($"set \"_A_!_wa_arr!_{i}=!_wr!\"");
            }

            this.EmitVarToSlot("_wa_arr", slot);
          }

          this.Line("rd /s /q \"!_wdir!\"");
          return;
        }

        // Sequential fallback for 0-1 tasks or tasks with in-memory reference arguments.
        for (var i = 0; i < count; ++i)
          this.EmitExpr(whenAll.Arguments[i], slot + 1 + i);

        if (!isVoid) {
          this.Line("set /a \"_HID+=1\"");
          this.Line("set \"_wa_arr=!_HID!\"");
          this.Line($"set \"_AL_!_wa_arr!={count.ToString(CultureInfo.InvariantCulture)}\"");
          for (var i = 0; i < count; ++i)
            this.EmitFrame($"set \"_A_!_wa_arr!_{i}=!{this.SlotVar(slot + 1 + i)}!\"");
          this.EmitVarToSlot("_wa_arr", slot);
        }
      }

      private void EmitIntrinsic(IrIntrinsic intrinsic, int slot) {
        if (intrinsic.Op == IrIntrinsicOp.AddressOf) {
          var target = ((IrLocalRef)intrinsic.Arguments[0]).Local;
          this.EmitReadAddressOfLocal(target, slot);
          return;
        }

        for (var i = 0; i < intrinsic.Arguments.Count; ++i)
          this.EmitExpr(intrinsic.Arguments[i], slot + 1 + i);

        switch (intrinsic.Op) {
          case IrIntrinsicOp.ArrayOf:
            if (intrinsic.Arguments.Count > CmdArrayStorageBudget)
              throw new Refusal($"array storage exceeding {CmdArrayStorageBudget} elements");
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_ao_id=!_HID!\"");
            this.Line($"set \"_AL_!_ao_id!={intrinsic.Arguments.Count.ToString(CultureInfo.InvariantCulture)}\"");
            for (var i = 0; i < intrinsic.Arguments.Count; ++i)
              this.EmitFrame($"set \"_A_!_ao_id!_{i}=!{this.SlotVar(slot + 1 + i)}!\"");
            this.EmitVarToSlot("_ao_id", slot);
            break;
          case IrIntrinsicOp.ArrayCopy:
            this.EmitArrayCopy(slot);
            break;
          case IrIntrinsicOp.ArrayFill:
            this.EmitSlotToVar(slot + 1, "_af_a");
            this.EmitSlotToVar(slot + 2, "_af_v");
            this.EmitNullTrap("_af_a");
            this.Line("for %%A in (!_af_a!) do set /a \"_af_lim=_AL_%%A-1\"");
            this.Line("if !_af_lim! GEQ 0 for /l %%I in (0,1,!_af_lim!) do for %%A in (!_af_a!) do set \"_A_%%A_%%I=!_af_v!\"");
            break;
          case IrIntrinsicOp.ArrayClear:
            this.EmitSlotToVar(slot + 1, "_ac_a");
            this.EmitNullTrap("_ac_a");
            var defTok = intrinsic.Arguments[0].Type.Element!.Integral.IsLong ? "0:0" : "0";
            this.Line("for %%A in (!_ac_a!) do set /a \"_ac_lim=_AL_%%A-1\"");
            this.Line($"if !_ac_lim! GEQ 0 for /l %%I in (0,1,!_ac_lim!) do for %%A in (!_ac_a!) do set \"_A_%%A_%%I={defTok}\"");
            break;
          case IrIntrinsicOp.RotateLeft:
          case IrIntrinsicOp.RotateRight:
            this.EmitRotate(intrinsic.Op == IrIntrinsicOp.RotateLeft, intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.ParallelBitExtract:
            this.EmitPextOrPdep(true, intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.ParallelBitDeposit:
            this.EmitPextOrPdep(false, intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.Abs:
          case IrIntrinsicOp.AbsWrap:
            this.EmitAbs(intrinsic.Op == IrIntrinsicOp.Abs, intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.Min:
          case IrIntrinsicOp.Max:
            this.EmitMinMax(intrinsic.Op == IrIntrinsicOp.Min, intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.Sign:
            this.EmitSign(intrinsic.Arguments[0].Type, slot);
            break;
          case IrIntrinsicOp.Clamp:
            this.EmitClamp(intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.PopCount:
            this._owner.RequireHelper("popcnt32");
            this.EmitSlotToVar(slot + 1, "_pc_v");
            this.Line("call :__ply_popcnt32 \"!_pc_v!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.LeadingZeroCount:
            this._owner.RequireHelper("clz32");
            this.EmitSlotToVar(slot + 1, "_lz_v");
            this.Line("call :__ply_clz32 \"!_lz_v!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.TrailingZeroCount:
            this._owner.RequireHelper("ctz32");
            this.EmitSlotToVar(slot + 1, "_tz_v");
            this.Line("call :__ply_ctz32 \"!_tz_v!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.StrLength:
            this._owner.RequireHelper("strlen");
            this.EmitSlotToVar(slot + 1, "_sl_h");
            this.EmitNullTrap("_sl_h");
            this.Line("call :__ply_strlen \"!_sl_h!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.StrConcat:
            this.EmitStrConcat(intrinsic.Arguments.Count, slot);
            break;
          case IrIntrinsicOp.StrEquals:
            this._owner.RequireHelper("streq");
            this.EmitSlotToVar(slot + 1, "_se_a");
            this.EmitSlotToVar(slot + 2, "_se_b");
            this.Line("call :__ply_streq \"!_se_a!\" \"!_se_b!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.StrSubstring:
            this._owner.RequireHelper("substr");
            this.EmitSlotToVar(slot + 1, "_ss_h");
            this.EmitSlotToVar(slot + 2, "_ss_st");
            if (intrinsic.Arguments.Count == 3) {
              this.EmitSlotToVar(slot + 3, "_ss_ln");
            } else {
              this._owner.RequireHelper("strlen");
              this.Line("call :__ply_strlen \"!_ss_h!\"");
              this.Line("set /a \"_ss_ln=_RET-_ss_st\"");
            }

            this.Line("call :__ply_substr \"!_ss_h!\" \"!_ss_st!\" \"!_ss_ln!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.StrIndexOf:
            this._owner.RequireHelper("stridx");
            this.EmitSlotToVar(slot + 1, "_si_h");
            this.EmitSlotToVar(slot + 2, "_si_n");
            if (intrinsic.Arguments.Count == 3)
              this.EmitSlotToVar(slot + 3, "_si_f");
            else
              this.Line("set \"_si_f=0\"");
            this.Line("call :__ply_stridx \"!_si_h!\" \"!_si_n!\" \"!_si_f!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.StrContains:
            this._owner.RequireHelper("stridx");
            this.EmitSlotToVar(slot + 1, "_si_h");
            this.EmitSlotToVar(slot + 2, "_si_n");
            this.Line("call :__ply_stridx \"!_si_h!\" \"!_si_n!\" \"0\"");
            this.EmitFrame($"if \"!_RET!\"==\"-1\" (set \"{this.SlotVar(slot)}=0\") else (set \"{this.SlotVar(slot)}=1\")");
            break;
          case IrIntrinsicOp.StrStartsWith:
            this._owner.RequireHelper("stridx");
            this.EmitSlotToVar(slot + 1, "_si_h");
            this.EmitSlotToVar(slot + 2, "_si_n");
            this.Line("call :__ply_stridx \"!_si_h!\" \"!_si_n!\" \"0\"");
            this.EmitFrame($"if \"!_RET!\"==\"0\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
            break;
          case IrIntrinsicOp.StrCodeUnitAt:
            this._owner.RequireHelper("strcode");
            this.EmitSlotToVar(slot + 1, "_sc_h");
            this.EmitSlotToVar(slot + 2, "_sc_i");
            this.Line("call :__ply_strcode \"!_sc_h!\" \"!_sc_i!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.FormatDotNet:
          case IrIntrinsicOp.FormatEcma:
            this.EmitFormatInt(intrinsic.Arguments[0].Type, slot);
            break;
          case IrIntrinsicOp.FormatBoolDotNet:
          case IrIntrinsicOp.FormatBoolEcma:
            var tStr = intrinsic.Op == IrIntrinsicOp.FormatBoolDotNet ? "True" : "true";
            var fStr = intrinsic.Op == IrIntrinsicOp.FormatBoolDotNet ? "False" : "false";
            this.EmitSlotToVar(slot + 1, "_fb_v");
            this.Line("set /a \"_HID+=1\"");
            this.Line($"if \"!_fb_v!\"==\"1\" (set \"_S_s!_HID!={tStr}\") else (set \"_S_s!_HID!={fStr}\")");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=s!_HID!\"");
            break;
          case IrIntrinsicOp.EnumHasFlag:
            this.EmitEnumHasFlag(intrinsic.Arguments[0].Type, slot);
            break;
          case IrIntrinsicOp.EnumToString:
            var enStr = intrinsic.Arguments[0].Type.EnumType!;
            this._owner._neededEnumToString.Add(enStr);
            this.EmitSlotToVar(slot + 1, "_ev");
            this.Line($"call :__ply_enum_str_{Sanitize(enStr.Name)} \"!_ev!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.EnumIsDefined:
            var enDef = intrinsic.Arguments[0].Type.EnumType!;
            this._owner._neededEnumIsDefined.Add(enDef);
            this.EmitSlotToVar(slot + 1, "_ev");
            this.Line($"call :__ply_enum_def_{Sanitize(enDef.Name)} \"!_ev!\"");
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.EnumParse:
            var enPar = intrinsic.Type.EnumType!;
            this._owner._neededEnumParse.Add(enPar);
            this.EmitSlotToVar(slot + 1, "_ep_s");
            this.EmitSlotToVar(slot + 2, "_ep_ic");
            this.Line($"call :__ply_enum_parse_{Sanitize(enPar.Name)} \"!_ep_s!\" \"!_ep_ic!\"");
            this.EmitCheckUnwind();
            this.EmitVarToSlot("_RET", slot);
            break;
          case IrIntrinsicOp.ViewOf:
            this.EmitViewOf(intrinsic, slot);
            break;
          case IrIntrinsicOp.Slice:
            this.EmitSlice(intrinsic, slot);
            break;
          case IrIntrinsicOp.ViewLength:
            this.EmitSlotToVar(slot + 1, "_vl_id");
            this.EmitFrame($"for %%V in (!_vl_id!) do set \"{this.SlotVar(slot)}=!_V_%%V_len!\"");
            break;
          case IrIntrinsicOp.StackAlloc:
            this.EmitStackAlloc(intrinsic, slot);
            break;
          case IrIntrinsicOp.ViewFill:
            this.EmitSlotToVar(slot + 1, "_vf_id");
            this.EmitSlotToVar(slot + 2, "_vf_val");
            this.Line("for %%V in (!_vf_id!) do (set \"_vf_a=!_V_%%V_arr!\" & set /a \"_vf_o=_V_%%V_off, _vf_l=_V_%%V_len-1\")");
            this.Line("if !_vf_l! GEQ 0 for /l %%I in (0,1,!_vf_l!) do (set /a \"_vi=_vf_o+%%I\" & for %%A in (!_vf_a!) do for %%K in (!_vi!) do set \"_A_%%A_%%K=!_vf_val!\")");
            break;
          case IrIntrinsicOp.ViewToArray:
            this.EmitSlotToVar(slot + 1, "_va_id");
            this.Line("for %%V in (!_va_id!) do (set \"_va_a=!_V_%%V_arr!\" & set /a \"_va_o=_V_%%V_off, _va_n=_V_%%V_len\")");
            this.Line("set /a \"_HID+=1\"");
            this.Line("set \"_va_out=!_HID!\"");
            this.Line("set \"_AL_!_va_out!=!_va_n!\"");
            this.Line("set /a \"_va_lim=_va_n-1\"");
            this.Line("if !_va_lim! GEQ 0 for /l %%I in (0,1,!_va_lim!) do (set /a \"_vi=_va_o+%%I\" & for %%A in (!_va_a!) do for %%K in (!_vi!) do set \"_A_!_va_out!_%%I=!_A_%%A_%%K!\")");
            this.EmitVarToSlot("_va_out", slot);
            break;
          case IrIntrinsicOp.ViewCopy:
          case IrIntrinsicOp.ViewTryCopy:
            this.EmitViewCopy(intrinsic.Op == IrIntrinsicOp.ViewTryCopy, slot);
            break;
          case IrIntrinsicOp.ViewEqual:
            this.EmitViewEqual(slot);
            break;
          case IrIntrinsicOp.Pin:
            this.EmitPin(intrinsic, slot);
            break;
          case IrIntrinsicOp.PointerAdd:
            this.EmitPointerAdd(intrinsic.Type.Element!, slot);
            break;
          case IrIntrinsicOp.PointerCast:
            this.EmitCopySlot(slot, slot + 1);
            break;
          case IrIntrinsicOp.PointerLoad:
            this.EmitPointerLoad(intrinsic.Type, slot);
            break;
          case IrIntrinsicOp.PointerStore:
            this.EmitPointerStore(intrinsic.Arguments[1].Type, slot);
            break;
          case IrIntrinsicOp.PointerDifference:
            this.EmitPointerDifference(intrinsic.Arguments[0].Type.Element!, slot);
            break;
          case IrIntrinsicOp.PointerEqual:
          case IrIntrinsicOp.PointerNotEqual:
          case IrIntrinsicOp.PointerLess:
          case IrIntrinsicOp.PointerLessOrEqual:
          case IrIntrinsicOp.PointerGreater:
          case IrIntrinsicOp.PointerGreaterOrEqual:
            this.EmitPointerCompare(intrinsic.Op, slot);
            break;
          case IrIntrinsicOp.RangeCount:
            this.EmitRangeCount(slot);
            break;
          case IrIntrinsicOp.StableSort:
            this._owner.RequireHelper("stablesort");
            this.EmitSlotToVar(slot + 1, "_ss_a");
            this.EmitSlotToVar(slot + 2, "_ss_c");
            this.Line("call :__ply_stablesort \"!_ss_a!\" \"!_ss_c!\"");
            this.EmitCheckUnwind();
            break;
          case IrIntrinsicOp.HighWord:
            this.EmitSlotToVar(slot + 1, "_hw_v");
            this.EmitFrame($"for /f \"tokens=1 delims=:\" %%A in (\"!_hw_v!\") do set \"{this.SlotVar(slot)}=%%A\"");
            break;
          case IrIntrinsicOp.JoinWords:
            this.EmitSlotToVar(slot + 1, "_jw_h");
            this.EmitSlotToVar(slot + 2, "_jw_l");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=!_jw_h!:!_jw_l!\"");
            break;
          default:
            throw new Refusal($"intrinsic {intrinsic.Op}");
        }
      }

      private void EmitArrayCopy(int slot) {
        this.EmitSlotToVar(slot + 1, "_cp_s");
        this.EmitSlotToVar(slot + 2, "_cp_si");
        this.EmitSlotToVar(slot + 3, "_cp_d");
        this.EmitSlotToVar(slot + 4, "_cp_di");
        this.EmitSlotToVar(slot + 5, "_cp_n");
        this.EmitNullTrap("_cp_s");
        this.EmitNullTrap("_cp_d");
        this.Line("set /a \"_cp_lim=_cp_n-1\"");
        this.Line("if !_cp_lim! GEQ 0 for /l %%I in (0,1,!_cp_lim!) do (set /a \"_k=_cp_si+%%I\" & for %%S in (!_cp_s!) do for %%K in (!_k!) do set \"_cp_b_%%I=!_A_%%S_%%K!\")");
        this.Line("if !_cp_lim! GEQ 0 for /l %%I in (0,1,!_cp_lim!) do (set /a \"_k=_cp_di+%%I\" & for %%D in (!_cp_d!) do for %%K in (!_k!) do set \"_A_%%D_%%K=!_cp_b_%%I!\")");
      }

      private void EmitRotate(bool left, IrType type, int slot) {
        var bits = type.Integral.Bits;
        if (bits == 64) {
          this._owner.RequireHelper("shl64");
          this._owner.RequireHelper("ushr64");
          this._owner.RequireHelper("bit64");
          this.EmitSlotToVar(slot + 1, "_rv");
          this.EmitSlotToVar(slot + 2, "_rc");
          this.Line("set /a \"_c=_rc&63, _ic=(64-_c)&63\"");
          var done = this._owner.NewLabel("_r64_d");
          this.EmitVarToSlot("_rv", slot);
          this.Line($"if !_c! EQU 0 goto :{done}");
          this.Line($"call :__ply_{(left ? "shl64" : "ushr64")} \"!_rv!\" \"!_c!\"");
          this.Line("set \"_rp1=!_RET!\"");
          this.Line($"call :__ply_{(left ? "ushr64" : "shl64")} \"!_rv!\" \"!_ic!\"");
          this.Line("call :__ply_bit64 or \"!_rp1!\" \"!_RET!\"");
          this.EmitVarToSlot("_RET", slot);
          this.Mark(done);
          return;
        }

        // 32-bit rotate (i32/u32).
        var l = this.SlotVar(slot);
        var v = this.SlotVar(slot + 1);
        var c = this.SlotVar(slot + 2);
        var done32 = this._owner.NewLabel("_r32_d");
        this.EmitFrame($"set /a \"_rc={c}&31, {l}={v}\"");
        this.Line($"if !_rc! EQU 0 goto :{done32}");
        if (left)
          this.EmitFrame($"set /a \"_ic=32-_rc, {l}=({v}<<_rc)|(({v}>>_ic)&(2147483647>>(_ic-1)))\"");
        else
          this.EmitFrame($"set /a \"_ic=32-_rc, {l}=(({v}>>_rc)&(2147483647>>(_rc-1)))|({v}<<_ic)\"");
        this.Mark(done32);
      }

      private void EmitPextOrPdep(bool extract, IrType type, int slot) {
        var helper = extract ? "pext32" : "pdep32";
        this._owner.RequireHelper(helper);
        this.EmitSlotToVar(slot + 1, "_px_v");
        this.EmitSlotToVar(slot + 2, "_px_m");
        this.Line($"call :__ply_{helper} \"!_px_v!\" \"!_px_m!\"");
        this.EmitVarToSlot("_RET", slot);
        this.EmitNarrowMaskSlot(type.Integral, slot);
      }

      private void EmitAbs(bool trap, IrType type, int slot) {
        this.EmitCopySlot(slot, slot + 1);
        if (!type.Integral.IsSigned)
          return;

        if (type.Integral.IsLong) {
          this._owner.RequireHelper("neg64");
          this.EmitSlotToVar(slot, "_ab_v");
          if (trap) {
            var ok = this._owner.NewLabel("_ab64_ok");
            this.Line($"if not \"!_ab_v!\"==\"-2147483648:0\" goto :{ok}");
            this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
            this.Mark(ok);
          }

          var pos = this._owner.NewLabel("_ab64_p");
          this.Line("for /f \"tokens=1 delims=:\" %%A in (\"!_ab_v!\") do set /a \"_sg=(%%A>>31)&1\"");
          this.Line($"if !_sg! EQU 0 goto :{pos}");
          this.Line("call :__ply_neg64 \"!_ab_v!\"");
          this.EmitVarToSlot("_RET", slot);
          this.Mark(pos);
          return;
        }

        this.EmitSlotToVar(slot, "_ab_v");
        if (trap) {
          var ok = this._owner.NewLabel("_ab_ok");
          var minVal = (int)IrOperators.Minimum(type.Integral);
          this.Line($"if not \"!_ab_v!\"==\"{minVal.ToString(CultureInfo.InvariantCulture)}\" goto :{ok}");
          this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
          this.Mark(ok);
        }

        this.EmitFrame($"set /a \"_sg=(_ab_v>>31)&1\" & if !_sg! EQU 1 set /a \"{this.SlotVar(slot)}=-_ab_v\"");
      }

      private void EmitMinMax(bool isMin, IrType type, int slot) {
        this.EmitCopySlot(slot, slot + 1);
        this.EmitCopySlot(slot + 1, slot + 2);
        var op = isMin ? IrBinaryOp.Less : IrBinaryOp.Greater;
        if (type.Integral.IsLong) {
          this._owner.RequireHelper(type.Integral.IsSigned ? "cmp_i64" : "cmp_u64");
          this.EmitSlotToVar(slot, "_mm_a");
          this.EmitSlotToVar(slot + 1, "_mm_b");
          this.Line($"call :__ply_cmp_{(type.Integral.IsSigned ? "i64" : "u64")} \"!_mm_a!\" \"!_mm_b!\"");
          this.EmitFrame($"if !_RET! {(isMin ? "GTR" : "LSS")} 0 set \"{this.SlotVar(slot)}=!_mm_b!\"");
        } else {
          this.EmitCopySlot(slot + 3, slot);
          this.EmitCopySlot(slot + 4, slot + 1);
          this.EmitOrderedCompare32(op, type.Integral.IsSigned, slot + 3);
          this.EmitFrame($"if \"!{this.SlotVar(slot + 3)}!\"==\"0\" set \"{this.SlotVar(slot)}=!{this.SlotVar(slot + 1)}!\"");
        }
      }

      private void EmitSign(IrType type, int slot) {
        this.EmitSlotToVar(slot + 1, "_sg_v");
        if (type.Integral.IsLong) {
          this._owner.RequireHelper(type.Integral.IsSigned ? "cmp_i64" : "cmp_u64");
          this.Line($"call :__ply_cmp_{(type.Integral.IsSigned ? "i64" : "u64")} \"!_sg_v!\" \"0:0\"");
          this.EmitVarToSlot("_RET", slot);
        } else if (!type.Integral.IsSigned) {
          this.EmitFrame($"if \"!_sg_v!\"==\"0\" (set \"{this.SlotVar(slot)}=0\") else (set \"{this.SlotVar(slot)}=1\")");
        } else {
          this.Line("set /a \"_s=(_sg_v>>31)&1\"");
          this.EmitFrame($"if \"!_sg_v!\"==\"0\" (set \"{this.SlotVar(slot)}=0\") else if !_s! EQU 1 (set \"{this.SlotVar(slot)}=-1\") else (set \"{this.SlotVar(slot)}=1\")");
        }
      }

      private void EmitClamp(IrType type, int slot) {
        // Arguments in slot+1 (val), slot+2 (lo), slot+3 (hi).
        var isLong = type.Integral.IsLong;
        var isSigned = type.Integral.IsSigned;
        if (isLong) {
          this._owner.RequireHelper(isSigned ? "cmp_i64" : "cmp_u64");
          var cmp = isSigned ? "cmp_i64" : "cmp_u64";
          this.EmitSlotToVar(slot + 1, "_cl_v");
          this.EmitSlotToVar(slot + 2, "_cl_lo");
          this.EmitSlotToVar(slot + 3, "_cl_hi");
          var ok = this._owner.NewLabel("_cl_ok");
          this.Line($"call :__ply_{cmp} \"!_cl_lo!\" \"!_cl_hi!\"");
          this.Line($"if !_RET! LEQ 0 goto :{ok}");
          this.EmitTrap(IrExceptionClass.ArithmeticException, "InvalidOperation");
          this.Mark(ok);
          this.Line($"call :__ply_{cmp} \"!_cl_v!\" \"!_cl_lo!\"");
          this.Line("if !_RET! LSS 0 set \"_cl_v=!_cl_lo!\"");
          this.Line($"call :__ply_{cmp} \"!_cl_v!\" \"!_cl_hi!\"");
          this.Line("if !_RET! GTR 0 set \"_cl_v=!_cl_hi!\"");
          this.EmitVarToSlot("_cl_v", slot);
        } else {
          this.EmitCopySlot(slot + 4, slot + 2);
          this.EmitCopySlot(slot + 5, slot + 3);
          this.EmitOrderedCompare32(IrBinaryOp.Greater, isSigned, slot + 4);
          var ok = this._owner.NewLabel("_cl_ok");
          this.EmitSlotToVar(slot + 4, "_cl_bad");
          this.Line($"if \"!_cl_bad!\"==\"0\" goto :{ok}");
          this.EmitTrap(IrExceptionClass.ArithmeticException, "InvalidOperation");
          this.Mark(ok);
          this.EmitCopySlot(slot, slot + 1);
          this.EmitCopySlot(slot + 4, slot);
          this.EmitCopySlot(slot + 5, slot + 2);
          this.EmitOrderedCompare32(IrBinaryOp.Less, isSigned, slot + 4);
          this.EmitFrame($"if \"!{this.SlotVar(slot + 4)}!\"==\"1\" set \"{this.SlotVar(slot)}=!{this.SlotVar(slot + 2)}!\"");
          this.EmitCopySlot(slot + 4, slot);
          this.EmitCopySlot(slot + 5, slot + 3);
          this.EmitOrderedCompare32(IrBinaryOp.Greater, isSigned, slot + 4);
          this.EmitFrame($"if \"!{this.SlotVar(slot + 4)}!\"==\"1\" set \"{this.SlotVar(slot)}=!{this.SlotVar(slot + 3)}!\"");
        }
      }

      private void EmitStrConcat(int count, int slot) {
        this.Line("set \"_sc_out=\"");
        for (var i = 0; i < count; ++i) {
          this.EmitSlotToVar(slot + 1 + i, "_sc_h");
          this.Line($"if not \"!_sc_h!\"==\"{_nullToken}\" for %%H in (!_sc_h!) do set \"_sc_out=!_sc_out!!_S_%%H!\"");
        }

        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_S_s!_HID!=!_sc_out!\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=s!_HID!\"");
      }

      private void EmitFormatInt(IrType type, int slot) {
        var integral = type.Integral;
        this.EmitSlotToVar(slot + 1, "_fi_v");
        if (integral == IrType.U32) {
          this._owner.RequireHelper("fmt_u32");
          this.Line("call :__ply_fmt_u32 \"!_fi_v!\"");
          this.Line("set \"_fi_v=!_RET!\"");
        } else if (integral == IrType.I64) {
          this._owner.RequireHelper("fmt_i64");
          this.Line("call :__ply_fmt_i64 \"!_fi_v!\"");
          this.Line("set \"_fi_v=!_RET!\"");
        } else if (integral == IrType.U64) {
          this._owner.RequireHelper("fmt_u64");
          this.Line("call :__ply_fmt_u64 \"!_fi_v!\"");
          this.Line("set \"_fi_v=!_RET!\"");
        }

        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_S_s!_HID!=!_fi_v!\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=s!_HID!\"");
      }

      private void EmitEnumHasFlag(IrType enumType, int slot) {
        if (enumType.Integral.IsLong) {
          this._owner.RequireHelper("bit64");
          this.EmitSlotToVar(slot + 1, "_ef_a");
          this.EmitSlotToVar(slot + 2, "_ef_b");
          this.Line("call :__ply_bit64 and \"!_ef_a!\" \"!_ef_b!\"");
          this.EmitFrame($"if \"!_RET!\"==\"!_ef_b!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
        } else {
          this.EmitFrame($"set /a \"_ef_m={this.SlotVar(slot + 1)}&{this.SlotVar(slot + 2)}\"");
          this.EmitFrame($"if \"!_ef_m!\"==\"!{this.SlotVar(slot + 2)}!\" (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
        }
      }

      private void EmitViewOf(IrIntrinsic intrinsic, int slot) {
        var srcType = intrinsic.Arguments[0].Type;
        var esz = intrinsic.Type.Element!.Size;
        this.EmitSlotToVar(slot + 1, "_vo_src");
        if (srcType.IsArray) {
          var notNull = this._owner.NewLabel("_vo_nn");
          var done = this._owner.NewLabel("_vo_dn");
          this.Line($"if not \"!_vo_src!\"==\"{_nullToken}\" goto :{notNull}");
          if (intrinsic.Arguments.Count == 1) {
            this.Line("set /a \"_HID+=1\"");
            this.Line($"set \"_V_!_HID!_arr=0\" & set \"_V_!_HID!_off=0\" & set \"_V_!_HID!_len=0\" & set \"_V_!_HID!_esz={esz}\"");
            this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
            this.Jump(done);
          } else {
            this.EmitTrap(IrExceptionClass.ArgumentOutOfRangeException, "ArgumentOutOfRange");
          }

          this.Mark(notNull);
          this.Line("set \"_vo_arr=!_vo_src!\" & set \"_vo_base=0\"");
          this.Line("for %%A in (!_vo_arr!) do set /a \"_vo_slen=_AL_%%A\"");
          this.EmitViewSliceCreate(intrinsic.Arguments.Count, esz, slot);
          this.Mark(done);
        } else {
          this.Line("for %%V in (!_vo_src!) do (set \"_vo_arr=!_V_%%V_arr!\" & set /a \"_vo_base=_V_%%V_off, _vo_slen=_V_%%V_len\")");
          this.EmitViewSliceCreate(intrinsic.Arguments.Count, esz, slot);
        }
      }

      private void EmitSlice(IrIntrinsic intrinsic, int slot) {
        var esz = intrinsic.Type.Element!.Size;
        this.EmitSlotToVar(slot + 1, "_vo_src");
        this.Line("for %%V in (!_vo_src!) do (set \"_vo_arr=!_V_%%V_arr!\" & set /a \"_vo_base=_V_%%V_off, _vo_slen=_V_%%V_len\")");
        this.EmitViewSliceCreate(intrinsic.Arguments.Count, esz, slot);
      }

      private void EmitViewSliceCreate(int argCount, int esz, int slot) {
        if (argCount == 1) {
          this.Line("set \"_vo_st=0\" & set \"_vo_ln=!_vo_slen!\"");
        } else if (argCount == 2) {
          this.EmitSlotToVar(slot + 2, "_vo_st");
          this.Line("set /a \"_vo_ln=_vo_slen-_vo_st\"");
        } else {
          this.EmitSlotToVar(slot + 2, "_vo_st");
          this.EmitSlotToVar(slot + 3, "_vo_ln");
        }

        var ok = this._owner.NewLabel("_vs_ok");
        this.Line("set /a \"_vo_end=_vo_st+_vo_ln, _vs1=(_vo_st>>31)&1, _vs2=(_vo_ln>>31)&1\"");
        this.Line($"if !_vs1! EQU 0 if !_vs2! EQU 0 if !_vo_end! LEQ !_vo_slen! goto :{ok}");
        this.EmitTrap(IrExceptionClass.ArgumentOutOfRangeException, "ArgumentOutOfRange");
        this.Mark(ok);
        this.Line("set /a \"_HID+=1, _vo_off=_vo_base+_vo_st\"");
        this.Line($"set \"_V_!_HID!_arr=!_vo_arr!\" & set \"_V_!_HID!_off=!_vo_off!\" & set \"_V_!_HID!_len=!_vo_ln!\" & set \"_V_!_HID!_esz={esz}\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitStackAlloc(IrIntrinsic intrinsic, int slot) {
        var elem = intrinsic.Type.Element!;
        var defTok = elem.Integral.IsLong ? "0:0" : "0";
        this.EmitSlotToVar(slot + 1, "_sa_len");
        var ok = this._owner.NewLabel("_stk_ok");
        this.Line("set /a \"_sa_s=(_sa_len>>31)&1\"");
        this.Line($"if !_sa_s! EQU 0 goto :{ok}");
        this.EmitTrap(IrExceptionClass.OverflowException, "IntegerOverflow");
        this.Mark(ok);
        this.Line("set /a \"_HID+=1\"");
        this.Line("set \"_sa_arr=!_HID!\"");
        this.Line("set \"_AL_!_sa_arr!=!_sa_len!\"");
        this.Line("set /a \"_sa_lim=_sa_len-1\"");
        this.Line($"if !_sa_lim! GEQ 0 for /l %%I in (0,1,!_sa_lim!) do set \"_A_!_sa_arr!_%%I={defTok}\"");
        if (intrinsic.Type.IsPointer) {
          this.Line("set /a \"_HID+=1, _sa_rb=_sa_len*" + elem.Size.ToString(CultureInfo.InvariantCulture) + "\"");
          this.Line($"set \"_P_!_HID!_arr=!_sa_arr!\" & set \"_P_!_HID!_base=0\" & set \"_P_!_HID!_boff=0\" & set \"_P_!_HID!_rb=!_sa_rb!\" & set \"_P_!_HID!_esz={elem.Size}\"");
        } else {
          this.Line("set /a \"_HID+=1\"");
          this.Line($"set \"_V_!_HID!_arr=!_sa_arr!\" & set \"_V_!_HID!_off=0\" & set \"_V_!_HID!_len=!_sa_len!\" & set \"_V_!_HID!_esz={elem.Size}\"");
        }

        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitViewCopy(bool isTry, int slot) {
        this.EmitSlotToVar(slot + 1, "_vc_s");
        this.EmitSlotToVar(slot + 2, "_vc_d");
        this.Line("for %%V in (!_vc_s!) do (set \"_vc_sa=!_V_%%V_arr!\" & set /a \"_vc_so=_V_%%V_off, _vc_sl=_V_%%V_len\")");
        this.Line("for %%V in (!_vc_d!) do (set \"_vc_da=!_V_%%V_arr!\" & set /a \"_vc_do=_V_%%V_off, _vc_dl=_V_%%V_len\")");
        var ok = this._owner.NewLabel("_vc_ok");
        var done = this._owner.NewLabel("_vc_dn");
        this.Line($"if !_vc_sl! LEQ !_vc_dl! goto :{ok}");
        if (isTry) {
          this.EmitSetSlotLiteral(slot, "0");
          this.Jump(done);
        } else {
          this.EmitTrap(IrExceptionClass.ArgumentOutOfRangeException, "ArgumentOutOfRange");
        }

        this.Mark(ok);
        this.Line("set /a \"_vc_lim=_vc_sl-1\"");
        this.Line("if !_vc_lim! GEQ 0 for /l %%I in (0,1,!_vc_lim!) do (set /a \"_k=_vc_so+%%I\" & for %%A in (!_vc_sa!) do for %%K in (!_k!) do set \"_vc_b_%%I=!_A_%%A_%%K!\")");
        this.Line("if !_vc_lim! GEQ 0 for /l %%I in (0,1,!_vc_lim!) do (set /a \"_k=_vc_do+%%I\" & for %%A in (!_vc_da!) do for %%K in (!_k!) do set \"_A_%%A_%%K=!_vc_b_%%I!\")");
        if (isTry)
          this.EmitSetSlotLiteral(slot, "1");
        this.Mark(done);
      }

      private void EmitViewEqual(int slot) {
        this.EmitSlotToVar(slot + 1, "_ve_a");
        this.EmitSlotToVar(slot + 2, "_ve_b");
        this.Line("for %%V in (!_ve_a!) do (set \"_ve_aa=!_V_%%V_arr!\" & set /a \"_ve_ao=_V_%%V_off, _ve_al=_V_%%V_len\")");
        this.Line("for %%V in (!_ve_b!) do (set \"_ve_ba=!_V_%%V_arr!\" & set /a \"_ve_bo=_V_%%V_off, _ve_bl=_V_%%V_len\")");
        var no = this._owner.NewLabel("_ve_no");
        var done = this._owner.NewLabel("_ve_dn");
        this.Line($"if !_ve_al! NEQ !_ve_bl! goto :{no}");
        this.Line("set /a \"_ve_lim=_ve_al-1\"");
        this.Line("if !_ve_lim! GEQ 0 for /l %%I in (0,1,!_ve_lim!) do (set /a \"_ka=_ve_ao+%%I, _kb=_ve_bo+%%I\" & for %%A in (!_ve_aa!) do for %%B in (!_ve_ba!) do for %%X in (!_ka!) do for %%Y in (!_kb!) do if not \"!_A_%%A_%%X!\"==\"!_A_%%B_%%Y!\" goto :" + no + ")");
        this.EmitSetSlotLiteral(slot, "1");
        this.Jump(done);
        this.Mark(no);
        this.EmitSetSlotLiteral(slot, "0");
        this.Mark(done);
      }

      private void EmitReadAddressOfLocal(IrLocal local, int slot) {
        var esz = local.Type.Size;
        this.EmitFrame($"set \"_ao_arr=!{this.LocalVar(local)}!\"");
        this.Line("set /a \"_HID+=1\"");
        this.Line($"set \"_P_!_HID!_arr=!_ao_arr!\" & set \"_P_!_HID!_base=0\" & set \"_P_!_HID!_boff=0\" & set \"_P_!_HID!_rb={esz}\" & set \"_P_!_HID!_esz={esz}\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitPin(IrIntrinsic pin, int slot) {
        var srcType = pin.Arguments[0].Type;
        var esz = pin.Type.Element!.Size;
        this.EmitSlotToVar(slot + 1, "_pn_src");
        var done = this._owner.NewLabel("_pn_dn");
        var notNull = this._owner.NewLabel("_pn_nn");

        if (srcType.IsArray) {
          this.Line($"if not \"!_pn_src!\"==\"{_nullToken}\" goto :{notNull}");
          if (pin.Arguments.Count == 1) {
            this.EmitSetSlotLiteral(slot, "0");
            this.Jump(done);
          } else {
            this.EmitTrap(IrExceptionClass.NullReferenceException, "NullReference");
          }

          this.Mark(notNull);
          this.Line("set \"_pn_arr=!_pn_src!\" & set \"_pn_base=0\"");
          this.Line("for %%A in (!_pn_arr!) do set /a \"_pn_len=_AL_%%A\"");
        } else {
          this.Line("for %%V in (!_pn_src!) do (set \"_pn_arr=!_V_%%V_arr!\" & set /a \"_pn_base=_V_%%V_off, _pn_len=_V_%%V_len\")");
        }

        if (pin.Arguments.Count == 1) {
          var nonEmpty = this._owner.NewLabel("_pn_ne");
          this.Line($"if !_pn_len! GTR 0 goto :{nonEmpty}");
          this.EmitSetSlotLiteral(slot, "0");
          this.Jump(done);
          this.Mark(nonEmpty);
          this.Line($"set /a \"_HID+=1, _pn_rb=_pn_len*{esz}\"");
          this.Line($"set \"_P_!_HID!_arr=!_pn_arr!\" & set \"_P_!_HID!_base=!_pn_base!\" & set \"_P_!_HID!_boff=0\" & set \"_P_!_HID!_rb=!_pn_rb!\" & set \"_P_!_HID!_esz={esz}\"");
          this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        } else {
          this.EmitSlotToVar(slot + 2, "_pn_idx");
          this.EmitBoundsTrap("_pn_idx", "_pn_len");
          this.Line($"set /a \"_HID+=1, _pn_rb=_pn_len*{esz}, _pn_boff=_pn_idx*{esz}\"");
          this.Line($"set \"_P_!_HID!_arr=!_pn_arr!\" & set \"_P_!_HID!_base=!_pn_base!\" & set \"_P_!_HID!_boff=!_pn_boff!\" & set \"_P_!_HID!_rb=!_pn_rb!\" & set \"_P_!_HID!_esz={esz}\"");
          this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
        }

        this.Mark(done);
      }

      private void EmitPointerAdd(IrType elemType, int slot) {
        var esz = elemType.Size;
        this.EmitSlotToVar(slot + 1, "_pa_p");
        this.EmitSlotToVar(slot + 2, "_pa_n");
        this.Line("if \"!_pa_p!\"==\"0\" (set \"_pa_arr=0\" & set \"_pa_base=0\" & set \"_pa_boff=0\" & set \"_pa_rb=0\") else (for %%P in (!_pa_p!) do (set \"_pa_arr=!_P_%%P_arr!\" & set \"_pa_base=!_P_%%P_base!\" & set \"_pa_boff=!_P_%%P_boff!\" & set \"_pa_rb=!_P_%%P_rb!\"))");
        this.Line($"set /a \"_HID+=1, _pa_nb=_pa_boff+_pa_n*{esz}\"");
        this.Line($"set \"_P_!_HID!_arr=!_pa_arr!\" & set \"_P_!_HID!_base=!_pa_base!\" & set \"_P_!_HID!_boff=!_pa_nb!\" & set \"_P_!_HID!_rb=!_pa_rb!\" & set \"_P_!_HID!_esz={esz}\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_HID!\"");
      }

      private void EmitPointerCheck(string ptrVar, int elemSize) {
        var nn = this._owner.NewLabel("_pt_nn");
        this.Line($"if not \"!{ptrVar}!\"==\"0\" for %%P in (!{ptrVar}!) do if not \"!_P_%%P_arr!\"==\"0\" goto :{nn}");
        this.EmitTrap(IrExceptionClass.NullReferenceException, "NullReference");
        this.Mark(nn);
        this.Line($"for %%P in (!{ptrVar}!) do (set \"_pt_arr=!_P_%%P_arr!\" & set /a \"_pt_base=_P_%%P_base, _pt_boff=_P_%%P_boff, _pt_rb=_P_%%P_rb\")");
        var ok = this._owner.NewLabel("_pt_ok");
        this.Line($"set /a \"_pt_max=_pt_rb-{elemSize}, _pt_s=(_pt_boff>>31)&1\"");
        this.Line($"if !_pt_s! EQU 0 if !_pt_boff! LEQ !_pt_max! goto :{ok}");
        this.EmitTrap(IrExceptionClass.IndexOutOfRangeException, "IndexOutOfRange");
        this.Mark(ok);
        this.Line($"set /a \"_pt_idx=_pt_base+(_pt_boff/{elemSize})\"");
      }

      private void EmitPointerLoad(IrType elemType, int slot) {
        this.EmitSlotToVar(slot + 1, "_pl_p");
        this.EmitPointerCheck("_pl_p", elemType.Size);
        this.EmitFrame($"for %%A in (!_pt_arr!) do for %%I in (!_pt_idx!) do set \"{this.SlotVar(slot)}=!_A_%%A_%%I!\"");
      }

      private void EmitPointerStore(IrType elemType, int slot) {
        this.EmitSlotToVar(slot + 1, "_ps_p");
        this.EmitSlotToVar(slot + 2, "_ps_v");
        this.EmitPointerCheck("_ps_p", elemType.Size);
        this.Line("for %%A in (!_pt_arr!) do for %%I in (!_pt_idx!) do set \"_A_%%A_%%I=!_ps_v!\"");
      }

      private void EmitPointerDifference(IrType elemType, int slot) {
        var esz = elemType.Size;
        this.EmitSlotToVar(slot + 1, "_pd_a");
        this.EmitSlotToVar(slot + 2, "_pd_b");
        this.Line($"if \"!_pd_a!\"==\"0\" (set \"_pda=0\" & set \"_pdp=0\") else (for %%P in (!_pd_a!) do (set \"_pda=!_P_%%P_arr!\" & set /a \"_pdp=_P_%%P_base*{esz}+_P_%%P_boff\"))");
        this.Line($"if \"!_pd_b!\"==\"0\" (set \"_pdb=0\" & set \"_pdq=0\") else (for %%P in (!_pd_b!) do (set \"_pdb=!_P_%%P_arr!\" & set /a \"_pdq=_P_%%P_base*{esz}+_P_%%P_boff\"))");
        var ok = this._owner.NewLabel("_pd_ok");
        this.Line($"if \"!_pda!\"==\"!_pdb!\" goto :{ok}");
        this.EmitTrap(IrExceptionClass.ArithmeticException, "InvalidOperation");
        this.Mark(ok);
        this.Line($"set /a \"_pdd=(_pdp-_pdq)/{esz}, _pdh=-((_pdd>>31)&1)\"");
        this.EmitFrame($"set \"{this.SlotVar(slot)}=!_pdh!:!_pdd!\"");
      }

      private void EmitPointerCompare(IrIntrinsicOp op, int slot) {
        this.EmitSlotToVar(slot + 1, "_pc_a");
        this.EmitSlotToVar(slot + 2, "_pc_b");
        this.Line("if \"!_pc_a!\"==\"0\" (set \"_pca=0\" & set \"_pcp=0\") else (for %%P in (!_pc_a!) do (set \"_pca=!_P_%%P_arr!\" & set /a \"_pcp=_P_%%P_base*_P_%%P_esz+_P_%%P_boff\"))");
        this.Line("if \"!_pc_b!\"==\"0\" (set \"_pcb=0\" & set \"_pcq=0\") else (for %%P in (!_pc_b!) do (set \"_pcb=!_P_%%P_arr!\" & set /a \"_pcq=_P_%%P_base*_P_%%P_esz+_P_%%P_boff\"))");
        if (op is IrIntrinsicOp.PointerEqual or IrIntrinsicOp.PointerNotEqual) {
          var same = op == IrIntrinsicOp.PointerEqual ? "1" : "0";
          var diff = op == IrIntrinsicOp.PointerEqual ? "0" : "1";
          this.EmitFrame($"if \"!_pca!\"==\"!_pcb!\" (if !_pcp! EQU !_pcq! (set \"{this.SlotVar(slot)}={same}\") else (set \"{this.SlotVar(slot)}={diff}\")) else (set \"{this.SlotVar(slot)}={diff}\")");
          return;
        }

        var ok = this._owner.NewLabel("_pc_ok");
        this.Line($"if \"!_pca!\"==\"!_pcb!\" goto :{ok}");
        this.EmitTrap(IrExceptionClass.ArithmeticException, "InvalidOperation");
        this.Mark(ok);
        var cmp = op switch {
          IrIntrinsicOp.PointerLess => "LSS",
          IrIntrinsicOp.PointerLessOrEqual => "LEQ",
          IrIntrinsicOp.PointerGreater => "GTR",
          _ => "GEQ"
        };
        this.EmitFrame($"if !_pcp! {cmp} !_pcq! (set \"{this.SlotVar(slot)}=1\") else (set \"{this.SlotVar(slot)}=0\")");
      }

      private void EmitRangeCount(int slot) {
        this.EmitSlotToVar(slot + 1, "_rg_s");
        this.EmitSlotToVar(slot + 2, "_rg_c");
        var ok = this._owner.NewLabel("_rg_ok");
        var bad = this._owner.NewLabel("_rg_bad");
        this.Line("set /a \"_rgs=(_rg_c>>31)&1\"");
        this.Line($"if !_rgs! NEQ 0 goto :{bad}");
        this.Line($"if !_rg_c! EQU 0 goto :{ok}");
        this.Line("set /a \"_rg_max=2147483647-_rg_c+1\"");
        this.Line($"if !_rg_s! LEQ !_rg_max! goto :{ok}");
        this.Mark(bad);
        this.EmitTrap(IrExceptionClass.ArgumentOutOfRangeException, "ArgumentOutOfRange");
        this.Mark(ok);
        this.EmitVarToSlot("_rg_c", slot);
      }

    }

  }

}
