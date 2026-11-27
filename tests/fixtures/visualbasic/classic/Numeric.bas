Attribute VB_Name = "Numeric"
Option Explicit

' Integer is 16 bits: 32767 + 1 overflows.
Public Function IncInteger(ByVal a As Integer) As Integer
    IncInteger = a + 1
End Function

Public Function MulLong(ByVal a As Long, ByVal b As Long) As Long
    MulLong = a * b
End Function

Public Function IntDiv(ByVal a As Long, ByVal b As Long) As Long
    IntDiv = a \ b
End Function

Public Function Modulo(ByVal a As Long, ByVal b As Long) As Long
    Modulo = a Mod b
End Function

Public Function RoundInt(ByVal d As Double) As Integer
    RoundInt = CInt(d)
End Function

Public Function ToByte(ByVal a As Long) As Byte
    ToByte = a
End Function

Public Function SumTo(ByVal n As Long) As Long
    Dim i As Long, total As Long
    For i = 1 To n
        total = total + i
    Next i
    SumTo = total
End Function

Public Function CountDown(ByVal n As Integer) As Long
    Dim i As Integer, steps As Long
    For i = n To 1 Step -2
        steps = steps + 1
    Next
    CountDown = steps
End Function

Public Function Collatz(ByVal n As Long) As Long
    Dim steps As Long
    Do While n <> 1
        If n Mod 2 = 0 Then
            n = n \ 2
        Else
            n = 3 * n + 1
        End If
        steps = steps + 1
    Loop
    Collatz = steps
End Function

Public Function Classify(ByVal v As Long) As Long
    Select Case v
        Case Is < 0
            Classify = -1
        Case 0
            Classify = 0
        Case 1 To 9, 11
            Classify = 1
        Case Else
            Classify = 2
    End Select
End Function

Public Function SafeDivide(ByVal a As Long, ByVal b As Long) As Long
    On Error GoTo Failed
    SafeDivide = a \ b
    Exit Function
Failed:
    SafeDivide = -Err.Number
End Function

Public Function ErrorOfOverflow() As Long
    Dim i As Integer
    On Error Resume Next
    i = 32767
    i = i + 1
    ErrorOfOverflow = Err.Number * 100000 + i
End Function

Public Function ArraySum(ByVal n As Long) As Long
    Dim a() As Long, i As Long, total As Long
    ReDim a(1 To n)
    For i = LBound(a) To UBound(a)
        a(i) = i * i
    Next i
    For i = 1 To n
        total = total + a(i)
    Next
    ArraySum = total
End Function

Public Function FixedArray() As Long
    Dim t(3) As Integer
    t(0) = 5
    t(3) = 7
    FixedArray = t(0) * 10 + t(3) + UBound(t) * 1000
End Function

Public Function Bits(ByVal a As Long, ByVal b As Long) As Long
    Bits = (a And b) Xor (a Or Not b)
End Function

Public Function Greeting(ByVal name As String) As String
    Greeting = "Hello, " & name & "! " & Len(name) & " " & Left(name, 2) & Mid(name, 2, 2) & Right(name, 1) & InStr(name, "l")
End Function

Public Function Both(ByVal a As Boolean, ByVal b As Boolean) As Boolean
    Both = a And b
End Function

Public Function Half(ByVal a As Long) As Double
    Half = a / 2
End Function
