namespace Polypiler.Core.Frontends.VisualBasic.Lowering;

internal sealed partial class VbaLowerer {

  /// <summary>
  /// The operators of a dynamic <c>Variant</c>, written in the subset over the IR's three operations
  /// on the dynamic type: <c>PlyIrVarType</c> reads the tag (the <c>VarType</c> number) and
  /// <c>PlyIrVarI16</c>, <c>PlyIrVarStr</c>, … the payload of a tag already tested. They compute what
  /// [MS-VBAL] §5.6.9 and the VBA reference of the operators state: arithmetic in the wider of the
  /// two types, a result that overflows its type promoted to the next wider one (<c>Byte</c>,
  /// <c>Integer</c>, <c>Long</c>, <c>Double</c>; <c>Single</c> to <c>Double</c>), <c>Empty</c> as 0 or
  /// <c>""</c>, <c>Null</c> through every operator but <c>&amp;</c>. A <c>String</c> that meets a
  /// number, and the text of a floating-point number, depend on the locale: the frontend refuses the
  /// code that could reach them, and the helpers raise error 13 there.
  /// </summary>
  private const string VariantHelperSource = """
    Private Function PlyVarDbl(ByVal v As Variant) As Double
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 2 Then
            PlyVarDbl = PlyIrVarI16(v)
        ElseIf t = 3 Then
            PlyVarDbl = PlyIrVarI32(v)
        ElseIf t = 5 Then
            PlyVarDbl = PlyIrVarF64(v)
        ElseIf t = 4 Then
            PlyVarDbl = PlyIrVarF32(v)
        ElseIf t = 17 Then
            PlyVarDbl = PlyIrVarU8(v)
        ElseIf t = 11 Then
            If PlyIrVarBool(v) Then PlyVarDbl = -1
        ElseIf t = 8 Then
            PlyVarDbl = PlyCDbl(PlyIrVarStr(v))
        ElseIf t = 7 Then
            PlyVarDbl = PlyIrVarDate(v)
        ElseIf t = 1 Then
            Error 94
        ElseIf t <> 0 Then
            Error 13
        End If
    End Function

    Private Function PlyVarRank(ByVal t As Integer) As Integer
        If t = 17 Then
            PlyVarRank = 1
        ElseIf t = 0 Or t = 2 Or t = 11 Then
            PlyVarRank = 2
        ElseIf t = 3 Then
            PlyVarRank = 3
        ElseIf t = 4 Then
            PlyVarRank = 5
        ElseIf t = 5 Or t = 8 Then
            PlyVarRank = 6
        ElseIf t = 7 Then
            PlyVarRank = 8
        End If
    End Function

    Private Function PlyVarFromWide(ByVal z As Double, ByVal r As Integer) As Variant
        If r = 1 And z >= 0 And z <= 255 Then
            PlyVarFromWide = CByte(z)
        ElseIf r <= 2 And z >= -32768 And z <= 32767 Then
            PlyVarFromWide = CInt(z)
        ElseIf r <= 3 And z >= -2147483648# And z <= 2147483647# Then
            PlyVarFromWide = CLng(z)
        ElseIf r = 5 And z > -3.4028235677973366E+38 And z < 3.4028235677973366E+38 Then
            PlyVarFromWide = CSng(z)
        Else
            PlyVarFromWide = PlyCheckD(z)
        End If
    End Function

    Private Function PlyVarArith(ByVal op As Integer, ByVal a As Variant, ByVal b As Variant) As Variant
        Dim ta As Integer, tb As Integer, r As Integer, rb As Integer, x As Double, y As Double, z As Double
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 1 Or tb = 1 Then
            PlyVarArith = Null
            Exit Function
        End If
        If op = 0 Then
            If ta = 8 And tb = 8 Then
                PlyVarArith = PlyIrVarStr(a) & PlyIrVarStr(b)
                Exit Function
            End If
            If ta = 0 And tb <> 0 Then
                PlyVarArith = b
                Exit Function
            End If
            If tb = 0 And ta <> 0 Then
                PlyVarArith = a
                Exit Function
            End If
        End If
        r = PlyVarRank(ta)
        rb = PlyVarRank(tb)
        If r = 0 Or rb = 0 Then Error 13
        If rb > r Then r = rb
        If r = 5 And (ta = 3 Or tb = 3) Then r = 6
        x = PlyVarDbl(a)
        y = PlyVarDbl(b)
        If r = 8 Then
            If op = 2 Or (op = 1 And ta = 7 And tb = 7) Then
                r = 6
            Else
                If op = 0 Then
                    z = PlyCheckD(x + y)
                Else
                    z = PlyCheckD(x - y)
                End If
                If z < -657434 Or z >= 2958466 Then
                    PlyVarArith = z
                Else
                    PlyVarArith = PlyIrMkDate(z)
                End If
                Exit Function
            End If
        End If
        If op = 0 Then
            PlyVarArith = PlyVarFromWide(x + y, r)
        ElseIf op = 1 Then
            PlyVarArith = PlyVarFromWide(x - y, r)
        Else
            PlyVarArith = PlyVarFromWide(x * y, r)
        End If
    End Function

    Private Function PlyVarNeg(ByVal a As Variant) As Variant
        Dim r As Integer
        If PlyIrVarType(a) = 1 Then
            PlyVarNeg = Null
            Exit Function
        End If
        r = PlyVarRank(PlyIrVarType(a))
        If r = 0 Then Error 13
        If r < 2 Then r = 2
        If r = 8 Then
            PlyVarNeg = -PlyVarDbl(a)
            If PlyVarDbl(a) <= 657434 Then PlyVarNeg = PlyIrMkDate(-PlyVarDbl(a))
        Else
            PlyVarNeg = PlyVarFromWide(-PlyVarDbl(a), r)
        End If
    End Function

    Private Function PlyVarDiv(ByVal a As Variant, ByVal b As Variant) As Variant
        Dim ta As Integer, tb As Integer, ra As Integer, rb As Integer
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 1 Or tb = 1 Then
            PlyVarDiv = Null
            Exit Function
        End If
        ra = PlyVarRank(ta)
        rb = PlyVarRank(tb)
        If ra = 0 Or rb = 0 Then Error 13
        If (ta = 4 Or tb = 4) And ra <> 3 And ra < 6 And rb <> 3 And rb < 6 Then
            PlyVarDiv = PlyDivS(PlyVarDbl(a), PlyVarDbl(b))
        Else
            PlyVarDiv = PlyDivD(PlyVarDbl(a), PlyVarDbl(b))
        End If
    End Function

    Private Function PlyVarPow(ByVal a As Variant, ByVal b As Variant) As Variant
        If PlyIrVarType(a) = 1 Or PlyIrVarType(b) = 1 Then
            PlyVarPow = Null
        Else
            PlyVarPow = PlyPow(PlyVarDbl(a), PlyVarDbl(b))
        End If
    End Function

    Private Function PlyVarWhole(ByVal v As Variant) As Double
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 4 Or t = 5 Or t = 7 Or t = 8 Then
            PlyVarWhole = CLng(PlyVarDbl(v))
        Else
            PlyVarWhole = PlyVarDbl(v)
        End If
    End Function

    Private Function PlyVarIntDiv(ByVal op As Integer, ByVal a As Variant, ByVal b As Variant) As Variant
        Dim ta As Integer, tb As Integer, r As Integer, rb As Integer, x As Double, y As Double, q As Double
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 1 Or tb = 1 Then
            PlyVarIntDiv = Null
            Exit Function
        End If
        r = PlyVarRank(ta)
        rb = PlyVarRank(tb)
        If r = 0 Or rb = 0 Then Error 13
        If r > 3 Then r = 3
        If rb > 3 Then rb = 3
        If rb > r Then r = rb
        x = PlyVarWhole(a)
        y = PlyVarWhole(b)
        If y = 0 Then Error 11
        q = Fix(x / y)
        If op = 0 Then
            PlyVarIntDiv = PlyVarFromWide(q, r)
        Else
            PlyVarIntDiv = PlyVarFromWide(x - y * q, r)
        End If
    End Function

    Private Function PlyVarText(ByVal v As Variant) As String
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 8 Then
            PlyVarText = PlyIrVarStr(v)
        ElseIf t = 11 Then
            If PlyIrVarBool(v) Then
                PlyVarText = "True"
            Else
                PlyVarText = "False"
            End If
        ElseIf t = 2 Then
            PlyVarText = CStr(PlyIrVarI16(v))
        ElseIf t = 3 Then
            PlyVarText = CStr(PlyIrVarI32(v))
        ElseIf t = 17 Then
            PlyVarText = CStr(PlyIrVarU8(v))
        ElseIf t = 5 Then
            PlyVarText = PlyIrFormat(PlyIrVarF64(v))
        ElseIf t = 4 Then
            PlyVarText = PlyIrFormat(PlyIrVarF32(v))
        ElseIf t > 1 Then
            Error 13
        End If
    End Function

    Private Function PlyVarCStr(ByVal v As Variant) As String
        If PlyIrVarType(v) = 1 Then Error 94
        PlyVarCStr = PlyVarText(v)
    End Function

    Private Function PlyVarConcat(ByVal a As Variant, ByVal b As Variant) As Variant
        If PlyIrVarType(a) = 1 And PlyIrVarType(b) = 1 Then
            PlyVarConcat = Null
        Else
            PlyVarConcat = PlyVarText(a) & PlyVarText(b)
        End If
    End Function

    Private Function PlyVarCmp(ByVal a As Variant, ByVal b As Variant) As Long
        Dim ta As Integer, tb As Integer, x As Double, y As Double
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 1 Or tb = 1 Then
            PlyVarCmp = 2
        ElseIf ta = 8 And tb = 8 Then
            PlyVarCmp = StrComp(PlyIrVarStr(a), PlyIrVarStr(b))
        ElseIf ta = 8 Then
            If tb = 0 Then
                PlyVarCmp = StrComp(PlyIrVarStr(a), "")
            Else
                PlyVarCmp = 1
            End If
        ElseIf tb = 8 Then
            If ta = 0 Then
                PlyVarCmp = StrComp("", PlyIrVarStr(b))
            Else
                PlyVarCmp = -1
            End If
        Else
            x = PlyVarDbl(a)
            y = PlyVarDbl(b)
            If x < y Then
                PlyVarCmp = -1
            ElseIf x > y Then
                PlyVarCmp = 1
            End If
        End If
    End Function

    Private Function PlyVarCmpT(ByVal a As Variant, ByVal b As Variant) As Long
        Dim ta As Integer, tb As Integer
        ta = PlyIrVarType(a)
        tb = PlyIrVarType(b)
        If ta = 8 And tb <> 8 And tb > 1 Then a = PlyCDbl(PlyIrVarStr(a))
        If tb = 8 And ta <> 8 And ta > 1 Then b = PlyCDbl(PlyIrVarStr(b))
        PlyVarCmpT = PlyVarCmp(a, b)
    End Function

    Private Function PlyVarCmpOp(ByVal c As Long, ByVal op As Integer) As Variant
        If c = 2 Then
            PlyVarCmpOp = Null
        ElseIf op = 0 Then
            PlyVarCmpOp = c = 0
        ElseIf op = 1 Then
            PlyVarCmpOp = c <> 0
        ElseIf op = 2 Then
            PlyVarCmpOp = c < 0
        ElseIf op = 3 Then
            PlyVarCmpOp = c <= 0
        ElseIf op = 4 Then
            PlyVarCmpOp = c > 0
        Else
            PlyVarCmpOp = c >= 0
        End If
    End Function

    Private Function PlyVarBool(ByVal v As Variant) As Boolean
        If PlyIrVarType(v) <> 11 Then Error 13
        PlyVarBool = PlyIrVarBool(v)
    End Function

    Private Function PlyVarLogic(ByVal op As Integer, ByVal a As Variant, ByVal b As Variant) As Variant
        Dim na As Boolean, nb As Boolean, x As Boolean, y As Boolean
        na = PlyIrVarType(a) = 1
        nb = PlyIrVarType(b) = 1
        If Not na Then x = PlyVarBool(a)
        If Not nb Then y = PlyVarBool(b)
        If op = 0 Then
            If (Not na And Not x) Or (Not nb And Not y) Then
                PlyVarLogic = False
            ElseIf na Or nb Then
                PlyVarLogic = Null
            Else
                PlyVarLogic = True
            End If
        ElseIf op = 1 Then
            If (Not na And x) Or (Not nb And y) Then
                PlyVarLogic = True
            ElseIf na Or nb Then
                PlyVarLogic = Null
            Else
                PlyVarLogic = False
            End If
        ElseIf na Or nb Then
            PlyVarLogic = Null
        Else
            PlyVarLogic = x <> y
        End If
    End Function

    Private Function PlyVarNot(ByVal a As Variant) As Variant
        If PlyIrVarType(a) = 1 Then
            PlyVarNot = Null
        Else
            PlyVarNot = Not PlyVarBool(a)
        End If
    End Function

    Private Function PlyVarLong(ByVal v As Variant) As Long
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 11 Then
            If PlyIrVarBool(v) Then PlyVarLong = -1
        ElseIf t <> 0 Then
            PlyVarLong = PlyVarDbl(v)
        End If
    End Function

    Private Function PlyVarBitKind(ByVal t As Integer) As Integer
        If t = 17 Then
            PlyVarBitKind = 1
        ElseIf t = 11 Then
            PlyVarBitKind = 2
        ElseIf t = 0 Or t = 2 Then
            PlyVarBitKind = 3
        ElseIf t <> 1 Then
            PlyVarBitKind = 4
        End If
    End Function

    Private Function PlyVarBitOf(ByVal r As Long, ByVal k As Integer) As Variant
        If k = 1 Then
            PlyVarBitOf = CByte(r And 255)
        ElseIf k = 2 Then
            PlyVarBitOf = r <> 0
        ElseIf k = 3 Then
            PlyVarBitOf = CInt(r)
        Else
            PlyVarBitOf = r
        End If
    End Function

    Private Function PlyVarBits(ByVal op As Integer, ByVal a As Variant, ByVal b As Variant) As Variant
        Dim ka As Integer, kb As Integer, k As Integer, x As Long, y As Long, r As Long
        ka = PlyVarBitKind(PlyIrVarType(a))
        kb = PlyVarBitKind(PlyIrVarType(b))
        If ka = 0 And kb = 0 Then
            PlyVarBits = Null
            Exit Function
        End If
        If ka = 0 Or kb = 0 Then
            If ka = 0 Then
                k = kb
                y = PlyVarLong(b)
            Else
                k = ka
                x = PlyVarLong(a)
            End If
            PlyVarBits = Null
            If op = 0 Then
                If x + y = 0 Then PlyVarBits = PlyVarBitOf(0, k)
            ElseIf op = 1 Then
                If x + y <> 0 Then PlyVarBits = PlyVarBitOf(x + y, k)
            ElseIf op = 4 Then
                If ka = 0 Then
                    If y <> 0 Then PlyVarBits = PlyVarBitOf(y, k)
                ElseIf x <> -1 Then
                    PlyVarBits = PlyVarBitOf(Not x, k)
                End If
            End If
            Exit Function
        End If
        k = ka
        If kb <> ka Then
            If ka = 4 Or kb = 4 Then
                k = 4
            Else
                k = 3
            End If
        End If
        x = PlyVarLong(a)
        y = PlyVarLong(b)
        If op = 0 Then
            r = x And y
        ElseIf op = 1 Then
            r = x Or y
        ElseIf op = 2 Then
            r = x Xor y
        ElseIf op = 3 Then
            r = Not (x Xor y)
        Else
            r = (Not x) Or y
        End If
        PlyVarBits = PlyVarBitOf(r, k)
    End Function

    Private Function PlyVarBitNot(ByVal a As Variant) As Variant
        Dim k As Integer
        k = PlyVarBitKind(PlyIrVarType(a))
        If k = 0 Then
            PlyVarBitNot = Null
        Else
            PlyVarBitNot = PlyVarBitOf(Not PlyVarLong(a), k)
        End If
    End Function

    Private Function PlyVarIsTrue(ByVal v As Variant) As Boolean
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 11 Then
            PlyVarIsTrue = PlyIrVarBool(v)
        ElseIf t = 8 Then
            PlyVarIsTrue = PlyTextBool(PlyIrVarStr(v))
        ElseIf t <> 0 And t <> 1 Then
            PlyVarIsTrue = PlyVarDbl(v) <> 0
        End If
    End Function

    Private Function PlyVarCBool(ByVal v As Variant) As Boolean
        If PlyIrVarType(v) = 1 Then Error 94
        PlyVarCBool = PlyVarIsTrue(v)
    End Function

    Private Function PlyVarCByte(ByVal v As Variant) As Byte
        If PlyIrVarType(v) = 11 Then
            If PlyIrVarBool(v) Then PlyVarCByte = 255
        Else
            PlyVarCByte = PlyVarDbl(v)
        End If
    End Function

    Private Function PlyVarTypeName(ByVal v As Variant) As String
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 0 Then
            PlyVarTypeName = "Empty"
        ElseIf t = 1 Then
            PlyVarTypeName = "Null"
        ElseIf t = 2 Then
            PlyVarTypeName = "Integer"
        ElseIf t = 3 Then
            PlyVarTypeName = "Long"
        ElseIf t = 4 Then
            PlyVarTypeName = "Single"
        ElseIf t = 5 Then
            PlyVarTypeName = "Double"
        ElseIf t = 8 Then
            PlyVarTypeName = "String"
        ElseIf t = 11 Then
            PlyVarTypeName = "Boolean"
        ElseIf t = 17 Then
            PlyVarTypeName = "Byte"
        Else
            PlyVarTypeName = "LongLong"
        End If
        If t = 6 Then
            PlyVarTypeName = "Currency"
        ElseIf t = 7 Then
            PlyVarTypeName = "Date"
        End If
    End Function

    Private Function PlyVarIsNumeric(ByVal v As Variant) As Boolean
        Dim t As Integer
        t = PlyIrVarType(v)
        If t = 8 Then
            PlyVarIsNumeric = PlyIsNumText(PlyIrVarStr(v))
        Else
            PlyVarIsNumeric = t <> 1 And t <> 7
        End If
    End Function

    Private Function PlyVarForTest(ByVal v As Variant, ByVal e As Variant, ByVal s As Variant) As Boolean
        If PlyVarDbl(s) >= 0 Then
            PlyVarForTest = PlyVarCmp(v, e) <= 0
        Else
            PlyVarForTest = PlyVarCmp(v, e) >= 0
        End If
    End Function
    """;

}
