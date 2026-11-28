Option Strict On
Option Explicit On

' Visual Basic .NET control flow: If/ElseIf, Select Case with values, ranges and Is clauses, For
' with a step evaluated once, Do and While loops, Exit and Continue, and For Each.
Public Module Control

    Public Function Classify(ByVal n As Integer) As String
        Select Case n
            Case Is < 0
                Return "negative"
            Case 0
                Return "zero"
            Case 1 To 9
                Return "digit"
            Case 10, 20, 30
                Return "round"
            Case Else
                Return "large"
        End Select
    End Function

    Public Function Weekday(ByVal day As Integer) As Integer
        Dim result As Integer = 0
        Select Case day
            Case 1, 7
                result = 2
            Case 2
                result = 1
                Exit Select
            Case 3, 4, 5
                result = 3
            Case Else
                result = -1
        End Select
        Return result
    End Function

    Public Function Named(ByVal text As String) As Integer
        Select Case text
            Case "one"
                Return 1
            Case "two", "zwei"
                Return 2
            Case Else
                Return 0
        End Select
    End Function

    Public Function SumStep(ByVal first As Integer, ByVal last As Integer, ByVal stepBy As Integer) As Integer
        Dim total As Integer = 0
        For i As Integer = first To last Step stepBy
            If i Mod 3 = 0 Then Continue For
            total += i
            If total > 1000 Then Exit For
        Next
        Return total
    End Function

    Public Function Countdown(ByVal n As Integer) As Integer
        Dim steps As Integer = 0
        For i As Integer = n To 1 Step -2
            steps += 1
        Next
        Return steps
    End Function

    Public Function PastTheEnd(ByVal start As Integer) As Integer
        Dim last As Integer = 0
        For i As Integer = start To Integer.MaxValue
            last = i
        Next
        Return last
    End Function

    Public Function Collatz(ByVal n As Long) As Integer
        Dim steps As Integer = 0
        Do While n <> 1
            If n Mod 2 = 0 Then
                n = n \ 2
            ElseIf n > 1000000 Then
                Exit Do
            Else
                n = 3 * n + 1
            End If
            steps += 1
        Loop
        Return steps
    End Function

    Public Function Halvings(ByVal n As Integer) As Integer
        Dim count As Integer = 0
        Do
            n = n \ 2
            count += 1
        Loop Until n = 0
        Return count
    End Function

    Public Function WhileSum(ByVal n As Integer) As Integer
        Dim i As Integer = 0
        Dim sum As Integer = 0
        While i < n
            i += 1
            If i = 5 Then Continue While
            sum += i
        End While
        Return sum
    End Function

    Public Function Kept(ByVal n As Integer) As Integer
        Dim result As Integer = 0
        For i As Integer = 1 To n
            Dim seen As Integer
            seen += i
            result = seen
        Next
        Return result
    End Function

    Public Function Total(ByVal values As Integer()) As Integer
        Dim sum As Integer = 0
        For Each value As Integer In values
            sum += value
        Next
        Return sum
    End Function

    Public Function Vowels(ByVal text As String) As Integer
        Dim count As Integer = 0
        For Each c As Char In text
            If c = "a"c OrElse c = "e"c OrElse c = "i"c OrElse c = "o"c OrElse c = "u"c Then count += 1
        Next
        Return count
    End Function

End Module
