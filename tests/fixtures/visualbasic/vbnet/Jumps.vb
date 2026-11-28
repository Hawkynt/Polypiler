Option Strict On
Option Explicit On

' GoTo and labels, the exits of a loop around the innermost one, and the unstructured error handling
' of Visual Basic: On Error GoTo, On Error Resume Next, Resume in its three forms and Err.
Public Module Jumps

    Private zero As Integer = 0

    ' The sum of 0 .. n - 1 by a backward GoTo.
    Public Function Triangle(ByVal n As Integer) As Integer
        Dim i As Integer = 0
        Dim total As Integer = 0
Again:
        If i >= n Then GoTo Done
        total += i
        i += 1
        GoTo Again
Done:
        Return total
    End Function

    ' The first pair i, j with i * j > limit, found by an Exit For from inside a Do.
    Public Function FirstProduct(ByVal limit As Integer) As Integer
        Dim found As Integer = -1
        For i As Integer = 1 To 20
            Dim j As Integer = 0
            Do
                j += 1
                If j > i Then Continue For
                If i * j > limit Then
                    found = i * 100 + j
                    Exit For
                End If
            Loop
        Next
        Return found
    End Function

    ' 100 \ d, or 1000 times the error's number where it fails, by On Error GoTo and Resume Next.
    Public Function Quotient(ByVal d As Integer) As Integer
        Dim result As Integer = 0
        On Error GoTo Handler
        result = 100 \ d
        Return result
Handler:
        result = Err.Number * 1000
        Resume Next
    End Function

    ' 100 \ d where d is not zero, else 100 \ 7: the handler changes d and Resume runs the statement again.
    Public Function Retried(ByVal d As Integer) As Integer
        On Error GoTo Handler
        Return 100 \ d
Handler:
        d = 7
        Resume
    End Function

    ' The element at index, or the negated error number after Resume to a label.
    Public Function Element(ByVal index As Integer) As Integer
        Dim values() As Integer = {10, 20, 30}
        On Error GoTo Handler
        Return values(index)
Handler:
        Resume Fallback
Fallback:
        Return -Err.Number
    End Function

    ' The errors of three divisions, each resumed after, as one number.
    Public Function Skipped(ByVal d As Integer) As Integer
        Dim errors As Integer = 0
        On Error Resume Next
        zero = 10 \ d
        errors = errors * 100 + Err.Number
        Err.Clear()
        zero = 10 \ (d - 1)
        errors = errors * 100 + Err.Number
        Err.Clear()
        zero = Integer.MaxValue + d
        errors = errors * 100 + Err.Number
        Return errors
    End Function

    ' An error in the condition of an If under Resume Next goes on inside its first branch.
    Public Function Branched(ByVal d As Integer) As Integer
        Dim taken As Integer = 0
        On Error Resume Next
        If 10 \ d > 2 Then
            taken = 1
        Else
            taken = 2
        End If
        Return taken * 100 + Err.Number
    End Function

    ' Err.Raise of a number the runtime has an exception for, and the number Err reads after it.
    Public Function Raised(ByVal n As Integer) As Integer
        On Error Resume Next
        Select Case n
            Case 1
                Err.Raise(6)
            Case 2
                Err.Raise(13)
            Case Else
                Err.Raise(5)
        End Select
        Return Err.Number
    End Function

    ' Err.Number in a Catch of Exception is the number of the exception's class, and 0 after the Catch.
    Public Function CaughtNumber(ByVal kind As Integer) As Integer
        Dim caught As Integer = 0
        Dim divisor As Integer = kind - kind
        Err.Clear()
        Try
            Select Case kind
                Case 0
                    zero = 1 \ divisor
                Case 1
                    Dim values() As Integer = {1}
                    zero = values(kind)
                Case 2
                    Err.Raise(77)
                Case Else
                    Throw New InvalidOperationException()
            End Select
        Catch ex As Exception
            caught = Err.Number
        End Try
        Return caught * 1000 + Err.Number
    End Function

    ' An error in a handler goes to the caller, here a Try.
    Public Function Twice(ByVal d As Integer) As Integer
        Try
            Return FailsTwice(d)
        Catch ex As OverflowException
            Return -6
        End Try
    End Function

    Private Function FailsTwice(ByVal d As Integer) As Integer
        On Error GoTo Handler
        Return 10 \ d
Handler:
        Return Integer.MaxValue + d + 1
    End Function

End Module
