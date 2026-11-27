Attribute VB_Name = "Records"
' User-defined types, an Enum, constants and module state of a classic standard module.
' Written for this repository.
Option Explicit
Option Base 1

Public Type Point
    X As Long
    Y As Integer
End Type

Public Type Segment
    A As Point
    B As Point
End Type

Public Enum Direction
    North
    East = 90
    South = 180
    West
End Enum

Private Const Scale As Long = 10
Private Const Unit = "m"
Private calls As Long

Private Function Length(s As Segment) As Long
    Length = (Abs(s.B.X - s.A.X) + Abs(s.B.Y - s.A.Y)) * Scale
End Function

Private Function Shifted(p As Point, ByVal dx As Long) As Point
    Dim q As Point
    q = p
    q.X = q.X + dx
    Shifted = q
End Function

Public Function Describe(ByVal x1 As Long, ByVal x2 As Long) As String
    Dim s As Segment
    s.A.X = x1
    s.B = Shifted(s.A, x2 - x1)
    s.B.Y = 3
    calls = calls + 1
    Describe = Length(s) & Unit & " " & calls
End Function

Public Function Turn(ByVal d As Direction, ByVal quarters As Long) As Long
    Turn = (d + quarters * 90) Mod 360
End Function

Public Function Corners(ByVal n As Long) As Long
    Dim points() As Point, history() As Long, i As Long
    ReDim points(1 To n)
    For i = 1 To n
        points(i).X = i * West
        points(i).Y = i
    Next i
    ReDim history(1 To n)
    For i = LBound(points) To UBound(points)
        history(i) = points(i).X + points(i).Y
        Corners = Corners + history(i)
    Next
End Function
