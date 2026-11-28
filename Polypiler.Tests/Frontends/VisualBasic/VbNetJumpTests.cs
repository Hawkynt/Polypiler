using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic.Net;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Backends.VisualBasic;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// <c>GoTo</c> and labels, the exits of a loop around the innermost one, <c>On Error</c> in its four
/// forms, <c>Resume</c> in its three and <c>Err</c>, lowered as a state machine: each procedure
/// compared with the compiler for arguments that take every path, and each refusal by name.
/// </summary>
[TestFixture]
public sealed class VbNetJumpTests {

  private static string Module(string members) => $"Option Strict On\nPublic Module M\nPrivate zero As Integer = 0\n{members}\nEnd Module\n";

  private static IrModule Imported(string source) {
    var imported = VbNetFrontend.Import("M.vb", source);
    Assert.That(imported.Diagnostics, Is.Empty, string.Join("\n", imported.Diagnostics));
    return imported.Module!;
  }

  /// <summary>A procedure <c>F</c> and its helpers: the interpreter does what the compiled procedure does, for each argument.</summary>
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim i As Integer = 0\nDim r As Integer = 0\nAgain:\nIf i >= n Then GoTo Done\nr += i\ni += 1\nGoTo Again\nDone:\nReturn r\nEnd Function", 0, 1, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nIf n > 2 Then GoTo Big\nReturn 1\nBig:\nIf n > 4 Then\nGoTo Huge\nElse\nReturn 2\nEnd If\nHuge:\nReturn 3\nEnd Function", 1, 3, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nFor i As Integer = 1 To n\nDim j As Integer = 0\nDo\nj += 1\nIf i * j > 6 Then Exit For\nIf j > i Then Continue For\nr += j\nLoop\nNext\nReturn r\nEnd Function", 0, 2, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nWhile r < 100\nFor Each c As Char In \"abc\"\nr += 1\nIf r = n Then Exit While\nNext\nr += 10\nEnd While\nReturn r\nEnd Function", 2, 13, 50)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nDo Until r > n\nSelect Case r\nCase 1\nr += 2\nIf n = 3 Then Exit Do\nCase Else\nr += 1\nEnd Select\nLoop\nReturn r\nEnd Function", 0, 3, 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nDo\nr += 1\nIf r = 2 Then Continue Do\nIf r > n Then Exit Do\nLoop While r < 10\nSelect Case CStr(r)\nCase \"1\", \"2\"\nGoTo Small\nCase Is > \"5\"\nr = -r\nEnd Select\nReturn r\nSmall:\nReturn 100\nEnd Function", 0, 1, 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer\nSelect Case n\nCase 1\nGoTo One\nCase 2 To 3\nr = 2\nCase Else\nr = 3\nEnd Select\nReturn r\nOne:\nReturn 1\nEnd Function", 1, 2, 9)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nDim r As Integer = 10 \\ n\nF = r + Err.Number\nEnd Function", 0, 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nOn Error GoTo Handler\nr = 100 \\ n\nr += 1\nReturn r\nHandler:\nr = Err.Number * 1000\nResume Next\nEnd Function", 0, 4)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim d As Integer = n\nOn Error GoTo Handler\nReturn 100 \\ d\nHandler:\nd = 7\nResume\nEnd Function", 0, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error GoTo Handler\nDim a(2) As Integer\na(n) = 1\nReturn n\nHandler:\nResume Fallback\nFallback:\nReturn -Err.Number - 1\nEnd Function", 1, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nReturn G(n)\nCatch ex As OverflowException\nReturn -6\nCatch ex As DivideByZeroException\nReturn -11\nEnd Try\nEnd Function\nPrivate Function G(ByVal n As Integer) As Integer\nOn Error GoTo Handler\nReturn 10 \\ n\nHandler:\nReturn Integer.MaxValue + n\nEnd Function", 0, 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nG(n)\nReturn Err.Number\nEnd Function\nPrivate Sub G(ByVal n As Integer)\nzero = 10 \\ n\nEnd Sub", 0, 3)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nG(n)\nReturn Err.Number\nEnd Function\nPrivate Sub G(ByVal n As Integer)\nOn Error Resume Next\nzero = 10 \\ n\nEnd Sub", 0, 3)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nzero = 10 \\ n\nCatch ex As DivideByZeroException\nzero = Err.Number\nEnd Try\nReturn zero * 100 + Err.Number\nEnd Function", 0, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nReturn G(n)\nCatch ex As DivideByZeroException\nReturn -11\nEnd Try\nEnd Function\nPrivate Function G(ByVal n As Integer) As Integer\nOn Error Resume Next\nzero = 1\nOn Error GoTo 0\nReturn 10 \\ n\nEnd Function", 0, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim count As Integer = 0\nOn Error GoTo Handler\nzero = 10 \\ (n - 1)\nReturn count\nHandler:\ncount += 1\nOn Error GoTo -1\nIf count < 3 Then zero = 10 \\ (n - 1)\nReturn count * 100 + Err.Number\nEnd Function", 1, 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nOn Error Resume Next\nErr.Raise(6)\nr = Err.Number\nErr.Clear()\nIf n > 1 Then Err.Raise(13)\nr = r * 100 + Err.Number\nErr.Number = 42\nReturn r * 1000 + Err.Number\nEnd Function", 0, 2)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nSelect Case n\nCase 5\nErr.Raise(5)\nCase 6\nErr.Raise(6)\nCase 9\nErr.Raise(9)\nCase 11\nErr.Raise(11)\nCase 13\nErr.Raise(13)\nCase Else\nErr.Raise(91)\nEnd Select\nCatch ex As ArgumentException\nReturn 5\nCatch ex As OverflowException\nReturn 6\nCatch ex As IndexOutOfRangeException\nReturn 9\nCatch ex As DivideByZeroException\nReturn 11\nCatch ex As InvalidCastException\nReturn 13\nCatch ex As NullReferenceException\nReturn 91\nEnd Try\nReturn 0\nEnd Function", 5, 6, 9, 11, 13, 91)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nOn Error Resume Next\nIf 10 \\ n > 2 Then\nr = 1\nElse\nr = 2\nEnd If\nReturn r * 100 + Err.Number\nEnd Function", 0, 1, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nOn Error Resume Next\nDo While 10 \\ n > r\nr += 1\nIf r > 20 Then Exit Do\nLoop\nReturn r * 100 + Err.Number\nEnd Function", 0, 1, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nOn Error Resume Next\nFor i As Integer = -1 To 1\nr += 10 \\ (i + n)\nNext\nSelect Case 10 \\ n\nCase 0\nr += 1000\nCase Else\nr += 2000\nEnd Select\nReturn r * 100 + Err.Number\nEnd Function", 0, 1, 5)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nReturn G(n)\nCatch ex As Exception\nReturn -1\nEnd Try\nEnd Function\nPrivate Function G(ByVal n As Integer) As Integer\nOn Error GoTo Handler\nIf n > 0 Then Resume Next\nReturn 1\nHandler:\nReturn 2\nEnd Function", 0, 1)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error GoTo Handler\nzero = 10 \\ n\nExit Function\nHandler:\nF = Err.Number\nEnd Function", 0, 1)]
  [TestCase("Public Sub F(ByVal n As Integer)\nOn Error GoTo Handler\nzero = 10 \\ n\nExit Sub\nHandler:\nzero = Err.Number\nEnd Sub", 0, 1)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nErr.Raise(1234)\nDim r As Integer = Err.Number\nErr.Raise(n)\nReturn r * 10000 + Err.Number\nEnd Function", 5, 6, 9, 11, 13, 91, 1234, 0)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nIf n > 0 Then Err.Raise(n + 1000)\nReturn 0\nCatch ex As Exception\nReturn -1\nEnd Try\nEnd Function", 0, 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nH()\nIf n = 0 Then Throw New InvalidOperationException()\nReturn Err.Number\nEnd Function\nPrivate Sub H()\nTry\nErr.Raise(1234)\nCatch ex As Exception\nEnd Try\nEnd Sub", 0, 1)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nG(n)\nReturn Err.Number\nEnd Function\nPrivate Sub G(ByVal n As Integer)\nIf n > 0 Then Resume Next\nEnd Sub", 0, 1)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nErr.Clear()\nTry\nIf n = 1 Then zero = Integer.MaxValue + n\nIf n = 2 Then Throw New ArithmeticException()\nzero = 1 \\ (n - n)\nCatch When n > 5\nReturn -1\nCatch ex As ArithmeticException\nReturn Err.Number\nEnd Try\nReturn -2\nEnd Function", 0, 1, 2, 7)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nDo Until r >= n\nr += 3\nIf Not (r < 5) Then GoTo Out\nLoop\nOut:\nReturn r\nEnd Function", 0, 1, 9)]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nDim r As Integer = 0\nFor Each c As Char In CStr(n * 37)\nIf c = \"5\"c Then GoTo Found\nr += 1\nNext\nReturn -r\nFound:\nReturn r\nEnd Function", 1, 4)]
  public void Procedure_ComputesWhatTheCompilerComputes(string members, params int[] arguments) {
    var source = Module(members);
    var module = Imported(source);
    var compiled = VbNetFixtures.Procedure(VbNetFixtures.Load(source, removeIntegerChecks: false), "F");
    foreach (var argument in arguments) {
      var call = new PrototypeCall("F", [argument]);
      Assert.That(PrototypeFixture.InterpretedOutcome(module, call), Is.EqualTo(PrototypeFixture.InvokedOutcome(compiled, call)), $"F({argument})");
    }
  }

  [Test]
  public void ProcedureWithoutJumps_StaysStructured() {
    var module = Imported(Module("Public Function F(ByVal n As Integer) As Integer\nFor i As Integer = 0 To n\nIf i = 3 Then Exit For\nNext\nReturn n\nEnd Function"));
    Assert.That(module.Functions.Single().Body.Statements.OfType<IrWhile>(), Is.Empty);
  }

  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nIf n = 1 Then GoTo x\nCatch ex As OverflowException\nEnd Try\nx:\nReturn 0\nEnd Function", "out of a Try")]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nFor i As Integer = 0 To 3\nTry\nIf i = n Then Continue For\nCatch ex As OverflowException\nEnd Try\nNext\nIf n = 1 Then GoTo x\nx:\nReturn 0\nEnd Function", "out of a Try")]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nx:\nn += 1\nCatch ex As OverflowException\nEnd Try\nReturn n\nEnd Function", "inside a Try")]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nOn Error Resume Next\nErr.Raise(1234, \"source\")\nReturn 0\nEnd Function", "Err.Raise(1234, \"source\")")]
  [TestCase("Public Function F(ByVal n As Integer) As String\nReturn Err.Description\nEnd Function", "Err")]
  [TestCase("Public Function F(ByVal n As Integer) As Integer\nTry\nReturn 1\nCatch ex As System.IO.IOException\nReturn Err.Number\nEnd Try\nEnd Function", "IOException")]
  public void Construct_OutsideTheSubset_IsPly2611(string members, string construct) {
    var imported = VbNetFrontend.Import("M.vb", Module(members));
    Assert.That(imported.Module, Is.Null);
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(VbNetFrontend.UnsupportedCode), string.Join("\n", imported.Diagnostics));
    Assert.That(imported.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.Contains(construct));
  }

}
