using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The classic Visual Basic frontend: modules imported and run by the IR interpreter, every result and
/// every run-time error checked against the semantics of VB6 / VBA ([MS-VBAL], ADR 2600) - the rows of
/// the probe that LibreOffice confirms are compared with it again in the acceptance tests.
/// </summary>
[TestFixture]
public sealed class VisualBasicFrontendTests {

  /// <summary>Imports a module and fails the test unless it is imported.</summary>
  internal static IrModule Import(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  /// <summary>Calls a function of a module: its value as invariant text, or <c>!</c> and the run-time error number of the trap or exception.</summary>
  internal static string Run(IrModule module, string function, params object[] arguments) {
    try {
      var value = IrInterpreter.Call(module, function, arguments);
      return value switch {
        bool flag => flag ? "True" : "False",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        null => "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
      };
    } catch (IrException exception) {
      return "!" + Number(exception.Class).ToString(CultureInfo.InvariantCulture);
    }
  }

  private static int Number(IrClassType type) {
    if (type == IrExceptionClass.DivideByZeroException)
      return 11;
    if (type == IrExceptionClass.OverflowException)
      return 6;
    if (type == IrExceptionClass.IndexOutOfRangeException)
      return 9;
    if (type == IrExceptionClass.InvalidCastException)
      return 13;
    return 5;
  }

  private static string Function(string returns, string body, string parameters = "")
    => $"Option Explicit\nPublic Function F({parameters}) As {returns}\n{body}\nEnd Function\n";

  private static string Refused(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  [TestCase("F = a + 1", (short)1, "2")]
  [TestCase("F = a + 1", (short)32766, "32767")]
  [TestCase("F = a + 1", (short)32767, "!6")]
  [TestCase("F = a - 1", (short)-32768, "!6")]
  [TestCase("F = a * a", (short)182, "!6")]
  [TestCase("F = -a", (short)-32768, "!6")]
  [TestCase("F = a \\ 2", (short)-7, "-3")]
  [TestCase("F = a Mod 2", (short)-7, "-1")]
  [TestCase("F = a \\ -1", (short)-32768, "!6")]
  [TestCase("F = a \\ 0", (short)1, "!11")]
  [TestCase("F = Not a", (short)5, "-6")]
  public void IntegerArithmetic_IsSixteenBitsAndTraps(string body, short argument, string expected) {
    var module = Import(Function("Integer", body, "ByVal a As Integer"));
    Assert.That(Run(module, "F", argument), Is.EqualTo(expected));
  }

  [TestCase("F = a * a", 46340, "2147395600")]
  [TestCase("F = a * a", 46341, "!6")]
  [TestCase("F = a + 2147483647", 1, "!6")]
  [TestCase("F = a \\ 2", -7, "-3")]
  [TestCase("F = a / 2", 5, "2.5")]
  [TestCase("F = a / 0", 5, "!11")]
  [TestCase("F = a / 0", 0, "!6")]
  [TestCase("F = 2 ^ a", 10, "1024")]
  [TestCase("F = 2 ^ a", 2000, "!6")]
  [TestCase("F = a And 6 Or 1", 5, "5")]
  [TestCase("F = a Xor 3", 5, "6")]
  [TestCase("F = a Eqv 3", 5, "-7")]
  [TestCase("F = a Imp 3", 5, "-5")]
  [TestCase("F = a Mod 0", 5, "!11")]
  public void LongArithmetic_TrapsOnOverflow(string body, int argument, string expected) {
    var returns = body.Contains('/', StringComparison.Ordinal) || body.Contains('^', StringComparison.Ordinal) ? "Double" : "Long";
    var module = Import(Function(returns, body, "ByVal a As Long"));
    Assert.That(Run(module, "F", argument), Is.EqualTo(expected));
  }

  [TestCase("F = CInt(d)", 2.5, "2")]
  [TestCase("F = CInt(d)", 3.5, "4")]
  [TestCase("F = CInt(d)", -2.5, "-2")]
  [TestCase("F = CInt(d)", 40000.0, "!6")]
  [TestCase("F = d", 1.5, "2")]
  [TestCase("F = Int(d)", -2.5, "-3")]
  [TestCase("F = Fix(d)", -2.5, "-2")]
  [TestCase("F = Round(d)", 2.5, "2")]
  [TestCase("F = d \\ 2", 7.5, "4")]
  [TestCase("F = d Mod 2", 5.5, "0")]
  [TestCase("F = Sgn(d) * 10 + Abs(-3)", -1.5, "-7")]
  public void FloatToInteger_RoundsHalfToEven(string body, double argument, string expected) {
    var module = Import(Function("Integer", body, "ByVal d As Double"));
    Assert.That(Run(module, "F", argument), Is.EqualTo(expected));
  }

  [TestCase("F = CByte(a)", 255, "255")]
  [TestCase("F = CByte(a)", 256, "!6")]
  [TestCase("F = a", -1, "!6")]
  [TestCase("F = CByte(a) + CByte(1)", 255, "!6")]
  [TestCase("F = CByte(a) - CByte(1)", 0, "!6")]
  public void Byte_IsUnsignedEightBits(string body, int argument, string expected) {
    var module = Import(Function("Byte", body, "ByVal a As Long"));
    Assert.That(Run(module, "F", argument), Is.EqualTo(expected));
  }

  [TestCase("Dim a(1 To 3) As Long\n    a(1) = 7\n    F = a(1) + LBound(a) * 10 + UBound(a) * 100", "317")]
  [TestCase("Dim a(1 To 3) As Long\n    a(4) = 7", "!9")]
  [TestCase("Dim a(2) As Integer\n    F = LBound(a) * 10 + UBound(a)", "2")]
  [TestCase("Dim a() As Long\n    ReDim a(0 To 4)\n    a(4) = 9\n    ReDim Preserve a(0 To 5)\n    F = a(4) + UBound(a)", "14")]
  [TestCase("Dim a() As Long\n    ReDim a(0 To -1)", "!9")]
  [TestCase("Dim a() As Long, b() As Long\n    ReDim a(0 To 1)\n    b = a\n    b(0) = 5\n    F = a(0)", "0")]
  [TestCase("Dim a() As Long\n    ReDim a(1)\n    Erase a\n    F = UBound(a)", "-1")]
  public void Arrays_KeepTheirLowerBound(string body, string expected) {
    var module = Import(Function("Long", "    " + body));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [Test]
  public void OptionBaseOne_SetsTheDefaultLowerBound() {
    var module = Import("Option Explicit\nOption Base 1\nPublic Function F() As Long\n    Dim a(3) As Long\n    F = LBound(a) * 10 + UBound(a)\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("13"));
  }

  [TestCase("    For i = 1 To 10\n        s = s + i\n    Next i\n    F = s", "55")]
  [TestCase("    For i = 10 To 1 Step -3\n        s = s + i\n    Next\n    F = s", "22")]
  [TestCase("    For i = 1 To 10\n        If i = 4 Then Exit For\n        s = s + i\n    Next i\n    F = s", "6")]
  [TestCase("    Do While s < 100\n        s = s * 2 + 1\n    Loop\n    F = s", "127")]
  [TestCase("    Do\n        s = s + 3\n    Loop Until s > 10\n    F = s", "12")]
  [TestCase("    Do Until s >= 5\n        s = s + 1\n    Loop\n    F = s", "5")]
  [TestCase("    Do\n        s = s + 1\n        If s = 7 Then Exit Do\n    Loop\n    F = s", "7")]
  [TestCase("    While s < 4\n        s = s + 1\n    Wend\n    F = s", "4")]
  [TestCase("    s = 5\n    Do\n        s = s - 1\n    Loop While s > 0\n    F = s", "0")]
  public void Loops_RunAsVisualBasicDoes(string body, string expected) {
    var module = Import(Function("Long", "    Dim i As Long, s As Long\n" + body));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [Test]
  public void ForCounter_OverflowsAfterTheLastPass() {
    var module = Import(Function("Long", "    Dim i As Integer, s As Long\n    For i = 32766 To 32767\n        s = s + 1\n    Next i\n    F = s"));
    Assert.That(Run(module, "F"), Is.EqualTo("!6"));
  }

  [Test]
  public void ForWithVariableStep_TestsByTheSignOfTheStep() {
    var module = Import(Function("Long", "    Dim i As Long, s As Long\n    For i = a To 1 Step b\n        s = s + 1\n    Next i\n    F = s", "ByVal a As Long, ByVal b As Long"));
    Assert.That(Run(module, "F", 10, -2), Is.EqualTo("5"));
    Assert.That(Run(module, "F", 1, 1), Is.EqualTo("1"));
  }

  [TestCase(-5, "-1")]
  [TestCase(0, "0")]
  [TestCase(5, "1")]
  [TestCase(11, "1")]
  [TestCase(10, "2")]
  public void SelectCase_TestsEveryClauseKind(int value, string expected) {
    var module = Import(Function("Long", "    Select Case v\n        Case Is < 0\n            F = -1\n        Case 0\n            F = 0\n        Case 1 To 9, 11\n            F = 1\n        Case Else\n            F = 2\n    End Select", "ByVal v As Long"));
    Assert.That(Run(module, "F", value), Is.EqualTo(expected));
  }

  [Test]
  public void SelectCase_OnStrings_ComparesByCodeUnit() {
    var module = Import(Function("Long", "    Select Case s\n        Case \"a\"\n            F = 1\n        Case \"b\" To \"d\"\n            F = 2\n    End Select", "ByVal s As String"));
    Assert.That(Run(module, "F", "a") + Run(module, "F", "c") + Run(module, "F", "z"), Is.EqualTo("120"));
  }

  [TestCase("    If a > 0 Then F = 1 Else F = 2", 5, "1")]
  [TestCase("    If a > 0 Then F = 1 Else F = 2", -5, "2")]
  [TestCase("    If a > 10 Then\n        F = 1\n    ElseIf a > 0 Then\n        F = 2\n    Else\n        F = 3\n    End If", 5, "2")]
  [TestCase("    If a Then F = 7", 3, "7")]
  [TestCase("    If a And 1 Then F = 1: F = F + 1", 3, "2")]
  public void If_TakesBooleansAndNumbers(string body, int value, string expected) {
    var module = Import(Function("Long", body, "ByVal a As Long"));
    Assert.That(Run(module, "F", value), Is.EqualTo(expected));
  }

  [TestCase("F = \"a\" & n & \"b\" & True", "a12bTrue")]
  [TestCase("F = Left(s, 2) & Mid(s, 2, 3) & Right(s, 1) & Mid(s, 4)", "Heellolo")]
  [TestCase("F = InStr(s, \"l\") & InStr(4, s, \"l\") & InStr(s, \"z\") & InStr(s, \"\")", "3401")]
  [TestCase("F = Len(s) & Asc(s) & AscW(\"B\") & Chr(66) & ChrW(67)", "57266BC")]
  [TestCase("F = String(3, \"x\") & Space(2) & String(2, 65)", "xxx  AA")]
  [TestCase("F = StrComp(\"a\", \"b\") & StrComp(\"b\", \"a\", 0) & StrComp(s, s)", "-110")]
  [TestCase("F = Hex(255) & Hex(-1) & Hex(CInt(-1)) & Hex(CByte(10))", "FFFFFFFFFFA")]
  [TestCase("F = CStr(n) & CStr(False) & s + s", "12FalseHelloHello")]
  [TestCase("F = IIf(n > 10, \"big\", \"small\")", "big")]
  [TestCase("F = Left(s, -1)", "!5")]
  [TestCase("F = Mid(s, 0, 1)", "!5")]
  [TestCase("F = Mid(s, 9, 1)", "")]
  [TestCase("F = Chr(300)", "!5")]
  [TestCase("F = Asc(\"\")", "!5")]
  public void Strings_FollowVisualBasic(string body, string expected) {
    var module = Import(Function("String", "    Dim n As Long, s As String\n    n = 12\n    s = \"Hello\"\n    " + body));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [TestCase("F = (s < \"b\") And (s <> \"x\") And Not (s = \"b\")", "True")]
  [TestCase("F = a And b", "False")]
  [TestCase("F = a Or b", "True")]
  [TestCase("F = a Xor b", "True")]
  [TestCase("F = a Eqv b", "False")]
  [TestCase("F = a Imp b", "False")]
  [TestCase("F = CBool(5) = a", "True")]
  [TestCase("F = a = (1 = 1)", "True")]
  public void Booleans_EvaluateBothOperands(string body, string expected) {
    var module = Import(Function("Boolean", "    Dim a As Boolean, b As Boolean, s As String\n    a = True\n    s = \"a\"\n    " + body));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [Test]
  public void And_EvaluatesTheRightOperandEvenWhenTheLeftIsFalse() {
    var module = Import(Function("Boolean", "    Dim z As Long\n    F = z <> 0 And 1 \\ z > 0"));
    Assert.That(Run(module, "F"), Is.EqualTo("!11"));
  }

  [TestCase("F = CLng(True) * 10 + CLng(False)", "-10")]
  [TestCase("F = CByte(True)", "255")]
  [TestCase("F = CLng(2.5) + CLng(3.5)", "6")]
  [TestCase("F = CSng(1.5) * 2 + CDbl(1)", "4")]
  [TestCase("F = CSng(1E+300)", "!6")]
  [TestCase("F = &HFFFF + &HFFFF& + &O17 + 1%", "65550")]
  [TestCase("F = &H80000000", "-2147483648")]
  [TestCase("F = 3000000000# - 2147483648# + 1", "852516353")]
  public void Conversions_AndLiterals(string body, string expected) {
    var module = Import(Function("Long", "    " + body));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [Test]
  public void FloatFunctions_AreChecked() {
    var module = Import(Function("Double", "    F = Sqr(16) + Exp(0) + Log(1) + Sin(0) + Cos(0) + Tan(0) + Atn(0) + 1.5! * 2"));
    Assert.That(Run(module, "F"), Is.EqualTo("9"));
    Assert.That(Run(Import(Function("Double", "    F = Sqr(-1)")), "F"), Is.EqualTo("!5"));
    Assert.That(Run(Import(Function("Double", "    F = Log(0)")), "F"), Is.EqualTo("!5"));
    Assert.That(Run(Import(Function("Double", "    F = Exp(1000)")), "F"), Is.EqualTo("!6"));
    Assert.That(Run(Import(Function("Single", "    F = CSng(1) / 0")), "F"), Is.EqualTo("!11"));
    Assert.That(Run(Import(Function("Single", "    F = 3E+38! * 10!")), "F"), Is.EqualTo("!6"));
  }

  [Test]
  public void OnErrorGoTo_RunsTheHandlerWithTheErrorNumber() {
    var module = Import("Option Explicit\nPublic Function F(ByVal a As Long, ByVal b As Long) As Long\n    On Error GoTo Failed\n    F = a \\ b\n    Exit Function\nFailed:\n    F = -Err.Number\nEnd Function\n");
    Assert.That(Run(module, "F", 7, 2), Is.EqualTo("3"));
    Assert.That(Run(module, "F", 1, 0), Is.EqualTo("-11"));
  }

  [TestCase("i = 32767\n    i = i + 1", "600000")]
  [TestCase("a(5) = 1", "900000")]
  [TestCase("Err.Raise 13", "1300000")]
  [TestCase("Error 5", "500000")]
  [TestCase("Err.Raise 91", "9100000")]
  [TestCase("Err.Raise 6\n    Err.Clear", "0")]
  public void OnErrorResumeNext_SetsErrAndGoesOn(string failing, string expected) {
    var module = Import(Function("Long", "    Dim i As Integer, a(1) As Long\n    On Error Resume Next\n    " + failing + "\n    F = Err.Number * 100000"));
    Assert.That(Run(module, "F"), Is.EqualTo(expected));
  }

  [Test]
  public void ResumeNext_GoesOnInsideTheIfWhoseConditionFailed() {
    var module = Import(Function("Long", "    Dim z As Long\n    On Error Resume Next\n    F = 1\n    If 1 \\ z > 0 Then\n        F = 2\n    Else\n        F = 3\n    End If"));
    Assert.That(Run(module, "F"), Is.EqualTo("2"));
  }

  [Test]
  public void HandlerThatEndsWithResumeNext_GoesOnAfterTheFailingStatement() {
    var module = Import("Option Explicit\nPublic Function F() As Long\n    Dim r As Long, z As Long\n    On Error GoTo Failed\n    r = 1\n    r = 10 \\ z\n    r = r + 5\n    F = r + Err.Number\n    Exit Function\nFailed:\n    r = r * 100\n    Resume Next\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("105"));
  }

  [Test]
  public void OnErrorGoToZero_EndsResumeNext() {
    var module = Import(Function("Long", "    Dim z As Long\n    On Error Resume Next\n    Err.Raise 5\n    On Error GoTo 0\n    F = Err.Number\n    F = 1 \\ z"));
    Assert.That(Run(module, "F"), Is.EqualTo("!11"));
  }

  [Test]
  public void ErrorInACalledProcedure_ReachesTheCallersHandler() {
    var module = Import("Option Explicit\nPrivate Function Inner(ByVal z As Long) As Long\n    Inner = 1 \\ z\nEnd Function\nPublic Function F() As Long\n    On Error Resume Next\n    F = Inner(0)\n    F = Err.Number\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("11"));
  }

  [Test]
  public void Records_EnumsConstantsAndGlobals() {
    var module = Import("""
      Attribute VB_Name = "Shapes"
      Option Explicit
      Public Type Point
          X As Long
          Y As Integer
      End Type
      Public Type Segment
          A As Point
          B As Point
      End Type
      Public Enum Color
          Red
          Green = 5
          Blue
      End Enum
      Private Const Scale As Long = 10
      Public Const Title = "seg"
      Private calls As Long
      Private Function Length(s As Segment) As Long
          Length = Abs(s.B.X - s.A.X) * Scale
      End Function
      Public Function Run() As String
          Dim s As Segment, c As Color, points(1) As Point
          calls = calls + 1
          s.A.X = 1
          s.B.X = 4
          points(1).Y = 3
          c = Blue
          Run = Title & Length(s) & c & Red & points(1).Y & calls
      End Function
      """);
    Assert.That(module.Name, Is.EqualTo("Shapes"));
    Assert.That(Run(module, "Run"), Is.EqualTo("seg306031"));
  }

  [Test]
  public void Procedures_OptionalParametersByValArraysAndSubs() {
    var module = Import("""
      Option Explicit
      Private Function Twice(Optional ByVal x As Long = 3) As Long
          Twice = x * 2
      End Function
      Private Sub Fill(a() As Long, ByVal value As Long)
          Dim i As Long
          For i = LBound(a) To UBound(a)
              a(i) = value
          Next
      End Sub
      Private Function Total(a() As Long) As Long
          Dim i As Long
          For i = 0 To UBound(a)
              Total = Total + a(i)
          Next i
      End Function
      Private Function Make() As Long()
          Dim r() As Long
          ReDim r(2)
          Make = r
      End Function
      Private Function Seven() As Long
          Seven = 7
      End Function
      Public Function Run(ByVal n As Long) As Long
          Dim a(2) As Long, b() As Long
          Fill a, n
          Call Fill(a, n + 1)
          b = Make()
          Run = Total(a) + Twice() + Twice(5) + UBound(b) + Seven
      End Function
      """);
    Assert.That(Run(module, "Run", 2), Is.EqualTo("34"));
  }

  [Test]
  public void ByRefParameterThatIsNotAssigned_IsAValue() {
    var module = Import("Option Explicit\nPrivate Function Twice(v As Long) As Long\n    Twice = v * 2\nEnd Function\nPublic Function F() As Long\n    F = Twice(21)\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("42"));
  }

  [Test]
  public void RecursionAndTheResultVariable() {
    var module = Import(Function("Long", "    If n <= 1 Then\n        F = 1\n        Exit Function\n    End If\n    F = n * F(n - 1)", "ByVal n As Long"));
    Assert.That(Run(module, "F", 10), Is.EqualTo("3628800"));
    Assert.That(Run(module, "F", 13), Is.EqualTo("!6"));
  }

  [Test]
  public void CommentsContinuationsLabelsAndDebugPrint() {
    var module = Import("Option Explicit ' explicit\nRem a remark\nPublic Sub S()\n    Debug.Print \"x\" & _\n        1\nEnd Sub\nPublic Function F() As Long\n    Dim a As Long: a = 2: F = a ' two\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("2"));
  }

  [TestCase("Public Function F() As Long\n    F = 1 +\nEnd Function", "PLY2645")]
  [TestCase("Public Function F() As Long\n    F = \"open\nEnd Function", "PLY2645")]
  [TestCase("Public Function F() As Long\n    F = 1\n", "PLY2645")]
  [TestCase("Public Function F() As Long\n    F = 1 ~ 2\nEnd Function", "PLY2645")]
  [TestCase("Public Function F() As Long\n    F = &H\nEnd Function", "PLY2645")]
  [TestCase("Public Type T\n    X As Long\n", "PLY2645")]
  [TestCase("Public Function F() As Long\n    For i = 1 To 2\n    Next j\nEnd Function", "PLY2645")]
  public void SyntaxErrors_ArePly2645(string source, string code)
    => Assert.That(Refused(source), Does.Contain(code));

  [TestCase("Public Function F() As Long\n    Dim x As Object\nEnd Function", "Object")]
  [TestCase("Public Function F() As Long\n    Dim x As LongPtr\nEnd Function", "LongPtr")]
  [TestCase("Public Function F() As Long\n    GoSub L\nEnd Function", "GoSub")]
  [TestCase("Public Function F() As Long\n    With X\n    End With\nEnd Function", "With")]
  [TestCase("Public Function F() As Long\n    For Each x In y\n    Next\nEnd Function", "For Each")]
  [TestCase("Public Function F() As Long\n    Static n As Long\nEnd Function", "Static")]
  [TestCase("Public Function F() As Long\n    Set x = Nothing\nEnd Function", "Set")]
  [TestCase("Public Function F() As Long\n    GoTo L\nL:\nEnd Function", "GoTo")]
  [TestCase("Public Function F() As Long\n    F = UCase(\"a\")\nEnd Function", "UCase")]
  [TestCase("Public Function F() As Long\n    F = 1 Like 2\nEnd Function", "Like")]
  [TestCase("Public Function F() As Long\n    Err.Raise 1234\nEnd Function", "Err.Raise")]
  [TestCase("Public Function F() As Long\n    Dim a(1, 2) As Long\nEnd Function", "multidimensional")]
  [TestCase("Public Sub S(ByRef v As Long)\n    v = 1\nEnd Sub", "passed by reference")]
  [TestCase("Public Sub S(Optional v As Long)\nEnd Sub", "Optional")]
  [TestCase("Public Property Get P() As Long\nEnd Property", "Property")]
  [TestCase("Declare Function GetTickCount Lib \"kernel32\" () As Long", "Declare")]
  [TestCase("Option Compare Text", "Option Compare Text")]
  [TestCase("Public Function F() As Long\n    On Error GoTo H\n    F = 1\nEnd Function", "without the label")]
  [TestCase("Public Function F() As Long\n    On Error Resume Next\n    For F = 1 To 2\n    Next\nEnd Function", "under On Error Resume Next")]
  [TestCase("Public Function F() As Long\n    Do\n        For F = 1 To 2\n            Exit Do\n        Next\n    Loop\nEnd Function", "Exit Do out of a For loop")]
  [TestCase("Public Function F() As Long\n    Resume Next\nEnd Function", "Resume outside")]
  [TestCase("Public Function F() As Long\n    F = Nothing\nEnd Function", "Nothing")]
  [TestCase("Public Type T\n    S As String\nEnd Type", "String field")]
  [TestCase("Private h() As Long\nPublic Sub S()\n    ReDim h(2)\nEnd Sub", "module-level array")]
  [TestCase("Private h() As Long\nPublic Sub S()\n    Erase h\nEnd Sub", "module-level array")]
  [TestCase("Private h() As Long\nPublic Sub S()\n    Dim a() As Long\n    h = a\nEnd Sub", "module-level array")]
  [TestCase("Public Function F() As Long\n    Dim s(1) As String\nEnd Function", "array of Strings")]
  public void ConstructsOutsideTheSubset_ArePly2646(string source, string construct) {
    var text = Refused("Option Explicit\n" + source + "\n");
    Assert.That(text, Does.Contain("PLY2646").And.Contain(construct));
  }

  [TestCase("Public Function F() As Long\n    F = x\nEnd Function", "'x' is not declared")]
  [TestCase("Public Function F() As Long\n    Dim p As Missing\nEnd Function", "not declared")]
  [TestCase("Public Function F() As Long\n    Undefined 1\nEnd Function", "no procedure")]
  [TestCase("Public Function F() As Long\n    F = G(1)\nEnd Function", "no array, function")]
  [TestCase("Public Sub S()\nEnd Sub\nPublic Function F() As Long\n    F = S()\nEnd Function", "has no value")]
  [TestCase("Public Function F() As Long\n    F = Len(1, 2)\nEnd Function", "takes 1")]
  [TestCase("Public Function F() As Long\nEnd Function\nPublic Function F() As Long\nEnd Function", "declared twice")]
  [TestCase("Public Function F() As Long\n    Dim a As Long, a As Long\nEnd Function", "declared twice")]
  [TestCase("Public Function F() As Long\n    Exit For\nEnd Function", "outside a For")]
  [TestCase("Private Function G(ByVal a As Long) As Long\nEnd Function\nPublic Function F() As Long\n    F = G()\nEnd Function", "needs the argument")]
  [TestCase("Private Function G() As Long\nEnd Function\nPublic Function F() As Long\n    F = G(1)\nEnd Function", "takes 0 arguments")]
  [TestCase("Public Type T\n    X As Long\nEnd Type\nPublic Function F() As Long\n    Dim t1 As T\n    F = t1.Y\nEnd Function", "no field")]
  [TestCase("Public Function F() As Long\n    Dim n As Long\n    For n = 1 To 2\n    Next\n    For s = 1 To 2\n    Next\nEnd Function", "counter")]
  public void UnresolvedNamesAndMismatches_ArePly2648(string source, string message) {
    var text = Refused("Option Explicit\n" + source + "\n");
    Assert.That(text, Does.Contain("PLY2648").And.Contain(message));
  }

  [Test]
  public void ModuleName_ComesFromTheFileWithoutAnAttribute() {
    var result = VisualBasicFrontend.Import("dir/Sample.bas", "Public Function F() As Long\n    F = 1\nEnd Function\n");
    Assert.That(result.Module!.Name, Is.EqualTo("Sample"));
    Assert.That(() => VisualBasicFrontend.Import(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => VisualBasicFrontend.Import("a", null!), Throws.ArgumentNullException);
  }

}
