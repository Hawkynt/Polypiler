namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>
  /// The conversions of text to numbers, written in the subset: <c>PlyNumText</c> reads a text as
  /// [MS-VBAL] §5.5.1.2.4's numeric-coercion-string - blanks, a sign, digits with the locale's digit
  /// group separator, the locale's decimal separator, an exponent after <c>E</c> or <c>D</c>, or
  /// <c>&amp;H</c>/<c>&amp;O</c> and its digits - and gives the number as the canonical text the IR's
  /// <c>ParseDotNet</c> reads exactly, or <c>""</c> for a text of no number; <c>PlyIrDec</c> and
  /// <c>PlyIrGrp</c> are the two separators of the locale of the import. <c>PlyVal</c> is <c>Val</c>,
  /// which reads every locale alike: blanks anywhere, a point, the longest number at the start.
  /// </summary>
  private const string LocaleHelperSource = """
    Private Function PlyNumBlank(ByVal s As String, ByVal i As Long) As Long
        Do While i < Len(s)
            If PlyIrCodeUnit(s, i) <> 32 And PlyIrCodeUnit(s, i) <> 9 Then Exit Do
            i = i + 1
        Loop
        PlyNumBlank = i
    End Function

    Private Function PlyNumRadix(ByVal s As String, ByVal i As Long, ByVal strict As Boolean) As String
        Dim n As Long, c As Long, b As Long, d As Long, v As Double
        n = Len(s)
        b = 8
        If i < n Then
            c = PlyIrCodeUnit(s, i) Or 32
            If c = 104 Then
                b = 16
                i = i + 1
            ElseIf c = 111 Then
                i = i + 1
            End If
        End If
        Do While i < n
            c = PlyIrCodeUnit(s, i)
            If c >= 48 And c <= 57 And c - 48 < b Then
                c = c - 48
            ElseIf b = 16 And (c Or 32) >= 97 And (c Or 32) <= 102 Then
                c = (c Or 32) - 87
            Else
                Exit Do
            End If
            v = v * b + c
            d = d + 1
            i = i + 1
        Loop
        If strict Then
            If d = 0 Or PlyNumBlank(s, i) < n Then Exit Function
        End If
        If v > 4294967295# Then Error 6
        If v > 65535 Then
            If v >= 2147483648# Then v = v - 4294967296#
        ElseIf v >= 32768 Then
            v = v - 65536
        End If
        PlyNumRadix = CStr(CLng(v))
    End Function

    Private Function PlyNumText(ByVal s As String) As String
        Dim i As Long, n As Long, c As Long, t As String, d As Long, e As Long, dec As Long, grp As Long
        dec = PlyIrDec()
        grp = PlyIrGrp()
        n = Len(s)
        i = PlyNumBlank(s, 0)
        If i < n Then
            c = PlyIrCodeUnit(s, i)
            If c = 38 Then
                PlyNumText = PlyNumRadix(s, i + 1, True)
                Exit Function
            End If
            If c = 43 Or c = 45 Then
                If c = 45 Then t = "-"
                i = PlyNumBlank(s, i + 1)
            End If
        End If
        Do While i < n
            c = PlyIrCodeUnit(s, i)
            If c >= 48 And c <= 57 Then
                t = t & PlyIrFromCodeUnit(c)
                d = d + 1
            ElseIf c <> grp Or d = 0 Then
                Exit Do
            End If
            i = i + 1
        Loop
        If i < n Then
            If PlyIrCodeUnit(s, i) = dec Then
                t = t & "."
                i = i + 1
                Do While i < n
                    c = PlyIrCodeUnit(s, i)
                    If c < 48 Or c > 57 Then Exit Do
                    t = t & PlyIrFromCodeUnit(c)
                    d = d + 1
                    i = i + 1
                Loop
            End If
        End If
        If d = 0 Then Exit Function
        i = PlyNumBlank(s, i)
        If i < n Then
            c = PlyIrCodeUnit(s, i) Or 32
            If c = 101 Or c = 100 Then
                t = t & "e"
                i = PlyNumBlank(s, i + 1)
                If i < n Then
                    c = PlyIrCodeUnit(s, i)
                    If c = 43 Or c = 45 Then
                        If c = 45 Then t = t & "-"
                        i = PlyNumBlank(s, i + 1)
                    End If
                End If
                Do While i < n
                    c = PlyIrCodeUnit(s, i)
                    If c < 48 Or c > 57 Then Exit Do
                    t = t & PlyIrFromCodeUnit(c)
                    e = e + 1
                    i = i + 1
                Loop
                If e = 0 Then Exit Function
                i = PlyNumBlank(s, i)
            End If
        End If
        If i = n Then PlyNumText = t
    End Function

    Private Function PlyCDbl(ByVal s As String) As Double
        Dim t As String, x As Double
        t = PlyNumText(s)
        If Len(t) = 0 Then Error 13
        x = PlyIrParse(t)
        If x - x <> 0 Then Error 6
        PlyCDbl = x
    End Function

    Private Function PlyCSng(ByVal s As String) As Single
        Dim t As String, x As Single
        t = PlyNumText(s)
        If Len(t) = 0 Then Error 13
        x = PlyIrParseS(t)
        If x - x <> 0 Then Error 6
        PlyCSng = x
    End Function

    Private Function PlyTextIs(ByVal s As String, ByVal w As String) As Boolean
        Dim i As Long
        If Len(s) <> Len(w) Then Exit Function
        For i = 0 To Len(s) - 1
            If (PlyIrCodeUnit(s, i) Or 32) <> PlyIrCodeUnit(w, i) Then Exit Function
        Next
        PlyTextIs = True
    End Function

    Private Function PlyTextBool(ByVal s As String) As Boolean
        If PlyTextIs(s, "true") Or s = "#TRUE#" Then
            PlyTextBool = True
        ElseIf Not PlyTextIs(s, "false") And s <> "#FALSE#" Then
            PlyTextBool = PlyCDbl(s) <> 0
        End If
    End Function

    Private Function PlyIsNumText(ByVal s As String) As Boolean
        PlyIsNumText = Len(PlyNumText(s)) > 0
    End Function

    Private Function PlyVal(ByVal s As String) As Double
        Dim i As Long, n As Long, c As Long, t As String, k As String, d As Long, j As Long, x As Double
        For i = 0 To Len(s) - 1
            c = PlyIrCodeUnit(s, i)
            If c <> 32 And c <> 9 And c <> 10 Then k = k & PlyIrFromCodeUnit(c)
        Next
        n = Len(k)
        i = 0
        If n > 0 Then
            c = PlyIrCodeUnit(k, 0)
            If c = 38 Then
                PlyVal = CLng(PlyNumRadix(k, 1, False))
                Exit Function
            End If
            If c = 43 Or c = 45 Then
                If c = 45 Then t = "-"
                i = 1
            End If
        End If
        Do While i < n
            c = PlyIrCodeUnit(k, i)
            If c < 48 Or c > 57 Then Exit Do
            t = t & PlyIrFromCodeUnit(c)
            d = d + 1
            i = i + 1
        Loop
        If i < n Then
            If PlyIrCodeUnit(k, i) = 46 Then
                t = t & "."
                i = i + 1
                Do While i < n
                    c = PlyIrCodeUnit(k, i)
                    If c < 48 Or c > 57 Then Exit Do
                    t = t & PlyIrFromCodeUnit(c)
                    d = d + 1
                    i = i + 1
                Loop
            End If
        End If
        If d = 0 Then Exit Function
        If i < n Then
            c = PlyIrCodeUnit(k, i) Or 32
            If c = 101 Or c = 100 Then
                j = i + 1
                If j < n Then
                    c = PlyIrCodeUnit(k, j)
                    If c = 43 Or c = 45 Then j = j + 1
                End If
                If j < n Then
                    c = PlyIrCodeUnit(k, j)
                    If c >= 48 And c <= 57 Then
                        t = t & "e"
                        If PlyIrCodeUnit(k, j - 1) = 45 Then t = t & "-"
                        Do While j < n
                            c = PlyIrCodeUnit(k, j)
                            If c < 48 Or c > 57 Then Exit Do
                            t = t & PlyIrFromCodeUnit(c)
                            j = j + 1
                        Loop
                    End If
                End If
            End If
        End If
        x = PlyIrParse(t)
        If x - x <> 0 Then Error 6
        PlyVal = x
    End Function
    """;

}
