Attribute VB_Name = "ClassicProbe"
' The probes of spike #1097: one function per row of the construct table of #1090 and the
' error-handling probes of #1096. Each returns what Visual Basic 6 / VBA documents as a number or a
' text; Probes() lists them as "name=value" lines. Written for this repository.
Option Explicit

Private Function IntegerOverflow() As Long
    Dim i As Integer
    On Error Resume Next
    i = 32767
    i = i + 1
    IntegerOverflow = Err.Number
End Function

Private Function LongOverflow() As Long
    Dim l As Long
    On Error Resume Next
    l = 2147483647
    l = l + 1
    LongOverflow = Err.Number
End Function

Private Function ByteOverflow() As Long
    Dim b As Byte
    On Error Resume Next
    b = 0
    b = b - 1
    ByteOverflow = Err.Number
End Function

Private Function IntegerProductInLong() As Long
    Dim i As Integer, j As Integer, k As Long
    On Error Resume Next
    i = 200
    j = 200
    k = i * j
    IntegerProductInLong = Err.Number
End Function

Private Function IntegerSumInLong() As Long
    Dim i As Integer, k As Long
    On Error Resume Next
    i = 32767
    k = i + 1
    IntegerSumInLong = Err.Number
End Function

Private Function LongProductInDouble() As Long
    Dim a As Long, d As Double
    On Error Resume Next
    a = 100000
    d = a * a
    LongProductInDouble = Err.Number
End Function

Private Function ConvertedProduct() As Long
    Dim i As Integer, j As Integer, k As Long
    On Error Resume Next
    i = 200
    j = 200
    k = CInt(i * j)
    ConvertedProduct = Err.Number
End Function

Private Function SmallestByMinusOne() As Long
    Dim i As Integer
    On Error Resume Next
    i = -32768
    i = i \ -1
    SmallestByMinusOne = Err.Number
End Function

Private Function DivisionByZero() As Long
    Dim l As Long, z As Long
    On Error Resume Next
    l = 5 \ z
    DivisionByZero = Err.Number
End Function

Private Function FloatDivisionByZero() As Long
    Dim d As Double, z As Double
    On Error Resume Next
    d = 5 / z
    FloatDivisionByZero = Err.Number
End Function

Private Function ZeroByZero() As Long
    Dim d As Double, z As Double
    On Error Resume Next
    d = z / z
    ZeroByZero = Err.Number
End Function

Private Function FloatOverflow() As Long
    Dim d As Double
    On Error Resume Next
    d = 1E+300
    d = d * 1E+300
    FloatOverflow = Err.Number
End Function

Private Function SubscriptOutOfRange() As Long
    Dim a(1 To 3) As Long
    On Error Resume Next
    a(4) = 1
    SubscriptOutOfRange = Err.Number
End Function

Private Function TypeMismatch() As Long
    Dim l As Long
    On Error Resume Next
    l = CLng("abc")
    TypeMismatch = Err.Number
End Function

Private Function Inner() As Long
    Dim z As Long
    Inner = 1 \ z
End Function

Private Function Propagated() As Long
    Dim r As Long
    On Error Resume Next
    r = Inner()
    Propagated = Err.Number
End Function

Private Sub RaiseFive()
    On Error Resume Next
    Err.Raise 5
End Sub

Private Function ClearedAtExit() As Long
    RaiseFive
    ClearedAtExit = Err.Number
End Function

Private Function HandlerNumber() As Long
    Dim z As Long
    On Error GoTo Failed
    HandlerNumber = 10 \ z
    Exit Function
Failed:
    HandlerNumber = 100 + Err.Number
End Function

Private Function ResumeNextInHandler() As Long
    Dim r As Long, z As Long
    On Error GoTo Failed
    r = 1
    r = 10 \ z
    r = r + 5
    ResumeNextInHandler = r
    Exit Function
Failed:
    Resume Next
End Function

Private Function ResumeWithoutError() As Long
    On Error GoTo Failed
    Resume Next
    ResumeWithoutError = -1
    Exit Function
Failed:
    ResumeWithoutError = Err.Number
End Function

Private Function ClearedByResumeNext() As Long
    On Error GoTo Failed
    Err.Raise 9
    ClearedByResumeNext = Err.Number
    Exit Function
Failed:
    Resume Next
End Function

Private Function ClearedByOnError() As Long
    On Error Resume Next
    Err.Raise 5
    On Error GoTo 0
    ClearedByOnError = Err.Number
End Function

Private Function KeptAfterSuccess() As Long
    Dim z As Long, r As Long
    On Error Resume Next
    r = 1 \ z
    r = 5
    KeptAfterSuccess = Err.Number
End Function

Private Function FailedIfCondition() As Long
    Dim z As Long
    On Error Resume Next
    FailedIfCondition = 1
    If 1 \ z > 0 Then
        FailedIfCondition = 2
    End If
End Function

Private Sub Increment(v As Long)
    v = v + 1
End Sub

Private Function ByReferenceByDefault() As Long
    Dim x As Long
    x = 1
    Increment x
    ByReferenceByDefault = x
End Function

Private Function DeclaredBounds() As String
    Dim a(1 To 3) As Long, b(3) As Long
    DeclaredBounds = LBound(a) & "," & UBound(a) & "," & LBound(b) & "," & UBound(b)
End Function

Private Function NoShortCircuit() As Long
    Dim z As Long
    On Error Resume Next
    If z <> 0 And 1 \ z > 0 Then
        NoShortCircuit = -1
    End If
    NoShortCircuit = Err.Number
End Function

Function Probes() As String
    Dim s As String
    s = s & "integer_overflow=" & IntegerOverflow() & Chr(10)
    s = s & "long_overflow=" & LongOverflow() & Chr(10)
    s = s & "byte_overflow=" & ByteOverflow() & Chr(10)
    s = s & "integer_product_in_long=" & IntegerProductInLong() & Chr(10)
    s = s & "integer_sum_in_long=" & IntegerSumInLong() & Chr(10)
    s = s & "long_product_in_double=" & LongProductInDouble() & Chr(10)
    s = s & "converted_product=" & ConvertedProduct() & Chr(10)
    s = s & "smallest_by_minus_one=" & SmallestByMinusOne() & Chr(10)
    s = s & "division_by_zero=" & DivisionByZero() & Chr(10)
    s = s & "float_division_by_zero=" & FloatDivisionByZero() & Chr(10)
    s = s & "zero_by_zero=" & ZeroByZero() & Chr(10)
    s = s & "float_overflow=" & FloatOverflow() & Chr(10)
    s = s & "subscript_out_of_range=" & SubscriptOutOfRange() & Chr(10)
    s = s & "type_mismatch=" & TypeMismatch() & Chr(10)
    s = s & "propagated=" & Propagated() & Chr(10)
    s = s & "cleared_at_exit=" & ClearedAtExit() & Chr(10)
    s = s & "handler_number=" & HandlerNumber() & Chr(10)
    s = s & "resume_next_in_handler=" & ResumeNextInHandler() & Chr(10)
    s = s & "resume_without_error=" & ResumeWithoutError() & Chr(10)
    s = s & "cleared_by_resume_next=" & ClearedByResumeNext() & Chr(10)
    s = s & "cleared_by_on_error=" & ClearedByOnError() & Chr(10)
    s = s & "kept_after_success=" & KeptAfterSuccess() & Chr(10)
    s = s & "failed_if_condition=" & FailedIfCondition() & Chr(10)
    s = s & "by_reference_by_default=" & ByReferenceByDefault() & Chr(10)
    s = s & "declared_bounds=" & DeclaredBounds() & Chr(10)
    s = s & "no_short_circuit=" & NoShortCircuit() & Chr(10)
    s = s & "integer_division=" & (5 \ 2) & "," & (-7 \ 2) & "," & (-7 Mod 2) & "," & (7 Mod -2) & "," & (7.5 \ 2) & "," & (5.5 Mod 2) & Chr(10)
    s = s & "cint_rounding=" & CInt(2.5) & "," & CInt(3.5) & "," & CInt(-2.5) & "," & CInt(0.5) & "," & CInt(1.5) & Chr(10)
    s = s & "clng_rounding=" & CLng(2.5) & "," & CLng(3.5) & "," & CLng(-3.5) & Chr(10)
    s = s & "cbyte_rounding=" & CByte(2.5) & "," & CByte(254.5) & Chr(10)
    s = s & "fix_int_round=" & Fix(-2.5) & "," & Int(-2.5) & "," & Round(2.5) & "," & Round(3.5) & Chr(10)
    s = s & "hex_literals=" & Hex(255) & "," & Hex(-1) & "," & &HFFFF & "," & &HFFFF& & Chr(10)
    s = s & "bitwise=" & (5 And 3) & "," & (5 Or 3) & "," & (5 Xor 3) & "," & (Not 5) & "," & ((5 Eqv 3) And 15) & Chr(10)
    s = s & "typename_integer_quotient=" & TypeName(5 \ 2) & Chr(10)
    s = s & "typename_integer_sum=" & TypeName(32767 + 1) & Chr(10)
    s = s & "typename_long_times_integer=" & TypeName(CLng(1) * 2) & Chr(10)
    s = s & "typename_power=" & TypeName(2 ^ 2) & Chr(10)
    s = s & "typename_byte_sum=" & TypeName(CByte(1) + CByte(1)) & Chr(10)
    s = s & "cstr_double=" & CStr(1E+20) & "," & CStr(CSng(1 / 3)) & Chr(10)
    Probes = s
End Function
