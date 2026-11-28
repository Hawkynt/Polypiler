Attribute VB_Name = "Inference"
Option Explicit

' Untyped parameters and results typed from the calls of the module.

Private Function Add(a, b)
    Add = a + b
End Function

Private Function Twice(x)
    Twice = x & x
End Function

Private Function Fact(n)
    If n <= 1 Then
        Fact = 1
    Else
        Fact = n * Fact(n - 1)
    End If
End Function

Private Function Big(x)
    Big = x > 5
End Function

Public Function Scaled(x, Optional y = 2)
    Scaled = x * y
End Function

Public Function Sums(ByVal n As Long) As String
    Sums = Add(n, CLng(1)) & " " & TypeName(Add(CInt(32767), CInt(1))) & " " & Add(1.5, 2.25)
End Function

Public Function Doubled(ByVal s As String) As String
    Doubled = Twice(s) & "|" & Twice(CInt(7))
End Function

Public Function Factorial(ByVal n As Long) As Double
    Factorial = Fact(n)
End Function

Public Function IsBig(ByVal n As Long) As Boolean
    IsBig = Big(n)
End Function

Public Function ScaledBoth(ByVal n As Integer) As Long
    ScaledBoth = Scaled(n) + Scaled(n, CInt(3))
End Function

' A ByVal parameter the body assigns a value of another kind stays a Variant: no call types it.
Private Function Bang(ByVal a)
    a = a & "!"
    Bang = a
End Function

Public Function Banged() As String
    Banged = Bang(CDbl(1))
End Function
