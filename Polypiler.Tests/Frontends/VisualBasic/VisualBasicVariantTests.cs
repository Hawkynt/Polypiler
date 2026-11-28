using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// <c>Variant</c> in the classic Visual Basic frontend: the locals the inference gives a fixed type,
/// the dynamic ones and what the helpers compute for them - promotion instead of overflow, <c>Empty</c>,
/// <c>Null</c>, the built-in functions that look at a Variant - and every construct refused because its
/// result would depend on the locale. Results are what VB6 / VBA documents ([MS-VBAL] §5.6.9, ADR 2600).
/// </summary>
[TestFixture]
public sealed class VisualBasicVariantTests {

  private static object? Call(IrModule module, string function, params object[] arguments) => IrInterpreter.Call(module, function, arguments);

  private static IrModule Import(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  private static string Refused(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Module, Is.Null);
    return string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
  }

  /// <summary>A value as the driver of the oracle prints it through <c>CStr</c>, a dynamic one by its payload.</summary>
  internal static string Text(object? value) => value switch {
    bool flag => flag ? "True" : "False",
    double number => NumberTextOperations.FormatVisualBasic(number, '.'),
    float number => NumberTextOperations.FormatVisualBasic(number, '.'),
    IrVariant { Value: null } => "",
    IrVariant variant => Text(variant.Value),
    null => "",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
  };

  /// <summary>The run-time error Visual Basic raises for an exception class of the IR.</summary>
  internal static int Number(IrClassType type) => type == IrExceptionClass.DivideByZeroException ? 11
    : type == IrExceptionClass.OverflowException ? 6
    : type == IrExceptionClass.IndexOutOfRangeException ? 9
    : type == IrExceptionClass.InvalidCastException ? 13
    : type == IrExceptionClass.InvalidOperationException ? 94
    : 5;

  private static string Run(IrModule module, string function, params object[] arguments) {
    try {
      return Text(IrInterpreter.Call(module, function, arguments));
    } catch (IrException exception) {
      return "!" + Number(exception.Class).ToString(CultureInfo.InvariantCulture);
    }
  }

  private static string Function(string returns, string body, string parameters = "")
    => $"Option Explicit\nPublic Function F({parameters}) As {returns}\n{body}\nEnd Function\n";

  private static string Result(string body, string parameters = "", params object[] arguments)
    => Run(Import(Function("String", body, parameters)), "F", arguments);

  private static IrType LocalType(IrModule module, string local) => module.Functions.Single(function => function.Name == "F").Locals.Single(candidate => candidate.Name == local).Type;

  [TestCase("Dim v\nv = 5\nF = v * 2", "i16")]
  [TestCase("Dim v As Variant\nv = 5&\nF = v", "i32")]
  [TestCase("Dim v\nv = \"a\"\nF = 1", "str")]
  [TestCase("Dim v\nv = 1.5\nF = v", "f64")]
  [TestCase("Dim v\nv = True\nF = 1", "bool")]
  [TestCase("Dim v\nIf F = 0 Then\nv = 1\nElse\nv = 2\nEnd If\nF = v", "i16")]
  [TestCase("Dim v\nSelect Case F\nCase 0\nv = 1\nCase Else\nv = 2\nEnd Select\nF = v", "i16")]
  [TestCase("Dim v, i As Integer\nFor i = 1 To 3\nv = i\nNext\nF = 0", "i16")]
  public void LocalOfOneType_BecomesThatType(string body, string type)
    => Assert.That(LocalType(Import(Function("Long", body)), "v").ToString(), Is.EqualTo(type));

  [TestCase("Dim v\nF = v", "read before its first assignment")]
  [TestCase("Dim v\nIf F = 0 Then v = 1\nF = v", "assigned in one branch only")]
  [TestCase("Dim v\nSelect Case F\nCase 0\nv = 1\nEnd Select\nF = v", "assigned in one case only")]
  [TestCase("Dim v\nv = 1\nv = \"a\"\nF = 0", "of two types")]
  [TestCase("Dim v\nv = CInt(1)\nv = v + 1\nF = v", "promoted")]
  [TestCase("Dim v\nv = Null\nF = 0", "Null")]
  [TestCase("Dim v\nOn Error Resume Next\nv = 1\nF = v", "skipped by On Error Resume Next")]
  [TestCase("Dim v\nDo While F < 3\nF = F + 1\nv = 1\nLoop\nF = v", "assigned in a loop only")]
  public void LocalOfSeveralKindsOrEmpty_StaysAVariant(string body, string because)
    => Assert.That(LocalType(Import(Function("Long", body)), "v"), Is.EqualTo(IrType.Variant), because);

  [TestCase("Dim v\nv = CInt(32767)\nv = v + 1\nF = TypeName(v) & \" \" & v", "Long 32768")]
  [TestCase("Dim v\nv = CInt(-32768)\nv = v - 1\nF = TypeName(v) & \" \" & v", "Long -32769")]
  [TestCase("Dim v\nv = CInt(200)\nv = v * v\nF = TypeName(v) & \" \" & v", "Long 40000")]
  [TestCase("Dim v\nv = CInt(-32768)\nv = -v\nF = TypeName(v) & \" \" & v", "Long 32768")]
  [TestCase("Dim v\nv = CByte(255)\nv = v + 1\nF = TypeName(v) & \" \" & v", "Integer 256")]
  [TestCase("Dim v\nv = CByte(255)\nv = v * v\nF = TypeName(v) & \" \" & v", "Long 65025")]
  [TestCase("Dim v\nv = CByte(0)\nv = v - 1\nF = TypeName(v) & \" \" & v", "Integer -1")]
  [TestCase("Dim v\nv = CByte(5)\nv = -v\nF = TypeName(v) & \" \" & v", "Integer -5")]
  [TestCase("Dim v\nv = CInt(5)\nv = v + CByte(1)\nF = TypeName(v) & \" \" & v", "Integer 6")]
  [TestCase("Dim v\nv = True\nv = v + v\nF = TypeName(v) & \" \" & v", "Integer -2")]
  [TestCase("Dim v\nv = CLng(2147483647)\nv = v + 1\nF = TypeName(v) & \" \" & CStr(v > 2147483647)", "Double True")]
  [TestCase("Dim v\nv = CLng(100000)\nv = v * v\nF = TypeName(v)", "Double")]
  [TestCase("Dim v\nv = CSng(1)\nv = v + 1\nF = TypeName(v)", "Single")]
  [TestCase("Dim v\nv = CSng(3E+38)\nv = v * 2\nF = TypeName(v)", "Double")]
  [TestCase("Dim v\nv = CSng(1)\nv = v + CLng(1)\nF = TypeName(v)", "Double")]
  [TestCase("Dim v, w\nv = CInt(7)\nIf F = \"\" Then w = Null\nw = v \\ 2\nF = TypeName(w) & \" \" & w & \" \" & (v Mod 2)", "Integer 3 1")]
  [TestCase("Dim v\nv = CInt(-32768)\nIf F = \"x\" Then v = Null\nF = TypeName(v \\ -1) & \" \" & (v \\ -1)", "Long 32768")]
  [TestCase("Dim v\nv = CLng(-2147483647) - 1\nIf F = \"x\" Then v = Null\nF = TypeName(v \\ -1)", "Double")]
  [TestCase("Dim v\nv = 7.5\nIf F = \"x\" Then v = Null\nF = TypeName(v \\ 2) & \" \" & CLng(v \\ 2) & \" \" & CLng(v Mod 3)", "Long 4 2")]
  public void VariantArithmetic_PromotesInsteadOfOverflowing(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CInt(1)\nIf F = \"x\" Then v = Null\nF = TypeName(v / 2) & \" \" & TypeName(v ^ 2)", "Double Double")]
  [TestCase("Dim v\nv = CSng(1)\nIf F = \"x\" Then v = Null\nF = TypeName(v / CInt(2))", "Single")]
  [TestCase("Dim v\nv = CSng(1)\nIf F = \"x\" Then v = Null\nF = TypeName(v / CLng(2))", "Double")]
  [TestCase("Dim v\nv = CInt(3)\nF = TypeName(v / 2) & \" \" & TypeName(v ^ 2) & \" \" & TypeName(CSng(v) / CInt(2))", "Double Double Single")]
  [TestCase("Dim v\nv = 0\nIf F = \"x\" Then v = Null\nF = CStr(CLng(1 / (v + 1)))", "1")]
  public void VariantDivisionAndPower_AreFloatingPoint(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = CInt(1)\nIf F = \"x\" Then v = Null\nF = CStr(CLng(1 / (v - 1)))", "!11")]
  [TestCase("Dim v\nv = CInt(0)\nIf F = \"x\" Then v = Null\nF = CStr(CLng(v / v))", "!6")]
  [TestCase("Dim v\nv = CInt(1)\nIf F = \"x\" Then v = Null\nF = CStr(v \\ 0)", "!11")]
  [TestCase("Dim v\nv = 1E+300\nIf F = \"x\" Then v = Null\nF = CStr(CLng(v * v))", "!6")]
  public void VariantArithmetic_RaisesTheErrorsOfTheTypedOne(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nF = TypeName(v) & \" \" & VarType(v) & \" \" & IsEmpty(v) & \" \" & IsNull(v)", "Empty 0 True False")]
  [TestCase("Dim v\nF = (v + 1) & \" \" & (v & \"a\") & \" \" & (v = 0) & \" \" & (v = \"\") & \" \" & TypeName(v + v)", "1 a True True Integer")]
  [TestCase("Dim v\nF = TypeName(v + CByte(1)) & \" \" & TypeName(v - CByte(1)) & \" \" & TypeName(v * 2.5)", "Byte Integer Double")]
  [TestCase("Dim v\nF = TypeName(v + \"a\") & \" \" & (v + \"a\")", "String a")]
  [TestCase("Dim v, w\nw = v\nF = TypeName(w) & IsEmpty(w)", "EmptyTrue")]
  [TestCase("Dim v\nIf v Then\nF = \"then\"\nElse\nF = \"else\"\nEnd If", "else")]
  [TestCase("Dim v\nDim i As Integer\ni = v\nF = i", "0")]
  [TestCase("Dim v\nDim s As String\ns = v\nF = \"[\" & s & \"]\"", "[]")]
  [TestCase("Dim v\nDim b As Boolean\nb = v\nF = b", "False")]
  [TestCase("Dim v\nF = TypeName(-v)", "Integer")]
  [TestCase("Dim v, w\nw = v\nF = TypeName(w \\ 1)", "Integer")]
  public void Empty_IsZeroOrTheEmptyString(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = Null\nF = TypeName(v) & \" \" & VarType(v) & \" \" & IsNull(v) & \" \" & IsEmpty(v)", "Null 1 True False")]
  [TestCase("Dim v\nv = Null\nF = TypeName(v + 1) & TypeName(v - 1) & TypeName(v * 1) & TypeName(v / 1) & TypeName(v \\ 1) & TypeName(v Mod 1) & TypeName(v ^ 1) & TypeName(-v)", "NullNullNullNullNullNullNullNull")]
  [TestCase("Dim v\nv = Null\nF = TypeName(v = 1) & TypeName(v < v) & TypeName(v & Null)", "NullNullNull")]
  [TestCase("Dim v\nv = Null\nF = v & \"a\"", "a")]
  [TestCase("Dim v\nv = Null\nF = \"a\" & v", "a")]
  [TestCase("Dim v\nv = Null\nIf v Then\nF = \"then\"\nElse\nF = \"else\"\nEnd If", "else")]
  [TestCase("Dim v\nv = Null\nIf v = 1 Then\nF = \"then\"\nElse\nF = \"else\"\nEnd If", "else")]
  [TestCase("Dim v\nv = Null\nSelect Case v\nCase 1\nF = \"one\"\nCase Else\nF = \"else\"\nEnd Select", "else")]
  [TestCase("Dim v\nv = Null\nDo While v\nLoop\nF = \"done\"", "done")]
  public void Null_PropagatesAndIsNotTrue(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase("Dim v\nv = Null\nDim i As Integer\ni = v\nF = i")]
  [TestCase("Dim v\nv = Null\nF = CStr(v)")]
  [TestCase("Dim v\nv = Null\nDim b As Boolean\nb = v\nF = b")]
  [TestCase("Dim v\nv = Null\nDim d As Double\nd = v\nF = CLng(d)")]
  [TestCase("Dim v\nv = Null\nF = CStr(CByte(v))")]
  [TestCase("Dim v\nv = Null\nError 94")]
  public void Null_WhereAValueIsNeeded_IsError94(string body) => Assert.That(Result(body), Is.EqualTo("!94"));

  [TestCase("Dim v\nv = Null\nF = TypeName(v And False) & \" \" & TypeName(v Or True) & \" \" & TypeName(v And True) & \" \" & TypeName(v Or False) & \" \" & TypeName(v Xor True) & \" \" & TypeName(Not v)", "Boolean Boolean Null Null Null Null")]
  [TestCase("Dim v\nv = Null\nF = CStr(False And v) & \" \" & CStr(True Or v)", "False True")]
  [TestCase("Dim v, w\nv = True\nw = False\nIf F = \"x\" Then v = Null\nIf F = \"x\" Then w = Null\nF = CStr(v And w) & CStr(v Or w) & CStr(v Xor w) & CStr(Not v)", "FalseTrueTrueFalse")]
  public void Logic_WithNull_IsThreeValued(string body, string expected) => Assert.That(Result(body), Is.EqualTo(expected));

  [TestCase(1, "Integer 2 True True")]
  [TestCase(2, "String 8 False True")]
  [TestCase(3, "Boolean 11 True True")]
  [TestCase(4, "Null 1 False ")]
  [TestCase(5, "Empty 0 True True")]
  [TestCase(6, "Double 5 True True")]
  [TestCase(7, "Long 3 True True")]
  [TestCase(8, "Byte 17 True True")]
  [TestCase(9, "Single 4 True True")]
  public void Introspection_OfADynamicVariant_ReadsItsTag(int which, string expected) {
    var body = """
      Dim v
      If which = 1 Then
          v = 5
      ElseIf which = 2 Then
          v = "five"
      ElseIf which = 3 Then
          v = True
      ElseIf which = 4 Then
          v = Null
      ElseIf which = 6 Then
          v = 1.5
      ElseIf which = 7 Then
          v = 5&
      ElseIf which = 8 Then
          v = CByte(5)
      ElseIf which = 9 Then
          v = 1.5!
      End If
      F = TypeName(v) & " " & VarType(v) & " " & (VarType(v) <> 8 And VarType(v) <> 1) & " " & (v = v)
      """;
    Assert.That(Result(body, "ByVal which As Integer", (short)which), Is.EqualTo(expected));
  }

  [TestCase("Dim v\nv = 5\nF = TypeName(v) & VarType(v) & IsEmpty(v) & IsNull(v) & IsNumeric(v)", "Integer2FalseFalseTrue")]
  [TestCase("F = TypeName(5&) & TypeName(1.5!) & TypeName(True) & TypeName(\"a\") & TypeName(CByte(1)) & TypeName(1.5) & VarType(\"a\") & VarType(True)", "LongSingleBooleanStringByteDouble811")]
  [TestCase("Dim v\nv = CInt(1)\nF = TypeName(v + 1) & VarType(v + 1) & TypeName(v * 1.5) & TypeName(Abs(v))", "Integer2DoubleInteger")]
  [TestCase("F = TypeName(G()) & IsEmpty(G()) & IsNull(G())", "IntegerFalseFalse")]
  [TestCase("Dim v\nv = Null\nF = IsNumeric(v) & IsNumeric(5) & IsNumeric(Empty)", "FalseTrueTrue")]
  [TestCase("F = TypeName(CVar(5)) & TypeName(CVar(\"a\")) & TypeName(Empty) & TypeName(Null) & VarType(Empty)", "IntegerStringEmptyNull0")]
  public void Introspection_OfATypedValue_IsItsType(string body, string expected)
    => Assert.That(Run(Import(Function("String", body) + "Private Function G() As Integer\nG = 1\nEnd Function\n"), "F"), Is.EqualTo(expected));

  [TestCase("Dim v, w\nv = 10\nIf which = 1 Then\nw = 9\nElse\nw = \"9\"\nEnd If\nF = CStr(v < w) & \" \" & CStr(w = w) & \" \" & CStr(w > v)", 1, "False True False")]
  [TestCase("Dim v, w\nv = 10\nIf which = 1 Then\nw = 9\nElse\nw = \"9\"\nEnd If\nF = CStr(v < w) & \" \" & CStr(w = w) & \" \" & CStr(w > v)", 2, "True True True")]
  [TestCase("Dim v, w\nIf which = 1 Then v = \"b\"\nIf which = 1 Then w = \"a\"\nF = CStr(v > w) & \" \" & CStr(v = \"\") & \" \" & CStr(\"\" = v)", 2, "False True True")]
  [TestCase("Dim v, w\nIf which = 1 Then v = \"b\"\nIf which = 1 Then w = \"a\"\nF = CStr(v > w) & \" \" & CStr(v = \"\") & \" \" & CStr(\"\" = v)", 1, "True False False")]
  [TestCase("Dim v\nIf which = 1 Then v = 1.5\nIf which = 2 Then v = True\nF = CStr(v <= 1.5) & CStr(v >= -1) & CStr(v <> 0)", 1, "TrueTrueTrue")]
  [TestCase("Dim v\nIf which = 1 Then v = 1.5\nIf which = 2 Then v = True\nF = CStr(v <= 1.5) & CStr(v >= -1) & CStr(v <> 0)", 2, "TrueTrueTrue")]
  public void Comparison_OfVariants_FollowsTheirKinds(string body, int which, string expected)
    => Assert.That(Result(body, "ByVal which As Integer", (short)which), Is.EqualTo(expected));

  [TestCase("Dim i, total\ntotal = 0\nFor i = 1 To 10\ntotal = total + i * n\nNext\nF = TypeName(total) & \" \" & CLng(total) & \" \" & TypeName(i) & \" \" & i", 3, "Integer 165 Integer 11")]
  [TestCase("Dim i, s\nFor i = 1 To 5\ns = s & i\nNext\nF = s & n", 7, "123457")]
  [TestCase("Dim i, c\nc = 0\nFor i = 1 To n\nc = c + 1\nNext\nF = CLng(c)", 5, "5")]
  [TestCase("Dim i, c\nc = 0\nFor i = n To 1 Step -1\nc = c + 1\nIf c = 3 Then Exit For\nNext\nF = CLng(c)", 5, "3")]
  [TestCase("Dim i, c\nc = 0\nFor i = 32766 To 32767\nc = c + 1\nNext\nF = TypeName(i) & \" \" & CLng(c)", 0, "Long 2")]
  [TestCase("Dim i, c\nc = 0\nFor i = 0.5 To 2\nc = c + 1\nNext\nF = TypeName(i) & \" \" & CLng(c)", 0, "Double 2")]
  [TestCase("Dim i, c\nc = 0\nFor i = CByte(1) To 3\nc = c + 1\nNext\nF = TypeName(i) & \" \" & CLng(c)", 0, "Integer 3")]
  [TestCase("Dim i\nFor i = 1 To 3\nNext\nF = i", 0, "4")]
  public void For_OverAVariantCounter_CountsAsVisualBasicDoes(string body, int n, string expected)
    => Assert.That(Result(body, "ByVal n As Integer", (short)n), Is.EqualTo(expected));

  [Test]
  public void For_WithConstantBoundsThatFit_KeepsItsTypedCounter() {
    var module = Import(Function("Long", "Dim i\nFor i = 1 To 10\nF = F + i\nNext"));
    Assert.That(LocalType(module, "i"), Is.EqualTo(IrType.I16));
    Assert.That(Run(module, "F"), Is.EqualTo("55"));
    Assert.That(LocalType(Import(Function("Long", "Dim i\nFor i = 1 To 32767\nNext")), "i"), Is.EqualTo(IrType.Variant));
    Assert.That(LocalType(Import(Function("Long", "Dim i\nFor i = 1 To 10& Step 1\nNext")), "i"), Is.EqualTo(IrType.Variant));
    Assert.That(LocalType(Import(Function("Long", "Dim i\nFor i = 1! To 10\nNext")), "i"), Is.EqualTo(IrType.Variant));
    Assert.That(LocalType(Import(Function("Long", "Dim i\nFor i = 1 To 10\ni = i + 1\nNext")), "i"), Is.EqualTo(IrType.Variant));
  }

  [Test]
  public void ParametersResultsAndGlobals_AreDynamic() {
    var source = """
      Option Explicit
      Private g
      Public Function Identity(x)
          Identity = x
      End Function
      Public Function KindOf(ByVal x As Variant) As String
          KindOf = TypeName(x)
      End Function
      Public Function Stored(x) As String
          g = x
          Stored = TypeName(g) & TypeName(Identity(g)) & TypeName(IIf(IsNull(x), 1, x))
      End Function
      """;
    var module = Import(source);
    Assert.That(module.Functions.Single(function => function.Name == "Identity").ReturnType, Is.EqualTo(IrType.Variant));
    Assert.That(module.Globals.Single(global => global.Name == "gg").Type, Is.EqualTo(IrType.Variant));
    Assert.That(Call(module, "Identity", new IrVariant(IrVariantTag.I16, (short)5)), Is.EqualTo(new IrVariant(IrVariantTag.I16, (short)5)));
    Assert.That(Call(module, "KindOf", new IrVariant(IrVariantTag.F64, 1.5)), Is.EqualTo("Double"));
    Assert.That(Call(module, "KindOf", IrVariant.Empty), Is.EqualTo("Empty"));
    Assert.That(Call(module, "Stored", new IrVariant(IrVariantTag.String, "a")), Is.EqualTo("StringStringString"));
    Assert.That(Call(module, "Stored", IrVariant.Null), Is.EqualTo("NullNullInteger"));
  }

  [Test]
  public void Arguments_AreBoxedWithTheirTypes() {
    var source = """
      Option Explicit
      Private Function KindOf(ByVal x) As String
          KindOf = TypeName(x)
      End Function
      Public Function F() As String
          Dim v
          v = CInt(1)
          F = KindOf(1) & KindOf(1&) & KindOf("a") & KindOf(True) & KindOf(CByte(1)) & KindOf(1.5) & KindOf(1.5!) & KindOf(v + 1) & KindOf(Null) & KindOf(v * 1.5)
      End Function
      """;
    Assert.That(Run(Import(source), "F"), Is.EqualTo("IntegerLongStringBooleanByteDoubleSingleIntegerNullDouble"));
  }

  [TestCase("Dim v\nv = 1^\nIf F = \"x\" Then v = Null\nF = \"\"", "a LongLong in a Variant")]
  [TestCase("Dim v\nv = Null\nDebug.Print v", "Debug.Print of a Variant that may be Null")]
  [TestCase("Dim v\nv = 1\nIf F = \"x\" Then v = Null\nF = Left(v, 1)", "Left of a Variant")]
  [TestCase("Dim i\nFor i = 1 To Null\nNext", "a For loop whose bounds may be a Null")]
  [TestCase("Dim i\nFor i = 1 To n\ni = \"a\"\nNext", "a For loop whose Variant counter 'i' may hold a String")]
  [TestCase("Dim v\nv = 1\nIf F = \"x\" Then v = Null\nF = CStr(v Like \"a\")", "Like")]
  public void LocaleOrUnmodelledConstruct_IsRefused(string body, string message)
    => Assert.That(Refused(Function("String", body, "ByVal n As Integer")), Does.Contain(message));

  [Test]
  public void ConstructsAroundVariants_AreRefused() {
    Assert.That(Refused("Option Explicit\nType R\nx As Variant\nEnd Type\n"), Does.Contain("the Variant field 'x' of a Type"));
    Assert.That(Refused("Option Explicit\nType R\nx\nEnd Type\n"), Does.Contain("the Variant field 'x' of a Type"));
    Assert.That(Refused("Option Explicit\nPublic Function F(a() As Variant) As Long\nEnd Function\n"), Does.Contain("the Variant array parameter 'a'"));
    Assert.That(Refused("Option Explicit\nPublic Function F() As Variant()\nEnd Function\n"), Does.Contain("returns a Variant array"));
    Assert.That(Refused("Option Explicit\nType R\nx As Long\nEnd Type\nPublic Function F() As Long\nDim r As R, v\nv = r\nEnd Function\n"), Does.Contain("a R in a Variant"));
    Assert.That(Refused("Option Explicit\nType R\nx As Long\nEnd Type\nPublic Function F(v) As Long\nDim r As R\nr = v\nEnd Function\n"), Does.Contain("Type mismatch: 'Variant' where 'R' is expected"));
    Assert.That(Refused("Option Explicit\nType R\nx As Long\nEnd Type\nPublic Function F() As Long\nDim r As R\nF = VarType(r)\nEnd Function\n"), Does.Contain("VarType of a R"));
    Assert.That(Refused("Option Explicit\nPublic Function F(ByVal i) As Long\nFor i = 1 To 3\nNext\nEnd Function\n"), Does.Contain("may hold a Null or String or Empty").Or.Contain("may hold a"));
    Assert.That(Refused("Option Explicit\nPublic Function F() As Long\nDim v\nv = 1\nExit For\nEnd Function\n"), Does.Contain("Exit For outside a For loop"));
  }

  [Test]
  public void Assignment_ThatTheInferenceMissedBeforeARefusal_DemotesTheLocal() {
    // The lowering with every local dynamic refuses Left of a Variant, which a typed local lowers; the
    // number assigned after it then makes the local dynamic, and the Left is refused as it is.
    Assert.That(Refused(Function("Long", "Dim v\nv = \"abc\"\nF = Len(Left(v, 1))\nv = 5")), Does.Contain("Left of a Variant"));
    var module = Import(Function("Long", "Dim v\nv = \"abc\"\nF = Len(Left(v, 2))"));
    Assert.That(LocalType(module, "v"), Is.EqualTo(IrType.String));
    Assert.That(Run(module, "F"), Is.EqualTo("2"));
  }

  [Test]
  public void ErrorHandler_SeesWhatTheGuardedCodeMayHaveAssigned() {
    var source = Function("String", "Dim v\nOn Error GoTo Failed\nv = 1\nv = 1 \\ 0\nF = \"none\"\nExit Function\nFailed:\nF = TypeName(v)");
    var module = Import(source);
    Assert.That(LocalType(module, "v"), Is.EqualTo(IrType.Variant));
    Assert.That(Run(module, "F"), Is.EqualTo("Integer"));
    var resume = Function("String", "Dim v\nOn Error GoTo Failed\nv = 1\nv = 1 \\ 0\nF = TypeName(v)\nExit Function\nFailed:\nResume Next");
    Assert.That(Run(Import(resume), "F"), Is.EqualTo("Integer"));
  }

  [Test]
  public void ProcedureWithoutVariants_IsLoweredOnce() {
    var module = Import(Function("Long", "Dim i As Long\nIf i = 0 Then\ni = 1\nEnd If\nDo While i < 3\ni = i + 1\nIf i = 2 Then Exit Do\nLoop\nF = i"));
    Assert.That(Run(module, "F"), Is.EqualTo("2"));
  }

  [Test]
  public void Fixture_PrintsTheDocumentedValues_InTheInterpreter() {
    var classic = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "visualbasic", "classic");
    var module = Import(File.ReadAllText(Path.Combine(classic, "Variants.bas")));
    var expected = File.ReadAllLines(Path.Combine(classic, "Variants.expected.txt")).Where(line => line.Length > 0).ToList();
    Assert.That(Calls().Select(call => Run(module, call.Function, call.Arguments)), Is.EqualTo(expected));
  }

  /// <summary>The calls of <c>Variants.cases.txt</c> as the interpreter takes them.</summary>
  internal static (string Function, object[] Arguments)[] Calls() => [
    ("Doubled", [21]), ("Promoted", []), ("IntegerProduct", [(short)200, (short)200]), ("ByteBelowZero", []), ("LongBeyond", []),
    ("Mixed", [(short)1]), ("Mixed", [(short)2]), ("Mixed", [(short)3]), ("Mixed", [(short)4]), ("Mixed", [(short)5]),
    ("EmptyRules", []), ("NullRules", []), ("NullConcatenated", []), ("NullIsNotTrue", []), ("NullToInteger", []), ("NullLogic", []),
    ("Accumulate", [(short)3]), ("Joined", [(short)7]), ("Compared", [(short)1]), ("Compared", [(short)2]),
    ("Divided", [(short)7, (short)2]), ("Divided", [(short)-32768, (short)-1]), ("Divided", [(short)5, (short)0]), ("Counted", [5]),
    ("Identity", [new IrVariant(IrVariantTag.I16, (short)5)]), ("KindOf", [new IrVariant(IrVariantTag.I16, (short)5)]), ("KindOf", [new IrVariant(IrVariantTag.String, "a")]),
    ("KindOf", [IrVariant.Empty]), ("KindOf", [new IrVariant(IrVariantTag.F64, 1.5)]), ("KindOf", [IrVariant.Null])
  ];

}
