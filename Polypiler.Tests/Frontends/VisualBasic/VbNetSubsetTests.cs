using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The edges of the Visual Basic .NET subset: each construct the frontend lowers is compared with the
/// compiler on a call of its own, and each one outside the subset is refused with <c>PLY2611</c>, a
/// source the compiler rejects with <c>PLY2610</c>.
/// </summary>
[TestFixture]
public sealed class VbNetSubsetTests {

  private static string Module(string members) => $"Option Strict On\nPublic Module M\n{members}\nEnd Module\n";

  /// <summary>A procedure <c>F</c> imported and compiled: the interpreter does what the compiled procedure does for the arguments.</summary>
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = 0\nDo Until i >= n\ni += 2\nLoop\nReturn i\nEnd Function", 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = 0\nDo\ni += 1\nLoop While i < n\nReturn i\nEnd Function", 0)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = 0\nDo\ni += 1\nIf i > n Then Exit Do\nLoop\nReturn i\nEnd Function", 4)]
  [TestCase("Public Function F(ByVal n As Integer) As Boolean\nReturn (n > 0) > (n > 5)\nEnd Function", 3)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nSelect Case CStr(n)\nCase \"1\" To \"3\"\nReturn 1\nCase Is > \"7\"\nReturn 2\nCase Else\nReturn 3\nEnd Select\nEnd Function", 8)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim c As Char = ChrW(65 + n)\nReturn c & CStr(AscW(c)) & CStr(AscW(\"z\"))\nEnd Function", 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Double\nReturn Math.Sqrt(n) + Math.Floor(-1.5) + Math.Max(n, 3) + Math.Round(2.5) + Math.Round(2.5, MidpointRounding.AwayFromZero) + Math.Pow(2, n) + Math.Sign(-n) + Math.Abs(-n)\nEnd Function", 4)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nDim s As String = \"Hello World\"\nReturn s.Substring(n) & s.IndexOf(\"o\"c).ToString() & s.IndexOf(\"World\", StringComparison.Ordinal).ToString() & s.Contains(\"lo\").ToString() & s.Replace(\"l\", \"L\") & s.StartsWith(\"He\", StringComparison.Ordinal).ToString() & s.Chars(1)\nEnd Function", 6)]
  [TestCase("Public Function F(ByVal n As Integer) As String\nReturn String.Join(\"-\", New String() {\"a\", CStr(n)}) & String.Concat(\"x\", \"y\") & String.Equals(\"a\", \"a\").ToString() & (1.5).ToString() & True.ToString()\nEnd Function", 9)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim a() As Integer = Nothing\nIf a Is Nothing Then n += 1\nReDim a(n)\nIf a IsNot Nothing Then n += a.Length\nReturn n\nEnd Function", 2)]
  [TestCase("Private table(3) As Integer\nPublic Function F(ByVal n As Integer) As Integer\ntable(n) = 5\nReturn table.Length + table(n)\nEnd Function", 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim s(2) As String\ns(n) = \"x\"\nReturn (s(0) & s(1) & s(2)).Length\nEnd Function", 1)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nThrow New InvalidOperationException(\"x\", New OverflowException())\nCatch ex As InvalidOperationException When ex.InnerException IsNot Nothing\nReturn n\nEnd Try\nEnd Function", 3)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim b As Byte = CByte(n)\nb = Not b\nDim s As Short = CShort(-n)\ns = s >> 1\nReturn b + s + CInt(b And CByte(15)) + CInt(s Or 1S)\nEnd Function", 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Single\nDim x As Single = n\nx /= 3\nReturn x + CSng(Fix(1.5)) + CSng(Int(-1.5))\nEnd Function", 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim p As Boolean = n > 2 Xor n < 10\nDim g As Boolean = (n > 2) Or Side()\nReturn CInt(p) + CInt(g) + counter\nEnd Function\nPrivate counter As Integer = 0\nPrivate Function Side() As Boolean\ncounter += 1\nReturn True\nEnd Function", 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nFor i As Integer = 0 To n\nIf i = 3 Then Return i * 10\nNext\nReturn -1\nEnd Function", 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Long\nDim total As Long = 0\nFor i As Long = n To 0 Step -1\ntotal += i\nNext\nDim x As Double = 0\nFor d As Double = 0 To 1 Step 0.25\nx += d\nNext\nReturn total + CLng(x)\nEnd Function", 4)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim k As Integer\nFor k = 1 To n\nNext\nReturn k\nEnd Function", 4)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim u As UShort = CUShort(n)\nu -= 1US\nDim ub As UInteger = CUInt(n)\nReturn CInt(u) + CInt(ub \\ 2UI) + Math.Min(n, 2)\nEnd Function", 1)]
  [TestCase("Public Structure P\nPublic A As Integer\nPublic B As Integer\nEnd Structure\nPublic Function F(ByVal n As Integer) As Integer\nDim p As New P With {.B = 2, .A = n}\nDim ps(1) As P\nps(0).A += n\nReturn p.A * 10 + p.B + ps(0).A\nEnd Function", 3)]
  public void Construct_ComputesWhatTheCompilerComputes(string members, int argument) {
    var source = Module(members);
    var imported = VbNetFrontend.Import("M.vb", source);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    var call = new PrototypeCall("F", [argument]);
    var expected = PrototypeFixture.InvokedOutcome(VbNetFixtures.Procedure(VbNetFixtures.Load(source, removeIntegerChecks: false), "F"), call);
    Assert.That(PrototypeFixture.InterpretedOutcome(imported.Module!, call), Is.EqualTo(expected));
  }

  [TestCase("Public Function F() As Decimal\nReturn 1D\nEnd Function", "Decimal")]
  [TestCase("Public Function F() As Integer\nStatic s As Integer\ns += 1\nReturn s\nEnd Function", "Static")]
  [TestCase("Public Sub F(ByRef x As Integer)\nx = 1\nEnd Sub", "ByRef")]
  [TestCase("Public Sub F(ParamArray xs As Integer())\nEnd Sub", "ParamArray")]
  [TestCase("Public Function F() As Integer\nDim a(1, 1) As Integer\nReturn 0\nEnd Function", "several dimensions")]
  [TestCase("Public Function F() As Integer\nTry\nExit Try\nCatch\nEnd Try\nReturn 0\nEnd Function", "Exit Try")]
  [TestCase("Public Function F() As Integer\nTry\nReturn 1\nCatch ex As System.IO.IOException\nReturn 2\nEnd Try\nEnd Function", "Catch of")]
  [TestCase("Public Function F() As String\nReturn Left(\"abc\", 1)\nEnd Function", "the call of")]
  [TestCase("Public Function F() As Integer\nReturn CInt(\"12\")\nEnd Function", "conversion from")]
  [TestCase("Public Function F(ByVal s As String) As Boolean\nReturn s Is Nothing\nEnd Function", "comparison of references")]
  [TestCase("Public Property P As Integer", "the member 'P'")]
  [TestCase("Public Class C\nEnd Class", "the class 'C'")]
  [TestCase("Public Structure S\nPublic A As Integer\nPublic Sub New(ByVal a As Integer)\nMe.A = a\nEnd Sub\nEnd Structure", "of the structure 'S'")]
  [TestCase("Public Function F() As Integer\nDim d As New System.Text.StringBuilder()\nReturn 0\nEnd Function", "StringBuilder")]
  [TestCase("Public Function F() As Integer()()\nReturn New Integer()() {New Integer() {1}}\nEnd Function", "array of arrays")]
  [TestCase("Public Function F(ByVal n As Integer) As String()\nReturn New String(n) {}\nEnd Function", "array of strings")]
  [TestCase("Private a As Integer = b\nPrivate b As Integer = 1", "initialized before")]
  [TestCase("Public Function F(ByVal x As ULong, ByVal y As Long) As Integer\nDim z As ULong = x\nz += CULng(y)\nReturn 0\nEnd Function", null)]
  public void Construct_OutsideTheSubset_IsPly2611(string members, string? construct) {
    var imported = VbNetFrontend.Import("M.vb", Module(members));
    if (construct is null) {
      Assert.That(imported.Diagnostics, Is.Empty);
      return;
    }

    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(VbNetFrontend.UnsupportedCode));
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(construct));
  }

  [Test]
  public void StringComparison_UnderOptionCompareText_IsPly2611() {
    var imported = VbNetFrontend.Import("M.vb", "Option Compare Text\n" + Module("Public Function F(ByVal s As String) As Boolean\nReturn s = \"A\"\nEnd Function"));
    Assert.That(imported.Diagnostics.Single().Message, Does.Contain("Option Compare Text"));
    var option = VbNetFrontend.Import("M.vb", Module("Public Function F(ByVal s As String) As Boolean\nReturn s < \"A\"\nEnd Function"), new VbNetImportOptions { OptionCompareText = true });
    Assert.That(option.Diagnostics.Single().Code, Is.EqualTo(VbNetFrontend.UnsupportedCode));
  }

  [TestCase("Public Function F() As Integer\nReturn = 1\nEnd Function")]
  [TestCase("Public Function F() As Integer\nDim x = y\nReturn 1\nEnd Function")]
  public void SourceTheCompilerRejects_IsPly2610(string members) {
    var imported = VbNetFrontend.Import("M.vb", Module(members));
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(VbNetFrontend.CompilerErrorCode));
    Assert.That(imported.Diagnostics[0].Message, Does.StartWith("BC"));
  }

  [TestCase("")]
  [TestCase("Public Module A\nEnd Module\nPublic Module B\nEnd Module\n")]
  public void FileWithoutOneModule_IsPly2611(string source)
    => Assert.That(VbNetFrontend.Import("M.vb", source).Diagnostics.Single().Code, Is.EqualTo(VbNetFrontend.UnsupportedCode));

  [Test]
  public void ConsoleWriteLine_IsALineOfText() {
    var module = VbNetFrontend.Import("M.vb", Module("Public Sub F(ByVal n As Integer)\nSystem.Console.WriteLine(n)\nSystem.Console.WriteLine()\nSystem.Console.WriteLine(\"x\")\nSystem.Console.WriteLine(True)\nEnd Sub")).Module!;
    var lines = module.Functions.Single().Body.Statements.OfType<Core.IR.IrWriteLine>().ToList();
    Assert.That(lines.Select(line => line.Value.GetType().Name), Is.EqualTo(new[] { "IrIntrinsic", "IrStrConst", "IrStrConst", "IrIntrinsic" }));
  }

  [Test]
  public void OptionStrictOff_ConvertsImplicitlyAsTheCompilerDoes() {
    var source = "Option Strict Off\nPublic Module M\nPublic Function F(ByVal x As Double) As Integer\nDim n As Integer = x\nReturn n\nEnd Function\nEnd Module\n";
    var imported = VbNetFrontend.Import("M.vb", source, new VbNetImportOptions { OptionStrict = false, OptionExplicit = false, OptionInfer = false });
    Assert.That(imported.Diagnostics, Is.Empty);
    Assert.That(PrototypeFixture.InterpretedOutcome(imported.Module!, new("F", [2.5])), Is.EqualTo("2"));
  }

  [TestCase("9", true)]
  [TestCase("16.9", true)]
  [TestCase("8", false)]
  [TestCase("17", false)]
  [TestCase("x", false)]
  [TestCase(null, false)]
  public void LanguageVersion_IsNineToSixteenNine(string? version, bool valid) {
    Assert.That(VbNetFrontend.IsLanguageVersion(version), Is.EqualTo(valid));
    if (!valid && version is not null)
      Assert.That(() => VbNetFrontend.Import("M.vb", Module(""), new VbNetImportOptions { LanguageVersion = version }), Throws.ArgumentException);
  }

  [Test]
  public void LanguageVersion_RulesOutLaterSyntax() {
    var imported = VbNetFrontend.Import("M.vb", Module("Public Function F() As Integer\nReturn &B101\nEnd Function"), new VbNetImportOptions { LanguageVersion = "14" });
    Assert.That(imported.Diagnostics.Single().Code, Is.EqualTo(VbNetFrontend.CompilerErrorCode));
    Assert.That(() => VbNetFrontend.Import(null!, ""), Throws.ArgumentNullException);
    Assert.That(() => VbNetFrontend.Import("M.vb", null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Caveats_NameTheInvariantCultureAndTheEmptyString() {
    var module = VbNetFrontend.Import("M.vb", Module("Public Function F(ByVal x As Double) As String\nReturn CStr(x)\nEnd Function")).Module!;
    Assert.That(module.Caveats, Is.EqualTo(new[] { VbNetFrontend.InvariantCulture, VbNetFrontend.NothingString }));
  }

}
