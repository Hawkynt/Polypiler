using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.VisualBasic.Printing;
using Polypiler.Core.Backends.VisualBasic.Syntax;

namespace Polypiler.Tests.Backends.VisualBasic;

/// <summary>The Visual Basic syntax tree: what the printer writes for each node, and that the reader reads it back to the same shape.</summary>
[TestFixture]
public sealed class VisualBasicSyntaxTests {

  private static void RoundTrips(string expression) {
    var read = VbSyntaxReader.ReadExpression(expression);
    Assert.That(VbPrinter.Print(read), Is.EqualTo(expression));
  }

  [TestCase("a + b * c")]
  [TestCase("(a + b) * c")]
  [TestCase("a - (b - c)")]
  [TestCase("a - b - c")]
  [TestCase("-a ^ 2")]
  [TestCase("(-5) ^ 2")]
  [TestCase("2 ^ (-1)")]
  [TestCase("a * (-b)")]
  [TestCase("Not a = b")]
  [TestCase("a = (Not b)")]
  [TestCase("Not a And b")]
  [TestCase("Not (a And b)")]
  [TestCase("a Or b And c")]
  [TestCase("(a Or b) And c")]
  [TestCase("a Xor b Eqv c Imp d")]
  [TestCase("a \\ b Mod c")]
  [TestCase("a & b + c")]
  [TestCase("a & (b = c)")]
  [TestCase("x.y.z(1, 2)(3)")]
  [TestCase("F()")]
  [TestCase("-(-a)")]
  [TestCase("\"say \"\"hi\"\"\"")]
  [TestCase("True And False")]
  [TestCase("&H80000000")]
  [TestCase("&HFFFF&")]
  [TestCase("&H8000000000000000^")]
  [TestCase("5&")]
  [TestCase("5%")]
  [TestCase("-5^")]
  [TestCase("1.5")]
  [TestCase("3#")]
  [TestCase("1E+300")]
  [TestCase("1E-05")]
  [TestCase("1.5!")]
  [TestCase("-2.25")]
  public void Expression_PrintsAndReadsBack(string expression) => RoundTrips(expression);

  [Test]
  public void Literals_AreWrittenInTheFormVisualBasicReads() {
    Assert.That(VbPrinter.Print(new VbInteger(-2147483648) { Hex = true }), Is.EqualTo("&H80000000"));
    Assert.That(VbPrinter.Print(new VbFloat(1e20, false)), Is.EqualTo("1E+20"));
    Assert.That(VbPrinter.Print(new VbFloat(0.1f, true)), Is.EqualTo("0.1!"));
    Assert.That(VbPrinter.Print(new VbFloat(-0.0, false)), Is.EqualTo("-0#"));
    Assert.That(VbPrinter.Print(new VbUnary("-", new VbInteger(-1))), Is.EqualTo("-(-1)"));
    Assert.That(() => VbPrinter.Print(new VbFloat(double.NaN, false)), Throws.ArgumentException);
    Assert.That(VbSyntaxReader.ReadExpression("-1.5"), Is.EqualTo(new VbFloat(-1.5, false)));
    Assert.That(VbSyntaxReader.ReadExpression(".5"), Is.EqualTo(new VbFloat(0.5, false)));
    Assert.That(VbPrinter.Precedence("^"), Is.GreaterThan(VbPrinter.Precedence("*")));
    Assert.That(() => VbPrinter.Precedence("?"), Throws.ArgumentException);
  }

  private const string Module = """
    Attribute VB_Name = "Sample"
    ' A header.
    '
    Option Explicit

    Public Type Point
        X As Long
        Values(0 To 3) As Integer
    End Type

    Private Const LIMIT As Long = 10
    Public Total As Double
    Private Table() As Long

    ' A procedure.
    Public Function Run(ByVal a As Long, b() As Long, ByRef p As Point) As Variant
        Dim i As Long, r() As Long
        Static calls As Long
        If a > 0 Then
            Run = 1
        ElseIf a < 0 Then
            Run = -1
        Else
            Run = 0
        End If
        Select Case a
            Case 1, 2 To 3, Is > 9
                Exit Function
            Case Else
                Run = 2
        End Select
        For i = 1 To 10 Step 2
            b(i) = i
        Next i
        Do While i > 0
            i = i - 1
        Loop
        Do
            i = i + 1
        Loop Until i > 3
        On Error GoTo Failed
        On Error GoTo 0
        On Error Resume Next
        ReDim Preserve r(0 To a)
        Erase r
        Err.Raise 6
        Call F((a + 1) * 2, 2)
        Mark
        GoTo Done
    Failed:
        Resume Next
        Resume
        Resume Done
    Done:
        Exit Function
    End Function

    Private Sub Mark()
        Debug.Print "x"
    End Sub
    """;

  [Test]
  public void Module_PrintsAndReadsBackWithTheSameShape() {
    var read = VbSyntaxReader.ReadModule(Module);
    var printed = VbPrinter.Print(read);
    Assert.That(printed, Is.EqualTo(Module.Replace("\r\n", "\n") + "\n"));
    Assert.That(VbSyntaxRewriter.Shape(VbSyntaxReader.ReadModule(printed)), Is.EqualTo(VbSyntaxRewriter.Shape(read)));
    Assert.That(read.Header, Is.EqualTo(new[] { "A header.", "" }));
    Assert.That(VbSyntaxRewriter.Names(read), Does.Contain("Mark").And.Contain("i"));
    Assert.That(VbSyntaxRewriter.DescendantsAndSelf(read).OfType<VbLabel>().Select(label => label.Name), Is.EqualTo(new[] { "Failed", "Done" }));
  }

  [Test]
  public void ContinuationsAndComments_AreRead() {
    var declarations = VbSyntaxReader.ReadDeclarations("Private Sub S() ' a comment\n    x = 1 + _\n        2 ' trailing\nEnd Sub\n");
    var procedure = (VbProcedure)declarations.Single();
    Assert.That(VbPrinter.Print(procedure.Body), Is.EqualTo("x = 1 + 2\n"));
    Assert.That(VbPrinter.Print(declarations[0]), Does.StartWith("Private Sub S()"));
  }

  [TestCase("x = \"open", "not closed")]
  [TestCase("x = 1 ~ 2", "unexpected character")]
  [TestCase("x = &H", "no digits")]
  [TestCase("x = (1", "expected ')'")]
  [TestCase("x = 1 1", "unexpected '1'")]
  [TestCase("If x Then", "unexpected 'End'")]
  [TestCase("For i = 1 To 2\nNext j", "another counter")]
  [TestCase("Dim 1 As Long", "expected a name")]
  [TestCase("x. = 2", "member name")]
  [TestCase("On Error GoTo", "expected a name")]
  [TestCase("Select Case x\n    Case Is ~ 1\nEnd Select", "unexpected character")]
  [TestCase("Select Case x\n    Case Is\nEnd Select", "comparison after Is")]
  [TestCase("x = \"a\" ' c\n    ' dangling\nEnd Sub", "comment stands in front")]
  [TestCase("x = .", "unexpected '.'")]
  public void Reader_RefusesWhatThePrinterDoesNotWrite(string body, string message) {
    var text = "Private Sub S()\n    " + body + "\nEnd Sub\n";
    Assert.That(() => VbSyntaxReader.ReadDeclarations(text), Throws.TypeOf<FormatException>().With.Message.Contains(message));
  }

  [Test]
  public void Reader_RefusesAModuleWithoutItsAttributeAndUnclosedBlocks() {
    Assert.That(() => VbSyntaxReader.ReadModule(""), Throws.TypeOf<FormatException>());
    Assert.That(() => VbSyntaxReader.ReadModule("Option Explicit\n"), Throws.TypeOf<FormatException>());
    Assert.That(() => VbSyntaxReader.ReadDeclarations("Private Type T\n    X As Long\n"), Throws.TypeOf<FormatException>().With.Message.Contains("End Type"));
    Assert.That(() => VbSyntaxReader.ReadDeclarations("Private Sub S()\n    Select Case x\n"), Throws.TypeOf<FormatException>().With.Message.Contains("End Select"));
    Assert.That(() => VbSyntaxReader.ReadDeclarations("Private Sub S()\n"), Throws.TypeOf<FormatException>());
    Assert.That(() => VbSyntaxReader.ReadExpression(null!), Throws.ArgumentNullException);
    Assert.That(() => VbSyntaxReader.ReadModule(null!), Throws.ArgumentNullException);
    Assert.That(() => VbSyntaxReader.ReadDeclarations(null!), Throws.ArgumentNullException);
  }

  private sealed record Unknown : VbExpression;

  private sealed record UnknownStatement : VbStatement;

  private sealed record UnknownDeclaration : VbDeclaration;

  [Test]
  public void Printer_RefusesNodesItDoesNotKnow() {
    Assert.That(() => VbPrinter.Print(new Unknown()), Throws.ArgumentException);
    Assert.That(() => VbPrinter.Print([new UnknownStatement()]), Throws.ArgumentException);
    Assert.That(() => VbPrinter.Print(new UnknownDeclaration()), Throws.ArgumentException);
    Assert.That(() => VbPrinter.Print((VbModule)null!), Throws.ArgumentNullException);
    Assert.That(() => VbPrinter.Print((VbExpression)null!), Throws.ArgumentNullException);
    Assert.That(() => VbPrinter.Print((VbDeclaration)null!), Throws.ArgumentNullException);
    Assert.That(() => VbPrinter.Print((System.Collections.Generic.IReadOnlyList<VbStatement>)null!), Throws.ArgumentNullException);
    Assert.That(VbSyntaxRewriter.Shape(new Unknown()), Is.EqualTo("known[]"));
    Assert.That(() => VbSyntaxRewriter.Shape(null!), Throws.ArgumentNullException);
    Assert.That(() => VbSyntaxRewriter.DescendantsAndSelf(null!).ToList(), Throws.ArgumentNullException);
  }

}
