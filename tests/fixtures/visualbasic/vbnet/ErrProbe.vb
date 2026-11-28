Option Strict On
Option Explicit On

' The error state of the executable dialect vbnet: the Err.Number Visual Basic .NET gives each
' exception, and when it keeps and clears it. Each probe returns a number the table
' ErrProbe.expected.txt records, from a run of the Visual Basic compiler and runtime of dotnet-sdk.
Public Module ErrProbe

    Private zero As Integer = 0

    Private Sub Fail(ByVal kind As String)
        Select Case kind
            Case "divide-by-zero"
                zero = 1 \ zero
            Case "overflow"
                zero = Integer.MaxValue + (zero + 1)
            Case "index-out-of-range"
                Dim values As Integer() = New Integer(0) {}
                zero = values(zero + 1)
            Case "invalid-cast"
                Dim boxed As Object = "text"
                zero = DirectCast(boxed, Integer())(0)
            Case "null-reference"
                Dim values As Integer() = Nothing
                zero = values(0)
            Case "other"
                Throw New InvalidOperationException()
            Case Else
                Err.Raise(1234)
        End Select
    End Sub

    Public Function NumberOf(ByVal kind As String) As Integer
        On Error Resume Next
        Fail(kind)
        Return Err.Number
    End Function

    Public Function KeptAfterSuccess() As Integer
        On Error Resume Next
        Fail("overflow")
        zero = 0
        Return Err.Number
    End Function

    Public Function ClearedByErrClear() As Integer
        On Error Resume Next
        Fail("overflow")
        Err.Clear()
        Return Err.Number
    End Function

    Public Function ClearedByOnError() As Integer
        On Error Resume Next
        Fail("overflow")
        On Error GoTo 0
        Return Err.Number
    End Function

    Public Function ClearedByResumeNext() As Integer
        On Error GoTo Handler
        Fail("overflow")
        Return Err.Number
Handler:
        Resume Next
    End Function

    Public Function CaughtByTry() As Integer
        Try
            Fail("divide-by-zero")
        Catch ex As DivideByZeroException
            Return Err.Number
        End Try
        Return -1
    End Function

End Module
