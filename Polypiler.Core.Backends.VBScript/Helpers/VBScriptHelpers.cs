using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.VBScript;

/// <summary>
/// Private VBScript 5.8 runtime helpers emitted into the generated script only when referenced.
/// Every identifier begins with an ASCII letter (<c>ply_*</c>) as VBScript 5.8 requires.
/// </summary>
internal static class VBScriptHelpers {

  public const string I8 = "ply_i8";
  public const string U8 = "ply_u8";
  public const string I16 = "ply_i16";
  public const string U16 = "ply_u16";
  public const string I32 = "ply_i32";
  public const string U32 = "ply_u32";
  public const string Mul32 = "ply_mul32";
  public const string And32 = "ply_and32";
  public const string Or32 = "ply_or32";
  public const string Xor32 = "ply_xor32";
  public const string Not32 = "ply_not32";
  public const string Shl32 = "ply_shl32";
  public const string Shr32 = "ply_shr32";
  public const string UShr32 = "ply_ushr32";
  public const string Div32 = "ply_div32";
  public const string Rem32 = "ply_rem32";
  public const string UDiv32 = "ply_udiv32";
  public const string URem32 = "ply_urem32";
  public const string I64 = "ply_i64";
  public const string U64 = "ply_u64";
  public const string Div64 = "ply_div64";
  public const string Rem64 = "ply_rem64";
  public const string And64 = "ply_and64";
  public const string Or64 = "ply_or64";
  public const string Xor64 = "ply_xor64";
  public const string Not64 = "ply_not64";
  public const string Shl64 = "ply_shl64";
  public const string Shr64 = "ply_shr64";
  public const string UShr64 = "ply_ushr64";
  public const string CheckRange = "ply_chk_range";
  public const string FloatRem = "ply_frem";
  public const string FloatToInt = "ply_f2i";
  public const string Min = "ply_min";
  public const string Max = "ply_max";
  public const string AbsSigned = "ply_abs_s";
  public const string RoundAway = "ply_round_away";
  public const string RoundHalfUp = "ply_round_hu";
  public const string CopySign = "ply_copysign";
  public const string Rotl32 = "ply_rotl32";
  public const string Rotr32 = "ply_rotr32";
  public const string PopCnt32 = "ply_popcnt32";
  public const string Clz32 = "ply_clz32";
  public const string Ctz32 = "ply_ctz32";
  public const string NewArr = "ply_new_arr";
  public const string CheckIdx = "ply_chk_idx";
  public const string ArrCopy = "ply_arr_copy";
  public const string ArrFill = "ply_arr_fill";
  public const string ArrEq = "ply_arr_eq";
  public const string LoadEndian = "ply_load_e";
  public const string StoreEndian = "ply_store_e";
  public const string StrUnit = "ply_str_unit";
  public const string StrFromUnits = "ply_str_from_u";
  public const string StrToUnits = "ply_str_to_u";
  public const string StrRepeat = "ply_str_rep";
  public const string StrSub = "ply_str_sub";
  public const string StrIndexOf = "ply_str_idx";
  public const string StrLastIndexOf = "ply_str_lidx";
  public const string StrStarts = "ply_str_sw";
  public const string StrEnds = "ply_str_ew";
  public const string StrContains = "ply_str_has";
  public const string StrReplace = "ply_str_repl";
  public const string StrReplaceFirst = "ply_str_repl1";
  public const string StrSplit = "ply_str_split";
  public const string StrTrim = "ply_str_trim";
  public const string StrPad = "ply_str_pad";
  public const string CheckNull = "ply_chk_null";
  public const string Unwrap = "ply_unwrap";
  public const string UnwrapObj = "ply_unwrap_o";
  public const string ErrSupport = "ply_err_support";
  public const string WriteLine = "ply_write_line";
  public const string Cell = "ply_cell";
  public const string CollectionSupport = "ply_collections";
  public const string ArrSeq = "ply_arr_seq";
  public const string TaskSupport = "ply_task_support";
  public const string SpanSupport = "ply_span_support";
  public const string PtrSupport = "ply_ptr_support";
  public const string Pext32 = "ply_pext_u32";
  public const string Pdep32 = "ply_pdep_u32";
  public const string Pext64 = "ply_pext_u64";
  public const string Pdep64 = "ply_pdep_u64";

  private static readonly Dictionary<string, string[]> _dependencies = new(StringComparer.Ordinal) {
    [Mul32] = [U32, I32],
    [And32] = [I32],
    [Or32] = [I32],
    [Xor32] = [I32],
    [Not32] = [I32],
    [Shl32] = [I32, U32],
    [Shr32] = [I32],
    [UShr32] = [I32, U32],
    [UDiv32] = [U32],
    [URem32] = [U32],
    [And64] = [U32, I32],
    [Or64] = [U32, I32],
    [Xor64] = [U32, I32],
    [Not64] = [U32, I32],
    [Shl64] = [I32, U64],
    [Shr64] = [I32],
    [UShr64] = [I32, U64],
    [Rotl32] = [I32, U32, Shl32, UShr32, Or32],
    [Rotr32] = [I32, U32, Shl32, UShr32, Or32],
    [PopCnt32] = [U32],
    [Clz32] = [U32],
    [Ctz32] = [U32],
    [Pext32] = [U32],
    [Pdep32] = [U32],
    [Pext64] = [U32, U64],
    [Pdep64] = [U32, U64],
    [LoadEndian] = [CheckIdx, I16, I32],
    [StoreEndian] = [CheckIdx, U32],
    [CollectionSupport] = [CheckNull],
    [ArrSeq] = [CollectionSupport],
    [TaskSupport] = [ErrSupport],
    [SpanSupport] = [I8, U8, I16, U16, I32, U32, I64, U64, NewArr],
    [PtrSupport] = [SpanSupport]
  };

  private static readonly string[] _order = [
    I8, U8, I16, U16, I32, U32, Mul32, And32, Or32, Xor32, Not32, Shl32, Shr32, UShr32,
    Div32, Rem32, UDiv32, URem32,
    I64, U64, Div64, Rem64, And64, Or64, Xor64, Not64, Shl64, Shr64, UShr64,
    CheckRange, FloatRem, FloatToInt, Min, Max, AbsSigned, RoundAway, RoundHalfUp, CopySign,
    Rotl32, Rotr32, PopCnt32, Clz32, Ctz32, Pext32, Pdep32, Pext64, Pdep64,
    NewArr, CheckIdx, ArrCopy, ArrFill, ArrEq, LoadEndian, StoreEndian,
    StrUnit, StrFromUnits, StrToUnits, StrRepeat, StrSub, StrIndexOf, StrLastIndexOf,
    StrStarts, StrEnds, StrContains, StrReplace, StrReplaceFirst, StrSplit, StrTrim, StrPad,
    CheckNull, Unwrap, UnwrapObj, ErrSupport, WriteLine,
    Cell, CollectionSupport, ArrSeq, TaskSupport, SpanSupport, PtrSupport
  ];

  private static readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal) {
    [I8] = """
      Function ply_i8(ByVal x)
        Dim m
        m = CDbl(x)
        m = m - Int(m / 256.0) * 256.0
        If m >= 128.0 Then m = m - 256.0
        ply_i8 = CLng(m)
      End Function
      """,
    [U8] = """
      Function ply_u8(ByVal x)
        Dim m
        m = CDbl(x)
        ply_u8 = CLng(m - Int(m / 256.0) * 256.0)
      End Function
      """,
    [I16] = """
      Function ply_i16(ByVal x)
        Dim m
        m = CDbl(x)
        m = m - Int(m / 65536.0) * 65536.0
        If m >= 32768.0 Then m = m - 65536.0
        ply_i16 = CLng(m)
      End Function
      """,
    [U16] = """
      Function ply_u16(ByVal x)
        Dim m
        m = CDbl(x)
        ply_u16 = CLng(m - Int(m / 65536.0) * 65536.0)
      End Function
      """,
    [I32] = """
      Function ply_i32(ByVal x)
        Dim m
        m = CDbl(x)
        m = m - Int(m / 4294967296.0) * 4294967296.0
        If m >= 2147483648.0 Then m = m - 4294967296.0
        ply_i32 = CLng(m)
      End Function
      """,
    [U32] = """
      Function ply_u32(ByVal x)
        Dim m
        m = CDbl(x)
        ply_u32 = m - Int(m / 4294967296.0) * 4294967296.0
      End Function
      """,
    [Mul32] = """
      Function ply_mul32(ByVal a, ByVal b)
        Dim ua, ub, al, ah, bl, bh, lo, md
        ua = ply_u32(a)
        ub = ply_u32(b)
        al = ua - Int(ua / 65536.0) * 65536.0
        ah = Int(ua / 65536.0)
        bl = ub - Int(ub / 65536.0) * 65536.0
        bh = Int(ub / 65536.0)
        lo = al * bl
        md = ah * bl + al * bh
        md = md - Int(md / 65536.0) * 65536.0
        ply_mul32 = ply_i32(lo + md * 65536.0)
      End Function
      """,
    [And32] = """
      Function ply_and32(ByVal a, ByVal b)
        ply_and32 = ply_i32(a) And ply_i32(b)
      End Function
      """,
    [Or32] = """
      Function ply_or32(ByVal a, ByVal b)
        ply_or32 = ply_i32(a) Or ply_i32(b)
      End Function
      """,
    [Xor32] = """
      Function ply_xor32(ByVal a, ByVal b)
        ply_xor32 = ply_i32(a) Xor ply_i32(b)
      End Function
      """,
    [Not32] = """
      Function ply_not32(ByVal a)
        ply_not32 = Not ply_i32(a)
      End Function
      """,
    [Shl32] = """
      Function ply_shl32(ByVal a, ByVal n)
        Dim s, p, u
        s = ply_i32(n) And 31
        If s = 0 Then
          ply_shl32 = ply_i32(a)
          Exit Function
        End If
        p = 2.0 ^ (32 - s)
        u = ply_u32(a)
        u = u - Int(u / p) * p
        ply_shl32 = ply_i32(u * (2.0 ^ s))
      End Function
      """,
    [Shr32] = """
      Function ply_shr32(ByVal a, ByVal n)
        Dim s
        s = ply_i32(n) And 31
        If s = 0 Then
          ply_shr32 = ply_i32(a)
          Exit Function
        End If
        ply_shr32 = CLng(Int(CDbl(ply_i32(a)) / (2.0 ^ s)))
      End Function
      """,
    [UShr32] = """
      Function ply_ushr32(ByVal a, ByVal n)
        Dim s
        s = ply_i32(n) And 31
        If s = 0 Then
          ply_ushr32 = ply_u32(a)
          Exit Function
        End If
        ply_ushr32 = Int(ply_u32(a) / (2.0 ^ s))
      End Function
      """,
    [Div32] = """
      Function ply_div32(ByVal a, ByVal b)
        If b = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        If CDbl(a) = -2147483648.0 And b = -1 Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        ply_div32 = CLng(Fix(CDbl(a) / CDbl(b)))
      End Function
      """,
    [Rem32] = """
      Function ply_rem32(ByVal a, ByVal b)
        If b = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        If CDbl(a) = -2147483648.0 And b = -1 Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        ply_rem32 = CLng(CDbl(a) - Fix(CDbl(a) / CDbl(b)) * CDbl(b))
      End Function
      """,
    [UDiv32] = """
      Function ply_udiv32(ByVal a, ByVal b)
        Dim ua, ub
        ua = ply_u32(a)
        ub = ply_u32(b)
        If ub = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        ply_udiv32 = Fix(ua / ub)
      End Function
      """,
    [URem32] = """
      Function ply_urem32(ByVal a, ByVal b)
        Dim ua, ub
        ua = ply_u32(a)
        ub = ply_u32(b)
        If ub = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        ply_urem32 = ua - Fix(ua / ub) * ub
      End Function
      """,
    [I64] = """
      Function ply_i64(ByVal x)
        Dim m
        m = Fix(CDbl(x))
        If m >= 9223372036854775808.0 Then m = m - 18446744073709551616.0
        If m < -9223372036854775808.0 Then m = m + 18446744073709551616.0
        ply_i64 = m
      End Function
      """,
    [U64] = """
      Function ply_u64(ByVal x)
        Dim m
        m = Fix(CDbl(x))
        If m < 0 Then m = m + 18446744073709551616.0
        If m >= 18446744073709551616.0 Then m = m - 18446744073709551616.0
        ply_u64 = m
      End Function
      """,
    [Div64] = """
      Function ply_div64(ByVal a, ByVal b)
        If CDbl(b) = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        ply_div64 = Fix(CDbl(a) / CDbl(b))
      End Function
      """,
    [Rem64] = """
      Function ply_rem64(ByVal a, ByVal b)
        If CDbl(b) = 0 Then Err.Raise vbObjectError + 1001, "DivideByZeroException", "Attempted to divide by zero."
        ply_rem64 = CDbl(a) - Fix(CDbl(a) / CDbl(b)) * CDbl(b)
      End Function
      """,
    [And64] = """
      Function ply_and64(ByVal a, ByVal b)
        Dim al, ah, bl, bh, rl, rh
        al = ply_u32(a)
        ah = ply_i32(Int(CDbl(a) / 4294967296.0))
        bl = ply_u32(b)
        bh = ply_i32(Int(CDbl(b) / 4294967296.0))
        rl = ply_u32(ply_i32(al) And ply_i32(bl))
        rh = CDbl(ah And bh)
        ply_and64 = rh * 4294967296.0 + rl
      End Function
      """,
    [Or64] = """
      Function ply_or64(ByVal a, ByVal b)
        Dim al, ah, bl, bh, rl, rh
        al = ply_u32(a)
        ah = ply_i32(Int(CDbl(a) / 4294967296.0))
        bl = ply_u32(b)
        bh = ply_i32(Int(CDbl(b) / 4294967296.0))
        rl = ply_u32(ply_i32(al) Or ply_i32(bl))
        rh = CDbl(ah Or bh)
        ply_or64 = rh * 4294967296.0 + rl
      End Function
      """,
    [Xor64] = """
      Function ply_xor64(ByVal a, ByVal b)
        Dim al, ah, bl, bh, rl, rh
        al = ply_u32(a)
        ah = ply_i32(Int(CDbl(a) / 4294967296.0))
        bl = ply_u32(b)
        bh = ply_i32(Int(CDbl(b) / 4294967296.0))
        rl = ply_u32(ply_i32(al) Xor ply_i32(bl))
        rh = CDbl(ah Xor bh)
        ply_xor64 = rh * 4294967296.0 + rl
      End Function
      """,
    [Not64] = """
      Function ply_not64(ByVal a)
        Dim al, ah, rl, rh
        al = ply_u32(a)
        ah = ply_i32(Int(CDbl(a) / 4294967296.0))
        rl = ply_u32(Not ply_i32(al))
        rh = CDbl(Not ah)
        ply_not64 = rh * 4294967296.0 + rl
      End Function
      """,
    [Shl64] = """
      Function ply_shl64(ByVal a, ByVal n)
        Dim s
        s = ply_i32(n) And 63
        ply_shl64 = ply_u64(CDbl(a) * (2.0 ^ s))
      End Function
      """,
    [Shr64] = """
      Function ply_shr64(ByVal a, ByVal n)
        Dim s
        s = ply_i32(n) And 63
        ply_shr64 = Int(CDbl(a) / (2.0 ^ s))
      End Function
      """,
    [UShr64] = """
      Function ply_ushr64(ByVal a, ByVal n)
        Dim s
        s = ply_i32(n) And 63
        ply_ushr64 = Int(ply_u64(a) / (2.0 ^ s))
      End Function
      """,
    [CheckRange] = """
      Function ply_chk_range(ByVal v, ByVal lo, ByVal hi)
        Dim d
        d = CDbl(v)
        If d < lo Or d > hi Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        ply_chk_range = d
      End Function
      """,
    [FloatRem] = """
      Function ply_frem(ByVal a, ByVal b)
        Dim da, db
        da = CDbl(a)
        db = CDbl(b)
        ply_frem = da - Fix(da / db) * db
      End Function
      """,
    [FloatToInt] = """
      Function ply_f2i(ByVal v, ByVal lo, ByVal hi, ByVal doTrap)
        Dim d
        d = Fix(CDbl(v))
        If d < lo Then
          If doTrap Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
          ply_f2i = lo
          Exit Function
        End If
        If d > hi Then
          If doTrap Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
          ply_f2i = hi
          Exit Function
        End If
        ply_f2i = d
      End Function
      """,
    [Min] = """
      Function ply_min(ByVal a, ByVal b)
        If a < b Then ply_min = a Else ply_min = b
      End Function
      """,
    [Max] = """
      Function ply_max(ByVal a, ByVal b)
        If a > b Then ply_max = a Else ply_max = b
      End Function
      """,
    [AbsSigned] = """
      Function ply_abs_s(ByVal x, ByVal minVal)
        If CDbl(x) = minVal Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        ply_abs_s = Abs(x)
      End Function
      """,
    [RoundAway] = """
      Function ply_round_away(ByVal x)
        Dim d
        d = CDbl(x)
        If d >= 0 Then ply_round_away = Int(d + 0.5) Else ply_round_away = -Int(-d + 0.5)
      End Function
      """,
    [RoundHalfUp] = """
      Function ply_round_hu(ByVal x)
        ply_round_hu = Int(CDbl(x) + 0.5)
      End Function
      """,
    [CopySign] = """
      Function ply_copysign(ByVal x, ByVal y)
        If CDbl(y) < 0 Then ply_copysign = -Abs(CDbl(x)) Else ply_copysign = Abs(CDbl(x))
      End Function
      """,
    [Rotl32] = """
      Function ply_rotl32(ByVal x, ByVal n, ByVal isSigned)
        Dim s, r
        s = ply_i32(n) And 31
        If s = 0 Then
          If isSigned Then ply_rotl32 = ply_i32(x) Else ply_rotl32 = ply_u32(x)
          Exit Function
        End If
        r = ply_or32(ply_shl32(x, s), ply_ushr32(x, 32 - s))
        If isSigned Then ply_rotl32 = r Else ply_rotl32 = ply_u32(r)
      End Function
      """,
    [Rotr32] = """
      Function ply_rotr32(ByVal x, ByVal n, ByVal isSigned)
        Dim s, r
        s = ply_i32(n) And 31
        If s = 0 Then
          If isSigned Then ply_rotr32 = ply_i32(x) Else ply_rotr32 = ply_u32(x)
          Exit Function
        End If
        r = ply_or32(ply_ushr32(x, s), ply_shl32(x, 32 - s))
        If isSigned Then ply_rotr32 = r Else ply_rotr32 = ply_u32(r)
      End Function
      """,
    [PopCnt32] = """
      Function ply_popcnt32(ByVal x)
        Dim u, c
        u = ply_u32(x)
        c = 0
        Do While u > 0
          If u - Int(u / 2.0) * 2.0 = 1 Then c = c + 1
          u = Int(u / 2.0)
        Loop
        ply_popcnt32 = CLng(c)
      End Function
      """,
    [Clz32] = """
      Function ply_clz32(ByVal x, ByVal bits)
        Dim u, c, top
        u = ply_u32(x)
        If bits < 32 Then u = u - Int(u / (2.0 ^ bits)) * (2.0 ^ bits)
        If u = 0 Then
          ply_clz32 = CLng(bits)
          Exit Function
        End If
        c = 0
        top = 2.0 ^ (bits - 1)
        Do While u < top
          c = c + 1
          top = top / 2.0
        Loop
        ply_clz32 = CLng(c)
      End Function
      """,
    [Ctz32] = """
      Function ply_ctz32(ByVal x, ByVal bits)
        Dim u, c
        u = ply_u32(x)
        If bits < 32 Then u = u - Int(u / (2.0 ^ bits)) * (2.0 ^ bits)
        If u = 0 Then
          ply_ctz32 = CLng(bits)
          Exit Function
        End If
        c = 0
        Do While u - Int(u / 2.0) * 2.0 = 0
          c = c + 1
          u = Int(u / 2.0)
        Loop
        ply_ctz32 = CLng(c)
      End Function
      """,
    [NewArr] = """
      Function ply_new_arr(ByVal n, ByVal defVal)
        Dim i, r()
        If n < 0 Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        If n = 0 Then
          ply_new_arr = Array()
          Exit Function
        End If
        ReDim r(n - 1)
        For i = 0 To n - 1
          r(i) = defVal
        Next
        ply_new_arr = r
      End Function
      """,
    [CheckIdx] = """
      Sub ply_chk_idx(ByRef a, ByVal i)
        If IsNull(a) Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If i < 0 Or i > UBound(a) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
      End Sub
      """,
    [ArrCopy] = """
      Sub ply_arr_copy(ByRef src, ByVal si, ByRef dst, ByVal di, ByVal n)
        Dim i, buf()
        If n < 0 Or si < 0 Or di < 0 Or si + n > UBound(src) + 1 Or di + n > UBound(dst) + 1 Then
          Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        End If
        If n = 0 Then Exit Sub
        ReDim buf(n - 1)
        For i = 0 To n - 1
          buf(i) = src(si + i)
        Next
        For i = 0 To n - 1
          dst(di + i) = buf(i)
        Next
      End Sub
      """,
    [ArrFill] = """
      Sub ply_arr_fill(ByRef a, ByVal v)
        Dim i
        For i = 0 To UBound(a)
          a(i) = v
        Next
      End Sub
      """,
    [LoadEndian] = """
      Function ply_load_e(ByRef buf, ByVal idx, ByVal width, ByVal isLittle, ByVal isSigned)
        Dim i, bi, acc
        If idx < 0 Or idx + width > UBound(buf) + 1 Then
          Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        End If
        acc = 0.0
        For i = 0 To width - 1
          If isLittle Then bi = idx + i Else bi = idx + width - 1 - i
          acc = acc + CDbl(buf(bi)) * (256.0 ^ i)
        Next
        If isSigned Then
          If width = 2 Then ply_load_e = ply_i16(acc) Else ply_load_e = ply_i32(acc)
        Else
          ply_load_e = acc
        End If
      End Function
      """,
    [StoreEndian] = """
      Sub ply_store_e(ByRef buf, ByVal idx, ByVal val, ByVal width, ByVal isLittle)
        Dim i, bi, u
        If idx < 0 Or idx + width > UBound(buf) + 1 Then
          Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        End If
        u = ply_u32(val)
        For i = 0 To width - 1
          If isLittle Then bi = idx + i Else bi = idx + width - 1 - i
          buf(bi) = CLng(u - Int(u / 256.0) * 256.0)
          u = Int(u / 256.0)
        Next
      End Sub
      """,
    [StrUnit] = """
      Function ply_str_unit(ByVal s, ByVal i)
        If i < 0 Or i >= Len(s) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        ply_str_unit = CLng(AscW(Mid(s, i + 1, 1)) And &HFFFF&)
      End Function
      """,
    [StrFromUnits] = """
      Function ply_str_from_u(ByRef arr, ByVal st, ByVal n)
        Dim i, res
        If st < 0 Or n < 0 Or st + n > UBound(arr) + 1 Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        res = ""
        For i = st To st + n - 1
          res = res & ChrW(arr(i))
        Next
        ply_str_from_u = res
      End Function
      """,
    [StrToUnits] = """
      Function ply_str_to_u(ByVal s)
        Dim n, i, r()
        n = Len(s)
        If n = 0 Then
          ply_str_to_u = Array()
          Exit Function
        End If
        ReDim r(n - 1)
        For i = 0 To n - 1
          r(i) = CLng(AscW(Mid(s, i + 1, 1)) And &HFFFF&)
        Next
        ply_str_to_u = r
      End Function
      """,
    [StrRepeat] = """
      Function ply_str_rep(ByVal s, ByVal n)
        Dim i, res
        If n < 0 Then Err.Raise vbObjectError + 1002, "OverflowException", "Arithmetic operation resulted in an overflow."
        res = ""
        For i = 1 To n
          res = res & s
        Next
        ply_str_rep = res
      End Function
      """,
    [StrSub] = """
      Function ply_str_sub(ByVal s, ByVal st, ByVal n)
        If st < 0 Or n < 0 Or st + n > Len(s) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        ply_str_sub = Mid(s, st + 1, n)
      End Function
      """,
    [StrIndexOf] = """
      Function ply_str_idx(ByVal s, ByVal needle, ByVal fromIdx)
        Dim p
        If fromIdx < 0 Or fromIdx > Len(s) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        If Len(needle) = 0 Then
          ply_str_idx = CLng(fromIdx)
          Exit Function
        End If
        p = InStr(fromIdx + 1, s, needle, 0)
        ply_str_idx = CLng(p - 1)
      End Function
      """,
    [StrLastIndexOf] = """
      Function ply_str_lidx(ByVal s, ByVal needle, ByVal fromIdx)
        Dim nl, st
        If fromIdx < 0 Or fromIdx > Len(s) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        nl = Len(needle)
        If nl = 0 Then
          ply_str_lidx = CLng(fromIdx)
          Exit Function
        End If
        st = fromIdx
        If st > Len(s) - nl Then st = Len(s) - nl
        Do While st >= 0
          If StrComp(Mid(s, st + 1, nl), needle, 0) = 0 Then
            ply_str_lidx = CLng(st)
            Exit Function
          End If
          st = st - 1
        Loop
        ply_str_lidx = -1
      End Function
      """,
    [StrStarts] = """
      Function ply_str_sw(ByVal s, ByVal needle)
        If Len(needle) > Len(s) Then
          ply_str_sw = False
          Exit Function
        End If
        ply_str_sw = (StrComp(Left(s, Len(needle)), needle, 0) = 0)
      End Function
      """,
    [StrEnds] = """
      Function ply_str_ew(ByVal s, ByVal needle)
        If Len(needle) > Len(s) Then
          ply_str_ew = False
          Exit Function
        End If
        ply_str_ew = (StrComp(Right(s, Len(needle)), needle, 0) = 0)
      End Function
      """,
    [StrContains] = """
      Function ply_str_has(ByVal s, ByVal needle)
        If Len(needle) = 0 Then
          ply_str_has = True
          Exit Function
        End If
        ply_str_has = (InStr(1, s, needle, 0) > 0)
      End Function
      """,
    [StrReplace] = """
      Function ply_str_repl(ByVal s, ByVal oldVal, ByVal newVal)
        If Len(oldVal) = 0 Then Err.Raise vbObjectError + 1005, "InvalidOperationException", "Empty search string."
        ply_str_repl = Replace(s, oldVal, newVal, 1, -1, 0)
      End Function
      """,
    [StrReplaceFirst] = """
      Function ply_str_repl1(ByVal s, ByVal oldVal, ByVal newVal)
        Dim p
        If Len(oldVal) = 0 Then
          ply_str_repl1 = newVal & s
          Exit Function
        End If
        p = InStr(1, s, oldVal, 0)
        If p = 0 Then
          ply_str_repl1 = s
          Exit Function
        End If
        ply_str_repl1 = Left(s, p - 1) & newVal & Mid(s, p + Len(oldVal))
      End Function
      """,
    [StrSplit] = """
      Function ply_str_split(ByVal s, ByVal sep, ByVal lim)
        Dim res, n, i, r()
        If lim = 0 Then
          ply_str_split = Array()
          Exit Function
        End If
        If Len(sep) = 0 Then
          n = Len(s)
          If lim > 0 And lim < n Then n = lim
          If n = 0 Then
            ply_str_split = Array()
            Exit Function
          End If
          ReDim r(n - 1)
          For i = 0 To n - 1
            r(i) = Mid(s, i + 1, 1)
          Next
          ply_str_split = r
          Exit Function
        End If
        If Len(s) = 0 Then
          ply_str_split = Array("")
          Exit Function
        End If
        res = Split(s, sep, -1, 0)
        If lim > 0 And lim < UBound(res) + 1 Then
          ReDim Preserve res(lim - 1)
        End If
        ply_str_split = res
      End Function
      """,
    [StrTrim] = """
      Function ply_str_trim(ByVal s, ByVal mode)
        Dim st, en, c
        st = 1
        en = Len(s)
        If mode = 0 Or mode = 1 Then
          Do While st <= en
            c = AscW(Mid(s, st, 1)) And &HFFFF&
            If c = 32 Or c = 9 Or c = 10 Or c = 11 Or c = 12 Or c = 13 Or c = 160 Or c = 133 Or c = 65279 Then
              st = st + 1
            Else
              Exit Do
            End If
          Loop
        End If
        If mode = 0 Or mode = 2 Then
          Do While en >= st
            c = AscW(Mid(s, en, 1)) And &HFFFF&
            If c = 32 Or c = 9 Or c = 10 Or c = 11 Or c = 12 Or c = 13 Or c = 160 Or c = 133 Or c = 65279 Then
              en = en - 1
            Else
              Exit Do
            End If
          Loop
        End If
        If en < st Then ply_str_trim = "" Else ply_str_trim = Mid(s, st, en - st + 1)
      End Function
      """,
    [StrPad] = """
      Function ply_str_pad(ByVal s, ByVal width, ByVal fill, ByVal atStart)
        Dim need, p
        need = width - Len(s)
        If need <= 0 Or Len(fill) = 0 Then
          ply_str_pad = s
          Exit Function
        End If
        p = ""
        Do While Len(p) < need
          p = p & fill
        Loop
        p = Left(p, need)
        If atStart Then ply_str_pad = p & s Else ply_str_pad = s & p
      End Function
      """,
    [CheckNull] = """
      Function ply_chk_null(ByVal o)
        If o Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        Set ply_chk_null = o
      End Function
      """,
    [Unwrap] = """
      Function ply_unwrap(ByVal v)
        If IsNull(v) Then Err.Raise vbObjectError + 1005, "InvalidOperationException", "Operation is not valid due to the current state of the object."
        ply_unwrap = v
      End Function
      """,
    [UnwrapObj] = """
      Function ply_unwrap_o(ByVal v)
        If v Is Nothing Then Err.Raise vbObjectError + 1005, "InvalidOperationException", "Operation is not valid due to the current state of the object."
        Set ply_unwrap_o = v
      End Function
      """,
    [ArrEq] = """
      Function ply_arr_eq(ByVal a, ByVal b)
        Dim i
        If IsNull(a) And IsNull(b) Then
          ply_arr_eq = True
          Exit Function
        End If
        If IsNull(a) Or IsNull(b) Then
          ply_arr_eq = False
          Exit Function
        End If
        If UBound(a) <> UBound(b) Then
          ply_arr_eq = False
          Exit Function
        End If
        For i = 0 To UBound(a)
          If a(i) <> b(i) Then
            ply_arr_eq = False
            Exit Function
          End If
        Next
        ply_arr_eq = True
      End Function
      """,
    [ErrSupport] = """
      Dim ply_last_err
      Set ply_last_err = Nothing

      Class ply_Exception
        Public ply_kind
        Public Message
        Public InnerException
        Public Function ply_is(ByVal t)
          If t = "Exception" Or t = ply_kind Then
            ply_is = True
            Exit Function
          End If
          If t = "ArithmeticException" And (ply_kind = "DivideByZeroException" Or ply_kind = "OverflowException") Then
            ply_is = True
            Exit Function
          End If
          If t = "ArgumentException" And ply_kind = "ArgumentOutOfRangeException" Then
            ply_is = True
            Exit Function
          End If
          ply_is = False
        End Function
      End Class

      Function ply_make_err(ByVal kind, ByVal msg, ByVal inner)
        Dim e
        Set e = New ply_Exception
        e.ply_kind = kind
        e.Message = msg
        Set e.InnerException = inner
        Set ply_make_err = e
      End Function

      Sub ply_throw(ByVal exObj)
        If exObj Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        Set ply_last_err = exObj
        Err.Raise vbObjectError + 1099, exObj.ply_kind, exObj.Message
      End Sub

      Function ply_capture_err(ByVal num, ByVal src, ByVal desc)
        Dim e, k
        If num = vbObjectError + 1099 And Not (ply_last_err Is Nothing) Then
          Set e = ply_last_err
          Set ply_last_err = Nothing
          Set ply_capture_err = e
          Exit Function
        End If
        If num = 11 Then
          k = "DivideByZeroException"
        ElseIf num = 6 Then
          k = "OverflowException"
        ElseIf num = 9 Then
          k = "IndexOutOfRangeException"
        ElseIf Len(src) > 0 Then
          k = src
        Else
          k = "Exception"
        End If
        Set ply_capture_err = ply_make_err(k, desc, Nothing)
      End Function
      """,
    [WriteLine] = """
      Sub ply_write_line(ByVal s)
        If IsNull(s) Or IsEmpty(s) Then WScript.Echo "" Else WScript.Echo CStr(s)
      End Sub
      """,
    [Pext32] = """
      Function ply_pext_u32(ByVal val, ByVal mask, ByVal bits)
        Dim v, m, res, p, b, outBit
        v = ply_u32(val)
        m = ply_u32(mask)
        If bits < 32 Then
          p = 2.0 ^ bits
          v = v - Int(v / p) * p
          m = m - Int(m / p) * p
        End If
        res = 0.0
        outBit = 1.0
        For b = 0 To bits - 1
          If m - Int(m / 2.0) * 2.0 = 1 Then
            If v - Int(v / 2.0) * 2.0 = 1 Then res = res + outBit
            outBit = outBit * 2.0
          End If
          v = Int(v / 2.0)
          m = Int(m / 2.0)
        Next
        ply_pext_u32 = res
      End Function
      """,
    [Pdep32] = """
      Function ply_pdep_u32(ByVal val, ByVal mask, ByVal bits)
        Dim v, m, res, p, b, posBit
        v = ply_u32(val)
        m = ply_u32(mask)
        If bits < 32 Then
          p = 2.0 ^ bits
          v = v - Int(v / p) * p
          m = m - Int(m / p) * p
        End If
        res = 0.0
        posBit = 1.0
        For b = 0 To bits - 1
          If m - Int(m / 2.0) * 2.0 = 1 Then
            If v - Int(v / 2.0) * 2.0 = 1 Then res = res + posBit
            v = Int(v / 2.0)
          End If
          m = Int(m / 2.0)
          posBit = posBit * 2.0
        Next
        ply_pdep_u32 = res
      End Function
      """,
    [Pext64] = """
      Function ply_pext_u64(ByVal val, ByVal mask)
        Dim uv, um, vl, vh, ml, mh, res, b, outBit
        uv = ply_u64(val)
        um = ply_u64(mask)
        vl = ply_u32(uv)
        vh = ply_u32(Int(uv / 4294967296.0))
        ml = ply_u32(um)
        mh = ply_u32(Int(um / 4294967296.0))
        res = 0.0
        outBit = 1.0
        For b = 0 To 31
          If ml - Int(ml / 2.0) * 2.0 = 1 Then
            If vl - Int(vl / 2.0) * 2.0 = 1 Then res = res + outBit
            outBit = outBit * 2.0
          End If
          vl = Int(vl / 2.0)
          ml = Int(ml / 2.0)
        Next
        For b = 0 To 31
          If mh - Int(mh / 2.0) * 2.0 = 1 Then
            If vh - Int(vh / 2.0) * 2.0 = 1 Then res = res + outBit
            outBit = outBit * 2.0
          End If
          vh = Int(vh / 2.0)
          mh = Int(mh / 2.0)
        Next
        ply_pext_u64 = res
      End Function
      """,
    [Pdep64] = """
      Function ply_pdep_u64(ByVal val, ByVal mask)
        Dim uv, um, vl, vh, ml, mh, res, b, posBit, curBit
        uv = ply_u64(val)
        um = ply_u64(mask)
        vl = ply_u32(uv)
        vh = ply_u32(Int(uv / 4294967296.0))
        ml = ply_u32(um)
        mh = ply_u32(Int(um / 4294967296.0))
        res = 0.0
        posBit = 1.0
        curBit = 0
        For b = 0 To 31
          If ml - Int(ml / 2.0) * 2.0 = 1 Then
            If curBit < 32 Then
              If vl - Int(vl / 2.0) * 2.0 = 1 Then res = res + posBit
              vl = Int(vl / 2.0)
            Else
              If vh - Int(vh / 2.0) * 2.0 = 1 Then res = res + posBit
              vh = Int(vh / 2.0)
            End If
            curBit = curBit + 1
          End If
          ml = Int(ml / 2.0)
          posBit = posBit * 2.0
        Next
        For b = 0 To 31
          If mh - Int(mh / 2.0) * 2.0 = 1 Then
            If curBit < 32 Then
              If vl - Int(vl / 2.0) * 2.0 = 1 Then res = res + posBit
              vl = Int(vl / 2.0)
            Else
              If vh - Int(vh / 2.0) * 2.0 = 1 Then res = res + posBit
              vh = Int(vh / 2.0)
            End If
            curBit = curBit + 1
          End If
          mh = Int(mh / 2.0)
          posBit = posBit * 2.0
        Next
        ply_pdep_u64 = res
      End Function
      """,
    [Cell] = """
      Class ply_Cell
        Public Val
      End Class
      """,
    [CollectionSupport] = """
      Function ply_key_eq(ByVal a, ByVal b, ByVal eqMode)
        If IsObject(a) Or IsObject(b) Then
          If Not (IsObject(a) And IsObject(b)) Then
            ply_key_eq = False
          ElseIf (a Is Nothing) And (b Is Nothing) Then
            ply_key_eq = True
          ElseIf (a Is Nothing) Or (b Is Nothing) Then
            ply_key_eq = False
          ElseIf eqMode = 1 Then
            ply_key_eq = a.ply_equals(b)
          Else
            ply_key_eq = (a Is b)
          End If
          Exit Function
        End If
        If IsNull(a) Or IsNull(b) Then
          ply_key_eq = (IsNull(a) And IsNull(b))
          Exit Function
        End If
        If VarType(a) = 8 And VarType(b) = 8 Then
          ply_key_eq = (StrComp(a, b, 0) = 0)
          Exit Function
        End If
        If VarType(a) = 11 Or VarType(b) = 11 Then
          ply_key_eq = (VarType(a) = VarType(b) And a = b)
          Exit Function
        End If
        ply_key_eq = (a = b)
      End Function

      Class ply_List
        Private m_items()
        Private m_cap
        Public Count
        Public Version

        Private Sub Class_Initialize()
          m_cap = 0
          Count = 0
          Version = 0
        End Sub

        Private Sub EnsureCap(ByVal need)
          Dim nc
          If need <= m_cap Then Exit Sub
          If m_cap = 0 Then nc = 4 Else nc = m_cap * 2
          Do While nc < need
            nc = nc * 2
          Loop
          ReDim Preserve m_items(nc - 1)
          m_cap = nc
        End Sub

        Public Function GetAt(ByVal i)
          If i < 0 Or i >= Count Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
          If IsObject(m_items(i)) Then Set GetAt = m_items(i) Else GetAt = m_items(i)
        End Function

        Public Sub SetAt(ByVal i, ByVal v)
          If i < 0 Or i >= Count Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
          If IsObject(v) Then Set m_items(i) = v Else m_items(i) = v
          Version = Version + 1
        End Sub

        Public Sub AddItem(ByVal v)
          Call EnsureCap(Count + 1)
          If IsObject(v) Then Set m_items(Count) = v Else m_items(Count) = v
          Count = Count + 1
          Version = Version + 1
        End Sub

        Public Sub InsertAt(ByVal idx, ByVal v)
          Dim i
          If idx < 0 Or idx > Count Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
          Call EnsureCap(Count + 1)
          For i = Count To idx + 1 Step -1
            If IsObject(m_items(i - 1)) Then Set m_items(i) = m_items(i - 1) Else m_items(i) = m_items(i - 1)
          Next
          If IsObject(v) Then Set m_items(idx) = v Else m_items(idx) = v
          Count = Count + 1
          Version = Version + 1
        End Sub

        Public Sub RemoveAt(ByVal idx)
          Dim i
          If idx < 0 Or idx >= Count Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
          For i = idx To Count - 2
            If IsObject(m_items(i + 1)) Then Set m_items(i) = m_items(i + 1) Else m_items(i) = m_items(i + 1)
          Next
          Count = Count - 1
          m_items(Count) = Empty
          Version = Version + 1
        End Sub

        Public Function RemoveLast(ByVal isObj)
          If Count = 0 Then
            If isObj Then Set RemoveLast = Nothing Else RemoveLast = Null
            Exit Function
          End If
          Count = Count - 1
          If IsObject(m_items(Count)) Then Set RemoveLast = m_items(Count) Else RemoveLast = m_items(Count)
          m_items(Count) = Empty
          Version = Version + 1
        End Function

        Public Sub ClearAll()
          Dim i
          For i = 0 To Count - 1
            m_items(i) = Empty
          Next
          Count = 0
          Version = Version + 1
        End Sub

        Public Function IndexOfItem(ByVal v, ByVal eqMode)
          Dim i
          For i = 0 To Count - 1
            If ply_key_eq(m_items(i), v, eqMode) Then
              IndexOfItem = CLng(i)
              Exit Function
            End If
          Next
          IndexOfItem = -1
        End Function

        Public Function ContainsItem(ByVal v, ByVal eqMode)
          ContainsItem = (IndexOfItem(v, eqMode) >= 0)
        End Function

        Public Function ToArr(ByVal isStruct)
          Dim r(), i
          If Count = 0 Then
            ToArr = Array()
            Exit Function
          End If
          ReDim r(Count - 1)
          For i = 0 To Count - 1
            If isStruct Then
              Set r(i) = m_items(i).ply_clone()
            ElseIf IsObject(m_items(i)) Then
              Set r(i) = m_items(i)
            Else
              r(i) = m_items(i)
            End If
          Next
          ToArr = r
        End Function
      End Class

      Function ply_new_list()
        Set ply_new_list = New ply_List
      End Function

      Function ply_list_from_arr(ByRef arr, ByVal isStruct)
        Dim l, i
        If IsNull(arr) Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        Set l = New ply_List
        For i = 0 To UBound(arr)
          If isStruct Then
            Call l.AddItem(arr(i).ply_clone())
          Else
            Call l.AddItem(arr(i))
          End If
        Next
        Set ply_list_from_arr = l
      End Function

      Class ply_Map
        Public OrderMode
        Public Count
        Public SlotCount
        Public Version
        Private m_keys()
        Private m_vals()
        Private m_used()
        Private m_cap
        Private m_free()
        Private m_freeCount
        Private m_freeCap

        Private Sub Class_Initialize()
          OrderMode = 0
          Count = 0
          SlotCount = 0
          Version = 0
          m_cap = 0
          m_freeCount = 0
          m_freeCap = 0
        End Sub

        Private Sub EnsureCap(ByVal need)
          Dim nc
          If need <= m_cap Then Exit Sub
          If m_cap = 0 Then nc = 4 Else nc = m_cap * 2
          Do While nc < need
            nc = nc * 2
          Loop
          ReDim Preserve m_keys(nc - 1)
          ReDim Preserve m_vals(nc - 1)
          ReDim Preserve m_used(nc - 1)
          m_cap = nc
        End Sub

        Private Sub PushFree(ByVal slot)
          Dim nc
          If m_freeCount >= m_freeCap Then
            If m_freeCap = 0 Then nc = 4 Else nc = m_freeCap * 2
            ReDim Preserve m_free(nc - 1)
            m_freeCap = nc
          End If
          m_free(m_freeCount) = slot
          m_freeCount = m_freeCount + 1
        End Sub

        Private Function FindSlot(ByVal k)
          Dim i
          For i = 0 To SlotCount - 1
            If m_used(i) Then
              If ply_key_eq(m_keys(i), k, 0) Then
                FindSlot = i
                Exit Function
              End If
            End If
          Next
          FindSlot = -1
        End Function

        Private Function PutCore(ByVal k, ByVal v, ByVal doReplace)
          Dim idx, slot
          idx = FindSlot(k)
          If idx >= 0 Then
            If doReplace Then
              If IsObject(v) Then Set m_vals(idx) = v Else m_vals(idx) = v
            End If
            PutCore = False
            Exit Function
          End If
          If OrderMode = 1 And m_freeCount > 0 Then
            m_freeCount = m_freeCount - 1
            slot = m_free(m_freeCount)
          Else
            Call EnsureCap(SlotCount + 1)
            slot = SlotCount
            SlotCount = SlotCount + 1
          End If
          If IsObject(k) Then Set m_keys(slot) = k Else m_keys(slot) = k
          If IsObject(v) Then Set m_vals(slot) = v Else m_vals(slot) = v
          m_used(slot) = True
          Count = Count + 1
          Version = Version + 1
          PutCore = True
        End Function

        Public Sub PutEntry(ByVal k, ByVal v)
          Dim dummy
          dummy = PutCore(k, v, True)
        End Sub

        Public Sub AddEntry(ByVal k, ByVal v)
          If Not PutCore(k, v, False) Then Err.Raise vbObjectError + 1008, "ArgumentException", "An item with the same key has already been added."
        End Sub

        Public Function TryAddEntry(ByVal k, ByVal v)
          TryAddEntry = PutCore(k, v, False)
        End Function

        Public Function GetEntry(ByVal k)
          Dim idx
          idx = FindSlot(k)
          If idx < 0 Then Err.Raise vbObjectError + 1009, "KeyNotFoundException", "The given key was not present in the dictionary."
          If IsObject(m_vals(idx)) Then Set GetEntry = m_vals(idx) Else GetEntry = m_vals(idx)
        End Function

        Public Function TryGetEntry(ByVal k, ByVal isObj)
          Dim idx
          idx = FindSlot(k)
          If idx < 0 Then
            If isObj Then Set TryGetEntry = Nothing Else TryGetEntry = Null
            Exit Function
          End If
          If IsObject(m_vals(idx)) Then Set TryGetEntry = m_vals(idx) Else TryGetEntry = m_vals(idx)
        End Function

        Public Function HasKey(ByVal k)
          HasKey = (FindSlot(k) >= 0)
        End Function

        Public Function RemoveKey(ByVal k)
          Dim idx
          idx = FindSlot(k)
          If idx < 0 Then
            RemoveKey = False
            Exit Function
          End If
          m_used(idx) = False
          m_keys(idx) = Empty
          m_vals(idx) = Empty
          Count = Count - 1
          If OrderMode = 1 Then Call PushFree(idx)
          RemoveKey = True
        End Function

        Public Sub ClearAll()
          Dim i
          For i = 0 To SlotCount - 1
            m_used(i) = False
            m_keys(i) = Empty
            m_vals(i) = Empty
          Next
          Count = 0
          m_freeCount = 0
          If OrderMode = 1 Then SlotCount = 0
        End Sub

        Public Function SlotUsed(ByVal i)
          SlotUsed = m_used(i)
        End Function

        Public Function KeyAt(ByVal i)
          If IsObject(m_keys(i)) Then Set KeyAt = m_keys(i) Else KeyAt = m_keys(i)
        End Function

        Public Function ValAt(ByVal i)
          If IsObject(m_vals(i)) Then Set ValAt = m_vals(i) Else ValAt = m_vals(i)
        End Function
      End Class

      Function ply_new_map(ByVal orderMode)
        Dim m
        Set m = New ply_Map
        m.OrderMode = orderMode
        Set ply_new_map = m
      End Function

      Class ply_Set
        Public OrderMode
        Public Count
        Public SlotCount
        Public Version
        Private m_keys()
        Private m_used()
        Private m_cap
        Private m_free()
        Private m_freeCount
        Private m_freeCap

        Private Sub Class_Initialize()
          OrderMode = 0
          Count = 0
          SlotCount = 0
          Version = 0
          m_cap = 0
          m_freeCount = 0
          m_freeCap = 0
        End Sub

        Private Sub EnsureCap(ByVal need)
          Dim nc
          If need <= m_cap Then Exit Sub
          If m_cap = 0 Then nc = 4 Else nc = m_cap * 2
          Do While nc < need
            nc = nc * 2
          Loop
          ReDim Preserve m_keys(nc - 1)
          ReDim Preserve m_used(nc - 1)
          m_cap = nc
        End Sub

        Private Sub PushFree(ByVal slot)
          Dim nc
          If m_freeCount >= m_freeCap Then
            If m_freeCap = 0 Then nc = 4 Else nc = m_freeCap * 2
            ReDim Preserve m_free(nc - 1)
            m_freeCap = nc
          End If
          m_free(m_freeCount) = slot
          m_freeCount = m_freeCount + 1
        End Sub

        Private Function FindSlot(ByVal k)
          Dim i
          For i = 0 To SlotCount - 1
            If m_used(i) Then
              If ply_key_eq(m_keys(i), k, 0) Then
                FindSlot = i
                Exit Function
              End If
            End If
          Next
          FindSlot = -1
        End Function

        Public Function AddVal(ByVal k)
          Dim slot
          If FindSlot(k) >= 0 Then
            AddVal = False
            Exit Function
          End If
          If OrderMode = 1 And m_freeCount > 0 Then
            m_freeCount = m_freeCount - 1
            slot = m_free(m_freeCount)
          Else
            Call EnsureCap(SlotCount + 1)
            slot = SlotCount
            SlotCount = SlotCount + 1
          End If
          If IsObject(k) Then Set m_keys(slot) = k Else m_keys(slot) = k
          m_used(slot) = True
          Count = Count + 1
          Version = Version + 1
          AddVal = True
        End Function

        Public Function RemoveVal(ByVal k)
          Dim idx
          idx = FindSlot(k)
          If idx < 0 Then
            RemoveVal = False
            Exit Function
          End If
          m_used(idx) = False
          m_keys(idx) = Empty
          Count = Count - 1
          If OrderMode = 1 Then Call PushFree(idx)
          RemoveVal = True
        End Function

        Public Function HasItem(ByVal k)
          HasItem = (FindSlot(k) >= 0)
        End Function

        Public Sub ClearAll()
          Dim i
          For i = 0 To SlotCount - 1
            m_used(i) = False
            m_keys(i) = Empty
          Next
          Count = 0
          m_freeCount = 0
          If OrderMode = 1 Then SlotCount = 0
        End Sub

        Public Function SlotUsed(ByVal i)
          SlotUsed = m_used(i)
        End Function

        Public Function KeyAt(ByVal i)
          If IsObject(m_keys(i)) Then Set KeyAt = m_keys(i) Else KeyAt = m_keys(i)
        End Function
      End Class

      Function ply_new_set(ByVal orderMode)
        Dim s
        Set s = New ply_Set
        s.OrderMode = orderMode
        Set ply_new_set = s
      End Function
      """,
    [ArrSeq] = """
      Class ply_ArrSeq
        Public Arr
        Public IsStructElem
        Public Function Enumerate()
          Set Enumerate = ply_list_from_arr(Arr, IsStructElem)
        End Function
      End Class

      Function ply_arr_seq(ByRef arr, ByVal isStruct)
        Dim s
        If IsNull(arr) Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        Set s = New ply_ArrSeq
        s.Arr = arr
        s.IsStructElem = isStruct
        Set ply_arr_seq = s
      End Function
      """,
    [TaskSupport] = """
      Class ply_Task
        Public HasError
        Public ErrObj
        Public Result
        Private Sub Class_Initialize()
          HasError = False
          Set ErrObj = Nothing
          Result = Empty
        End Sub
      End Class

      Function ply_task_from_result(ByVal v)
        Dim t
        Set t = New ply_Task
        If IsObject(v) Then Set t.Result = v Else t.Result = v
        Set ply_task_from_result = t
      End Function

      Function ply_task_completed()
        Set ply_task_completed = New ply_Task
      End Function

      Function ply_task_fault(ByVal e)
        Dim t
        Set t = New ply_Task
        t.HasError = True
        Set t.ErrObj = e
        Set ply_task_fault = t
      End Function

      Function ply_task_await(ByVal t)
        If t Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If t.HasError Then Call ply_throw(t.ErrObj)
        If IsObject(t.Result) Then Set ply_task_await = t.Result Else ply_task_await = t.Result
      End Function

      Sub ply_task_await_void(ByVal t)
        If t Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If t.HasError Then Call ply_throw(t.ErrObj)
      End Sub

      Function ply_task_when_all(ByVal tasks, ByVal isVoid)
        Dim i, n, t, r()
        n = UBound(tasks) + 1
        For i = 0 To n - 1
          Set t = tasks(i)
          If t.HasError Then
            Set ply_task_when_all = ply_task_fault(t.ErrObj)
            Exit Function
          End If
        Next
        If isVoid Then
          Set ply_task_when_all = ply_task_completed()
          Exit Function
        End If
        If n = 0 Then
          Set ply_task_when_all = ply_task_from_result(Array())
          Exit Function
        End If
        ReDim r(n - 1)
        For i = 0 To n - 1
          Set t = tasks(i)
          If IsObject(t.Result) Then Set r(i) = t.Result Else r(i) = t.Result
        Next
        Set ply_task_when_all = ply_task_from_result(r)
      End Function
      """,
    [SpanSupport] = """
      Function ply_wrap_kind(ByVal kind, ByVal lo, ByVal hi)
        Select Case kind
          Case 1 : ply_wrap_kind = ply_i8(lo)
          Case 2 : ply_wrap_kind = ply_u8(lo)
          Case 3 : ply_wrap_kind = ply_i16(lo)
          Case 4 : ply_wrap_kind = ply_u16(lo)
          Case 5 : ply_wrap_kind = ply_i32(lo)
          Case 6 : ply_wrap_kind = ply_u32(lo)
          Case 7 : ply_wrap_kind = ply_i64(hi * 4294967296.0 + lo)
          Case Else : ply_wrap_kind = ply_u64(hi * 4294967296.0 + lo)
        End Select
      End Function

      Class ply_Buf
        Public Data
        Public Length
        Public ElemKind
        Public ElemSize
        Public IsNullArr

        Private Sub Class_Initialize()
          Data = Array()
          Length = 0
          ElemKind = 5
          ElemSize = 4
          IsNullArr = False
        End Sub

        Public Function GetElem(ByVal i)
          GetElem = Data(i)
        End Function

        Public Sub SetElem(ByVal i, ByVal v)
          Data(i) = v
        End Sub

        Public Function GetAtChecked(ByVal i)
          If IsNullArr Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
          If i < 0 Or i >= Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
          GetAtChecked = Data(i)
        End Function

        Public Sub SetAtChecked(ByVal i, ByVal v)
          If IsNullArr Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
          If i < 0 Or i >= Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
          Data(i) = v
        End Sub

        Public Function GetByte(ByVal bytePos)
          Dim idx, subIdx, v, u
          idx = Int(bytePos / ElemSize)
          subIdx = bytePos - idx * ElemSize
          v = Data(idx)
          If ElemSize = 8 Then
            u = ply_u64(v)
            If subIdx < 4 Then
              u = ply_u32(u)
            Else
              u = ply_u32(Int(u / 4294967296.0))
              subIdx = subIdx - 4
            End If
          Else
            u = ply_u32(v)
          End If
          u = Int(u / (256.0 ^ subIdx))
          GetByte = CLng(u - Int(u / 256.0) * 256.0)
        End Function

        Public Sub SetByte(ByVal bytePos, ByVal bVal)
          Dim idx, subIdx, v, lo, hi, scale, oldB, nb
          idx = Int(bytePos / ElemSize)
          subIdx = bytePos - idx * ElemSize
          v = Data(idx)
          nb = ply_u8(bVal)
          If ElemSize = 8 Then
            lo = ply_u32(v)
            hi = ply_u32(Int(ply_u64(v) / 4294967296.0))
            If subIdx < 4 Then
              scale = 256.0 ^ subIdx
              oldB = Int(lo / scale)
              oldB = oldB - Int(oldB / 256.0) * 256.0
              lo = lo + (CDbl(nb) - oldB) * scale
            Else
              scale = 256.0 ^ (subIdx - 4)
              oldB = Int(hi / scale)
              oldB = oldB - Int(oldB / 256.0) * 256.0
              hi = hi + (CDbl(nb) - oldB) * scale
            End If
            Data(idx) = ply_wrap_kind(ElemKind, lo, hi)
          Else
            lo = ply_u32(v)
            scale = 256.0 ^ subIdx
            oldB = Int(lo / scale)
            oldB = oldB - Int(oldB / 256.0) * 256.0
            lo = lo + (CDbl(nb) - oldB) * scale
            Data(idx) = ply_wrap_kind(ElemKind, lo, 0.0)
          End If
        End Sub
      End Class

      Function ply_buf_wrap(ByRef arr, ByVal elemKind, ByVal elemSize)
        Dim b
        Set b = New ply_Buf
        b.ElemKind = elemKind
        b.ElemSize = elemSize
        If IsNull(arr) Then
          b.IsNullArr = True
          b.Data = Array()
          b.Length = 0
        Else
          b.IsNullArr = False
          b.Data = arr
          b.Length = UBound(arr) + 1
        End If
        Set ply_buf_wrap = b
      End Function

      Function ply_buf_single(ByVal v, ByVal elemKind, ByVal elemSize)
        Dim b, a(0)
        a(0) = v
        Set b = New ply_Buf
        b.ElemKind = elemKind
        b.ElemSize = elemSize
        b.IsNullArr = False
        b.Data = a
        b.Length = 1
        Set ply_buf_single = b
      End Function

      Function ply_raw_read(ByVal buf, ByVal bytePos, ByVal elemKind, ByVal elemSize)
        Dim lo, hi, k
        If elemKind = buf.ElemKind And (bytePos - Int(bytePos / elemSize) * elemSize) = 0 Then
          ply_raw_read = buf.GetElem(Int(bytePos / elemSize))
          Exit Function
        End If
        lo = 0.0
        hi = 0.0
        For k = 0 To elemSize - 1
          If k < 4 Then
            lo = lo + CDbl(buf.GetByte(bytePos + k)) * (256.0 ^ k)
          Else
            hi = hi + CDbl(buf.GetByte(bytePos + k)) * (256.0 ^ (k - 4))
          End If
        Next
        ply_raw_read = ply_wrap_kind(elemKind, lo, hi)
      End Function

      Sub ply_raw_write(ByVal buf, ByVal bytePos, ByVal v, ByVal elemKind, ByVal elemSize)
        Dim lo, hi, k, b
        If elemKind = buf.ElemKind And (bytePos - Int(bytePos / elemSize) * elemSize) = 0 Then
          Call buf.SetElem(Int(bytePos / elemSize), v)
          Exit Sub
        End If
        If elemSize = 8 Then
          lo = ply_u32(v)
          hi = ply_u32(Int(ply_u64(v) / 4294967296.0))
        Else
          lo = ply_u32(v)
          hi = 0.0
        End If
        For k = 0 To elemSize - 1
          If k < 4 Then
            b = CLng(lo - Int(lo / 256.0) * 256.0)
            lo = Int(lo / 256.0)
          Else
            b = CLng(hi - Int(hi / 256.0) * 256.0)
            hi = Int(hi / 256.0)
          End If
          Call buf.SetByte(bytePos + k, b)
        Next
      End Sub

      Class ply_Span
        Public Buf
        Public ByteOffset
        Public Length
        Public ElemKind
        Public ElemSize

        Private Sub Class_Initialize()
          Set Buf = Nothing
          ByteOffset = 0
          Length = 0
          ElemKind = 5
          ElemSize = 4
        End Sub

        Public Function GetAt(ByVal i)
          If i < 0 Or i >= Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
          GetAt = ply_raw_read(Buf, ByteOffset + CDbl(i) * CDbl(ElemSize), ElemKind, ElemSize)
        End Function

        Public Sub SetAt(ByVal i, ByVal v)
          If i < 0 Or i >= Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
          Call ply_raw_write(Buf, ByteOffset + CDbl(i) * CDbl(ElemSize), v, ElemKind, ElemSize)
        End Sub

        Public Function Slice(ByVal startIdx, ByVal count)
          Dim c, s
          If count < 0 Then c = Length - startIdx Else c = count
          If startIdx < 0 Or c < 0 Or startIdx + c > Length Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Slice range was out of bounds."
          Set s = New ply_Span
          Set s.Buf = Buf
          s.ByteOffset = ByteOffset + CDbl(startIdx) * CDbl(ElemSize)
          s.Length = CLng(c)
          s.ElemKind = ElemKind
          s.ElemSize = ElemSize
          Set Slice = s
        End Function

        Public Sub CopyTo(ByVal dst)
          If Not TryCopyTo(dst) Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Destination is too short."
        End Sub

        Public Function TryCopyTo(ByVal dst)
          Dim i, tmp()
          If Length > dst.Length Then
            TryCopyTo = False
            Exit Function
          End If
          If Length > 0 Then
            ReDim tmp(Length - 1)
            For i = 0 To Length - 1
              tmp(i) = GetAt(i)
            Next
            For i = 0 To Length - 1
              Call dst.SetAt(i, tmp(i))
            Next
          End If
          TryCopyTo = True
        End Function

        Public Sub FillAll(ByVal v)
          Dim i
          For i = 0 To Length - 1
            Call SetAt(i, v)
          Next
        End Sub

        Public Function ToArr()
          Dim i, r()
          If Length = 0 Then
            ToArr = Array()
            Exit Function
          End If
          ReDim r(Length - 1)
          For i = 0 To Length - 1
            r(i) = GetAt(i)
          Next
          ToArr = r
        End Function

        Public Function SeqEqual(ByVal other)
          Dim i
          If Length <> other.Length Then
            SeqEqual = False
            Exit Function
          End If
          For i = 0 To Length - 1
            If GetAt(i) <> other.GetAt(i) Then
              SeqEqual = False
              Exit Function
            End If
          Next
          SeqEqual = True
        End Function

        Public Function Reinterpret(ByVal newKind, ByVal newSize)
          Dim s
          Set s = New ply_Span
          Set s.Buf = Buf
          s.ByteOffset = ByteOffset
          s.Length = CLng(Int((CDbl(Length) * CDbl(ElemSize)) / CDbl(newSize)))
          s.ElemKind = newKind
          s.ElemSize = newSize
          Set Reinterpret = s
        End Function
      End Class

      Function ply_span_empty(ByVal elemKind, ByVal elemSize)
        Dim s
        Set s = New ply_Span
        s.ElemKind = elemKind
        s.ElemSize = elemSize
        Set ply_span_empty = s
      End Function

      Function ply_span_from_buf(ByVal buf, ByVal startIdx, ByVal count)
        Dim c, s
        If buf.IsNullArr Then
          If startIdx <> 0 Or count > 0 Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Span range was out of bounds."
          Set ply_span_from_buf = ply_span_empty(buf.ElemKind, buf.ElemSize)
          Exit Function
        End If
        If count < 0 Then c = buf.Length - startIdx Else c = count
        If startIdx < 0 Or c < 0 Or startIdx + c > buf.Length Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Span range was out of bounds."
        Set s = New ply_Span
        Set s.Buf = buf
        s.ByteOffset = CDbl(startIdx) * CDbl(buf.ElemSize)
        s.Length = CLng(c)
        s.ElemKind = buf.ElemKind
        s.ElemSize = buf.ElemSize
        Set ply_span_from_buf = s
      End Function

      Function ply_span_from_arr(ByRef arr, ByVal elemKind, ByVal elemSize, ByVal startIdx, ByVal count)
        Set ply_span_from_arr = ply_span_from_buf(ply_buf_wrap(arr, elemKind, elemSize), startIdx, count)
      End Function

      Function ply_span_stackalloc(ByVal n, ByVal elemKind, ByVal elemSize)
        Dim arr
        arr = ply_new_arr(n, 0)
        Set ply_span_stackalloc = ply_span_from_buf(ply_buf_wrap(arr, elemKind, elemSize), 0, n)
      End Function

      Function ply_span_load_e(ByVal sp, ByVal at, ByVal width, ByVal isLittle, ByVal isSigned)
        Dim i, bi, acc
        If at < 0 Or at + width > sp.Length Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
        acc = 0.0
        For i = 0 To width - 1
          If isLittle Then bi = at + i Else bi = at + width - 1 - i
          acc = acc + CDbl(sp.GetAt(bi)) * (256.0 ^ i)
        Next
        If isSigned Then
          If width = 2 Then ply_span_load_e = ply_i16(acc) Else ply_span_load_e = ply_i32(acc)
        Else
          ply_span_load_e = acc
        End If
      End Function

      Sub ply_span_store_e(ByVal sp, ByVal at, ByVal val, ByVal width, ByVal isLittle)
        Dim i, bi, u
        If at < 0 Or at + width > sp.Length Then Err.Raise vbObjectError + 1003, "ArgumentOutOfRangeException", "Index was out of range."
        u = ply_u32(val)
        For i = 0 To width - 1
          If isLittle Then bi = at + i Else bi = at + width - 1 - i
          Call sp.SetAt(bi, CLng(u - Int(u / 256.0) * 256.0))
          u = Int(u / 256.0)
        Next
      End Sub
      """,
    [PtrSupport] = """
      Class ply_Ptr
        Public Buf
        Public RegionStart
        Public RegionBytes
        Public Offset
        Private Sub Class_Initialize()
          Set Buf = Nothing
          RegionStart = 0.0
          RegionBytes = 0.0
          Offset = 0.0
        End Sub
      End Class

      Function ply_ptr_from_buf(ByVal buf, ByVal regionStart, ByVal regionBytes, ByVal offset)
        Dim p
        Set p = New ply_Ptr
        Set p.Buf = buf
        p.RegionStart = CDbl(regionStart)
        p.RegionBytes = CDbl(regionBytes)
        p.Offset = CDbl(offset)
        Set ply_ptr_from_buf = p
      End Function

      Function ply_ptr_pin_span(ByVal sp, ByVal hasIdx, ByVal idx)
        Dim rBytes
        rBytes = CDbl(sp.Length) * CDbl(sp.ElemSize)
        If Not hasIdx Then
          If sp.Length = 0 Then
            Set ply_ptr_pin_span = Nothing
          Else
            Set ply_ptr_pin_span = ply_ptr_from_buf(sp.Buf, sp.ByteOffset, rBytes, 0.0)
          End If
          Exit Function
        End If
        If idx < 0 Or idx >= sp.Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        Set ply_ptr_pin_span = ply_ptr_from_buf(sp.Buf, sp.ByteOffset, rBytes, CDbl(idx) * CDbl(sp.ElemSize))
      End Function

      Function ply_ptr_pin_buf(ByVal buf, ByVal hasIdx, ByVal idx)
        Dim rBytes
        If buf.IsNullArr Then
          If hasIdx Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
          Set ply_ptr_pin_buf = Nothing
          Exit Function
        End If
        rBytes = CDbl(buf.Length) * CDbl(buf.ElemSize)
        If Not hasIdx Then
          If buf.Length = 0 Then
            Set ply_ptr_pin_buf = Nothing
          Else
            Set ply_ptr_pin_buf = ply_ptr_from_buf(buf, 0.0, rBytes, 0.0)
          End If
          Exit Function
        End If
        If idx < 0 Or idx >= buf.Length Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the array."
        Set ply_ptr_pin_buf = ply_ptr_from_buf(buf, 0.0, rBytes, CDbl(idx) * CDbl(buf.ElemSize))
      End Function

      Function ply_ptr_pin_arr(ByRef arr, ByVal elemKind, ByVal elemSize, ByVal hasIdx, ByVal idx)
        Set ply_ptr_pin_arr = ply_ptr_pin_buf(ply_buf_wrap(arr, elemKind, elemSize), hasIdx, idx)
      End Function

      Function ply_ptr_stackalloc(ByVal n, ByVal elemKind, ByVal elemSize)
        Dim arr, b
        arr = ply_new_arr(n, 0)
        Set b = ply_buf_wrap(arr, elemKind, elemSize)
        Set ply_ptr_stackalloc = ply_ptr_from_buf(b, 0.0, CDbl(n) * CDbl(elemSize), 0.0)
      End Function

      Function ply_ptr_add(ByVal p, ByVal delta, ByVal elemSize)
        If p Is Nothing Then
          Set ply_ptr_add = ply_ptr_from_buf(Nothing, 0.0, 0.0, CDbl(delta) * CDbl(elemSize))
        Else
          Set ply_ptr_add = ply_ptr_from_buf(p.Buf, p.RegionStart, p.RegionBytes, p.Offset + CDbl(delta) * CDbl(elemSize))
        End If
      End Function

      Function ply_ptr_load(ByVal p, ByVal elemKind, ByVal elemSize)
        If p Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If p.Buf Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If p.Offset < 0 Or p.Offset > p.RegionBytes - CDbl(elemSize) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the pointer region."
        ply_ptr_load = ply_raw_read(p.Buf, p.RegionStart + p.Offset, elemKind, elemSize)
      End Function

      Sub ply_ptr_store(ByVal p, ByVal v, ByVal elemKind, ByVal elemSize)
        If p Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If p.Buf Is Nothing Then Err.Raise vbObjectError + 1004, "NullReferenceException", "Object reference not set to an instance of an object."
        If p.Offset < 0 Or p.Offset > p.RegionBytes - CDbl(elemSize) Then Err.Raise vbObjectError + 1003, "IndexOutOfRangeException", "Index was outside the bounds of the pointer region."
        Call ply_raw_write(p.Buf, p.RegionStart + p.Offset, v, elemKind, elemSize)
      End Sub

      Function ply_ptr_same_region(ByVal a, ByVal b)
        Dim ab, bb
        If a Is Nothing Then Set ab = Nothing Else Set ab = a.Buf
        If b Is Nothing Then Set bb = Nothing Else Set bb = b.Buf
        ply_ptr_same_region = (ab Is bb)
      End Function

      Function ply_ptr_pos(ByVal p)
        If p Is Nothing Then ply_ptr_pos = 0.0 Else ply_ptr_pos = p.RegionStart + p.Offset
      End Function

      Function ply_ptr_eq(ByVal a, ByVal b)
        If Not ply_ptr_same_region(a, b) Then
          ply_ptr_eq = False
          Exit Function
        End If
        ply_ptr_eq = (ply_ptr_pos(a) = ply_ptr_pos(b))
      End Function

      Function ply_ptr_cmp(ByVal a, ByVal b)
        If Not ply_ptr_same_region(a, b) Then Err.Raise vbObjectError + 1005, "InvalidOperationException", "Cannot compare pointers from different regions."
        ply_ptr_cmp = ply_ptr_pos(a) - ply_ptr_pos(b)
      End Function

      Function ply_ptr_diff(ByVal a, ByVal b, ByVal elemSize)
        ply_ptr_diff = Fix(ply_ptr_cmp(a, b) / CDbl(elemSize))
      End Function
      """
  };

  /// <summary>Returns the VBScript source blocks for all referenced helpers and their transitive dependencies in deterministic order.</summary>
  public static IReadOnlyList<string> Resolve(ISet<string> requested) {
    var needed = new HashSet<string>(requested, StringComparer.Ordinal);
    var queue = new Queue<string>(requested);
    while (queue.Count > 0) {
      var current = queue.Dequeue();
      if (!_dependencies.TryGetValue(current, out var deps))
        continue;
      foreach (var dep in deps)
        if (needed.Add(dep))
          queue.Enqueue(dep);
    }

    return [.. _order.Where(needed.Contains).Select(name => _bodies[name])];
  }

}
