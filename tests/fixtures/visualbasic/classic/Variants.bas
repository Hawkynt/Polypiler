Attribute VB_Name = "Variants"
Option Explicit

' Variant locals that hold one type become variables of that type; the others stay Variants.

Public Function Doubled(ByVal n As Long) As Long
    Dim v
    v = n
    Doubled = v * 2
End Function

Public Function Promoted() As String
    Dim v
    v = CInt(32767)
    v = v + 1
    Promoted = TypeName(v) & " " & v
End Function

Public Function IntegerProduct(ByVal a As Integer, ByVal b As Integer) As Long
    Dim v
    v = a
    v = v * b
    IntegerProduct = v
End Function

Public Function ByteBelowZero() As String
    Dim v
    v = CByte(0)
    v = v - 1
    ByteBelowZero = TypeName(v) & " " & v
End Function

Public Function LongBeyond() As Double
    Dim v
    v = CLng(2147483647)
    v = v + 1
    LongBeyond = v
End Function

Public Function Mixed(ByVal which As Integer) As String
    Dim v
    If which = 1 Then
        v = 5
    ElseIf which = 2 Then
        v = "five"
    ElseIf which = 3 Then
        v = True
    ElseIf which = 4 Then
        v = Null
    End If
    Mixed = TypeName(v) & " " & VarType(v)
End Function

Public Function EmptyRules() As String
    Dim v, w
    EmptyRules = TypeName(v) & " " & IsEmpty(v) & " " & (v + 1) & " " & (v & "a") & " " & (v = 0) & " " & (v = "")
    w = v + v
    EmptyRules = EmptyRules & " " & TypeName(w)
End Function

Public Function NullRules() As String
    Dim v, w
    v = Null
    w = v + 1
    NullRules = IsNull(w) & " " & IsNull(v = 1) & " " & IsNull(v & Null) & " " & TypeName(w)
End Function

Public Function NullConcatenated() As String
    Dim v
    v = Null
    NullConcatenated = v & "a"
End Function

Public Function NullIsNotTrue() As String
    Dim v
    v = Null
    If v Then
        NullIsNotTrue = "then"
    Else
        NullIsNotTrue = "else"
    End If
End Function

Public Function NullToInteger() As Integer
    Dim v
    v = Null
    NullToInteger = v
End Function

Public Function NullLogic() As String
    Dim v
    v = Null
    NullLogic = TypeName(v And False) & " " & TypeName(v Or True) & " " & TypeName(v And True) & " " & TypeName(Not v)
End Function

Public Function Accumulate(ByVal n As Integer) As String
    Dim i, total
    total = 0
    For i = 1 To 10
        total = total + i * n
    Next
    Accumulate = TypeName(total) & " " & CLng(total)
End Function

Public Function Joined(ByVal n As Integer) As String
    Dim i, s
    For i = 1 To 5
        s = s & i
    Next
    Joined = s & n
End Function

Public Function Compared(ByVal which As Integer) As String
    Dim a, b
    a = 10
    If which = 1 Then
        b = 9
    Else
        b = "9"
    End If
    Compared = (a < b) & " " & (b = b)
End Function

Public Function Divided(ByVal a As Integer, ByVal b As Integer) As String
    Dim v, q
    v = a
    If b = 0 Then v = Null
    q = v \ b
    Divided = TypeName(q) & "|" & q & "|" & (v Mod b)
End Function

Public Function Counted(ByVal n As Long) As Long
    Dim i, count
    count = 0
    For i = 1 To n
        count = count + 1
    Next
    Counted = count
End Function

Public Function Identity(x)
    Identity = x
End Function

Public Function KindOf(x) As String
    KindOf = TypeName(x)
End Function
