Attribute VB_Name = "Bitwise"
Option Explicit

' Bitwise operators of Variants, with Null, and arrays of Variants.

Public Function Bits(ByVal which As Integer) As String
    Dim v, w, r
    v = CInt(12)
    w = CInt(10)
    If which = 9 Then v = Null
    Select Case which
    Case 1
        r = v And w
    Case 2
        r = v Or w
    Case 3
        r = v Xor w
    Case 4
        r = v Eqv w
    Case 5
        r = v Imp w
    Case Else
        r = Not v
    End Select
    Bits = TypeName(r) & " " & r
End Function

Public Function Widths(ByVal which As Integer) As String
    Dim v, w
    v = CByte(200)
    w = CByte(15)
    If which = 2 Then w = CLng(100000)
    If which = 3 Then w = 2.5
    If which = 9 Then v = Null
    Dim a, n
    a = v And w
    n = Not v
    Widths = TypeName(a) & " " & a & " " & TypeName(n) & " " & n
End Function

Public Function WithNull(ByVal which As Integer) As String
    Dim v, n, a, o, b
    v = CInt(which)
    n = Null
    a = v And n
    o = v Or n
    b = n And v
    WithNull = TypeName(a) & "|" & TypeName(o) & "|" & TypeName(b)
End Function

Public Function Elements() As String
    Dim a(2)
    a(0) = 1
    a(1) = "x"
    a(2) = Null
    Elements = TypeName(a(0)) & TypeName(a(1)) & TypeName(a(2)) & UBound(a) & LBound(a)
End Function

Public Function Listed() As String
    Dim v
    v = Array(1, "a", True)
    Listed = TypeName(v(1)) & " " & UBound(v) & " " & v(0) & " " & IsArray(v)
End Function

Public Function Resized(ByVal n As Long) As String
    Dim a()
    ReDim a(1 To n)
    a(n) = 2.5
    Resized = TypeName(a(n)) & " " & a(n) & " " & UBound(a) & " " & TypeName(a(1))
End Function
