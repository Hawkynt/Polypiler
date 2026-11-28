using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// One Visual Basic .NET procedure per construct the frontend lowers by a rule of its own - the operator
/// types of compound assignments of every numeric type, the unary operators of the narrow types, the
/// branches out of a <c>Sub</c>, a switch whose sections all leave, a <c>For Each</c> into a variable
/// declared before - compared with the compiler, with integer checks on and removed.
/// </summary>
[TestFixture]
public sealed class VbNetOperationTests {

  private static string Module(string members) => $"Option Strict On\nPublic Module M\n{members}\nEnd Module\n";

  [TestCase("Dim s As Short = CShort(n)\ns += 1S\ns -= CShort(n)\nDim sb As SByte = CSByte(n)\nsb *= CSByte(2)\nDim us As UShort = CUShort(n)\nus += 1US\nDim ul As ULong = CULng(n)\nul += 2UL\nDim l As Long = n\nl += CUInt(n)\nl \\= 2\nDim d As Double = n\nd ^= 2\nd /= n\nDim g As Single = n\ng -= 0.5F\nDim b As Byte = CByte(n)\nb <<= 2\nb >>= 1\nReturn CStr(s) & CStr(sb) & CStr(us) & CStr(ul) & CStr(l) & CStr(d) & CStr(g) & CStr(b)")]
  [TestCase("Dim a() As Integer = New Integer(3) {}\nDim i As Integer = 1\na(i) += n\na(i + 1) += 2\nReturn CStr(a(1) + a(2))")]
  [TestCase("Dim s As Short = CShort(n)\nDim neg As Short = -s\nDim p As Integer = +n\nDim t As Boolean = CBool(n)\nDim e As Exception = New OverflowException()\nReturn CStr(neg) & CStr(p) & CStr(t) & e.Message")]
  [TestCase("Dim flag As Boolean = n > 1\nReturn CStr((n > 0) And (n < 5)) & CStr((n > 0) Or Not flag) & CStr(18446744073709551615UL) & CStr(CInt(Fix(n)) + CInt(Int(n)))")]
  [TestCase("Dim v As Integer\nDim total As Integer = 0\nFor Each v In New Integer() {n, 2, 3}\ntotal += v\nNext\nReturn CStr(total) & CStr(v)")]
  [TestCase("Select Case n\nCase 1\nReturn \"one\"\nCase 2\nExit Select\nCase Else\nReturn \"other\"\nEnd Select\nReturn \"two\"")]
  [TestCase("Dim text As String = \"\"\nSelect Case CStr(n)\nCase \"2\"\ntext = \"b\"\nExit Select\nCase Else\ntext = \"x\"\nEnd Select\nReport(text)\nReturn text")]
  [TestCase("Dim r As String = \"\"\nTry\nr &= Checked(n)\nCatch ex As OverflowException\nr &= \"!\"\nEnd Try\nReturn r")]
  [TestCase("Dim x As Double = n + 0.5\nReturn CStr(Math.Sin(x) + Math.Cos(x) + Math.Tan(x) + Math.Atan(x) + Math.Atan2(x, 2) + Math.Exp(x) + Math.Log(x) + Math.Log10(x) + Math.Ceiling(x) + Math.Truncate(-x) + Math.Round(x, MidpointRounding.ToEven) + Math.Sign(-x) + Math.Min(x, 1.0) + Math.Max(x, 1.0) + Math.Asin(0.5) + Math.Acos(0.5)) & CStr(Math.Abs(-n)) & CStr(Fix(n) + Int(n)) & CStr(-x)")]
  [TestCase("F = \"\"\nIf n > 2 Then Exit Function\nF = CStr(G(n)) & CStr(G(CDbl(n)))\nDim s As String = \"a\"\ns += \"b\" + s\nF &= s.ToString() & String.Concat(\"x\", \"y\", \"z\") & True.ToString() & s.EndsWith(\"b\", StringComparison.Ordinal).ToString() & s.IndexOf(\"a\", 1, StringComparison.Ordinal).ToString()")]
  [TestCase("Dim l As Long = n\nl += n\nDim s As Short = CShort(n)\ns += CByte(1)\nDim u As UInteger = CUInt(n)\nu += 1UI\nDim i As Integer = n\ni += CUShort(2)\nDim d As Double = 7.5\nd += n\nReturn CStr(l) & CStr(s) & CStr(u) & CStr(i) & CStr(d)")]
  [TestCase("Dim p As New Pair()\nDim q As New Pair With {.B = n}\nReturn CStr(p.A + q.B + n)")]
  [TestCase("Select Case n\nCase 0\nReturn \"zero\"\nCase 1\nIf n > 0 Then Exit Select\nReturn \"never\"\nCase Else\nReturn \"many\"\nEnd Select\nReturn \"one\"")]
  public void Construct_ComputesWhatTheCompilerComputes(string body) {
    var members = "Public Function F(ByVal n As Integer) As String\n" + body + "\nEnd Function\n"
      + "Private Sub Report(ByVal text As String)\nIf text.Length = 0 Then Exit Sub\nlast = text\nEnd Sub\nPrivate last As String = \"\"\n"
      + "Private Function Checked(ByVal n As Integer) As String\nReturn CStr(CByte(n * 100))\nEnd Function\n"
      + "Private Function G(ByVal x As Integer) As Integer\nReturn x + 1\nEnd Function\nPrivate Function G(ByVal x As Double) As Double\nReturn x / 2\nEnd Function\n"
      + "Public Structure Pair\nPublic A As Integer\nPublic B As Integer\nEnd Structure";
    var source = Module(members);
    foreach (var removed in new[] { false, true }) {
      var imported = VbNetFrontend.Import("M.vb", source, new VbNetImportOptions { RemoveIntegerChecks = removed });
      Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
      var assembly = VbNetFixtures.Load(source, removeIntegerChecks: removed);
      foreach (var argument in new[] { 0, 1, 2, 3 }) {
        var call = new PrototypeCall("F", [argument]);
        Assert.That(PrototypeFixture.InterpretedOutcome(imported.Module!, call), Is.EqualTo(PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(assembly, "F"), call)), $"{argument}, checks removed: {removed}");
      }
    }
  }

  [TestCase("Public Function F(ByVal a As Integer()) As Integer\nReturn a.Rank\nEnd Function", "a.Rank")]
  [TestCase("Public Function F() As Integer\nDim total As Integer = 0\nFor Each x As Integer In New System.Collections.Generic.List(Of Integer)()\ntotal += x\nNext\nReturn total\nEnd Function", "List")]
  [TestCase("Public Function F(ByVal x As ULong, ByVal y As Long) As ULong\nDim z As ULong = x\nz += y\nReturn z\nEnd Function", "compound assignment")]
  [TestCase("Public Function F() As Integer\nDim e As New ArgumentException(\"a\", \"b\")\nReturn 0\nEnd Function", "creation")]
  [TestCase("Private table(n) As Integer\nPrivate n As Integer = 3", "no constant")]
  [TestCase("Public Function F() As Integer\nDim ex As Exception = Nothing\nTry\nReturn 1\nCatch ex\nReturn 2\nEnd Try\nEnd Function", "declared elsewhere")]
  [TestCase("Public Function F() As Boolean\nReturn \"a\" Like \"a*\"\nEnd Function", "operator")]
  [TestCase("Public Function F(ByVal s As String) As Char\nReturn CChar(s)\nEnd Function", "conversion from")]
  [TestCase("Public Structure P\nPublic A As Integer\nPublic B As Integer\nEnd Structure\nPrivate c As Integer = 0\nPrivate Function N() As Integer\nc += 1\nReturn c\nEnd Function\nPublic Function F() As Integer\nDim p As New P With {.B = N(), .A = N()}\nReturn p.A\nEnd Function", "out of their order")]
  [TestCase("Public Function F() As Integer\nDim a(,) As Integer\nReDim a(1, 1)\nReturn 0\nEnd Function", "several dimensions")]
  public void Construct_OutsideTheSubset_IsPly2611(string members, string construct) {
    var imported = VbNetFrontend.Import("M.vb", $"Option Strict Off\nPublic Module M\n{members}\nEnd Module\n", new VbNetImportOptions { OptionStrict = false });
    Assert.That(imported.Diagnostics, Has.Some.Property("Code").EqualTo(VbNetFrontend.UnsupportedCode));
    Assert.That(string.Join("\n", imported.Diagnostics), Does.Contain(construct));
  }

}
