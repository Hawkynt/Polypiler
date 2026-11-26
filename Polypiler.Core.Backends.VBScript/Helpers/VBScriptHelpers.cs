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
    [LoadEndian] = [CheckIdx, I16, I32],
    [StoreEndian] = [CheckIdx, U32]
  };

  private static readonly string[] _order = [
    I8, U8, I16, U16, I32, U32, Mul32, And32, Or32, Xor32, Not32, Shl32, Shr32, UShr32,
    Div32, Rem32, UDiv32, URem32,
    I64, U64, Div64, Rem64, And64, Or64, Xor64, Not64, Shl64, Shr64, UShr64,
    CheckRange, FloatRem, FloatToInt, Min, Max, AbsSigned, RoundAway, RoundHalfUp, CopySign,
    Rotl32, Rotr32, PopCnt32, Clz32, Ctz32,
    NewArr, CheckIdx, ArrCopy, ArrFill, ArrEq, LoadEndian, StoreEndian,
    StrUnit, StrFromUnits, StrToUnits, StrRepeat, StrSub, StrIndexOf, StrLastIndexOf,
    StrStarts, StrEnds, StrContains, StrReplace, StrReplaceFirst, StrSplit, StrTrim, StrPad,
    CheckNull, Unwrap, UnwrapObj, ErrSupport, WriteLine
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
