namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>
  /// The helpers of <c>Currency</c> and <c>Date</c>, in the subset. <c>Currency</c>: the conversion of a
  /// <c>Double</c> rounds <c>x * 10000</c> half to even as oleaut32's <c>VarCyFromR8</c> does, the
  /// product is the exact product of the ten thousandths divided by 10000 with a tie to even, every
  /// result outside the 64 bits error 6; its text has the digits of the amount without trailing zeros.
  /// <c>Date</c>: <c>DateSerial</c> and the parts of a date count days with Howard Hinnant's
  /// <c>days_from_civil</c> and <c>civil_from_days</c> (public domain) from 1899-12-30; the time is
  /// rounded to the second. A dynamic <c>Variant</c> that may hold a <c>Currency</c> goes through the
  /// helpers with a <c>C</c> at the end, which fall back to those of the other kinds, so that a module
  /// without <c>Currency</c> carries no 64-bit integer.
  /// </summary>
  private const string CurrencyDateHelperSource = """
    Private Function PlyCurFromD(ByVal x As Double) As LongLong
        PlyCurFromD = x * 10000
    End Function

    Private Function PlyCurDbl(ByVal c As LongLong) As Double
        PlyCurDbl = c / 10000
    End Function

    Private Function PlyCurInt(ByVal c As LongLong) As LongLong
        Dim q As LongLong, r As LongLong
        q = c \ 10000
        r = c Mod 10000
        If r > 5000 Or (r = 5000 And q Mod 2 <> 0) Then q = q + 1
        If r < -5000 Or (r = -5000 And q Mod 2 <> 0) Then q = q - 1
        PlyCurInt = q
    End Function

    Private Function PlyCurMul(ByVal a As LongLong, ByVal b As LongLong) As LongLong
        Dim qa As LongLong, ra As LongLong, qb As LongLong, rb As LongLong, t As LongLong, w As LongLong, f As LongLong
        qa = a \ 10000
        ra = a Mod 10000
        qb = b \ 10000
        rb = b Mod 10000
        t = ra * rb
        w = qa * qb * 10000 + qa * rb + ra * qb + t \ 10000
        f = t Mod 10000
        If f > 5000 Or (f = 5000 And w Mod 2 <> 0) Then w = w + 1
        If f < -5000 Or (f = -5000 And w Mod 2 <> 0) Then w = w - 1
        PlyCurMul = w
    End Function

    Private Function PlyCurText(ByVal c As LongLong) As String
        Dim q As LongLong, r As LongLong, s As String, d As String
        q = c \ 10000
        r = c Mod 10000
        If r < 0 Then r = -r
        s = CStr(q)
        If c < 0 And q = 0 Then s = "-0"
        If r <> 0 Then
            d = CStr(r + 10000)
            Do While PlyIrCodeUnit(d, Len(d) - 1) = 48
                d = PlyIrSubstring(d, 0, Len(d) - 1)
            Loop
            s = s & PlyIrFromCodeUnit(PlyIrDec()) & PlyIrSubstring(d, 1, Len(d) - 1)
        End If
        PlyCurText = s
    End Function

    Private Function PlyDateCheck(ByVal x As Double) As Double
        If x < -657434 Or x >= 2958466 Then Error 6
        PlyDateCheck = x
    End Function

    Private Function PlyCivil(ByVal y As Double, ByVal m As Double, ByVal d As Double) As Double
        Dim era As Double, yoe As Double, doy As Double
        If m <= 2 Then y = y - 1
        era = Int(y / 400)
        yoe = y - era * 400
        If m > 2 Then
            doy = Int((153 * (m - 3) + 2) / 5) + d - 1
        Else
            doy = Int((153 * (m + 9) + 2) / 5) + d - 1
        End If
        PlyCivil = era * 146097 + yoe * 365 + Int(yoe / 4) - Int(yoe / 100) + doy - 693899
    End Function

    Private Function PlyDateSerial(ByVal y As Long, ByVal m As Long, ByVal d As Long) As Double
        Dim k As Double, days As Double
        If y >= 0 And y <= 29 Then
            y = y + 2000
        ElseIf y >= 30 And y <= 99 Then
            y = y + 1900
        End If
        k = Int((m - 1) / 12)
        days = PlyCivil(y + k, m - 12 * k, 1) + d - 1
        If days < -657434 Or days > 2958465 Then Error 5
        PlyDateSerial = days
    End Function

    Private Function PlyTimeSerial(ByVal h As Long, ByVal m As Long, ByVal s As Long) As Double
        Dim t As Double, days As Double
        t = h * 3600# + m * 60# + s
        days = Int(t / 86400)
        t = t - days * 86400
        If days < 0 Then
            PlyTimeSerial = days - t / 86400
        Else
            PlyTimeSerial = days + t / 86400
        End If
    End Function

    Private Function PlyDateSecs(ByVal v As Double) As Double
        Dim s As Double
        s = Int(Abs(v - Fix(v)) * 86400 + 0.5)
        If s >= 86400 Then s = s - 86400
        PlyDateSecs = s
    End Function

    Private Function PlyDateDays(ByVal v As Double) As Double
        PlyDateDays = Fix(v)
        If Int(Abs(v - Fix(v)) * 86400 + 0.5) >= 86400 Then PlyDateDays = Fix(v) + 1
    End Function

    Private Function PlyDatePart(ByVal v As Double, ByVal part As Integer) As Integer
        Dim z As Double, era As Double, doe As Double, yoe As Double, doy As Double, mp As Double, y As Double, m As Double, s As Double
        If part >= 4 Then
            s = PlyDateSecs(v)
            If part = 4 Then
                PlyDatePart = Int(s / 3600)
            ElseIf part = 5 Then
                PlyDatePart = Int(s / 60) - 60 * Int(s / 3600)
            Else
                PlyDatePart = s - 60 * Int(s / 60)
            End If
            Exit Function
        End If
        z = PlyDateDays(v) + 693899
        era = Int(z / 146097)
        doe = z - era * 146097
        yoe = Int((doe - Int(doe / 1460) + Int(doe / 36524) - Int(doe / 146096)) / 365)
        doy = doe - (365 * yoe + Int(yoe / 4) - Int(yoe / 100))
        mp = Int((5 * doy + 2) / 153)
        If mp < 10 Then
            m = mp + 3
        Else
            m = mp - 9
        End If
        y = yoe + era * 400
        If m <= 2 Then y = y + 1
        If part = 1 Then
            PlyDatePart = y
        ElseIf part = 2 Then
            PlyDatePart = m
        Else
            PlyDatePart = doy - Int((153 * mp + 2) / 5) + 1
        End If
    End Function

    Private Function PlyWeekday(ByVal v As Double, ByVal first As Integer) As Integer
        Dim d As Double
        If first = 0 Then first = 1
        If first < 1 Or first > 7 Then Error 5
        d = PlyDateDays(v) + 6 - (first - 1)
        PlyWeekday = d - 7 * Int(d / 7) + 1
    End Function

    Private Function PlyVarDate(ByVal v As Variant) As Double
        If PlyIrVarType(v) = 7 Then
            PlyVarDate = PlyIrVarDate(v)
        Else
            PlyVarDate = PlyDateCheck(PlyVarDbl(v))
        End If
    End Function

    Private Function PlyVarCur(ByVal v As Variant) As LongLong
        Dim t As Integer, n As LongLong
        t = PlyIrVarType(v)
        If t = 6 Then
            PlyVarCur = PlyIrVarCur(v)
        ElseIf t = 0 Or t = 2 Or t = 3 Or t = 11 Or t = 17 Then
            n = PlyVarLong(v)
            PlyVarCur = n * 10000
        Else
            PlyVarCur = PlyCurFromD(PlyVarDbl(v))
        End If
    End Function

    Private Function PlyVarDblC(ByVal v As Variant) As Double
        If PlyIrVarType(v) = 6 Then
            PlyVarDblC = PlyCurDbl(PlyIrVarCur(v))
        Else
            PlyVarDblC = PlyVarDbl(v)
        End If
    End Function

    Private Function PlyVarArithC(ByVal op As Integer, ByVal a As Variant, ByVal b As Variant) As Variant
        Dim ta As Integer, tb As Integer
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 1 Or tb = 1 Then
            PlyVarArithC = Null
        ElseIf ta = 7 Or tb = 7 Or (ta <> 6 And tb <> 6) Then
            If ta = 6 Then a = PlyVarDblC(a)
            If tb = 6 Then b = PlyVarDblC(b)
            PlyVarArithC = PlyVarArith(op, a, b)
        ElseIf op = 0 And ta = 0 Then
            PlyVarArithC = b
        ElseIf op = 0 And tb = 0 Then
            PlyVarArithC = a
        ElseIf op = 2 And (ta = 4 Or ta = 5 Or ta = 8 Or tb = 4 Or tb = 5 Or tb = 8) Then
            PlyVarArithC = PlyCheckD(PlyVarDblC(a) * PlyVarDblC(b))
        ElseIf op = 0 Then
            PlyVarArithC = PlyIrMkCur(PlyVarCur(a) + PlyVarCur(b))
        ElseIf op = 1 Then
            PlyVarArithC = PlyIrMkCur(PlyVarCur(a) - PlyVarCur(b))
        Else
            PlyVarArithC = PlyIrMkCur(PlyCurMul(PlyVarCur(a), PlyVarCur(b)))
        End If
    End Function

    Private Function PlyVarNegC(ByVal a As Variant) As Variant
        If PlyIrVarType(a) = 6 Then
            PlyVarNegC = PlyIrMkCur(-PlyIrVarCur(a))
        Else
            PlyVarNegC = PlyVarNeg(a)
        End If
    End Function

    Private Function PlyVarDivC(ByVal a As Variant, ByVal b As Variant) As Variant
        If PlyIrVarType(a) = 1 Or PlyIrVarType(b) = 1 Then
            PlyVarDivC = Null
        ElseIf PlyIrVarType(a) = 6 Or PlyIrVarType(b) = 6 Then
            PlyVarDivC = PlyDivD(PlyVarDblC(a), PlyVarDblC(b))
        Else
            PlyVarDivC = PlyVarDiv(a, b)
        End If
    End Function

    Private Function PlyVarCmpC(ByVal a As Variant, ByVal b As Variant) As Long
        If PlyIrVarType(a) = 6 Then a = PlyVarDblC(a)
        If PlyIrVarType(b) = 6 Then b = PlyVarDblC(b)
        PlyVarCmpC = PlyVarCmp(a, b)
    End Function

    Private Function PlyVarTextC(ByVal v As Variant) As String
        If PlyIrVarType(v) = 6 Then
            PlyVarTextC = PlyCurText(PlyIrVarCur(v))
        Else
            PlyVarTextC = PlyVarText(v)
        End If
    End Function

    Private Function PlyVarCStrC(ByVal v As Variant) As String
        If PlyIrVarType(v) = 1 Then Error 94
        PlyVarCStrC = PlyVarTextC(v)
    End Function
    """;

}
