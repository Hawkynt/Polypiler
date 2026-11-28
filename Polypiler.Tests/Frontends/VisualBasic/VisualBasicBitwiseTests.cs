using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The bitwise operators of <c>Variant</c>s ([MS-VBAL] §5.6.9.8): the effective type the kinds give
/// and the rules of <c>Null</c>; and arrays of <c>Variant</c>s, <c>Array(…)</c> and <c>IsArray</c> (#1567).
/// </summary>
[TestFixture]
public sealed class VisualBasicBitwiseTests {

  internal static IrModule Import(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  internal static string Refused(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  internal static string Run(IrModule module, string function, params object[] arguments) {
    try {
      return VisualBasicVariantTests.Text(IrInterpreter.Call(module, function, arguments));
    } catch (IrException exception) {
      return "!" + VisualBasicVariantTests.Number(exception.Class);
    }
  }

  internal static string Function(string body, string head = "")
    => $"Option Explicit\n{head}Public Function F(ByVal n As Integer) As String\n{body}\nEnd Function\n";

  private static string Result(string body, string head = "", short n = 0) => Run(Import(Function(body, head)), "F", n);

  /// <summary>Two locals that may be <c>Null</c>, so that they stay dynamic, and an expression of them.</summary>
  private static string Pair(string a, string b, string expression)
    => $"Dim v, w\nv = {a}\nw = {b}\nIf n = 9 Then v = Null\nIf n = 9 Then w = Null\nF = TypeName({expression}) & \" \" & ({expression})";

  [TestCase("CInt(5)", "CInt(3)", "v And w", "Integer 1")]
  [TestCase("CByte(5)", "CByte(3)", "v Or w", "Byte 7")]
  [TestCase("5.5", "3", "v Xor w", "Long 5")]
  [TestCase("CInt(5)", "CInt(3)", "v Eqv w", "Integer -7")]
  [TestCase("CInt(5)", "CInt(3)", "v Imp w", "Integer -5")]
  [TestCase("True", "False", "v Eqv w", "Boolean False")]
  [TestCase("True", "False", "v Imp w", "Boolean False")]
  [TestCase("True", "CInt(5)", "v And w", "Integer 5")]
  [TestCase("CByte(1)", "True", "v Or w", "Integer -1")]
  [TestCase("CLng(6)", "CInt(3)", "v And w", "Long 2")]
  [TestCase("Empty", "CInt(5)", "v Or w", "Integer 5")]
  [TestCase("Empty", "Empty", "v Or w", "Integer 0")]
  [TestCase("Empty", "CByte(5)", "v Or w", "Integer 5")]
  [TestCase("1.5!", "CByte(4)", "v Or w", "Long 6")]
  public void Bitwise_OfIntegerVariants_IsInTheEffectiveType(string a, string b, string expression, string expected)
    => Assert.That(Result(Pair(a, b, expression)), Is.EqualTo(expected));

  [TestCase("CInt(0)", "Null", "v And w", "Integer 0")]
  [TestCase("CInt(5)", "Null", "v And w", "Null ")]
  [TestCase("Null", "CByte(0)", "v And w", "Byte 0")]
  [TestCase("CInt(5)", "Null", "v Or w", "Integer 5")]
  [TestCase("CInt(0)", "Null", "v Or w", "Null ")]
  [TestCase("Null", "CLng(7)", "v Or w", "Long 7")]
  [TestCase("CInt(5)", "Null", "v Xor w", "Null ")]
  [TestCase("CInt(5)", "Null", "v Eqv w", "Null ")]
  [TestCase("CInt(-1)", "Null", "v Imp w", "Null ")]
  [TestCase("CInt(0)", "Null", "v Imp w", "Integer -1")]
  [TestCase("CInt(5)", "Null", "v Imp w", "Integer -6")]
  [TestCase("Null", "CInt(5)", "v Imp w", "Integer 5")]
  [TestCase("Null", "CInt(0)", "v Imp w", "Null ")]
  [TestCase("Null", "Null", "v And w", "Null ")]
  [TestCase("False", "Null", "v Imp w", "Boolean True")]
  [TestCase("Null", "True", "v Imp w", "Boolean True")]
  [TestCase("True", "Null", "v Eqv w", "Null ")]
  public void Bitwise_WithNull_FollowsTheRulesOfEachOperator(string a, string b, string expression, string expected)
    => Assert.That(Result(Pair(a, b, expression)), Is.EqualTo(expected));

  [TestCase("CByte(5)", "Byte 250")]
  [TestCase("CInt(0)", "Integer -1")]
  [TestCase("CInt(-1)", "Integer 0")]
  [TestCase("CByte(255)", "Byte 0")]
  [TestCase("1.5", "Long -3")]
  [TestCase("CLng(5)", "Long -6")]
  [TestCase("Empty", "Integer -1")]
  [TestCase("Null", "Null ")]
  [TestCase("True", "Boolean False")]
  public void Not_OfAVariant_IsInTheTypeOfItsKind(string a, string expected)
    => Assert.That(Result($"Dim v\nv = {a}\nIf n = 9 Then v = Null\nF = TypeName(Not v) & \" \" & (Not v)"), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CInt(1)\nF = TypeName((v + 1) And 1) & \" \" & ((v + 1) And 3)", "Integer 2")]
  [TestCase("Dim v\nv = CInt(32767)\nF = TypeName((v + 1) Or 1) & \" \" & ((v + 1) Or 1)", "Long 32769")]
  [TestCase("Dim v\nv = CInt(1)\nF = TypeName(Not (v + 1))", "Integer")]
  [TestCase("Dim v\nv = CInt(5)\nF = TypeName(v And 3) & \" \" & (v And 3)", "Integer 1")]
  public void Bitwise_OfAPromotingResult_TakesItsTag(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = 1E+10\nIf n = 9 Then v = Null\nF = CStr(CLng(v And 1))", "!6")]
  [TestCase("Dim v\nv = 2147483647.5\nIf n = 9 Then v = Null\nF = CStr(CLng(Not v))", "!6")]
  public void Bitwise_OfANumberBeyondLong_IsError6(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = \"3\"\nIf n = 9 Then v = Null\nF = TypeName(v And 1) & \" \" & (v And 1)", "Long 1")]
  [TestCase("Dim v\nv = \"1\"\nIf n = 9 Then v = Null\nF = TypeName(Not v) & \" \" & (Not v)", "Long -2")]
  [TestCase("Dim v\nv = \"x\"\nIf n = 9 Then v = Null\nF = CStr(v Or 1)", "!13")]
  [TestCase("Dim v\nv = #1/2/2000#\nIf n = 9 Then v = Null\nF = TypeName(v And 1) & \" \" & (v And 1)", "Long 1")]
  public void Bitwise_OfANumericStringOrADate_IsInLong(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CCur(1)\nIf n = 9 Then v = Null\nF = CStr(CLng(v And 1))", "Currency")]
  [TestCase("Dim v\nv = 1^\nIf n = 9 Then v = Null\nF = CStr(CLng(v And 1))", "LongLong")]
  public void Bitwise_ThatTheFrontendDoesNotModel_IsRefused(string body, string message) => Assert.That(Refused(Function(body)), Does.Contain(message));

  [TestCase("Dim a(2)\na(0) = 1\na(1) = \"x\"\na(2) = Null\nF = TypeName(a(0)) & TypeName(a(1)) & TypeName(a(2)) & UBound(a) & LBound(a)", "IntegerStringNull20")]
  [TestCase("Dim a(1) As Variant\nF = TypeName(a(1)) & IsEmpty(a(0))", "EmptyTrue")]
  [TestCase("Dim a()\nReDim a(1 To 3)\na(3) = 2.5\nReDim Preserve a(1 To 4)\nF = TypeName(a(3)) & TypeName(a(4)) & UBound(a)", "DoubleEmpty4")]
  [TestCase("Dim a()\nReDim a(2)\nErase a\nF = UBound(a)", "-1")]
  [TestCase("Dim a(1)\nF = TypeName(a(2))", "!9")]
  [TestCase("Dim a(1), b\na(0) = 1\nb = a\na(0) = 2\nF = b(0) & a(0)", "12")]
  [TestCase("Dim a(1)\na(0) = CInt(32767)\na(0) = a(0) + 1\nF = TypeName(a(0)) & \" \" & a(0)", "Long 32768")]
  public void VariantArray_HoldsAValueOfAnyKindPerElement(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = Array(1, \"a\", True)\nF = TypeName(v(1)) & UBound(v) & LBound(v) & IsArray(v)", "", "String20True")]
  [TestCase("Dim v\nv = Array(1, \"a\", True)\nF = TypeName(v(1)) & UBound(v) & LBound(v)", "Option Base 1\n", "Integer31")]
  [TestCase("Dim v\nv = Array()\nF = UBound(v) & \" \" & LBound(v)", "", "-1 0")]
  [TestCase("Dim v, a(1)\nv = 1\nF = IsArray(v) & IsArray(a) & IsArray(n) & IsArray(Array())", "", "FalseTrueFalseTrue")]
  [TestCase("Dim v\nv = Array(1, 2)\nv = Array(3)\nF = UBound(v) & v(0)", "", "03")]
  public void ArrayFunction_MakesAVariantArray(string body, string head, string expected) => Assert.That(Result(body, head), Is.EqualTo(expected));

  [Test]
  public void VariantLocalOnlyEverAnArray_BecomesTheArray() {
    var module = Import(Function("Dim v\nv = Array(1)\nF = TypeName(v(0))"));
    var local = module.Functions.Single(function => function.Name == "F").Locals.Single(candidate => candidate.Name == "v");
    Assert.That(local.Type, Is.EqualTo(IrType.Array(IrType.Variant)));
  }

  [TestCase("Dim v\nv = Array(1)\nv = 2", "a Variant that holds an array")]
  [TestCase("Dim v\nIf n = 1 Then v = Array(1)\nF = TypeName(v(0))", "a Variant that holds an array")]
  [TestCase("Dim v\nv = 1\nF = TypeName(v(0))", "an element of the Variant 'v'")]
  [TestCase("Dim a()\nReDim a(2 To 3)\nDim v\nv = a", "a Variant that holds an array")]
  public void VariantThatMayHoldAnArrayAndAnotherKind_IsRefused(string body, string message) => Assert.That(Refused(Function(body)), Does.Contain(message));

}
