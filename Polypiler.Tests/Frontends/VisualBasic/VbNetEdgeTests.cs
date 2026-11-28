using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The boundaries of the Visual Basic .NET lowering the fixtures leave out: the operator types of
/// compound assignments across signs and widths, the conversions and comparisons of each kind of
/// operand, the members of <c>String</c> and <c>Math</c> by overload, each compared with the compiler,
/// and each refusal by the construct it names.
/// </summary>
[TestFixture]
public sealed class VbNetEdgeTests {

  private static string Module(string members, string strict = "On") => $"Option Strict {strict}\nPublic Module M\n{members}\nEnd Module\n";

  private static void Agrees(string source, object argument, bool strict) {
    var imported = VbNetFrontend.Import("M.vb", source, new VbNetImportOptions { OptionStrict = strict });
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var call = new PrototypeCall("F", [argument]);
    var expected = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(VbNetFixtures.Load(source, removeIntegerChecks: false), "F"), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(imported.Module!, call), Is.EqualTo(expected));
  }

  /// <summary>A procedure <c>F</c> under <c>Option Strict On</c>: the interpreter does what the compiled procedure does.</summary>
  [TestCase("Public Function F(ByVal n As Integer) As Short\nDim s As Short = CShort(n)\ns += CByte(3)\nReturn s\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Long\nDim l As Long = n\nl += CUInt(3)\nl -= 2S\nl *= CUShort(2)\nl = l Xor CSByte(-1)\nReturn l\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = n\ni += CUShort(9)\ni = i And CByte(255)\nReturn i\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Double\nDim d As Double = n\nd /= 2\nd ^= 2\nd -= CSng(0.5)\nReturn d\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Single\nDim x As Single = n\nx *= 2\nx += CLng(1)\nReturn x\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim s As String = \"a\"\ns += \"b\"\ns &= CStr(n)\nReturn s\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Byte\nDim b As Byte = CByte(n)\nb <<= 5\nb >>= 1\nReturn b\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As SByte\nDim b As SByte = CSByte(n)\nb = -b\nb = Not b\nReturn b\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As SByte\nDim b As SByte = CSByte(n - 135)\nReturn -b\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As UInteger\nDim u As UInteger = CUInt(n)\nReturn Not u\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Long\nDim u As ULong = CULng(n)\nReturn CLng(Not u And 255UL) + (+n)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Boolean\nDim b As Byte = CByte(n)\nDim s As SByte = 3\nReturn b > CByte(2) AndAlso s <= CSByte(n) AndAlso CUShort(n) <> 0US\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Boolean\nReturn (n > 3) >= (n > 9) And Not (n = 2)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Boolean\nReturn CDbl(n) < 7.5 And CSng(n) >= 7 And CLng(n) = 7L And CULng(n) > 3UL\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Boolean\nReturn \"abc\" <> CStr(n) AndAlso \"b\" > \"a\" AndAlso \"a\" <= CStr(n)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim t As String = \"Hello\"\nReturn t.Substring(1, 2) & t.IndexOf(\"l\"c, 3).ToString() & t.EndsWith(\"lo\", StringComparison.Ordinal).ToString() & t.IndexOf(\"lo\", 1, StringComparison.Ordinal).ToString() & t.ToString()\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Double\nReturn Math.Round(2.5, MidpointRounding.ToEven) + Math.Round(CSng(n) / 2) + Math.Atan2(1, n) + Math.Log(n) + Math.Sign(-2.5) + Math.Abs(CShort(-n)) + Math.Max(1L, CLng(n)) + Math.Min(2.5, n)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Double\nReturn Math.Exp(1) + Math.Asin(0.5) + Math.Acos(0.5) + Math.Atan(n) + Math.Tan(1) + Math.Log10(n) + Math.Ceiling(1.5) + Math.Truncate(-2.5)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nReturn CInt(Fix(n)) + CInt(Int(-n)) + AscW(CStr(n)) + AscW(ChrW(n + 64)) + Len(CStr(n))\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim c As Char = \"x\"c\nReturn c & CStr(n) & c.ToString() & n.ToString() & (n > 3).ToString() & CStr(CByte(n))\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim a() As Integer = {1, 2, n}\nDim total As Integer = 0\nFor Each x As Integer In a\ntotal += x\nNext\nDim e() As Integer = New Integer(2) {}\nReturn total + e.Length\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim total As Integer = 0\nDim c As Char\nFor Each c In CStr(n * 11)\ntotal += AscW(c)\nNext\nReturn total\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nSelect Case n\nCase 1, 2\nReturn 1\nCase 7\nIf n > 3 Then Exit Select\nReturn 2\nCase Else\nReturn 3\nEnd Select\nReturn 4\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nFor i As Integer = 1 To n\nSelect Case i\nCase 2\nContinue For\nCase 5\nExit For\nEnd Select\nr += i\nNext\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nWhile r < n\nr += 2\nIf r = 4 Then Continue While\nEnd While\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nReturn 10 \\ (n - 7)\nCatch ex As DivideByZeroException\nReturn -1\nFinally\nn = 0\nEnd Try\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nDo While r < n\nTry\nr += 1\nIf r = 3 Then Exit Do\nCatch ex As OverflowException\nEnd Try\nLoop\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nFor i As Integer = n To 1 Step -2\nr += i\nNext\nFor j As UInteger = 1UI To CUInt(n) Step 3UI\nr += CInt(j)\nNext\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nDim s As Integer = -1\nIf n > 3 Then s = 2\nFor i As Integer = 0 To 10 Step s\nr += i\nNext\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim e As New ArgumentException(\"bad\")\nDim o As New OverflowException()\nReturn e.Message & CStr(o.Message.Length > 0) & CStr(e.InnerException Is Nothing)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim a(n) As Short\nReDim Preserve a(n + 2)\na(n + 2) += 3S\nReturn a.Length + a(n + 2)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Double\nDim b As Boolean = n > 3\nIf b = False Then Return -1\nReturn CDbl(b) + CSng(Not b)\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nSelect Case n\nCase 1\nIf n > 0 Then Exit Select\nReturn 1\nCase 7\nTry\nIf n > 1 Then Exit Select\nCatch ex As OverflowException\nEnd Try\nReturn 2\nCase Else\nReturn 3\nEnd Select\nReturn 4\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nSelect Case n\nCase 7\nReturn 1\nn = 2\nCase Else\nReturn 3\nEnd Select\nReturn 4\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nSelect Case CStr(n)\nCase \"7\"\nIf n > 3 Then Exit Select\nr = 5\nCase Else\nr = 6\nEnd Select\nReturn r\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nTry\nr = 10 \\ (n - 7)\nCatch ex As DivideByZeroException\nr = -1\nFinally\nr += 100\nEnd Try\nReturn r\nEnd Function", 7)]
  public void Construct_ComputesWhatTheCompilerComputes(string members, int argument) => Agrees(Module(members), argument, true);

  /// <summary>Under <c>Option Strict Off</c> a compound assignment narrows back implicitly, checked as the compiler checks it.</summary>
  [TestCase("Public Function F(ByVal n As Integer) As Byte\nDim b As Byte = 1\nb += n\nReturn b\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Byte\nDim b As Byte = 1\nb += n * 100\nReturn b\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Short\nDim s As Short = 1\ns *= CLng(n)\ns \\= 2.5\nReturn s\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = n\ni /= 2\nReturn i\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As UShort\nDim u As UShort = 3\nu += CSByte(n)\nReturn u\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = n\ni = i And 2.5\ni = i Or 1.5F\nReturn i\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Short\nDim s As Short = 1\ns += CUInt(n)\nReturn s\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As SByte\nDim s As SByte = 1\ns += CByte(n)\nReturn s\nEnd Function", 7)]
  public void StrictOff_ComputesWhatTheCompilerComputes(string members, int argument) => Agrees(Module(members, "Off"), argument, false);

  [TestCase("Public Function F() As String\nReturn String.Concat(\"a\", 1)\nEnd Function", "String.Concat")]
  [TestCase("Public Function F() As Boolean\nReturn \"a\".Equals(\"b\")\nEnd Function", "String.Equals")]
  [TestCase("Public Function F() As String\nReturn String.Join(\",\", New System.Collections.Generic.List(Of String)())\nEnd Function", "List")]
  [TestCase("Public Function F() As Integer\nReturn \"ab\".IndexOf(\"b\")\nEnd Function", "String.IndexOf")]
  [TestCase("Public Function F() As Boolean\nReturn \"ab\".StartsWith(\"a\")\nEnd Function", "String.StartsWith")]
  [TestCase("Public Function F() As Boolean\nReturn \"ab\".Contains(\"a\", StringComparison.Ordinal)\nEnd Function", "String.Contains")]
  [TestCase("Public Function F() As String\nReturn \"ab\".Replace(\"a\", \"b\", StringComparison.Ordinal)\nEnd Function", "String.Replace")]
  [TestCase("Public Function F() As String\nReturn \"ab\".ToUpper()\nEnd Function", "String.ToUpper")]
  [TestCase("Public Function F() As Double\nReturn Math.Round(2.5, MidpointRounding.ToZero)\nEnd Function", "Math.Round")]
  [TestCase("Public Function F() As Double\nReturn Math.Round(2.5, 1)\nEnd Function", "Math.Round")]
  [TestCase("Public Sub F()\nSystem.Console.WriteLine(\"{0}\", 1)\nEnd Sub", "Console.WriteLine")]
  [TestCase("Public Sub F()\nSystem.Console.Write(1)\nEnd Sub", "Console.Write")]
  [TestCase("Public Structure P\nPublic A As Integer\nPublic Sub New(ByVal a As Integer)\nMe.A = a\nEnd Sub\nEnd Structure\nPublic Function F() As Integer\nReturn New P(1).A\nEnd Function", "constructor of its own")]
  [TestCase("Public Function F() As Integer\nDim a(,) As Integer = {{1}}\nReturn 0\nEnd Function", "several dimensions")]
  [TestCase("Public Function F() As Integer\nDim e As New ArgumentException(\"a\", \"b\", New Exception())\nReturn 0\nEnd Function", "the creation")]
  [TestCase("Public Function F() As Integer\nDim e As New ArgumentException(\"a\", \"b\")\nReturn 0\nEnd Function", "the creation")]
  [TestCase("Public Function F(ByVal x As Object) As Boolean\nReturn x Is Nothing\nEnd Function", "Object")]
  [TestCase("Public Function F(ByVal x As Exception, ByVal y As Exception) As Boolean\nReturn x Is y\nEnd Function", "comparison of references")]
  [TestCase("Public Function F(ByVal x As Exception) As Boolean\nReturn x IsNot Nothing\nEnd Function", null)]
  [TestCase("Public Function F(ByVal x As Char) As Integer\nReturn CInt(AscW(x) > 64)\nEnd Function", null)]
  [TestCase("Public Function F(ByVal x As Char) As Boolean\nReturn x > \"a\"c\nEnd Function", null)]
  [TestCase("Public Function F(ByVal x As String) As Char\nReturn CChar(x)\nEnd Function", "conversion from String to Char")]
  [TestCase("Public Function F(ByVal x As String) As Boolean\nReturn x Like CStr(1)\nEnd Function", "operator")]
  [TestCase("Public Function F(ByVal x As String) As Boolean\nReturn x Like \"a\" OrElse x Like \"b\"\nEnd Function", "operator")]
  [TestCase("Public Function F(ByVal x As Exception) As String\nReturn x.StackTrace\nEnd Function", "the expression")]
  [TestCase("Public Function F() As Integer\nDim q As Integer = 0\nSelect Case q\nCase Is <> 1, 3 To 4\nReturn 1\nEnd Select\nReturn 0\nEnd Function", null)]
  [TestCase("Public Function F(ByVal e As ArgumentException) As Integer\nDim o As Exception = e\nReturn 0\nEnd Function", null)]
  [TestCase("Public Sub F()\nG()\nEnd Sub\nPublic Sub G()\nEnd Sub", null)]
  [TestCase("Public Function F() As Integer\nDim r As New Random()\nReturn r.Next()\nEnd Function", "Random")]
  [TestCase("Public Sub F()\nDim r As New Random()\nr.NextBytes(Nothing)\nEnd Sub", "Random")]
  [TestCase("Public Function F(ByVal x As Exception) As Integer\nDim e As ArgumentException = DirectCast(x, ArgumentException)\nReturn 0\nEnd Function", "conversion from")]
  [TestCase("Public Function F() As Integer\nDim r As Integer\nTry\nr = 1\nCatch ex As Exception When r > 0\nEnd Try\nReturn r\nEnd Function", null)]
  [TestCase("Public Function F() As Integer\nDim ex As Exception = Nothing\nTry\nReturn 1\nCatch ex\nReturn 2\nEnd Try\nEnd Function", "declared elsewhere")]
  [TestCase("Public Function F() As Integer\nReturn Environment.TickCount\nEnd Function", "the expression")]
  [TestCase("Public Sub F()\nEnvironment.ExitCode = 1\nEnd Sub", "an assignment to")]
  [TestCase("Public Sub F(ByRef x As Integer)\nG(x)\nEnd Sub\nPublic Sub G(ByRef y As Integer)\nEnd Sub", "the ByRef parameter 'x'")]
  [TestCase("Public Structure P\nPublic Property X As Integer\nEnd Structure\nPublic Function F() As Integer\nDim p As New P With {.X = 1}\nReturn 0\nEnd Function", "the initializer")]
  [TestCase("Public Function F() As Integer\nDim g As Guid = Guid.NewGuid()\nReturn 0\nEnd Function", "Guid")]
  [TestCase("Public Sub F()\nStop\nEnd Sub", "the statement 'Stop'")]
  [TestCase("Public Function F() As Integer\nDim xs As New System.Collections.Generic.List(Of Integer)()\nFor Each x As Integer In xs\nGoTo e\nNext\ne:\nReturn 0\nEnd Function", "List")]
  public void Construct_OutsideTheSubset_IsPly2611(string members, string? construct) {
    var imported = VbNetFrontend.Import("M.vb", Module(members));
    if (construct is null) {
      Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
      return;
    }

    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(VbNetFrontend.UnsupportedCode), string.Join("\n", imported.Diagnostics));
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(construct));
  }

}
