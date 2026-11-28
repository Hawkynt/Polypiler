using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using static Polypiler.Tests.Frontends.VisualBasic.VisualBasicFrontendTests;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>The boundaries of the classic Visual Basic lowering: every refusal by name, and the constructs the main tests leave out.</summary>
[TestFixture]
public sealed class VisualBasicLoweringEdgeTests {

  private static string Refused(string source) {
    var result = VisualBasicFrontend.Import("Edge.bas", "Option Explicit\n" + source + "\n");
    Assert.That(result.Module, Is.Null, "imported");
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  [TestCase("Public Sub S(a As Long, a As Long)\nEnd Sub", "PLY2648", "declared twice")]
  [TestCase("Public Sub S(a() As Long)\n    ReDim a(2)\nEnd Sub", "PLY2646", "resized")]
  [TestCase("Public Sub S()\n    Dim a(1 To 0) As Long\nEnd Sub", "PLY2648", "upper bound below")]
  [TestCase("Public Sub S(a() As Long)\n    Erase a\nEnd Sub", "PLY2646", "resized")]
  [TestCase("Public Sub S()\n    On Error GoTo H\n    On Error Resume Next\nH:\nEnd Sub", "PLY2646", "second On Error")]
  [TestCase("Public Sub S()\n    On Error GoTo H\n    Exit Sub\nH:\n    On Error GoTo 0\nEnd Sub", "PLY2646", "handler with On Error")]
  [TestCase("Public Sub S()\n    On Error GoTo H\n    Exit Sub\nH:\n    Resume\nEnd Sub", "PLY2646", "Resume other")]
  [TestCase("Public Sub S()\n    Dim i As Long\n    On Error Resume Next\n    Select Case i\n    End Select\nEnd Sub", "PLY2646", "Select Case under")]
  [TestCase("Public Sub S()\n    Dim i As Long\n    On Error Resume Next\n    Do\n    Loop\nEnd Sub", "PLY2646", "Do under")]
  [TestCase("Public Sub S()\n    Dim i As Long\n    On Error Resume Next\n    If i Then\n    ElseIf i Then\n    End If\nEnd Sub", "PLY2646", "If with ElseIf under")]
  [TestCase("Public Sub S()\n    Dim i As Long\n    If i Then\n        On Error Resume Next\n    End If\nEnd Sub", "PLY2646", "inside a block")]
  [TestCase("Public Sub S()\n    Dim i As Long\n    If i Then\nL:\n    End If\nEnd Sub", "PLY2646", "label L")]
  [TestCase("Public Sub S()\n    Err.Raise 5, \"x\"\nEnd Sub", "PLY2646", "Err.Raise")]
  [TestCase("Public Sub S()\n    Err.Description = 1\nEnd Sub", "PLY2646", "store into")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Err.Description\nEnd Sub", "PLY2646", "Err.Description")]
  [TestCase("Public Sub S()\n    Err.Clear 1\nEnd Sub", "PLY2646", "Err.Clear")]
  [TestCase("Public Sub S()\n    Dim x As Long\n    x.Y = 1\nEnd Sub", "PLY2648", "no field")]
  [TestCase("Public Sub S()\n    Dim x As Long\n    x(1) = 1\nEnd Sub", "PLY2648", "no array")]
  [TestCase("Public Sub S()\n    Dim x As Long\n    x(1).Y = 1\nEnd Sub", "PLY2648", "no array")]
  [TestCase("Public Sub S()\n    S2 = 1\nEnd Sub\nPublic Sub S2()\nEnd Sub", "PLY2648", "no variable")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n.Show\nEnd Sub", "PLY2646", "member")]
  [TestCase("Public Sub S()\n    Exit Property\nEnd Sub", "PLY2646", "Exit Property")]
  [TestCase("Public Function F() As Long\n    Exit Sub\nEnd Function", "PLY2646", "Exit Sub")]
  [TestCase("Public Sub S()\n    Dim a() As Long\n    ReDim a(1, 2)\nEnd Sub", "PLY2646", "multidimensional")]
  [TestCase("Public Sub S()\n    Dim a As Long\n    ReDim a(2)\nEnd Sub", "PLY2648", "no array variable")]
  [TestCase("Public Sub S()\n    Dim a() As Long\n    ReDim a(2)\n    ReDim a(1 To 3)\nEnd Sub", "PLY2646", "lower bound 1")]
  [TestCase("Public Type P\n    X As Long\nEnd Type\nPublic Sub S()\n    Dim a() As P\n    ReDim Preserve a(2)\nEnd Sub", "PLY2646", "ReDim Preserve")]
  [TestCase("Public Sub S()\n    Dim a() As Boolean\nEnd Sub", "PLY2646", "array of Booleans")]
  [TestCase("Public Sub S()\n    Dim a As Long\n    Erase a\nEnd Sub", "PLY2648", "Erase of something")]
  [TestCase("Public Enum E\n    A = &H7FFFFFFFFFFF\nEnd Enum", "PLY2646", "no Long")]
  [TestCase("Public Enum E\n    A = 1.5\nEnd Enum", "PLY2646", "no integer constant")]
  [TestCase("Private Const C As Long = Len(\"a\")", "PLY2646", "no constant expression")]
  [TestCase("Option Base 2", "PLY2646", "Option Base 2")]
  [TestCase("Public Function F() As Decimal\nEnd Function", "PLY2646", "Decimal")]
  [TestCase("Public Function F() As Currency\nEnd Function", "PLY2646", "Currency")]
  [TestCase("Public Function F() As String * 4\nEnd Function", "PLY2646", "String * n")]
  [TestCase("Public Type T\n    A() As Long\nEnd Type", "PLY2646", "array field")]
  [TestCase("Public Sub S()\n    Dim x As Long\n    x = 1 Is 2\nEnd Sub", "PLY2646", "Is")]
  [TestCase("Public Sub S()\n    Dim s As String\n    s = -\"a\"\nEnd Sub", "PLY2648", "negation")]
  [TestCase("Public Sub S()\n    Dim s As String\n    s = Not \"a\"\nEnd Sub", "PLY2648", "Not")]
  [TestCase("Public Sub S()\n    Dim b As Boolean\n    b = \"a\" < 1\nEnd Sub", "PLY2648", "comparison")]
  [TestCase("Public Sub S()\n    If \"a\" Then\n    End If\nEnd Sub", "PLY2648", "condition")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = \"a\" \\ 2\nEnd Sub", "PLY2648", "\\")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = CLng(\"5\")\nEnd Sub", "PLY2646", "of a String")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Len(5)\nEnd Sub", "PLY2648", "Len")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Abs(\"a\")\nEnd Sub", "PLY2648", "Abs")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Int(\"a\")\nEnd Sub", "PLY2648", "Int")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Round(\"a\")\nEnd Sub", "PLY2648", "Round")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Sgn(\"a\")\nEnd Sub", "PLY2648", "Sgn")]
  [TestCase("Public Sub S()\n    Dim n As Double\n    n = Sqr(\"a\")\nEnd Sub", "PLY2648", "Sqr")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = UBound(n)\nEnd Sub", "PLY2648", "UBound")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = IIf(True, 1, \"a\")\nEnd Sub", "PLY2648", "IIf")]
  [TestCase("Public Sub S()\n    Dim s As String\n    s = Hex(1.5)\nEnd Sub", "PLY2648", "Hex")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = StrComp(\"a\", \"b\", 1)\nEnd Sub", "PLY2646", "StrComp")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Mid(\"a\")\nEnd Sub", "PLY2648", "Mid")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Left(\"a\")\nEnd Sub", "PLY2648", "Left")]
  [TestCase("Public Sub S()\n    Dim n As Long\n    n = Val(\"a\")\nEnd Sub", "PLY2646", "Val")]
  [TestCase("Public Sub S()\n    Dim a(1) As Long\n    a(1, 2) = 3\nEnd Sub", "PLY2646", "assignment to")]
  [TestCase("Public Sub S()\n    Dim a(1) As Long, n As Long\n    n = a(1, 2)\nEnd Sub", "PLY2646", "multidimensional")]
  [TestCase("Public Sub S()\n    Dim a(1) As Long, n As Long\n    n = a(\"x\")\nEnd Sub", "PLY2648", "Type mismatch")]
  [TestCase("Public Sub S(a() As Long)\nEnd Sub\nPublic Sub T()\n    Dim b(1 To 2) As Long\n    S b\nEnd Sub", "PLY2648", "array argument")]
  [TestCase("Public Sub S()\n    Dim d As Double\n    Debug.Print d\nEnd Sub", "PLY2646", "text of a Double")]
  public void Refusal_NamesTheConstruct(string source, string code, string text) {
    var diagnostics = Refused(source);
    Assert.That(diagnostics, Does.Contain(code).And.Contain(text));
  }

  [Test]
  public void OptionsAndTypeCharacters_AreRead() {
    var module = Import("Option Explicit\nOption Compare Binary\nOption Private Module\nPublic Function F%(ByVal a&, b$)\n    Dim d#, s!\n    d = 1.5\n    s = 2.5!\n    F = a + Len(b) + d + s\nEnd Function\n");
    Assert.That(Run(module, "F", 1, "ab"), Is.EqualTo("7"));
  }

  [Test]
  public void LongLong_IsSixtyFourBits() {
    var module = Import("Public Function F(ByVal a As LongLong) As LongLong\n    F = a * 2 + 5^\nEnd Function\n");
    Assert.That(Run(module, "F", 1L << 40), Is.EqualTo("2199023255557"));
    Assert.That(Run(module, "F", long.MaxValue), Is.EqualTo("!6"));
    Assert.That(Run(Import("Public Function F() As LongLong\n    F = &H7FFFFFFFFFFFFFFF \\ 2\nEnd Function\n"), "F"), Is.EqualTo("4611686018427387903"));
    Assert.That(Run(Import("Public Function F() As Long\n    F = 3000000000 - 2147483647\nEnd Function\n"), "F"), Is.EqualTo("852516353"));
  }

  [Test]
  public void LocalConstants_ErrNumberAssignmentAndDebugPrintOfIntegers() {
    var module = Import("Public Function F() As Long\n    Const K As Integer = 7\n    Const T = \"t\"\n    Err.Number = K\n    Debug.Print K\n    Debug.Print T\n    F = Err.Number + Len(T)\nEnd Function\n");
    Assert.That(IrRun(module), Is.EqualTo("8"));
  }

  private static string IrRun(Polypiler.Core.IR.IrModule module) {
    var instance = Polypiler.Core.IR.IrInterpreter.Instantiate(module, _ => { });
    return System.Convert.ToString(instance.Call("F", []), System.Globalization.CultureInfo.InvariantCulture)!;
  }

  [Test]
  public void ExitSubAndExitDo_InsideTheirOwnLoops() {
    var module = Import("Private n As Long\nPublic Sub S(ByVal k As Long)\n    Do\n        n = n + 1\n        If n >= k Then Exit Do\n    Loop\n    If n > 100 Then Exit Sub\n    n = n * 10\nEnd Sub\nPublic Function F() As Long\n    S 3\n    F = n\nEnd Function\n");
    Assert.That(IrRun(module), Is.EqualTo("30"));
  }

  [Test]
  public void ModuleVariablesAndBooleanArraysCopy() {
    var module = Import("Public Type P\n    X As Long\nEnd Type\nPrivate flags(1) As Integer\nPublic Function F() As Long\n    Dim a() As P, b() As P\n    ReDim a(0 To 1)\n    a(1).X = 5\n    b = a\n    a(1).X = 0\n    flags(0) = b(1).X\n    F = flags(0) + UBound(flags)\nEnd Function\n");
    Assert.That(IrRun(module), Is.EqualTo("6"));
  }

  [Test]
  public void DoLoopWhoseConditionNeedsStatements_IsTestedInside() {
    var module = Import("Public Function F() As Long\n    Dim n As Long\n    Do While IIf(n < 3, n + 1, 0) > n\n        n = n + 1\n    Loop\n    Do\n        n = n + 1\n    Loop Until IIf(n > 5, 1, 0) * 2 > 1\n    F = n\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("6"));
  }

  [Test]
  public void SelectCaseWithElseIfConditionsThatNeedStatements() {
    var module = Import("Public Function F(ByVal v As Long) As Long\n    If v = 1 Then\n        F = 1\n    ElseIf IIf(v > 5, v * 2, 0) > 0 Then\n        F = 2\n    End If\n    Select Case v\n    End Select\nEnd Function\n");
    Assert.That(Run(module, "F", 7) + Run(module, "F", 1) + Run(module, "F", 3), Is.EqualTo("210"));
  }

  [Test]
  public void ForWithFloatCounter_StepsInItsType() {
    var module = Import("Public Function F() As Long\n    Dim x As Double, n As Long\n    For x = 0 To 1 Step 0.25\n        n = n + 1\n    Next\n    F = n\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("5"));
  }

  [Test]
  public void FunctionWithoutParentheses_IsCalledByName() {
    var module = Import("Private Function Seven() As Long\n    Seven = 7\nEnd Function\nPublic Function F() As Long\n    F = Seven + Seven()\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("14"));
  }

  [Test]
  public void ArrayFieldElement_AndByteArithmetic() {
    var module = Import("Public Type P\n    X As Long\nEnd Type\nPublic Function F() As Long\n    Dim ps(2) As P, b As Byte\n    ps(2).X = 4\n    b = 200\n    F = ps(2).X + b \\ 3 + (b Mod 7) + (Not b) + -b\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("-71"));
  }

}
