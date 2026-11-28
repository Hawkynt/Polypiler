Attribute VB_Name = "CurrencyDate"
Option Explicit

' Currency: scaled 64-bit integers with banker's rounding. Date: days since 1899-12-30.

Public Function CurProduct(ByVal a As Currency, ByVal b As Currency) As String
    CurProduct = CStr(a * b)
End Function

Public Function CurSum(ByVal a As Currency, ByVal b As Double) As String
    CurSum = TypeName(a + b) & " " & (a + b)
End Function

Public Function CurTimes(ByVal a As Currency, ByVal b As Double) As String
    CurTimes = TypeName(a * b) & " " & (a * b)
End Function

Public Function CurQuotient(ByVal a As Currency, ByVal b As Currency) As String
    CurQuotient = TypeName(a / b) & " " & (a / b)
End Function

Public Function CurFrom(ByVal x As Double) As String
    CurFrom = CStr(CCur(x))
End Function

Public Function CurOverflow() As String
    Dim c As Currency
    c = 900000000000000@
    c = c + c
    CurOverflow = CStr(c)
End Function

Public Function CurVariant(ByVal which As Integer) As String
    Dim v
    v = CCur(1.5)
    If which = 1 Then v = Null
    v = v * 3
    CurVariant = TypeName(v) & " " & v
End Function

Public Function CurVariantOverflow(ByVal which As Integer) As String
    Dim v
    v = CCur(900000000000000#)
    If which = 1 Then v = Null
    v = v * 10
    CurVariantOverflow = TypeName(v)
End Function

Public Function CurToLong(ByVal c As Currency) As Long
    CurToLong = c
End Function

Public Function CurCompare(ByVal c As Currency) As Boolean
    CurCompare = c = 1.5
End Function

Public Function Serial(ByVal y As Integer, ByVal m As Integer, ByVal d As Integer) As Double
    Serial = DateSerial(y, m, d)
End Function

Public Function Literal() As Double
    Literal = #1/2/2000#
End Function

Public Function LiteralIso() As Double
    LiteralIso = #2000-01-02# + 0.75
End Function

Public Function Parts(ByVal x As Double) As String
    Dim d As Date
    d = x
    Parts = Year(d) & "-" & Month(d) & "-" & Day(d) & " " & Hour(d) & ":" & Minute(d) & ":" & Second(d) & " " & Weekday(d) & " " & Weekday(d, 2)
End Function

Public Function DateKinds() As String
    Dim d As Date
    d = #1/2/2000#
    DateKinds = TypeName(d + 1) & " " & TypeName(d - #1/1/2000#) & " " & CDbl(d - 1) & " " & TypeName(d * 2) & " " & (d < #1/3/2000#)
End Function

Public Function DateVariant(ByVal which As Integer) As String
    Dim v
    v = #1/2/2000#
    If which = 1 Then v = Null
    v = v + 1
    DateVariant = TypeName(v) & " " & VarType(v) & " " & CDbl(v)
End Function

Public Function TimeOfDay() As Double
    TimeOfDay = TimeSerial(13, 30, 5)
End Function

Public Function DateOverflow() As Double
    Dim d As Date
    d = #12/31/9999#
    d = d + 1
    DateOverflow = d
End Function
