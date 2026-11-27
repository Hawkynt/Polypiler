using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Batch;

public static partial class BatchEmitter {

  private sealed partial class UnifiedEmitter {

    private void EmitDynamicDispatchTrampolines(List<string> lines) {
      var concreteClasses = this._module.Types.OfType<IrClassType>().Where(c => !c.IsAbstract).ToList();

      foreach (var root in this._neededVirtualDispatch) {
        var slotId = this._virtualSlotIds[root];
        lines.Add($":__ply_v_{slotId}");
        lines.Add("for %%O in (%~1) do set \"_vt=!_O_%%O___t!\"");
        foreach (var cl in concreteClasses) {
          var selected = cl.Select(root);
          if (cl.OwnerOf(selected) is null)
            continue;
          lines.Add($"if \"!_vt!\"==\"{this._classIds[cl]}\" goto :{this._functionLabels[selected.Function]}");
        }

        lines.Add("goto :eof");
      }

      foreach (var ifaceMethod in this._neededInterfaceDispatch) {
        var mid = this._interfaceMethodIds[ifaceMethod];
        lines.Add($":__ply_i_{mid}");
        lines.Add("for %%O in (%~1) do set \"_vt=!_O_%%O___t!\"");
        foreach (var cl in concreteClasses) {
          if (cl.Implementation(ifaceMethod) is not { } impl)
            continue;
          var selected = cl.Select(impl);
          lines.Add($"if \"!_vt!\"==\"{this._classIds[cl]}\" goto :{this._functionLabels[selected.Function]}");
        }

        lines.Add("goto :eof");
      }
    }

    private void EmitStructHelpers(List<string> lines) {
      // Transitively include nested struct copies and record/struct equalities.
      for (var changed = true; changed;) {
        changed = false;
        foreach (var st in this._neededStructCopies.ToList())
          foreach (var field in st.Fields)
            if (field.Type.StructType is { } nested)
              changed |= this._neededStructCopies.Add(nested);

        foreach (var st in this._neededStructEqs.ToList())
          foreach (var field in st.Fields)
            if (field.Type.StructType is { } nested)
              changed |= this._neededStructEqs.Add(nested);
            else if (field.Type.ClassType is { IsRecord: true } rec)
              changed |= this._neededRecordEqs.Add(rec);

        foreach (var cl in this._neededRecordEqs.ToList())
          foreach (var field in cl.AllFields)
            if (field.Type.StructType is { } nested)
              changed |= this._neededStructEqs.Add(nested);
            else if (field.Type.ClassType is { IsRecord: true } rec)
              changed |= this._neededRecordEqs.Add(rec);
      }

      foreach (var st in this._neededStructCopies) {
        var sid = this._structIds[st];
        lines.Add($":__ply_copy_s{sid}");
        lines.Add("set /a \"_SP+=1\"");
        lines.Add("for %%S in (!_SP!) do set \"_F%%S_s=%~1\"");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add("for %%S in (!_SP!) do set \"_F%%S_d=!_HID!\"");
        foreach (var field in st.Fields) {
          var fk = this._fieldKeys[field];
          if (field.Type.StructType is { } nested) {
            lines.Add($"for %%S in (!_SP!) do for %%A in (!_F%%S_s!) do set \"_fv=!_O_%%A_{fk}!\"");
            lines.Add($"call :__ply_copy_s{this._structIds[nested]} \"!_fv!\"");
            lines.Add($"for %%S in (!_SP!) do for %%B in (!_F%%S_d!) do set \"_O_%%B_{fk}=!_RET!\"");
          } else {
            lines.Add($"for %%S in (!_SP!) do for %%A in (!_F%%S_s!) do for %%B in (!_F%%S_d!) do set \"_O_%%B_{fk}=!_O_%%A_{fk}!\"");
          }
        }

        lines.Add("for %%S in (!_SP!) do set \"_RET=!_F%%S_d!\"");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
      }

      foreach (var st in this._neededStructEqs) {
        var sid = this._structIds[st];
        lines.Add($":__ply_eq_s{sid}");
        lines.Add("set /a \"_SP+=1\"");
        lines.Add("for %%S in (!_SP!) do (set \"_F%%S_a=%~1\" & set \"_F%%S_b=%~2\")");
        foreach (var field in st.Fields)
          this.EmitFieldEqualityCheck(lines, field, $"_ply_eq_s{sid}_no");
        lines.Add("set \"_RET=1\"");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
        lines.Add($":_ply_eq_s{sid}_no");
        lines.Add("set \"_RET=0\"");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
      }

      foreach (var cl in this._neededRecordEqs) {
        var cid = this._classIds[cl];
        lines.Add($":__ply_req_c{cid}");
        lines.Add("set /a \"_SP+=1\"");
        lines.Add("for %%S in (!_SP!) do (set \"_F%%S_a=%~1\" & set \"_F%%S_b=%~2\")");
        lines.Add($"if \"%~1\"==\"%~2\" (set \"_RET=1\" & set /a \"_SP-=1\" & goto :eof)");
        lines.Add($"if \"%~1\"==\"{_nullToken}\" (set \"_RET=0\" & set /a \"_SP-=1\" & goto :eof)");
        lines.Add($"if \"%~2\"==\"{_nullToken}\" (set \"_RET=0\" & set /a \"_SP-=1\" & goto :eof)");
        lines.Add("for %%A in (%~1) do for %%B in (%~2) do if not \"!_O_%%A___t!\"==\"!_O_%%B___t!\" (set \"_RET=0\" & set /a \"_SP-=1\" & goto :eof)");
        foreach (var field in cl.AllFields)
          this.EmitFieldEqualityCheck(lines, field, $"_ply_req_c{cid}_no");
        lines.Add("set \"_RET=1\"");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
        lines.Add($":_ply_req_c{cid}_no");
        lines.Add("set \"_RET=0\"");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
      }
    }

    private void EmitFieldEqualityCheck(List<string> lines, IrField field, string failLabel) {
      var fk = this._fieldKeys[field];
      lines.Add($"for %%S in (!_SP!) do for %%A in (!_F%%S_a!) do set \"_ea=!_O_%%A_{fk}!\"");
      lines.Add($"for %%S in (!_SP!) do for %%B in (!_F%%S_b!) do set \"_eb=!_O_%%B_{fk}!\"");
      if (field.Type.StructType is { } nested) {
        lines.Add($"call :__ply_eq_s{this._structIds[nested]} \"!_ea!\" \"!_eb!\"");
        lines.Add($"if \"!_RET!\"==\"0\" goto :{failLabel}");
      } else if (field.Type.ClassType is { IsRecord: true } rec) {
        lines.Add($"call :__ply_req_c{this._classIds[rec]} \"!_ea!\" \"!_eb!\"");
        lines.Add($"if \"!_RET!\"==\"0\" goto :{failLabel}");
      } else {
        lines.Add($"if not \"!_ea!\"==\"!_eb!\" goto :{failLabel}");
      }
    }

    private void EmitEnumHelpers(List<string> lines) {
      foreach (var en in this._neededEnumToString) {
        var eid = Sanitize(en.Name);
        lines.Add($":__ply_enum_str_{eid}");
        lines.Add("set \"_ev=%~1\"");
        foreach (var m in en.Members) {
          var mv = FormatConstValue(en.Underlying, m.Value);
          lines.Add($"if \"!_ev!\"==\"{mv}\" (set /a \"_HID+=1\" & set \"_S_s!_HID!={this.EscapeStringLiteral(m.Name)}\" & set \"_RET=s!_HID!\" & goto :eof)");
        }

        if (en.Underlying.IsLong) {
          this.RequireHelper(en.Underlying.IsSigned ? "fmt_i64" : "fmt_u64");
          lines.Add($"call :__ply_fmt_{(en.Underlying.IsSigned ? "i64" : "u64")} \"!_ev!\"");
          lines.Add("set /a \"_HID+=1\" & set \"_S_s!_HID!=!_RET!\" & set \"_RET=s!_HID!\" & goto :eof");
        } else if (en.Underlying == IrType.U32) {
          this.RequireHelper("fmt_u32");
          lines.Add("call :__ply_fmt_u32 \"!_ev!\"");
          lines.Add("set /a \"_HID+=1\" & set \"_S_s!_HID!=!_RET!\" & set \"_RET=s!_HID!\" & goto :eof");
        } else {
          lines.Add("set /a \"_HID+=1\" & set \"_S_s!_HID!=!_ev!\" & set \"_RET=s!_HID!\" & goto :eof");
        }
      }

      foreach (var en in this._neededEnumIsDefined) {
        var eid = Sanitize(en.Name);
        lines.Add($":__ply_enum_def_{eid}");
        lines.Add("set \"_ev=%~1\"");
        foreach (var m in en.Members) {
          var mv = FormatConstValue(en.Underlying, m.Value);
          lines.Add($"if \"!_ev!\"==\"{mv}\" (set \"_RET=1\" & goto :eof)");
        }

        lines.Add("set \"_RET=0\"");
        lines.Add("goto :eof");
      }

      foreach (var en in this._neededEnumParse) {
        var eid = Sanitize(en.Name);
        this.RequireHelper("trap");
        lines.Add($":__ply_enum_parse_{eid}");
        lines.Add("set \"_eh=%~1\"");
        lines.Add("set \"_eic=%~2\"");
        lines.Add($"if \"!_eh!\"==\"{_nullToken}\" (call :__ply_trap {this._classIds[IrExceptionClass.FormatException]} InvalidFormat & goto :eof)");
        lines.Add("for %%H in (!_eh!) do set \"_es=!_S_%%H!\"");
        foreach (var m in en.Members) {
          var mv = FormatConstValue(en.Underlying, m.Value);
          var mn = this.EscapeStringLiteral(m.Name);
          lines.Add($"if \"!_eic!\"==\"1\" (if /i \"!_es!\"==\"{mn}\" (set \"_RET={mv}\" & goto :eof)) else (if \"!_es!\"==\"{mn}\" (set \"_RET={mv}\" & goto :eof))");
        }

        lines.Add($"call :__ply_trap {this._classIds[IrExceptionClass.FormatException]} InvalidFormat");
        lines.Add("goto :eof");
      }
    }

    private void EmitRuntimeHelpers(List<string> lines) {
      // Expand transitive helper dependencies.
      var expanded = true;
      while (expanded) {
        expanded = false;
        void Dep(string parent, params string[] children) {
          if (this._usedHelpers.Contains(parent))
            foreach (var c in children)
              expanded |= this._usedHelpers.Add(c);
        }

        Dep("sdiv64", "udiv64", "neg64", "trap");
        Dep("udiv64", "add64", "sub64", "cmp_u64", "shl64", "trap");
        Dep("neg64", "sub64");
        Dep("fmt_i64", "fmt_u64", "neg64");
        Dep("fmt_u64", "udiv64");
        Dep("parse_i64", "parse_u64", "neg64");
        Dep("parse_u64", "mul64", "add64");
        Dep("map_ops", "streq", "trap");
        Dep("set_ops", "streq", "trap");
        Dep("list_ops", "streq", "trap");
        Dep("streq", "strlen");
        Dep("strcmp", "strlen", "strcode");
        Dep("stridx", "strlen", "streq");
        Dep("strridx", "strlen", "streq");
        Dep("substr", "strlen", "trap");
        Dep("strcode", "strlen", "trap");
        Dep("mulchk32", "trap");
        Dep("udiv32", "trap");
        Dep("cmp_i64", "cmp_u32");
        Dep("cmp_u64", "cmp_u32");
      }

      if (this._usedHelpers.Contains("trap")) {
        lines.Add(":__ply_trap");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add("set \"_O_!_HID!___t=%~1\"");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add("set \"_S_s!_HID!=%~2\"");
        lines.Add("set /a \"_tid=_HID-1\"");
        lines.Add("for %%T in (!_tid!) do (");
        lines.Add("  set \"_O_%%T_f_Message=s!_HID!\"");
        lines.Add($"  set \"_O_%%T_f_InnerException={_nullToken}\"");
        lines.Add("  set \"_EX_OBJ=%%T\"");
        lines.Add(")");
        lines.Add("set \"_EX_TYPE=%~1\"");
        lines.Add("set \"_EX_TRAP=%~2\"");
        lines.Add("set \"_EX_ACTIVE=1\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("cmp_u32")) {
        lines.Add(":__ply_cmp_u32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_a=%~1^(-2147483647-1), _b=%~2^(-2147483647-1)\"");
        lines.Add("set /a \"_sa=(_a>>31)&1, _sb=(_b>>31)&1\"");
        lines.Add("if !_sa! EQU !_sb! goto :_cu32_s");
        lines.Add("if !_sa! GTR !_sb! (set \"_RET=-1\") else (set \"_RET=1\")");
        lines.Add("goto :_cu32_d");
        lines.Add(":_cu32_s");
        lines.Add("set /a \"_ma=_a&2147483647, _mb=_b&2147483647\"");
        lines.Add("if !_ma! LSS !_mb! (set \"_RET=-1\") else if !_ma! GTR !_mb! (set \"_RET=1\") else (set \"_RET=0\")");
        lines.Add(":_cu32_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("udiv32")) {
        lines.Add(":__ply_udiv32");
        lines.Add($"if \"%~2\"==\"0\" (call :__ply_trap {this._classIds[IrExceptionClass.DivideByZeroException]} DivideByZero & goto :eof)");
        lines.Add("set /a \"_ud_a=%~1, _ud_b=%~2, _ud_q=0, _ud_r=0\"");
        lines.Add("for /l %%I in (31,-1,0) do (");
        lines.Add("  set /a \"_ud_r=(_ud_r<<1)|((_ud_a>>%%I)&1)\"");
        lines.Add("  set /a \"_ua=_ud_r^(-2147483647-1), _ub=_ud_b^(-2147483647-1)\"");
        lines.Add("  set /a \"_usa=(_ua>>31)&1, _usb=(_ub>>31)&1, _uma=_ua&2147483647, _umb=_ub&2147483647, _ge=0\"");
        lines.Add("  if !_usa! LSS !_usb! set \"_ge=1\"");
        lines.Add("  if !_usa! EQU !_usb! if !_uma! GEQ !_umb! set \"_ge=1\"");
        lines.Add("  if !_ge! EQU 1 set /a \"_ud_r-=_ud_b, _ud_q|=(1<<%%I)\"");
        lines.Add(")");
        lines.Add("set \"_RET=!_ud_q!\"");
        lines.Add("set \"_REM=!_ud_r!\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("mulchk32")) {
        lines.Add(":__ply_mulchk32");
        lines.Add("set /a \"_ma=%~1, _mb=%~2\"");
        lines.Add("if !_ma! EQU 0 (set \"_RET=0\" & goto :eof)");
        lines.Add("if !_mb! EQU 0 (set \"_RET=0\" & goto :eof)");
        lines.Add("set /a \"_mp=_ma*_mb, _mn=-2147483647-1\"");
        lines.Add($"if !_ma! EQU !_mn! if !_mb! EQU -1 (call :__ply_trap {this._classIds[IrExceptionClass.OverflowException]} IntegerOverflow & goto :eof)");
        lines.Add($"if !_mb! EQU !_mn! if !_ma! EQU -1 (call :__ply_trap {this._classIds[IrExceptionClass.OverflowException]} IntegerOverflow & goto :eof)");
        lines.Add("set /a \"_mq=_mp/_ma\"");
        lines.Add($"if !_mq! NEQ !_mb! (call :__ply_trap {this._classIds[IrExceptionClass.OverflowException]} IntegerOverflow & goto :eof)");
        lines.Add("set \"_RET=!_mp!\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("fmt_u32")) {
        lines.Add(":__ply_fmt_u32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_x=%~1, _s=(_x>>31)&1\"");
        lines.Add("if !_s! EQU 0 (set \"_RET=!_x!\" & goto :_fu32_d)");
        lines.Add("set /a \"_h=(_x>>1)&2147483647, _q=_h/5, _r=(_x&1)+(_h%%5)*2\"");
        lines.Add("if !_r! GEQ 10 set /a \"_q+=1, _r-=10\"");
        lines.Add("set \"_RET=!_q!!_r!\"");
        lines.Add(":_fu32_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("parse_u32")) {
        lines.Add(":__ply_parse_u32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_s=%~1\"");
        lines.Add("set /a \"_acc=0\"");
        lines.Add(":_pu32_lp");
        lines.Add("if not defined _s goto :_pu32_dn");
        lines.Add("set \"_ch=!_s:~0,1!\"");
        lines.Add("set \"_s=!_s:~1!\"");
        lines.Add("set /a \"_acc=_acc*10+_ch\"");
        lines.Add("goto :_pu32_lp");
        lines.Add(":_pu32_dn");
        lines.Add("set \"_RET=!_acc!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("pext32")) {
        lines.Add(":__ply_pext32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_pv=%~1, _pm=%~2, _pr=0, _pn=0\"");
        lines.Add("for /l %%I in (0,1,31) do (set /a \"_pb=(_pm>>%%I)&1\" & if !_pb! EQU 1 (set /a \"_pr|=(((_pv>>%%I)&1)<<_pn), _pn+=1\"))");
        lines.Add("set \"_RET=!_pr!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("pdep32")) {
        lines.Add(":__ply_pdep32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_pv=%~1, _pm=%~2, _pr=0, _pn=0\"");
        lines.Add("for /l %%I in (0,1,31) do (set /a \"_pb=(_pm>>%%I)&1\" & if !_pb! EQU 1 (set /a \"_pr|=(((_pv>>_pn)&1)<<%%I), _pn+=1\"))");
        lines.Add("set \"_RET=!_pr!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("popcnt32")) {
        lines.Add(":__ply_popcnt32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_v=%~1, _c=0\"");
        lines.Add("for /l %%I in (0,1,31) do set /a \"_c+=(_v>>%%I)&1\"");
        lines.Add("set \"_RET=!_c!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("clz32")) {
        lines.Add(":__ply_clz32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_v=%~1, _c=32\"");
        lines.Add("for /l %%I in (31,-1,0) do (if !_c! EQU 32 (set /a \"_b=(_v>>%%I)&1\" & if !_b! EQU 1 set /a \"_c=31-%%I\"))");
        lines.Add("set \"_RET=!_c!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("ctz32")) {
        lines.Add(":__ply_ctz32");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set /a \"_v=%~1, _c=32\"");
        lines.Add("for /l %%I in (0,1,31) do (if !_c! EQU 32 (set /a \"_b=(_v>>%%I)&1\" & if !_b! EQU 1 set /a \"_c=%%I\"))");
        lines.Add("set \"_RET=!_c!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      this.Emit64BitHelpers(lines);
      this.EmitStringHelpers(lines);
      this.EmitCollectionHelpers(lines);
      this.EmitSequenceHelpers(lines);
    }

    private void Emit64BitHelpers(List<string> lines) {
      if (this._usedHelpers.Contains("add64")) {
        lines.Add(":__ply_add64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h1=%%A, _l1=%%B\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do set /a \"_h2=%%A, _l2=%%B\"");
        lines.Add("set /a \"_lo=_l1+_l2, _s1=(_l1>>31)&1, _s2=(_l2>>31)&1, _ss=(_lo>>31)&1\"");
        lines.Add("set /a \"_c1=_s1&_s2, _c2=_s1|_s2, _c3=_ss^1, _cy=_c1|(_c2&_c3), _hi=_h1+_h2+_cy\"");
        lines.Add("set \"_RET=!_hi!:!_lo!\"");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("sub64")) {
        lines.Add(":__ply_sub64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h1=%%A, _l1=%%B\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do set /a \"_h2=%%A, _l2=%%B\"");
        lines.Add("set /a \"_lo=_l1-_l2, _s1=(_l1>>31)&1, _s2=(_l2>>31)&1, _sd=(_lo>>31)&1\"");
        lines.Add("set /a \"_b1=(_s1^1)&_s2, _b2=(_s1^_s2)^1, _bw=_b1|(_b2&_sd), _hi=_h1-_h2-_bw\"");
        lines.Add("set \"_RET=!_hi!:!_lo!\"");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("neg64")) {
        lines.Add(":__ply_neg64");
        lines.Add("call :__ply_sub64 \"0:0\" \"%~1\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("mul64")) {
        lines.Add(":__ply_mul64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h1=%%A, _l1=%%B\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do set /a \"_h2=%%A, _l2=%%B\"");
        lines.Add("set /a \"_a0=_l1&65535, _a1=(_l1>>16)&65535, _b0=_l2&65535, _b1=(_l2>>16)&65535\"");
        lines.Add("set /a \"_p00=_a0*_b0, _p01=_a0*_b1, _p10=_a1*_b0, _p11=_a1*_b1\"");
        lines.Add("set /a \"_m0=(_p00>>16)&65535, _m1=_p01&65535, _m2=_p10&65535, _m=_m0+_m1+_m2\"");
        lines.Add("set /a \"_k1=(_p01>>16)&65535, _k2=(_p10>>16)&65535, _k3=(_m>>16)&65535\"");
        lines.Add("set /a \"_k4=_h1*_l2, _k5=_l1*_h2, _hi=_p11+_k1+_k2+_k3+_k4+_k5, _lo=_l1*_l2\"");
        lines.Add("set \"_RET=!_hi!:!_lo!\"");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("bit64")) {
        lines.Add(":__ply_bit64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do (set /a \"_h1=%%A, _l1=%%B\")");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~3\") do (set /a \"_h2=%%A, _l2=%%B\")");
        lines.Add("if \"%~1\"==\"and\" set /a \"_hi=_h1&_h2, _lo=_l1&_l2\"");
        lines.Add("if \"%~1\"==\"or\" set /a \"_hi=_h1|_h2, _lo=_l1|_l2\"");
        lines.Add("if \"%~1\"==\"xor\" set /a \"_hi=_h1^_h2, _lo=_l1^_l2\"");
        lines.Add("if \"%~1\"==\"not\" set /a \"_hi=~_h1, _lo=~_l1\"");
        lines.Add("set \"_RET=!_hi!:!_lo!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("shl64")) {
        lines.Add(":__ply_shl64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h=%%A, _l=%%B\"");
        lines.Add("set /a \"_c=%~2&63\"");
        lines.Add("if !_c! EQU 0 (set \"_RET=!_h!:!_l!\" & goto :_shl64_d)");
        lines.Add("if !_c! EQU 32 (set \"_RET=!_l!:0\" & goto :_shl64_d)");
        lines.Add("if !_c! GTR 32 (set /a \"_nh=_l<<(_c-32)\" & set \"_RET=!_nh!:0\" & goto :_shl64_d)");
        lines.Add("set /a \"_rc=32-_c, _mk=2147483647>>(_rc-1), _hi1=_h<<_c, _hi2=(_l>>_rc)&_mk, _nh=_hi1|_hi2, _nl=_l<<_c\"");
        lines.Add("set \"_RET=!_nh!:!_nl!\"");
        lines.Add(":_shl64_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("shr64")) {
        lines.Add(":__ply_shr64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h=%%A, _l=%%B\"");
        lines.Add("set /a \"_c=%~2&63, _sb=(_h>>31)&1, _sg=-_sb\"");
        lines.Add("if !_c! EQU 0 (set \"_RET=!_h!:!_l!\" & goto :_shr64_d)");
        lines.Add("if !_c! EQU 32 (set \"_RET=!_sg!:!_h!\" & goto :_shr64_d)");
        lines.Add("if !_c! GTR 32 (set /a \"_sc=_c-32, _hx=_h^_sg, _nl=(_hx>>_sc)^_sg\" & set \"_RET=!_sg!:!_nl!\" & goto :_shr64_d)");
        lines.Add("set /a \"_hx=_h^_sg, _nh=(_hx>>_c)^_sg, _mk=2147483647>>(_c-1), _lo1=(_l>>_c)&_mk, _lo2=_h<<(32-_c), _nl=_lo1|_lo2\"");
        lines.Add("set \"_RET=!_nh!:!_nl!\"");
        lines.Add(":_shr64_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("ushr64")) {
        lines.Add(":__ply_ushr64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do set /a \"_h=%%A, _l=%%B\"");
        lines.Add("set /a \"_c=%~2&63\"");
        lines.Add("if !_c! EQU 0 (set \"_RET=!_h!:!_l!\" & goto :_ushr64_d)");
        lines.Add("if !_c! EQU 32 (set \"_RET=0:!_h!\" & goto :_ushr64_d)");
        lines.Add("if !_c! GTR 32 (set /a \"_sc=_c-32, _mk=2147483647>>(_sc-1), _nl=(_h>>_sc)&_mk\" & set \"_RET=0:!_nl!\" & goto :_ushr64_d)");
        lines.Add("set /a \"_mk=2147483647>>(_c-1), _nh=(_h>>_c)&_mk, _lo1=(_l>>_c)&_mk, _lo2=_h<<(32-_c), _nl=_lo1|_lo2\"");
        lines.Add("set \"_RET=!_nh!:!_nl!\"");
        lines.Add(":_ushr64_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("cmp_i64")) {
        lines.Add(":__ply_cmp_i64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do (set /a \"_h1=%%A, _l1=%%B\")");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do (set /a \"_h2=%%A, _l2=%%B\")");
        lines.Add("set /a \"_s1=(_h1>>31)&1, _s2=(_h2>>31)&1\"");
        lines.Add("if !_s1! EQU !_s2! goto :_ci64_s");
        lines.Add("if !_s1! GTR !_s2! (set \"_RET=-1\") else (set \"_RET=1\")");
        lines.Add("goto :_ci64_d");
        lines.Add(":_ci64_s");
        lines.Add("set /a \"_m1=_h1&2147483647, _m2=_h2&2147483647\"");
        lines.Add("if !_m1! LSS !_m2! (set \"_RET=-1\" & goto :_ci64_d)");
        lines.Add("if !_m1! GTR !_m2! (set \"_RET=1\" & goto :_ci64_d)");
        lines.Add("call :__ply_cmp_u32 \"!_l1!\" \"!_l2!\"");
        lines.Add(":_ci64_d");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("cmp_u64")) {
        lines.Add(":__ply_cmp_u64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~1\") do (set /a \"_h1=%%A, _l1=%%B\")");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"%~2\") do (set /a \"_h2=%%A, _l2=%%B\")");
        lines.Add("call :__ply_cmp_u32 \"!_h1!\" \"!_h2!\"");
        lines.Add("if \"!_RET!\"==\"0\" call :__ply_cmp_u32 \"!_l1!\" \"!_l2!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("udiv64")) {
        lines.Add(":__ply_udiv64");
        lines.Add($"if \"%~2\"==\"0:0\" (call :__ply_trap {this._classIds[IrExceptionClass.DivideByZeroException]} DivideByZero & goto :eof)");
        lines.Add("set \"_u64_a=%~1\"");
        lines.Add("set \"_u64_b=%~2\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"!_u64_a!\") do (set /a \"_u64_ah=%%A, _u64_al=%%B\")");
        lines.Add("set \"_u64_q=0:0\"");
        lines.Add("set \"_u64_r=0:0\"");
        lines.Add("for /l %%I in (63,-1,0) do (");
        lines.Add("  call :__ply_shl64 \"!_u64_r!\" \"1\"");
        lines.Add("  set \"_u64_r=!_RET!\"");
        lines.Add("  if %%I GEQ 32 (set /a \"_bit=(_u64_ah>>(%%I-32))&1\") else (set /a \"_bit=(_u64_al>>%%I)&1\")");
        lines.Add("  if !_bit! EQU 1 (call :__ply_add64 \"!_u64_r!\" \"0:1\" & set \"_u64_r=!_RET!\")");
        lines.Add("  call :__ply_cmp_u64 \"!_u64_r!\" \"!_u64_b!\"");
        lines.Add("  if !_RET! GEQ 0 (");
        lines.Add("    call :__ply_sub64 \"!_u64_r!\" \"!_u64_b!\"");
        lines.Add("    set \"_u64_r=!_RET!\"");
        lines.Add("    call :__ply_shl64 \"0:1\" \"%%I\"");
        lines.Add("    call :__ply_add64 \"!_u64_q!\" \"!_RET!\"");
        lines.Add("    set \"_u64_q=!_RET!\"");
        lines.Add("  )");
        lines.Add(")");
        lines.Add("set \"_RET=!_u64_q!\"");
        lines.Add("set \"_REM=!_u64_r!\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("sdiv64")) {
        lines.Add(":__ply_sdiv64");
        lines.Add($"if \"%~2\"==\"0:0\" (call :__ply_trap {this._classIds[IrExceptionClass.DivideByZeroException]} DivideByZero & goto :eof)");
        lines.Add($"if \"%~1\"==\"-2147483648:0\" if \"%~2\"==\"-1:-1\" (call :__ply_trap {this._classIds[IrExceptionClass.OverflowException]} IntegerOverflow & goto :eof)");
        lines.Add("set \"_s64_a=%~1\"");
        lines.Add("set \"_s64_b=%~2\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"!_s64_a!\") do set /a \"_s64_sa=(%%A>>31)&1\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"!_s64_b!\") do set /a \"_s64_sb=(%%A>>31)&1\"");
        lines.Add("if !_s64_sa! EQU 1 (call :__ply_neg64 \"!_s64_a!\" & set \"_s64_a=!_RET!\")");
        lines.Add("if !_s64_sb! EQU 1 (call :__ply_neg64 \"!_s64_b!\" & set \"_s64_b=!_RET!\")");
        lines.Add("call :__ply_udiv64 \"!_s64_a!\" \"!_s64_b!\"");
        lines.Add("set \"_s64_q=!_RET!\"");
        lines.Add("set \"_s64_r=!_REM!\"");
        lines.Add("set /a \"_s64_nq=_s64_sa^_s64_sb\"");
        lines.Add("if !_s64_nq! EQU 1 (call :__ply_neg64 \"!_s64_q!\" & set \"_s64_q=!_RET!\")");
        lines.Add("if !_s64_sa! EQU 1 (call :__ply_neg64 \"!_s64_r!\" & set \"_s64_r=!_RET!\")");
        lines.Add("set \"_RET=!_s64_q!\"");
        lines.Add("set \"_REM=!_s64_r!\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("fmt_u64")) {
        lines.Add(":__ply_fmt_u64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_v=%~1\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"!_v!\") do (set /a \"_h=%%A, _l=%%B\")");
        lines.Add("if !_h! EQU 0 if !_l! GEQ 0 (set \"_RET=!_l!\" & goto :_fu64_out)");
        lines.Add("set \"_out=\"");
        lines.Add(":_fu64_lp");
        lines.Add("if \"!_v!\"==\"0:0\" goto :_fu64_dn");
        lines.Add("call :__ply_udiv64 \"!_v!\" \"0:10\"");
        lines.Add("set \"_v=!_RET!\"");
        lines.Add("for /f \"tokens=2 delims=:\" %%D in (\"!_REM!\") do set \"_out=%%D!_out!\"");
        lines.Add("goto :_fu64_lp");
        lines.Add(":_fu64_dn");
        lines.Add("if not defined _out set \"_out=0\"");
        lines.Add("set \"_RET=!_out!\"");
        lines.Add(":_fu64_out");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("fmt_i64")) {
        lines.Add(":__ply_fmt_i64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_v=%~1\"");
        lines.Add("for /f \"tokens=1,2 delims=:\" %%A in (\"!_v!\") do (set /a \"_h=%%A, _l=%%B, _sg=(%%A>>31)&1\")");
        lines.Add("if !_h! EQU 0 if !_l! GEQ 0 (set \"_RET=!_l!\" & goto :_fi64_out)");
        lines.Add("if !_h! EQU -1 if !_l! LSS 0 (set \"_RET=!_l!\" & goto :_fi64_out)");
        lines.Add("if !_sg! EQU 0 goto :_fi64_pos");
        lines.Add("call :__ply_neg64 \"!_v!\"");
        lines.Add("call :__ply_fmt_u64 \"!_RET!\"");
        lines.Add("set \"_RET=-!_RET!\"");
        lines.Add("goto :_fi64_out");
        lines.Add(":_fi64_pos");
        lines.Add("call :__ply_fmt_u64 \"!_v!\"");
        lines.Add(":_fi64_out");
        lines.Add("(");
        lines.Add("  endlocal");
        lines.Add("  set \"_RET=%_RET%\"");
        lines.Add(")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("parse_u64")) {
        lines.Add(":__ply_parse_u64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_s=%~1\"");
        lines.Add("set \"_acc=0:0\"");
        lines.Add(":_pu64_lp");
        lines.Add("if not defined _s goto :_pu64_dn");
        lines.Add("set \"_d=!_s:~0,1!\"");
        lines.Add("set \"_s=!_s:~1!\"");
        lines.Add("call :__ply_mul64 \"!_acc!\" \"0:10\"");
        lines.Add("call :__ply_add64 \"!_RET!\" \"0:!_d!\"");
        lines.Add("set \"_acc=!_RET!\"");
        lines.Add("goto :_pu64_lp");
        lines.Add(":_pu64_dn");
        lines.Add("set \"_RET=!_acc!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("parse_i64")) {
        lines.Add(":__ply_parse_i64");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_s=%~1\"");
        lines.Add("set \"_neg=0\"");
        lines.Add("if \"!_s:~0,1!\"==\"-\" (set \"_neg=1\" & set \"_s=!_s:~1!\")");
        lines.Add("call :__ply_parse_u64 \"!_s!\"");
        lines.Add("if \"!_neg!\"==\"1\" call :__ply_neg64 \"!_RET!\"");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }
    }

    private void EmitStringHelpers(List<string> lines) {
      if (this._usedHelpers.Contains("strlen")) {
        lines.Add(":__ply_strlen");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("for %%H in (%~1) do set \"_s=!_S_%%H!\"");
        lines.Add("if not defined _s (set \"_RET=0\" & goto :_sl_out)");
        lines.Add("set /a \"_len=0\"");
        lines.Add("for %%P in (4096 2048 1024 512 256 128 64 32 16 8 4 2 1) do (if not \"!_s:~%%P,1!\"==\"\" (set /a \"_len+=%%P\" & set \"_s=!_s:~%%P!\"))");
        lines.Add("set /a \"_len+=1\"");
        lines.Add("set \"_RET=!_len!\"");
        lines.Add(":_sl_out");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("streq")) {
        lines.Add(":__ply_streq");
        lines.Add("setlocal EnableExtensions EnableDelayedExpansion");
        lines.Add("set \"_h1=%~1\"");
        lines.Add("set \"_h2=%~2\"");
        lines.Add("if \"!_h1!\"==\"!_h2!\" (set \"_RET=1\" & goto :_seq_out)");
        lines.Add($"if \"!_h1!\"==\"{_nullToken}\" (set \"_RET=0\" & goto :_seq_out)");
        lines.Add($"if \"!_h2!\"==\"{_nullToken}\" (set \"_RET=0\" & goto :_seq_out)");
        lines.Add("call :__ply_strlen \"!_h1!\"");
        lines.Add("set \"_l1=!_RET!\"");
        lines.Add("call :__ply_strlen \"!_h2!\"");
        lines.Add("if not \"!_l1!\"==\"!_RET!\" (set \"_RET=0\" & goto :_seq_out)");
        lines.Add("if \"!_l1!\"==\"0\" (set \"_RET=1\" & goto :_seq_out)");
        lines.Add("for %%A in (!_h1!) do set \"_s1=!_S_%%A!\"");
        lines.Add("for %%B in (!_h2!) do set \"_s2=!_S_%%B!\"");
        lines.Add("set \"_n1=!_s1:\"=!\"");
        lines.Add("set \"_n2=!_s2:\"=!\"");
        lines.Add("if \"!_n1!\"==\"!_s1!\" if \"!_n2!\"==\"!_s2!\" (if \"!_s1!\"==\"!_s2!\" (set \"_RET=1\") else (set \"_RET=0\") & goto :_seq_out)");
        lines.Add("set /a \"_lim=_l1-1\"");
        lines.Add("for /l %%I in (0,1,!_lim!) do (");
        lines.Add("  set \"_c1=!_s1:~%%I,1!\"");
        lines.Add("  set \"_c2=!_s2:~%%I,1!\"");
        lines.Add("  set \"_q1=!_c1:\"=!\"");
        lines.Add("  set \"_q2=!_c2:\"=!\"");
        lines.Add("  if not defined _q1 (if defined _q2 (set \"_RET=0\" & goto :_seq_out)) else if not defined _q2 (set \"_RET=0\" & goto :_seq_out) else if not \"!_c1!\"==\"!_c2!\" (set \"_RET=0\" & goto :_seq_out)");
        lines.Add(")");
        lines.Add("set \"_RET=1\"");
        lines.Add(":_seq_out");
        lines.Add("(endlocal & set \"_RET=%_RET%\")");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("substr")) {
        lines.Add(":__ply_substr");
        lines.Add("set \"_sh=%~1\"");
        lines.Add("set /a \"_st=%~2, _sl=%~3\"");
        lines.Add($"if \"!_sh!\"==\"{_nullToken}\" (call :__ply_trap {this._classIds[IrExceptionClass.NullReferenceException]} NullReference & goto :eof)");
        lines.Add("call :__ply_strlen \"!_sh!\"");
        lines.Add("set /a \"_tot=_RET, _end=_st+_sl\"");
        lines.Add($"if !_st! LSS 0 (call :__ply_trap {this._classIds[IrExceptionClass.IndexOutOfRangeException]} IndexOutOfRange & goto :eof)");
        lines.Add($"if !_sl! LSS 0 (call :__ply_trap {this._classIds[IrExceptionClass.IndexOutOfRangeException]} IndexOutOfRange & goto :eof)");
        lines.Add($"if !_end! GTR !_tot! (call :__ply_trap {this._classIds[IrExceptionClass.IndexOutOfRangeException]} IndexOutOfRange & goto :eof)");
        lines.Add("set /a \"_HID+=1\"");
        lines.Add("set \"_nh=s!_HID!\"");
        lines.Add("if !_sl! EQU 0 (set \"_S_!_nh!=\") else (for %%H in (!_sh!) do for %%A in (!_st!) do for %%B in (!_sl!) do set \"_S_!_nh!=!_S_%%H:~%%A,%%B!\")");
        lines.Add("set \"_RET=!_nh!\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("stridx")) {
        lines.Add(":__ply_stridx");
        lines.Add("set \"_ih=%~1\"");
        lines.Add("set \"_in=%~2\"");
        lines.Add("set /a \"_if=%~3\"");
        lines.Add("call :__ply_strlen \"!_ih!\"");
        lines.Add("set /a \"_hl=_RET\"");
        lines.Add("call :__ply_strlen \"!_in!\"");
        lines.Add("set /a \"_nl=_RET\"");
        lines.Add("if !_nl! EQU 0 (set \"_RET=!_if!\" & goto :eof)");
        lines.Add("set /a \"_lim=_hl-_nl\"");
        lines.Add("if !_if! GTR !_lim! (set \"_RET=-1\" & goto :eof)");
        lines.Add("for %%A in (!_ih!) do set \"_hs=!_S_%%A!\"");
        lines.Add("for %%B in (!_in!) do set \"_ns=!_S_%%B!\"");
        lines.Add("for /l %%I in (!_if!,1,!_lim!) do (");
        lines.Add("  for %%L in (!_nl!) do set \"_sub=!_hs:~%%I,%%L!\"");
        lines.Add("  if \"!_sub!\"==\"!_ns!\" (set \"_RET=%%I\" & goto :eof)");
        lines.Add(")");
        lines.Add("set \"_RET=-1\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("strcode")) {
        lines.Add(":__ply_strcode");
        lines.Add("set \"_ch_h=%~1\"");
        lines.Add("set /a \"_ch_i=%~2\"");
        lines.Add("call :__ply_strlen \"!_ch_h!\"");
        lines.Add($"if !_ch_i! LSS 0 (call :__ply_trap {this._classIds[IrExceptionClass.IndexOutOfRangeException]} IndexOutOfRange & goto :eof)");
        lines.Add($"if !_ch_i! GEQ !_RET! (call :__ply_trap {this._classIds[IrExceptionClass.IndexOutOfRangeException]} IndexOutOfRange & goto :eof)");
        lines.Add("for %%H in (!_ch_h!) do for %%I in (!_ch_i!) do set \"_ch_c=!_S_%%H:~%%I,1!\"");
        lines.Add("set \"_RET=0\"");
        lines.Add("set \"_asc= !\"#$%%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~\"");
        lines.Add("for /l %%K in (0,1,94) do (set \"_ac=!_asc:~%%K,1!\" & if \"!_ac!\"==\"!_ch_c!\" (set /a \"_RET=%%K+32\" & goto :eof))");
        lines.Add("goto :eof");
      }
    }

    private void EmitCollectionHelpers(List<string> lines) {
      if (this._usedHelpers.Contains("list_ops")) {
        // Key equality check helper for list contains / indexof.
        lines.Add(":__ply_list_find");
        lines.Add("set \"_lf_id=%~1\"");
        lines.Add("set \"_lf_val=%~2\"");
        lines.Add("set \"_lf_str=%~3\"");
        lines.Add("for %%L in (!_lf_id!) do set /a \"_lf_n=_L_%%L_len-1\"");
        lines.Add("if !_lf_n! LSS 0 (set \"_RET=-1\" & goto :eof)");
        lines.Add("for /l %%I in (0,1,!_lf_n!) do (");
        lines.Add("  for %%L in (!_lf_id!) do set \"_lf_cur=!_L_%%L_%%I!\"");
        lines.Add("  if \"!_lf_str!\"==\"1\" (");
        lines.Add("    call :__ply_streq \"!_lf_cur!\" \"!_lf_val!\"");
        lines.Add("    if \"!_RET!\"==\"1\" (set \"_RET=%%I\" & goto :eof)");
        lines.Add("  ) else (");
        lines.Add("    if \"!_lf_cur!\"==\"!_lf_val!\" (set \"_RET=%%I\" & goto :eof)");
        lines.Add("  )");
        lines.Add(")");
        lines.Add("set \"_RET=-1\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("map_ops") || this._usedHelpers.Contains("set_ops")) {
        // Unified slot-based hash/table lookup: returns slot index or -1.
        lines.Add(":__ply_map_find");
        lines.Add("set \"_mf_id=%~1\"");
        lines.Add("set \"_mf_key=%~2\"");
        lines.Add("set \"_mf_str=%~3\"");
        lines.Add("for %%M in (!_mf_id!) do set /a \"_mf_n=_M_%%M_cap-1\"");
        lines.Add("if !_mf_n! LSS 0 (set \"_RET=-1\" & goto :eof)");
        lines.Add("for /l %%I in (0,1,!_mf_n!) do (");
        lines.Add("  for %%M in (!_mf_id!) do set \"_mf_u=!_M_%%M_u_%%I!\"");
        lines.Add("  if \"!_mf_u!\"==\"1\" (");
        lines.Add("    for %%M in (!_mf_id!) do set \"_mf_k=!_M_%%M_k_%%I!\"");
        lines.Add("    if \"!_mf_str!\"==\"1\" (");
        lines.Add("      call :__ply_streq \"!_mf_k!\" \"!_mf_key!\"");
        lines.Add("      if \"!_RET!\"==\"1\" (set \"_RET=%%I\" & goto :eof)");
        lines.Add("    ) else (");
        lines.Add("      if \"!_mf_k!\"==\"!_mf_key!\" (set \"_RET=%%I\" & goto :eof)");
        lines.Add("    )");
        lines.Add("  )");
        lines.Add(")");
        lines.Add("set \"_RET=-1\"");
        lines.Add("goto :eof");

        // Allocate a new slot respecting MapOrder (0 = Insertion, 1 = SlotReuse).
        lines.Add(":__ply_map_alloc");
        lines.Add("set \"_ma_id=%~1\"");
        lines.Add("for %%M in (!_ma_id!) do (set \"_ma_ord=!_M_%%M_ord!\" & set /a \"_ma_fn=_M_%%M_fn\" & set /a \"_ma_cap=_M_%%M_cap\")");
        lines.Add("if \"!_ma_ord!\"==\"1\" if !_ma_fn! GTR 0 (");
        lines.Add("  set /a \"_ma_fn-=1\"");
        lines.Add("  for %%M in (!_ma_id!) do (set \"_M_%%M_fn=!_ma_fn!\" & for %%F in (!_ma_fn!) do set \"_RET=!_M_%%M_f_%%F!\")");
        lines.Add("  goto :eof");
        lines.Add(")");
        lines.Add("set \"_RET=!_ma_cap!\"");
        lines.Add("set /a \"_ma_cap+=1\"");
        lines.Add("for %%M in (!_ma_id!) do set \"_M_%%M_cap=!_ma_cap!\"");
        lines.Add("goto :eof");

        // Remove a key from map/set, pushing slot to free list if SlotReuse.
        lines.Add(":__ply_map_rem");
        lines.Add("set \"_mr_id=%~1\"");
        lines.Add("call :__ply_map_find \"%~1\" \"%~2\" \"%~3\"");
        lines.Add("if \"!_RET!\"==\"-1\" (set \"_RET=0\" & goto :eof)");
        lines.Add("set \"_mr_sl=!_RET!\"");
        lines.Add("for %%M in (!_mr_id!) do (");
        lines.Add("  for %%S in (!_mr_sl!) do set \"_M_%%M_u_%%S=0\"");
        lines.Add("  set /a \"_M_%%M_cnt-=1\"");
        lines.Add("  if \"!_M_%%M_ord!\"==\"1\" (");
        lines.Add("    set /a \"_fn=_M_%%M_fn\"");
        lines.Add("    for %%F in (!_fn!) do set \"_M_%%M_f_%%F=!_mr_sl!\"");
        lines.Add("    set /a \"_M_%%M_fn+=1\"");
        lines.Add("  )");
        lines.Add(")");
        lines.Add("set \"_RET=1\"");
        lines.Add("goto :eof");
      }
    }

    private void EmitSequenceHelpers(List<string> lines) {
      if (this._usedHelpers.Contains("arrseq")) {
        lines.Add(":__ply_arrseq");
        lines.Add("set /a \"_SP+=1\"");
        lines.Add("for %%S in (!_SP!) do (");
        lines.Add("  for %%Q in (%~1) do set \"_F%%S_arr=!_O_%%Q_a0!\"");
        lines.Add("  for %%A in (!_F%%S_arr!) do set /a \"_F%%S_len=_AL_%%A\"");
        lines.Add("  set \"_F%%S_i=0\"");
        lines.Add(")");
        lines.Add(":_ply_as_lp");
        lines.Add("for %%S in (!_SP!) do (if !_F%%S_i! GEQ !_F%%S_len! goto :_ply_as_dn)");
        lines.Add("for %%S in (!_SP!) do for %%A in (!_F%%S_arr!) do for %%I in (!_F%%S_i!) do set \"_YVAL=!_A_%%A_%%I!\"");
        lines.Add("for %%C in (!_CS!) do call :!_C%%C_cb! \"!_C%%C_sp!\"");
        lines.Add("for %%C in (!_CS!) do set \"_yctl=!_C%%C_ctl!\"");
        lines.Add("if not \"!_yctl!\"==\"0\" goto :_ply_as_dn");
        lines.Add("for %%S in (!_SP!) do set /a \"_F%%S_i+=1\"");
        lines.Add("goto :_ply_as_lp");
        lines.Add(":_ply_as_dn");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
      }

      if (this._usedHelpers.Contains("stablesort")) {
        // Stable insertion sort of i32[] indices using comparator closure.
        lines.Add(":__ply_stablesort");
        lines.Add("set /a \"_SP+=1\"");
        lines.Add("for %%S in (!_SP!) do (set \"_F%%S_a=%~1\" & set \"_F%%S_cmp=%~2\")");
        lines.Add("for %%S in (!_SP!) do for %%A in (!_F%%S_a!) do set /a \"_F%%S_n=_AL_%%A\"");
        lines.Add("for %%S in (!_SP!) do set \"_F%%S_i=1\"");
        lines.Add(":_ss_outer");
        lines.Add("for %%S in (!_SP!) do if !_F%%S_i! GEQ !_F%%S_n! goto :_ss_done");
        lines.Add("for %%S in (!_SP!) do for %%A in (!_F%%S_a!) do for %%I in (!_F%%S_i!) do set \"_F%%S_key=!_A_%%A_%%I!\"");
        lines.Add("for %%S in (!_SP!) do set /a \"_F%%S_j=_F%%S_i-1\"");
        lines.Add(":_ss_inner");
        lines.Add("for %%S in (!_SP!) do if !_F%%S_j! LSS 0 goto :_ss_ins");
        lines.Add("for %%S in (!_SP!) do for %%A in (!_F%%S_a!) do for %%J in (!_F%%S_j!) do set \"_ss_cur=!_A_%%A_%%J!\"");
        lines.Add("for %%S in (!_SP!) do (set \"_ss_c=!_F%%S_cmp!\" & set \"_ss_k=!_F%%S_key!\")");
        lines.Add("for %%C in (!_ss_c!) do (set \"_cfn=!_O_%%C___fn!\" & set \"_ck=!_O_%%C___kind!\" & set \"_cr=!_O_%%C___recv!\")");
        lines.Add("if \"!_ck!\"==\"1\" (call :!_cfn! \"!_ss_c!\" \"!_ss_cur!\" \"!_ss_k!\") else if \"!_ck!\"==\"2\" (call :!_cfn! \"!_cr!\" \"!_ss_cur!\" \"!_ss_k!\") else (call :!_cfn! \"!_ss_cur!\" \"!_ss_k!\")");
        lines.Add("if \"!_EX_ACTIVE!\"==\"1\" goto :_ss_done");
        lines.Add("if !_RET! LEQ 0 goto :_ss_ins");
        lines.Add("for %%S in (!_SP!) do (set /a \"_j1=_F%%S_j+1\" & for %%A in (!_F%%S_a!) do for %%J in (!_F%%S_j!) do for %%K in (!_j1!) do set \"_A_%%A_%%K=!_A_%%A_%%J!\" & set /a \"_F%%S_j-=1\")");
        lines.Add("goto :_ss_inner");
        lines.Add(":_ss_ins");
        lines.Add("for %%S in (!_SP!) do (set /a \"_j1=_F%%S_j+1\" & for %%A in (!_F%%S_a!) do for %%K in (!_j1!) do set \"_A_%%A_%%K=!_F%%S_key!\" & set /a \"_F%%S_i+=1\")");
        lines.Add("goto :_ss_outer");
        lines.Add(":_ss_done");
        lines.Add("set /a \"_SP-=1\"");
        lines.Add("goto :eof");
      }
    }

  }

}
