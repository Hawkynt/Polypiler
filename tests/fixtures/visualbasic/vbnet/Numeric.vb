Option Strict On
Option Explicit On

' Visual Basic .NET numeric semantics: Integer is 32 bits and checked, \ truncates, Mod keeps the
' dividend's sign, CInt rounds half to even, True is -1, And and Or evaluate both operands.
Public Module Numeric

    Private calls As Integer = 0

    Private Function Count(ByVal value As Boolean) As Boolean
        calls += 1
        Return value
    End Function

    Public Function AddInt(ByVal a As Integer, ByVal b As Integer) As Integer
        Return a + b
    End Function

    Public Function MulLong(ByVal a As Long, ByVal b As Long) As Long
        Return a * b
    End Function

    Public Function IntDiv(ByVal a As Integer, ByVal b As Integer) As Integer
        Return a \ b
    End Function

    Public Function Modulo(ByVal a As Integer, ByVal b As Integer) As Integer
        Return a Mod b
    End Function

    Public Function FloatMod(ByVal a As Double, ByVal b As Double) As Double
        Return a Mod b
    End Function

    Public Function Rounded(ByVal x As Double) As Integer
        Return CInt(x)
    End Function

    Public Function Truncated(ByVal x As Double) As Integer
        Return CInt(Fix(x))
    End Function

    Public Function Floored(ByVal x As Double) As Long
        Return CLng(Int(x))
    End Function

    Public Function ByteSum(ByVal a As Byte, ByVal b As Byte) As Byte
        Return a + b
    End Function

    Public Function ShortProduct(ByVal a As Short, ByVal b As Short) As Short
        Return a * b
    End Function

    Public Function ByteShift(ByVal a As Byte, ByVal n As Integer) As Byte
        Return a << n
    End Function

    Public Function Unsigned(ByVal a As UInteger, ByVal b As UInteger) As UInteger
        Return (a - b) Xor (a >> 3)
    End Function

    Public Function MixedSigns(ByVal a As UInteger, ByVal b As Integer) As Long
        Return a + b
    End Function

    Public Function TrueValue(ByVal flag As Boolean) As Integer
        Return CInt(flag)
    End Function

    Public Function TrueByte(ByVal flag As Boolean) As Byte
        Return CByte(flag)
    End Function

    Public Function ShortCircuit(ByVal a As Boolean, ByVal b As Boolean) As Integer
        calls = 0
        Dim both As Boolean = Count(a) And Count(b)
        Dim first As Boolean = Count(a) AndAlso Count(b)
        Return calls * 10 + CInt(both) + CInt(first) * 4
    End Function

    Public Function Power(ByVal x As Double, ByVal y As Double) As Double
        Return x ^ y + x / 4
    End Function

    Public Function Narrow(ByVal x As Long) As Integer
        Return CInt(x)
    End Function

    Public Function Compound(ByVal start As Byte, ByVal times As Integer) As Byte
        Dim value As Byte = start
        For i As Integer = 1 To times
            value += CByte(50)
        Next
        Return value
    End Function

    Public Function Bits(ByVal a As Integer, ByVal b As Integer) As Integer
        Return (a And b) Or (Not a And &HFF) Xor (b << 4)
    End Function

    Public Function Negated(ByVal a As Integer) As Integer
        Return -a
    End Function

End Module
