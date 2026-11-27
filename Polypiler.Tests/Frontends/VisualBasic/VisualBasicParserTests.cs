using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using static Polypiler.Tests.Frontends.VisualBasic.VisualBasicFrontendTests;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>The lexical and syntactic forms of the classic Visual Basic parser: what it reads, and the code of what it does not.</summary>
[TestFixture]
public sealed class VisualBasicParserTests {

  private static string Diagnostics(string source)
    => string.Join("\n", VisualBasicFrontend.Import("P.bas", source).Diagnostics.Select(diagnostic => diagnostic.ToString()));

  [TestCase("Public Function F() As Long\n    F = &HFFFFFFFFFFFFFFFFF\nEnd Function", "PLY2645", "does not fit")]
  [TestCase("Public Function F() As Long\n    F = 1\nEnd Function\nPublic Enum E\n    A\n", "PLY2645", "End Enum")]
  [TestCase("Public Function F() As Long\n    F = 1\nEnd Function\nX = 1", "PLY2645", "cannot start a declaration")]
  [TestCase("Public Function F(ParamArray a()) As Long\nEnd Function", "PLY2646", "ParamArray")]
  [TestCase("Public Function F() As Long\n    Do While F < 1\n    Loop Until F > 2\nEnd Function", "PLY2645", "both ends")]
  [TestCase("Public Function F() As Long\n    F = 1 2\nEnd Function", "PLY2645", "unexpected '2'")]
  [TestCase("Public Function F() As Long\n    If F Then F = 1 Else F = 2 3\nEnd Function", "PLY2645", "unexpected")]
  [TestCase("Public Function F(a As Long) As Long\n    Select Case a\n        Case Is ~\n    End Select\nEnd Function", "PLY2645", "unexpected character")]
  [TestCase("Public Function F(a As Long) As Long\n    Select Case a\n        Case Is Like 1\n    End Select\nEnd Function", "PLY2645", "comparison after Is")]
  [TestCase("Public Function F() As Long\n    Exit 1\nEnd Function", "PLY2645", "expected Do, For")]
  [TestCase("Public Function F() As Long\n    F.\nEnd Function", "PLY2645", "member name")]
  [TestCase("Public Function F() As Long\n    Dim x As\nEnd Function", "PLY2645", "expected a type")]
  [TestCase("Public Function F() As Long\n    Dim 5\nEnd Function", "PLY2645", "expected a name")]
  [TestCase("Public Function F() As Long\n    F = (1\nEnd Function", "PLY2645", "expected ')'")]
  [TestCase("Public Function F() As Long\n    Select Case F\n    Loop\nEnd Function", "PLY2645", "expected 'End'")]
  [TestCase("Static Function F() As Long\nEnd Function", "PLY2646", "Static procedure")]
  [TestCase("Public Property Let P(v As Long)\n    Dim x As Long\n    x = v\nEnd Property", "PLY2646", "Property")]
  [TestCase("Friend Sub S()\nEnd Sub", "PLY2646", "Friend")]
  [TestCase("Public Event Changed()", "PLY2646", "Event")]
  [TestCase("Implements IFoo", "PLY2646", "Implements")]
  [TestCase("Public Function F() As Long\n    Dim c As New Collection\nEnd Function", "PLY2648", "New Collection")]
  [TestCase("Public Function F() As Long\n    Dim c As Scripting.Dictionary\nEnd Function", "PLY2648", "Scripting.Dictionary")]
  [TestCase("Public Function F() As Long\n    Dim c@\nEnd Function", "PLY2646", "Currency")]
  [TestCase("Public Function F() As Long\n    On F GoTo 10, 20\nEnd Function", "PLY2646", "On")]
  [TestCase("Public Function F() As Long\n    On Error GoTo -1\nEnd Function", "PLY2646", "GoTo -1")]
  [TestCase("Public Function F() As Long\n    On Error GoTo H\n    Exit Function\nH:\n    Resume 10\nEnd Function", "PLY2646", "Resume")]
  [TestCase("Public Function F() As Long\n    If F Then GoTo 10\n10\nEnd Function", "PLY2646", "GoTo")]
  [TestCase("Public Function F() As Long\n    Stop\nEnd Function", "PLY2646", "Stop")]
  [TestCase("Public Function F() As Long\n    Return\nEnd Function", "PLY2646", "Return")]
  [TestCase("Public Function F() As Long\n10  F = 1\nEnd Function", "PLY2646", "label 10")]
  public void Form_IsRefusedWithItsCode(string source, string code, string text)
    => Assert.That(Diagnostics("Option Explicit\n" + source + "\n"), Does.Contain(code).And.Contain(text));

  [Test]
  public void LexicalForms_AreRead() {
    var module = Import(""""
      Option Explicit
      Rem the whole line
      Private Const Quote As String = "say ""hi"""
      Private Function Twice(ByVal v As Long) As Long
          Twice = v * 2
      End Function
      Private Sub Note(ByVal v As Long)
      End Sub
      Public Function F(ByVal a As Long) As Long
          Dim d As Double, e As Double, b As Boolean
          d = 1D2
          e = 2.5E-1
          b = a =< 3 Or a => 9 Or a >< 4
          Note (a)
          Note (a) + 1
          Call Note(a)
          F = Twice(a) + +1 + Len(Quote) + d + e * 4 + CLng(b)
      End Function
      """");
    Assert.That(Run(module, "F", 3), Is.EqualTo("115"));
  }

  [Test]
  public void ReDimWithAType_AndAFieldTarget() {
    var module = Import("Public Function F() As Long\n    Dim a() As Long\n    ReDim a(1 To 2) As Long\n    F = UBound(a)\nEnd Function\n");
    Assert.That(Run(module, "F"), Is.EqualTo("2"));
    Assert.That(Diagnostics("Public Type T\n    X As Long\nEnd Type\nPublic Sub S()\n    Dim t1 As T\n    ReDim t1.X(2)\nEnd Sub\n"), Does.Contain("PLY2648"));
  }

  [Test]
  public void SubCalledWithParenthesesAndAStatementPrefix() {
    var module = Import("Private n As Long\nPrivate Sub Add(ByVal v As Long)\n    n = n + v\nEnd Sub\nPublic Function F() As Long\n    Add(2)\n    Add (3), \n    Let n = n + 1\n    F = n\nEnd Function\n".Replace("Add (3), \n", "Add (3)\n"));
    Assert.That(Run(module, "F"), Is.EqualTo("6"));
  }

}
