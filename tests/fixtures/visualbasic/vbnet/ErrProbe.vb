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

    Public Function ClearedAfterCatch() As Integer
        Try
            Fail("divide-by-zero")
        Catch ex As DivideByZeroException
        End Try
        Return Err.Number
    End Function

    Private Sub Nested(ByVal kind As String)
        Fail(kind)
    End Sub

    Public Function InCalledProcedure() As Integer
        On Error Resume Next
        Nested("index-out-of-range")
        Return Err.Number
    End Function

    Private Sub Skipping()
        On Error Resume Next
        Fail("overflow")
    End Sub

    Public Function KeptAfterReturn() As Integer
        Err.Clear()
        Skipping()
        Return Err.Number
    End Function

    Public Function ResumeNextInIfCondition() As Integer
        Dim taken As Integer = 0
        On Error Resume Next
        If 1 \ zero > 0 Then
            taken = 1
        Else
            taken = 2
        End If
        Return taken * 100 + Err.Number
    End Function

    Public Function ResumeNextInDoWhileCondition() As Integer
        Dim passes As Integer = 0
        On Error Resume Next
        Do While 1 \ zero > passes
            passes += 1
            If passes > 2 Then Exit Do
        Loop
        Return passes * 100 + Err.Number
    End Function

    Public Function ResumeNextInSelectCase() As Integer
        Dim taken As Integer = 0
        On Error Resume Next
        Select Case 1 \ zero
            Case 0
                taken = 1
            Case Else
                taken = 2
        End Select
        Return taken * 100 + Err.Number
    End Function

    Private Sub ResumeOutsideHandler()
        On Error GoTo 0
        Resume Next
    End Sub

    Public Function ResumeWithoutError() As Integer
        On Error Resume Next
        ResumeOutsideHandler()
        Return Err.Number
    End Function

End Module
