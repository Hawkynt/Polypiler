Attribute VB_Name = "Locale"
Option Explicit

' The text of floating-point numbers and strings read as numbers, by the regional settings.

Public Function DoubleText(ByVal x As Double) As String
    DoubleText = CStr(x)
End Function

Public Function SingleText(ByVal x As Single) As String
    SingleText = CStr(x)
End Function

Public Function Third() As String
    Third = "x" & (1 / 3)
End Function

Public Function Joined(ByVal a As Double, ByVal b As Double) As String
    Joined = a & ";" & b
End Function

Public Function Parsed(ByVal s As String) As Double
    Parsed = CDbl(s)
End Function

Public Function ParsedLong(ByVal s As String) As Long
    ParsedLong = CLng(s)
End Function

Public Function ParsedSingle(ByVal s As String) As Single
    ParsedSingle = CSng(s)
End Function

Public Function Valued(ByVal s As String) As Double
    Valued = Val(s)
End Function

Public Function Numeric(ByVal s As String) As Boolean
    Numeric = IsNumeric(s)
End Function

Public Function Sum(ByVal s As String) As Double
    Sum = s + 1
End Function

Public Function Assigned(ByVal s As String) As Double
    Dim d As Double
    d = s
    Assigned = d
End Function

Public Function VariantSum(ByVal which As Integer) As String
    Dim v
    v = "2.5"
    If which = 1 Then v = 2.5
    v = v * 2
    VariantSum = TypeName(v) & " " & v
End Function

Public Function VariantThird(ByVal which As Integer) As String
    Dim v
    v = 1
    If which = 1 Then v = Null
    v = v / 3
    VariantThird = TypeName(v) & " " & v
End Function

Public Function Truth(ByVal s As String) As Boolean
    Truth = CBool(s)
End Function

Public Function Compared(ByVal s As String) As Boolean
    Compared = s < 10
End Function
