Option Strict On
Option Explicit On

' Visual Basic .NET structures, enumerations, arrays by upper bound, ReDim Preserve, strings and
' Try/Catch/Finally with a filter and a rethrow.
Public Module Records

    Public Enum Shade As Byte
        Light = 1
        Dark = 200
    End Enum

    Public Structure Point
        Public X As Integer
        Public Y As Integer
    End Structure

    Private Const Scale As Integer = 3
    Private ReadOnly Weights As Integer() = {1, 2, 4, 8}
    Private log As String = ""

    Public Function Distance(ByVal x As Integer, ByVal y As Integer) As Integer
        Dim p As New Point With {.X = x, .Y = y}
        Dim q As Point = p
        q.X = 0
        Return Math.Abs(p.X - q.X) + Math.Abs(p.Y) * Scale
    End Function

    Public Function Darkness(ByVal dark As Boolean) As Integer
        Dim value As Shade = If(dark, Shade.Dark, Shade.Light)
        Return CInt(value) + Weights(3)
    End Function

    Public Function Squares(ByVal n As Integer) As Integer
        Dim values(n) As Integer
        For i As Integer = 0 To n
            values(i) = i * i
        Next
        ReDim Preserve values(n + 2)
        Return values.Length * 1000 + values(n)
    End Function

    Public Function Walk(ByVal steps As Integer) As Integer
        Dim path(steps - 1) As Point
        For i As Integer = 0 To steps - 1
            path(i).X = i
            path(i).Y = i * 2
        Next
        Dim sum As Integer = 0
        For Each place As Point In path
            sum += place.X + place.Y
        Next
        Return sum
    End Function

    Public Function Greeting(ByVal name As String, ByVal times As Integer) As String
        Dim text As String = ""
        For i As Integer = 1 To times
            text &= name & CStr(i)
        Next
        Return text & "/" & Len(text).ToString() & "/" & text.Substring(0, 1)
    End Function

    Public Function SafeDivide(ByVal a As Integer, ByVal b As Integer) As Integer
        log = ""
        Try
            Return a \ b
        Catch ex As DivideByZeroException When a > 0
            log = "positive"
            Return -1
        Catch ex As DivideByZeroException
            log = "other"
            Return -2
        Finally
            log &= "!"
        End Try
    End Function

    Public Function LastLog() As String
        Return log
    End Function

    Public Function Checked(ByVal a As Integer) As Integer
        Try
            Return a * a
        Catch ex As OverflowException
            Return -1
        End Try
    End Function

    Public Function Raise(ByVal n As Integer) As String
        Try
            If n < 0 Then Throw New ArgumentException("negative")
            Return "ok"
        Catch ex As ArgumentException
            Return ex.Message
        End Try
    End Function

    Public Function Rethrown(ByVal n As Integer) As Integer
        Try
            Try
                Return 10 \ n
            Catch ex As DivideByZeroException
                log = "inner"
                Throw
            End Try
        Catch ex As Exception
            Return -100
        End Try
    End Function

End Module
