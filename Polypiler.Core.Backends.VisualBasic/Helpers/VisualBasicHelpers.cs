using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Syntax;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// The helpers that stay with the Visual Basic backend: each is an operation of the IR written with
/// what classic Visual Basic has, where it has no expression for it. Visual Basic's integer arithmetic
/// always traps (error 6) where the IR's wraps, so the wrapping sum, difference, product and negation
/// of 32 bits are computed exactly in <c>Double</c> and reduced modulo 2^32; it has no shifts and no
/// unsigned type, so an unsigned 32-bit value is carried as the bits of a <c>Long</c> and its division,
/// remainder, shifts and conversions are helpers; and an empty array has no bounds, so the length of
/// an array and a new array are helpers per element type (<see cref="ArrayHelpers"/>). Each is read
/// into the syntax tree once and declared in a module only when the module names it.
/// </summary>
internal static class VisualBasicHelpers {

  /// <summary>The folder of the helpers in the repository, which their origin names.</summary>
  public const string Folder = "Polypiler.Core.Backends.VisualBasic/Helpers";

  /// <summary>An integral <c>Double</c> below 2^53 in magnitude reduced modulo 2^32 to the bits of a <c>Long</c>.</summary>
  public const string Wrap32 = "PlyWrap32";

  /// <summary>The unsigned value of the bits of a <c>Long</c>, as a <c>Double</c>.</summary>
  public const string Unsigned32 = "PlyU32";

  /// <summary>The sum modulo 2^32.</summary>
  public const string Add32 = "PlyAdd32";

  /// <summary>The difference modulo 2^32.</summary>
  public const string Subtract32 = "PlySub32";

  /// <summary>The product modulo 2^32, from 16-bit halves so that every partial product is exact.</summary>
  public const string Multiply32 = "PlyMul32";

  /// <summary>The negation modulo 2^32: the smallest value negates to itself.</summary>
  public const string Negate32 = "PlyNeg32";

  /// <summary>
  /// The quotient of signed 32-bit integers; the overflow trap for the smallest value divided by -1,
  /// which <c>\</c> raises in Visual Basic but which ends LibreOffice Basic with a processor fault.
  /// </summary>
  public const string Divide32 = "PlyDiv32";

  /// <summary>The remainder of signed 32-bit integers; the overflow trap for the smallest value and -1.</summary>
  public const string Remainder32 = "PlyRem32";

  /// <summary>The quotient of unsigned 32-bit integers.</summary>
  public const string DivideUnsigned32 = "PlyDivU32";

  /// <summary>The remainder of unsigned 32-bit integers.</summary>
  public const string RemainderUnsigned32 = "PlyRemU32";

  /// <summary>The checked sum of unsigned 32-bit integers.</summary>
  public const string AddUnsigned32 = "PlyAddU32";

  /// <summary>The checked difference of unsigned 32-bit integers.</summary>
  public const string SubtractUnsigned32 = "PlySubU32";

  /// <summary>The checked product of unsigned 32-bit integers.</summary>
  public const string MultiplyUnsigned32 = "PlyMulU32";

  /// <summary>Shift left of 32 bits by the low five bits of the count.</summary>
  public const string ShiftLeft32 = "PlyShl32";

  /// <summary>Arithmetic shift right of a signed 32-bit integer.</summary>
  public const string ShiftRight32 = "PlySar32";

  /// <summary>Logical shift right of 32 bits.</summary>
  public const string ShiftRightLogical32 = "PlyShr32";

  /// <summary>The overflow trap unless the bits of a <c>Long</c> read as a non-negative number: the checked conversion between <c>i32</c> and <c>u32</c>.</summary>
  public const string CheckSign32 = "PlyChkSign32";

  /// <summary>A <c>Long</c> reduced to 16 bits, as an <c>Integer</c>.</summary>
  public const string Wrap16 = "PlyWrap16";

  /// <summary>A <c>Long</c> reduced to 8 bits, signed, as an <c>Integer</c>.</summary>
  public const string Wrap8 = "PlyWrap8";

  /// <summary>The overflow trap unless a <c>Long</c> lies in -128..127; the value as an <c>Integer</c>.</summary>
  public const string Check8 = "PlyChk8";

  /// <summary>The overflow trap unless a <c>Long</c> lies in 0..65535.</summary>
  public const string CheckUnsigned16 = "PlyChkU16";

  /// <summary>A <c>Double</c> truncated toward zero to the bits of a <c>u32</c>; the overflow trap outside 0..2^32-1.</summary>
  public const string ToUnsigned32 = "PlyToU32";

  /// <summary>A <c>Double</c> truncated toward zero and clamped to a range.</summary>
  public const string Saturate = "PlySat";

  /// <summary>The IEEE 754 minimum of two numbers that are no NaN.</summary>
  public const string Minimum = "PlyFMin";

  /// <summary>The IEEE 754 maximum of two numbers that are no NaN.</summary>
  public const string Maximum = "PlyFMax";

  /// <summary>Rounding half away from zero.</summary>
  public const string RoundAway = "PlyRoundAway";

  /// <summary>Rounding half toward positive infinity.</summary>
  public const string RoundHalfUp = "PlyRoundHalfUp";

  /// <summary>The code unit of a string at an index; the bounds trap outside it.</summary>
  public const string CodeUnitAt = "PlyCharAt";

  /// <summary>A part of a string by start and length; the argument trap outside it.</summary>
  public const string Substring = "PlySubstring";

  /// <summary>The first position of a needle at or after a start, or -1; the argument trap for a start outside the string.</summary>
  public const string IndexOf = "PlyIndexOf";

  /// <summary>A string of a count of copies of a string; the overflow trap for a negative count.</summary>
  public const string Repeat = "PlyRepeat";

  /// <summary>The ordinal comparison of two strings as -1, 0 or 1.</summary>
  public const string CompareOrdinal = "PlyCompare";

  /// <summary>
  /// The text <c>CStr</c> writes of a number with the decimal separator the IR names in place of the
  /// one of the run-time locale, and a three-digit exponent with a leading zero (LibreOffice writes
  /// <c>1E+020</c>) shortened to two digits as Visual Basic writes it.
  /// </summary>
  public const string NumberText = "PlyNumberText";

  /// <summary><c>FormatVisualBasic</c> of a <c>Double</c>: its <c>CStr</c>, normalized.</summary>
  public const string FormatDouble = "PlyFmtR8";

  /// <summary><c>FormatVisualBasic</c> of a <c>Single</c>.</summary>
  public const string FormatSingle = "PlyFmtR4";

  /// <summary><c>ParseDotNet</c> of a <c>Double</c>: blanks and group separators removed, a text of other characters than those of a number error 13, then <c>Val</c>, which reads a point in every locale.</summary>
  public const string ParseDouble = "PlyParseR8";

  private const string Source = """
    Private Function PlyWrap32(ByVal d As Double) As Long
        d = d - Int(d / 4294967296#) * 4294967296#
        If d >= 2147483648# Then
            d = d - 4294967296#
        End If
        PlyWrap32 = CLng(d)
    End Function

    Private Function PlyU32(ByVal a As Long) As Double
        If a < 0 Then
            PlyU32 = CDbl(a) + 4294967296#
        Else
            PlyU32 = CDbl(a)
        End If
    End Function

    Private Function PlyAdd32(ByVal a As Long, ByVal b As Long) As Long
        PlyAdd32 = PlyWrap32(CDbl(a) + CDbl(b))
    End Function

    Private Function PlySub32(ByVal a As Long, ByVal b As Long) As Long
        PlySub32 = PlyWrap32(CDbl(a) - CDbl(b))
    End Function

    Private Function PlyMul32(ByVal a As Long, ByVal b As Long) As Long
        Dim ua As Double, ub As Double, al As Double, ah As Double, bl As Double, bh As Double, middle As Double
        ua = PlyU32(a)
        ub = PlyU32(b)
        ah = Int(ua / 65536#)
        al = ua - ah * 65536#
        bh = Int(ub / 65536#)
        bl = ub - bh * 65536#
        middle = ah * bl + al * bh
        middle = middle - Int(middle / 65536#) * 65536#
        PlyMul32 = PlyWrap32(al * bl + middle * 65536#)
    End Function

    Private Function PlyNeg32(ByVal a As Long) As Long
        If a = &H80000000 Then
            PlyNeg32 = a
        Else
            PlyNeg32 = -a
        End If
    End Function

    Private Function PlyDiv32(ByVal a As Long, ByVal b As Long) As Long
        If b = -1 Then
            If a = &H80000000 Then
                Err.Raise 6
            End If
            PlyDiv32 = -a
        Else
            PlyDiv32 = a \ b
        End If
    End Function

    Private Function PlyRem32(ByVal a As Long, ByVal b As Long) As Long
        If b = -1 Then
            If a = &H80000000 Then
                Err.Raise 6
            End If
            PlyRem32 = 0
        Else
            PlyRem32 = a Mod b
        End If
    End Function

    Private Function PlyDivU32(ByVal a As Long, ByVal b As Long) As Long
        Dim ua As Double, ub As Double, q As Double
        If b = 0 Then
            Err.Raise 11
        End If
        ua = PlyU32(a)
        ub = PlyU32(b)
        q = Int(ua / ub)
        If ua - q * ub < 0 Then
            q = q - 1
        ElseIf ua - q * ub >= ub Then
            q = q + 1
        End If
        PlyDivU32 = PlyWrap32(q)
    End Function

    Private Function PlyRemU32(ByVal a As Long, ByVal b As Long) As Long
        Dim ua As Double, ub As Double, q As Double
        If b = 0 Then
            Err.Raise 11
        End If
        ua = PlyU32(a)
        ub = PlyU32(b)
        q = Int(ua / ub)
        If ua - q * ub < 0 Then
            q = q - 1
        ElseIf ua - q * ub >= ub Then
            q = q + 1
        End If
        PlyRemU32 = PlyWrap32(ua - q * ub)
    End Function

    Private Function PlyAddU32(ByVal a As Long, ByVal b As Long) As Long
        Dim d As Double
        d = PlyU32(a) + PlyU32(b)
        If d > 4294967295# Then
            Err.Raise 6
        End If
        PlyAddU32 = PlyWrap32(d)
    End Function

    Private Function PlySubU32(ByVal a As Long, ByVal b As Long) As Long
        Dim d As Double
        d = PlyU32(a) - PlyU32(b)
        If d < 0 Then
            Err.Raise 6
        End If
        PlySubU32 = PlyWrap32(d)
    End Function

    Private Function PlyMulU32(ByVal a As Long, ByVal b As Long) As Long
        Dim d As Double
        d = PlyU32(a) * PlyU32(b)
        If d > 4294967295# Then
            Err.Raise 6
        End If
        PlyMulU32 = PlyWrap32(d)
    End Function

    Private Function PlyShl32(ByVal a As Long, ByVal n As Long) As Long
        PlyShl32 = PlyWrap32(PlyU32(a) * 2 ^ (n And 31))
    End Function

    Private Function PlySar32(ByVal a As Long, ByVal n As Long) As Long
        PlySar32 = CLng(Int(CDbl(a) / 2 ^ (n And 31)))
    End Function

    Private Function PlyShr32(ByVal a As Long, ByVal n As Long) As Long
        PlyShr32 = PlyWrap32(Int(PlyU32(a) / 2 ^ (n And 31)))
    End Function

    Private Function PlyChkSign32(ByVal a As Long) As Long
        If a < 0 Then
            Err.Raise 6
        End If
        PlyChkSign32 = a
    End Function

    Private Function PlyWrap16(ByVal a As Long) As Integer
        a = a And &HFFFF&
        If a >= 32768 Then
            a = a - 65536
        End If
        PlyWrap16 = CInt(a)
    End Function

    Private Function PlyWrap8(ByVal a As Long) As Integer
        a = a And 255
        If a >= 128 Then
            a = a - 256
        End If
        PlyWrap8 = CInt(a)
    End Function

    Private Function PlyChk8(ByVal a As Long) As Integer
        If a < -128 Or a > 127 Then
            Err.Raise 6
        End If
        PlyChk8 = CInt(a)
    End Function

    Private Function PlyChkU16(ByVal a As Long) As Long
        If a < 0 Or a > 65535 Then
            Err.Raise 6
        End If
        PlyChkU16 = a
    End Function

    Private Function PlyToU32(ByVal d As Double) As Long
        d = Fix(d)
        If d < 0 Or d > 4294967295# Then
            Err.Raise 6
        End If
        PlyToU32 = PlyWrap32(d)
    End Function

    Private Function PlySat(ByVal d As Double, ByVal lowest As Double, ByVal highest As Double) As Double
        d = Fix(d)
        If d < lowest Then
            PlySat = lowest
        ElseIf d > highest Then
            PlySat = highest
        Else
            PlySat = d
        End If
    End Function

    Private Function PlyFMin(ByVal a As Double, ByVal b As Double) As Double
        If b < a Then
            PlyFMin = b
        Else
            PlyFMin = a
        End If
    End Function

    Private Function PlyFMax(ByVal a As Double, ByVal b As Double) As Double
        If b > a Then
            PlyFMax = b
        Else
            PlyFMax = a
        End If
    End Function

    Private Function PlyRoundAway(ByVal d As Double) As Double
        If d < 0 Then
            PlyRoundAway = -Int(-d + 0.5)
        Else
            PlyRoundAway = Int(d + 0.5)
        End If
    End Function

    Private Function PlyRoundHalfUp(ByVal d As Double) As Double
        PlyRoundHalfUp = Int(d + 0.5)
    End Function

    Private Function PlyCharAt(ByVal s As String, ByVal i As Long) As Long
        If i < 0 Or i >= Len(s) Then
            Err.Raise 9
        End If
        PlyCharAt = AscW(Mid(s, i + 1, 1)) And &HFFFF&
    End Function

    Private Function PlySubstring(ByVal s As String, ByVal start As Long, ByVal count As Long) As String
        If start < 0 Or count < 0 Or start > Len(s) - count Then
            Err.Raise 5
        End If
        PlySubstring = Mid(s, start + 1, count)
    End Function

    Private Function PlyIndexOf(ByVal s As String, ByVal find As String, ByVal start As Long) As Long
        If start < 0 Or start > Len(s) Then
            Err.Raise 5
        End If
        If Len(find) = 0 Then
            PlyIndexOf = start
        Else
            PlyIndexOf = InStr(start + 1, s, find, 0) - 1
        End If
    End Function

    Private Function PlyRepeat(ByVal s As String, ByVal count As Long) As String
        Dim i As Long, r As String
        If count < 0 Then
            Err.Raise 6
        End If
        For i = 1 To count
            r = r & s
        Next i
        PlyRepeat = r
    End Function

    Private Function PlyCompare(ByVal a As String, ByVal b As String) As Long
        PlyCompare = CLng(StrComp(a, b, 0))
    End Function

    Private Function PlyNumberText(ByVal s As String, ByVal sep As String) As String
        Dim d As String, e As Long, k As Long
        d = Mid(CStr(0.5), 2, 1)
        If d <> sep Then
            s = Replace(s, d, sep)
        End If
        e = InStr(s, "E")
        If e > 0 Then
            k = e + 2
            Do While k < Len(s) - 1 And Mid(s, k, 1) = "0"
                k = k + 1
            Loop
            s = Left(s, e + 1) & Mid(s, k)
        End If
        PlyNumberText = s
    End Function

    Private Function PlyFmtR8(ByVal x As Double, ByVal sep As String) As String
        PlyFmtR8 = PlyNumberText(CStr(x), sep)
    End Function

    Private Function PlyFmtR4(ByVal x As Single, ByVal sep As String) As String
        PlyFmtR4 = PlyNumberText(CStr(x), sep)
    End Function

    Private Function PlyParseR8(ByVal s As String) As Double
        Dim i As Long
        s = Replace(Trim(s), ",", "")
        For i = 1 To Len(s)
            If InStr("0123456789.eE+-", Mid(s, i, 1)) = 0 Then
                Err.Raise 13
            End If
        Next i
        PlyParseR8 = Val(s)
    End Function
    """;

  /// <summary>What each helper calls of the others.</summary>
  private static readonly Dictionary<string, string[]> _needs = new(StringComparer.Ordinal) {
    [Add32] = [Wrap32],
    [Subtract32] = [Wrap32],
    [Multiply32] = [Unsigned32, Wrap32],
    [DivideUnsigned32] = [Unsigned32, Wrap32],
    [RemainderUnsigned32] = [Unsigned32, Wrap32],
    [AddUnsigned32] = [Unsigned32, Wrap32],
    [SubtractUnsigned32] = [Unsigned32, Wrap32],
    [MultiplyUnsigned32] = [Unsigned32, Wrap32],
    [ShiftLeft32] = [Unsigned32, Wrap32],
    [ShiftRightLogical32] = [Unsigned32, Wrap32],
    [ToUnsigned32] = [Wrap32],
    [FormatDouble] = [NumberText],
    [FormatSingle] = [NumberText]
  };

  private static readonly Lazy<IReadOnlyDictionary<string, VbProcedure>> _declarations = new(() =>
    VbSyntaxReader.ReadDeclarations(Source).Cast<VbProcedure>().ToDictionary(procedure => procedure.Name, procedure => procedure with { IsHelper = true }, StringComparer.Ordinal));

  /// <summary>The names of every fixed helper, in the order a module declares them.</summary>
  public static IReadOnlyList<string> Order { get; } = [
    Wrap32, Unsigned32, Add32, Subtract32, Multiply32, Negate32, Divide32, Remainder32, DivideUnsigned32, RemainderUnsigned32,
    AddUnsigned32, SubtractUnsigned32, MultiplyUnsigned32, ShiftLeft32, ShiftRight32, ShiftRightLogical32, CheckSign32,
    Wrap16, Wrap8, Check8, CheckUnsigned16, ToUnsigned32, Saturate, Minimum, Maximum, RoundAway, RoundHalfUp,
    CodeUnitAt, Substring, IndexOf, Repeat, CompareOrdinal, NumberText, FormatDouble, FormatSingle, ParseDouble
  ];

  /// <summary>The helpers a helper calls.</summary>
  /// <param name="name">The helper.</param>
  /// <returns>The names.</returns>
  public static IReadOnlyList<string> Needs(string name) => _needs.TryGetValue(name, out var needed) ? needed : [];

  /// <summary>The declaration of a fixed helper.</summary>
  /// <param name="name">The helper.</param>
  /// <returns>The procedure.</returns>
  public static VbProcedure Declaration(string name) => _declarations.Value[name];

}

/// <summary>
/// The helpers of an array, one set per element type: its length, which an array without elements
/// has no bounds for, and a new array of a length, which <c>ReDim</c> cannot make empty. Their text is
/// a template whose element type and name suffix are filled in.
/// </summary>
internal static class ArrayHelpers {

  /// <summary>The prefix of the length helper of an element type.</summary>
  public const string Length = "PlyLen_";

  /// <summary>The prefix of the new-array helper of an element type.</summary>
  public const string New = "PlyNew_";

  /// <summary>The prefix of the range check of a copy.</summary>
  public const string Copy = "PlyCopy_";

  /// <summary>The prefix of the fill helper.</summary>
  public const string Fill = "PlyFill_";

  private const string Template = """
    Private Function PlyLen_S(a() As T) As Long
        On Error GoTo PlyNone
        PlyLen_S = UBound(a) - LBound(a) + 1
        Exit Function
    PlyNone:
        PlyLen_S = 0
    End Function

    Private Function PlyNew_S(ByVal n As Long) As Variant
        Dim r() As T
        If n < 0 Then
            Err.Raise 6
        End If
        If n > 0 Then
            ReDim r(0 To n - 1)
        End If
        PlyNew_S = r
    End Function

    Private Sub PlyCopy_S(source() As T, ByVal sourceIndex As Long, target() As T, ByVal targetIndex As Long, ByVal count As Long)
        Dim i As Long
        If count < 0 Or sourceIndex < 0 Or targetIndex < 0 Or sourceIndex > PlyLen_S(source) - count Or targetIndex > PlyLen_S(target) - count Then
            Err.Raise 9
        End If
        If targetIndex <= sourceIndex Then
            For i = 0 To count - 1
                target(targetIndex + i) = source(sourceIndex + i)
            Next i
        Else
            For i = count - 1 To 0 Step -1
                target(targetIndex + i) = source(sourceIndex + i)
            Next i
        End If
    End Sub

    Private Sub PlyFill_S(target() As T, ByVal value As T)
        Dim i As Long
        For i = 0 To PlyLen_S(target) - 1
            target(i) = value
        Next i
    End Sub
    """;

  /// <summary>The helpers of one element type, by their names.</summary>
  /// <param name="type">The element type, as Visual Basic names it.</param>
  /// <param name="suffix">The suffix of the helpers' names.</param>
  /// <returns>The procedures, by name.</returns>
  public static IReadOnlyDictionary<string, VbProcedure> For(string type, string suffix) {
    var text = Template.Replace("_S", "_" + suffix, StringComparison.Ordinal).Replace("As T", "As " + type, StringComparison.Ordinal);
    return VbSyntaxReader.ReadDeclarations(text).Cast<VbProcedure>().ToDictionary(procedure => procedure.Name, procedure => procedure with { IsHelper = true }, StringComparer.Ordinal);
  }

  /// <summary>What one helper of a set needs of the same set.</summary>
  /// <param name="prefix">The helper's prefix.</param>
  /// <returns>The prefixes it needs.</returns>
  public static IReadOnlyList<string> Needs(string prefix) => prefix is Copy or Fill ? [Length] : [];

}
