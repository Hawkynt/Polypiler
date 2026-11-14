using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.String;

/// <summary>
/// The string type of the IR built by hand: its typing rules, the verifier's checks and the
/// interpreter, whose results are those of .NET's ordinal members or of ECMA-262 where the two differ.
/// </summary>
[TestFixture]
public sealed class IrStringTests {

  private static readonly IrType _str = IrType.String;

  private static IrStrConst S(string value) => new(value);

  private static IrConst I(int value) => new(IrType.I32, value);

  private static IrConst U(char value) => new(IrType.U16, value);

  private static IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  /// <summary>A module with one public function F that returns the expression.</summary>
  private static IrModule Returning(IrExpr value)
    => new("M", [new IrFunction("F", true, value.Type, [], [], new IrBlock([new IrReturn(value)]))]);

  private static object? Run(IrExpr value) {
    var module = Returning(value);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return IrInterpreter.Call(module, "F", []);
  }

  private static IrTrap Trap(IrExpr value) {
    var module = Returning(value);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", []))!.Trap;
  }

  [Test]
  public void Type_IsAStorableReferenceToCodeUnits() {
    Assert.That((_str.ToString(), _str.IsString, _str.IsStorable, _str.IsNumeric, IrType.Array(_str).IsStorable, IrType.Array(_str).ToString()),
      Is.EqualTo(("str", true, true, false, true, "str[]")));
    Assert.That(IrType.Array(IrType.Array(_str)).IsStorable, Is.False);
  }

  [Test]
  public void Operations_AreTheStrMembersOfTheEnumeration() {
    var named = Enum.GetValues<IrIntrinsicOp>().Where(op => op.ToString().StartsWith("Str", StringComparison.Ordinal));
    Assert.That(StringOperations.Operations, Is.EquivalentTo(named));
    Assert.That(StringOperations.Operations.All(StringOperations.IsString), Is.True);
    Assert.That(StringOperations.IsString(IrIntrinsicOp.Abs), Is.False);
  }

  [Test]
  public void Signatures_AreWhatTheTypingRuleAccepts() {
    foreach (var op in StringOperations.Operations)
      foreach (var (result, arguments) in StringOperations.Signatures(op))
        Assert.That(IrOperators.Accepts(op, result, arguments), Is.True, op.ToString());

    Assert.That(() => StringOperations.Signatures(IrIntrinsicOp.Abs), Throws.ArgumentException);
  }

  [TestCase(2, true)]
  [TestCase(5, true)]
  [TestCase(1, false)]
  [TestCase(0, false)]
  public void Concat_TakesTwoStringsOrMore(int count, bool accepted)
    => Assert.That(IrOperators.Accepts(IrIntrinsicOp.StrConcat, _str, [.. Enumerable.Repeat(_str, count)]), Is.EqualTo(accepted));

  [Test]
  public void Concat_RefusesANumberAndAnotherResult() {
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.StrConcat, _str, [_str, IrType.I32]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.StrConcat, IrType.I32, [_str, _str]), Is.False);
  }

  private static IEnumerable<TestCaseData> IllTyped() {
    TestCaseData Case(string name, IrModule module, string message) => new TestCaseData(module, message).SetName("IllTyped: " + name);
    var units = new IrLocal("u", IrType.Array(IrType.U16));
    var texts = new IrLocal("t", IrType.Array(_str));
    IrModule Statements(params IrStmt[] body) => new("M", [new IrFunction("F", true, IrType.Void, [units, texts], [], new IrBlock(body))]);
    yield return Case("concat of an i32", Statements(new IrExprStmt(Op(IrIntrinsicOp.StrConcat, _str, S("a"), I(1)))), "StrConcat with the type str is applied to (str, i32)");
    yield return Case("substring at an f64", Statements(new IrExprStmt(Op(IrIntrinsicOp.StrSubstring, _str, S("a"), new IrConst(IrType.F64, 0), I(1)))),
      "StrSubstring with the type str is applied to (str, f64, i32)");
    yield return Case("u16 stored as a str", Statements(new IrStore(new IrLocalRef(texts), I(0), U('a'))), "the value stored in an array element has the type u16, expected str");
    yield return Case("trim with a variable set", Statements(new IrExprStmt(Op(IrIntrinsicOp.StrTrim, _str, S("a"), new IrArrayLength(new IrLocalRef(units))))),
      "the white-space set of StrTrim is not the constant 0 (DotNet) or 1 (Ecma)");
    yield return Case("trim with an unknown set", Statements(new IrExprStmt(Op(IrIntrinsicOp.StrTrimEnd, _str, S("a"), I(2)))),
      "the white-space set of StrTrimEnd is not the constant 0 (DotNet) or 1 (Ecma)");
    yield return Case("array of strings without elements", Statements(new IrExprStmt(new IrNewArray(IrType.Array(_str), I(1)))),
      "an array of strings is created without its elements, which no string could be");
    yield return Case("equality operator on strings", Statements(new IrExprStmt(new IrBinary(IrBinaryOp.Equal, S("a"), S("b")))), "Equal is applied to str and str");
    yield return Case("string constant with numbers", new IrModule("M", [], [new IrModuleConst("C", _str, [1])]), "a constant of the type str with 0 values");
    yield return Case("number constant with strings", new IrModule("M", [], [new IrModuleConst("C", IrType.I32, [1]) { Strings = ["x"] }]),
      "a constant of the type i32 with values of another type");
    yield return Case("two strings for one", new IrModule("M", [], [IrModuleConst.OfStrings("C", _str, ["a", "b"])]), "a constant of the type str with 2 values");
  }

  [TestCaseSource(nameof(IllTyped))]
  public void Verifier_RefusesIllTypedStrings(IrModule module, string message)
    => Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Has.Some.Contains(message));

  [Test]
  public void Constants_AreAStringAndATableOfStrings() {
    var one = IrModuleConst.OfStrings("Name", _str, ["h\u00E9"]);
    var table = IrModuleConst.OfStrings("Names", IrType.Array(_str), ["a", "", "\uD800"]);
    var body = Op(IrIntrinsicOp.StrConcat, _str, new IrConstRef(one), new IrArrayLoad(new IrConstRef(table), I(2)));
    var module = new IrModule("M", [new IrFunction("F", true, _str, [], [], new IrBlock([new IrReturn(body)]))], [one, table]);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.EqualTo("h\u00E9\uD800"));
  }

  [Test]
  public void Strings_CrossTheBoundaryAsTheyAre() {
    var text = new IrLocal("s", _str);
    var parts = new IrLocal("p", IrType.Array(_str));
    var module = new IrModule("M", [new IrFunction("F", true, _str, [text, parts], [], new IrBlock([
      new IrReturn(Op(IrIntrinsicOp.StrJoin, _str, new IrLocalRef(text), new IrLocalRef(parts)))]))]);
    Assert.That(IrInterpreter.Call(module, "F", ["-", new[] { "a", "b" }]), Is.EqualTo("a-b"));
    Assert.That(() => IrInterpreter.Call(module, "F", [1, new[] { "a" }]), Throws.ArgumentException);
  }

  [Test]
  public void ArrayOf_BuildsAnArrayOfStrings() {
    var array = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(_str), [S("x"), S("y")]);
    Assert.That(Run(new IrArrayLoad(array, I(1))), Is.EqualTo("y"));
  }

  [Test]
  public void Struct_WithAStringFieldHasNoDefault() {
    var named = new IrStructType("Named", [new IrField("Name", _str, false)]);
    Assert.That(named.HasDefault, Is.False);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Struct(named), [], [], new IrBlock([new IrReturn(new IrStructDefault(IrType.Struct(named)))]))]) { Types = [named] };
    Assert.That(IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message), Has.Some.Contains("no default value"));
  }

  // ---------------------------------------------------------------- the operations, one equivalence class and every boundary each

  [TestCase("", 0)]
  [TestCase("a", 1)]
  [TestCase("a\U0001F600", 3)]
  public void Length_CountsCodeUnits(string text, int length) => Assert.That(Run(Op(IrIntrinsicOp.StrLength, IrType.I32, S(text))), Is.EqualTo(length));

  [TestCase(0, 'a')]
  [TestCase(2, '\uD83D')]
  [TestCase(3, '\uDE00')]
  public void CodeUnitAt_ReadsOneUnit(int index, char unit)
    => Assert.That(Run(Op(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, S("ab\U0001F600"), I(index))), Is.EqualTo((ushort)unit));

  [TestCase(-1)]
  [TestCase(4)]
  public void CodeUnitAt_TrapsOutside(int index)
    => Assert.That(Trap(Op(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, S("ab\U0001F600"), I(index))), Is.EqualTo(IrTrap.IndexOutOfRange));

  [Test]
  public void Concat_JoinsInOrder()
    => Assert.That(Run(Op(IrIntrinsicOp.StrConcat, _str, S("a"), S(""), S("\uD83D"), S("\uDE00"))), Is.EqualTo("a\U0001F600"));

  [Test]
  public void FromCodeUnit_IsOneUnit() => Assert.That(Run(Op(IrIntrinsicOp.StrFromCodeUnit, _str, U('\uDFFF'))), Is.EqualTo("\uDFFF"));

  private static IrExpr Units(params char[] units)
    => new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U16), [.. units.Select(U)]);

  [TestCase(0, 3, "abc")]
  [TestCase(1, 1, "b")]
  [TestCase(3, 0, "")]
  public void FromCodeUnits_TakesARange(int start, int length, string expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrFromCodeUnits, _str, Units('a', 'b', 'c'), I(start), I(length))), Is.EqualTo(expected));

  [TestCase(-1, 1)]
  [TestCase(0, -1)]
  [TestCase(2, 2)]
  [TestCase(4, 0)]
  public void FromCodeUnits_TrapsOutside(int start, int length)
    => Assert.That(Trap(Op(IrIntrinsicOp.StrFromCodeUnits, _str, Units('a', 'b', 'c'), I(start), I(length))), Is.EqualTo(IrTrap.IndexOutOfRange));

  [Test]
  public void ToCodeUnits_IsANewArray()
    => Assert.That(Run(Op(IrIntrinsicOp.StrToCodeUnits, IrType.Array(IrType.U16), S("a\uD800"))), Is.EqualTo(new ushort[] { 'a', 0xD800 }));

  [TestCase(0, "")]
  [TestCase(1, "ab")]
  [TestCase(3, "ababab")]
  public void Repeat_RepeatsAString(int count, string expected) => Assert.That(Run(Op(IrIntrinsicOp.StrRepeat, _str, S("ab"), I(count))), Is.EqualTo(expected));

  [Test]
  public void Repeat_RepeatsACodeUnit() => Assert.That(Run(Op(IrIntrinsicOp.StrRepeat, _str, U('x'), I(2))), Is.EqualTo("xx"));

  [Test]
  public void Repeat_TrapsForANegativeCount() => Assert.That(Trap(Op(IrIntrinsicOp.StrRepeat, _str, U('x'), I(-1))), Is.EqualTo(IrTrap.IntegerOverflow));

  [TestCase(0, 0, "")]
  [TestCase(0, 3, "abc")]
  [TestCase(1, 2, "bc")]
  [TestCase(3, 0, "")]
  public void Substring_TakesARange(int start, int length, string expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrSubstring, _str, S("abc"), I(start), I(length))), Is.EqualTo(expected));

  [TestCase(-1, 0)]
  [TestCase(4, 0)]
  [TestCase(0, -1)]
  [TestCase(1, 3)]
  [TestCase(int.MaxValue, 1)]
  public void Substring_TrapsOutside(int start, int length)
    => Assert.That(Trap(Op(IrIntrinsicOp.StrSubstring, _str, S("abc"), I(start), I(length))), Is.EqualTo(IrTrap.IndexOutOfRange));

  [TestCase("a", "a", true)]
  [TestCase("e\u0301", "\u00E9", false)]
  [TestCase("", "", true)]
  [TestCase("a", "A", false)]
  public void Equals_IsOrdinal(string left, string right, bool expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrEquals, IrType.Bool, S(left), S(right))), Is.EqualTo(expected));

  [TestCase("a", "B", 1)]
  [TestCase("B", "a", -1)]
  [TestCase("ab", "a", 1)]
  [TestCase("a", "ab", -1)]
  [TestCase("x", "x", 0)]
  [TestCase("\uFFFF", "\U0001F600", 1)]
  public void CompareOrdinal_IsTheSignByCodeUnit(string left, string right, int expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, S(left), S(right))), Is.EqualTo(expected));

  [TestCase("abcabc", "bc", 0, 1)]
  [TestCase("abcabc", "bc", 2, 4)]
  [TestCase("abcabc", "bc", 5, -1)]
  [TestCase("abc", "", 3, 3)]
  [TestCase("abc", "", 0, 0)]
  [TestCase("abc", "abc", 0, 0)]
  [TestCase("abc", "abcd", 0, -1)]
  [TestCase("", "", 0, 0)]
  public void IndexOf_FindsTheFirstStartAtOrAfter(string text, string needle, int from, int expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrIndexOf, IrType.I32, S(text), S(needle), I(from))), Is.EqualTo(expected));

  [TestCase("abcabc", "bc", 6, 4)]
  [TestCase("abcabc", "bc", 3, 1)]
  [TestCase("abcabc", "bc", 0, -1)]
  [TestCase("abc", "", 3, 3)]
  [TestCase("abc", "", 1, 1)]
  [TestCase("abc", "abc", 3, 0)]
  [TestCase("abc", "abcd", 3, -1)]
  public void LastIndexOf_FindsTheLastStartAtOrBefore(string text, string needle, int from, int expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, S(text), S(needle), I(from))), Is.EqualTo(expected));

  [TestCase(IrIntrinsicOp.StrIndexOf, -1)]
  [TestCase(IrIntrinsicOp.StrIndexOf, 4)]
  [TestCase(IrIntrinsicOp.StrLastIndexOf, -1)]
  [TestCase(IrIntrinsicOp.StrLastIndexOf, 4)]
  public void Search_TrapsOutsideTheString(IrIntrinsicOp op, int from)
    => Assert.That(Trap(Op(op, IrType.I32, S("abc"), U('a'), I(from))), Is.EqualTo(IrTrap.IndexOutOfRange));

  [TestCase("abcab", "ab", 0, 3)]
  [TestCase("abc", "", 0, 3)]
  [TestCase("abc", "x", -1, -1)]
  public void Search_WithoutAPositionGoesOverTheWholeString(string text, string needle, int first, int last) {
    Assert.That(Run(Op(IrIntrinsicOp.StrIndexOf, IrType.I32, S(text), S(needle))), Is.EqualTo(first));
    Assert.That(Run(Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, S(text), S(needle))), Is.EqualTo(last));
  }

  [TestCase(0, "abc")]
  [TestCase(3, "")]
  public void Substring_WithoutALengthGoesToTheEnd(int start, string expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrSubstring, _str, S("abc"), I(start))), Is.EqualTo(expected));

  [TestCase(-1)]
  [TestCase(4)]
  public void Substring_WithoutALengthTrapsOutside(int start)
    => Assert.That(Trap(Op(IrIntrinsicOp.StrSubstring, _str, S("abc"), I(start))), Is.EqualTo(IrTrap.IndexOutOfRange));

  [Test]
  public void FromCodeUnits_WithoutARangeTakesAll() => Assert.That(Run(Op(IrIntrinsicOp.StrFromCodeUnits, _str, Units('x', 'y'))), Is.EqualTo("xy"));

  [Test]
  public void Search_TakesACodeUnit() {
    Assert.That(Run(Op(IrIntrinsicOp.StrIndexOf, IrType.I32, S("abab"), U('b'), I(0))), Is.EqualTo(1));
    Assert.That(Run(Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, S("abab"), U('b'), I(4))), Is.EqualTo(3));
  }

  [TestCase(IrIntrinsicOp.StrStartsWith, "abc", "ab", true)]
  [TestCase(IrIntrinsicOp.StrStartsWith, "abc", "", true)]
  [TestCase(IrIntrinsicOp.StrStartsWith, "e\u0301", "e", true)]
  [TestCase(IrIntrinsicOp.StrStartsWith, "ab", "abc", false)]
  [TestCase(IrIntrinsicOp.StrEndsWith, "abc", "bc", true)]
  [TestCase(IrIntrinsicOp.StrEndsWith, "abc", "abc", true)]
  [TestCase(IrIntrinsicOp.StrEndsWith, "abc", "ab", false)]
  [TestCase(IrIntrinsicOp.StrContains, "abc", "b", true)]
  [TestCase(IrIntrinsicOp.StrContains, "abc", "", true)]
  [TestCase(IrIntrinsicOp.StrContains, "abc", "B", false)]
  public void Tests_AreOrdinal(IrIntrinsicOp op, string text, string needle, bool expected)
    => Assert.That(Run(Op(op, IrType.Bool, S(text), S(needle))), Is.EqualTo(expected));

  [TestCase(IrIntrinsicOp.StrStartsWith, "", false)]
  [TestCase(IrIntrinsicOp.StrEndsWith, "xa", true)]
  [TestCase(IrIntrinsicOp.StrContains, "bab", true)]
  public void Tests_TakeACodeUnit(IrIntrinsicOp op, string text, bool expected)
    => Assert.That(Run(Op(op, IrType.Bool, S(text), U('a'))), Is.EqualTo(expected));

  [TestCase("abcb", "b", "XY", "aXYcXY")]
  [TestCase("aaa", "aa", "b", "ba")]
  [TestCase("abc", "x", "y", "abc")]
  public void ReplaceAll_ReplacesEveryOccurrence(string text, string needle, string replacement, string expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrReplaceAll, _str, S(text), S(needle), S(replacement))), Is.EqualTo(expected));

  [Test]
  public void ReplaceAll_TakesTwoCodeUnits() => Assert.That(Run(Op(IrIntrinsicOp.StrReplaceAll, _str, S("abab"), U('a'), U('-'))), Is.EqualTo("-b-b"));

  [Test]
  public void ReplaceAll_TrapsForAnEmptyNeedle()
    => Assert.That(Trap(Op(IrIntrinsicOp.StrReplaceAll, _str, S("ab"), S(""), S("x"))), Is.EqualTo(IrTrap.InvalidOperation));

  [TestCase("abcb", "b", "XY", "aXYcb")]
  [TestCase("ab", "", "x", "xab")]
  [TestCase("ab", "c", "x", "ab")]
  [TestCase("ab", "ab", "", "")]
  public void ReplaceFirst_ReplacesTheFirstOccurrence(string text, string needle, string replacement, string expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrReplaceFirst, _str, S(text), S(needle), S(replacement))), Is.EqualTo(expected));

  [TestCase("a,b,,c", ",", -1, new[] { "a", "b", "", "c" })]
  [TestCase("", ",", -1, new[] { "" })]
  [TestCase(",", ",", -1, new[] { "", "" })]
  [TestCase("abc", "", -1, new[] { "a", "b", "c" })]
  [TestCase("", "", -1, new string[0])]
  [TestCase("a::b::c", "::", 2, new[] { "a", "b" })]
  [TestCase("a,b", ",", 0, new string[0])]
  [TestCase("a,b", ",", 5, new[] { "a", "b" })]
  public void Split_IsTheSplitOfEcma262(string text, string separator, int limit, string[] expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), S(text), S(separator), I(limit))), Is.EqualTo(expected));

  [Test]
  public void Split_TakesACodeUnit()
    => Assert.That(Run(Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), S("a b"), U(' '), I(-1))), Is.EqualTo(new[] { "a", "b" }));

  [Test]
  public void Join_PutsTheSeparatorBetween() {
    var parts = new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(_str), [S("a"), S(""), S("c")]);
    Assert.That(Run(Op(IrIntrinsicOp.StrJoin, _str, S(", "), parts)), Is.EqualTo("a, , c"));
  }

  [TestCase(IrIntrinsicOp.StrTrim, WhitespaceSet.DotNet, "\uFEFFa\uFEFF")]
  [TestCase(IrIntrinsicOp.StrTrim, WhitespaceSet.Ecma, "a")]
  [TestCase(IrIntrinsicOp.StrTrimStart, WhitespaceSet.DotNet, "\uFEFFa\uFEFF \u0085")]
  [TestCase(IrIntrinsicOp.StrTrimStart, WhitespaceSet.Ecma, "a\uFEFF \u0085")]
  [TestCase(IrIntrinsicOp.StrTrimEnd, WhitespaceSet.DotNet, " \uFEFFa\uFEFF")]
  [TestCase(IrIntrinsicOp.StrTrimEnd, WhitespaceSet.Ecma, " \uFEFFa\uFEFF \u0085")]
  public void Trim_KeepsTheSetOfEachLanguage(IrIntrinsicOp op, WhitespaceSet set, string expected) {
    var text = op == IrIntrinsicOp.StrTrim ? "\uFEFFa\uFEFF" : " \uFEFFa\uFEFF \u0085";
    Assert.That(Run(Op(op, _str, S(text), StringOperations.Set(set))), Is.EqualTo(expected));
  }

  [Test]
  public void Trim_OfOnlyWhiteSpaceIsEmpty()
    => Assert.That(Run(Op(IrIntrinsicOp.StrTrim, _str, S(" \t\u3000"), StringOperations.Set(WhitespaceSet.Ecma))), Is.EqualTo(""));

  [Test]
  public void DotNetSet_IsCharIsWhiteSpace() {
    var whiteSpace = Enumerable.Range(0, 0x10000).Select(unit => (char)unit).Where(char.IsWhiteSpace);
    Assert.That(StringOperations.DotNetWhitespace, Is.EqualTo(whiteSpace));
  }

  [Test]
  public void EcmaSet_IsWhiteSpaceAndLineTerminators() {
    // ECMA-262 2022 §12.2: TAB, VT, FF, ZWNBSP, and every code point of the category Zs; §12.3: LF, CR, LS, PS.
    var spaces = Enumerable.Range(0, 0x10000).Select(unit => (char)unit).Where(unit => char.GetUnicodeCategory(unit) == System.Globalization.UnicodeCategory.SpaceSeparator);
    var expected = spaces.Concat(['\t', '\v', '\f', '\uFEFF', '\n', '\r', '\u2028', '\u2029']).Order();
    Assert.That(StringOperations.EcmaWhitespace, Is.EqualTo(expected));
    Assert.That((StringOperations.IsWhitespace('\u180E', WhitespaceSet.Ecma), StringOperations.IsWhitespace('\u200B', WhitespaceSet.DotNet)), Is.EqualTo((false, false)));
  }

  [TestCase(IrIntrinsicOp.StrPadStart, 3, "ab", "abx")]
  [TestCase(IrIntrinsicOp.StrPadStart, 4, "ab", "abax")]
  [TestCase(IrIntrinsicOp.StrPadStart, 1, "ab", "x")]
  [TestCase(IrIntrinsicOp.StrPadStart, 2, "ab", "ax")]
  [TestCase(IrIntrinsicOp.StrPadStart, -1, "ab", "x")]
  [TestCase(IrIntrinsicOp.StrPadStart, 5, "", "x")]
  [TestCase(IrIntrinsicOp.StrPadEnd, 4, "ab", "xaba")]
  [TestCase(IrIntrinsicOp.StrPadEnd, 1, "ab", "x")]
  public void Pad_CutsTheLastFiller(IrIntrinsicOp op, int length, string filler, string expected)
    => Assert.That(Run(Op(op, _str, S("x"), I(length), S(filler))), Is.EqualTo(expected));

  [TestCase(0, 0x61)]
  [TestCase(1, 0x1F600)]
  [TestCase(2, 0xDE00)]
  [TestCase(3, 0xD800)]
  [TestCase(5, 0xDC00)]
  public void CodePointAt_JoinsAPairOnly(int index, int expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrCodePointAt, IrType.I32, S("a\U0001F600\uD800x\uDC00"), I(index))), Is.EqualTo(expected));

  [TestCase(0, 1)]
  [TestCase(1, 2)]
  [TestCase(2, 1)]
  [TestCase(3, 1)]
  public void NextCodePoint_IsTheWidthAtAnIndex(int index, int expected)
    => Assert.That(Run(Op(IrIntrinsicOp.StrNextCodePoint, IrType.I32, S("a\U0001F600\uD800"), I(index))), Is.EqualTo(expected));

  [TestCase(IrIntrinsicOp.StrCodePointAt, -1)]
  [TestCase(IrIntrinsicOp.StrCodePointAt, 1)]
  [TestCase(IrIntrinsicOp.StrNextCodePoint, 1)]
  public void CodePoints_TrapOutside(IrIntrinsicOp op, int index)
    => Assert.That(Trap(Op(op, IrType.I32, S("a"), I(index))), Is.EqualTo(IrTrap.IndexOutOfRange));

  // ---------------------------------------------------------------- the interpreter against .NET on random strings

  /// <summary>
  /// 200 random strings per operation, seed 2026, of four classes - ASCII, Latin-1, the BMP, and
  /// surrogates paired and alone - through the interpreter and through the .NET member each operation
  /// stands for, with its ordinal overload.
  /// </summary>
  [Test]
  public void Interpreter_AgreesWithDotNetOnRandomStrings() {
    var random = new Random(2026);
    var mismatches = new List<string>();
    for (var round = 0; round < 200; ++round) {
      var text = RandomText(random, round % 4);
      var fresh = RandomText(random, round % 4);
      var needle = random.Next(3) == 0 ? "" : text.Length > 2 && random.Next(2) == 0 ? text.Substring(random.Next(text.Length - 1), 2) : fresh[..Math.Min(2, fresh.Length)];
      var other = RandomText(random, round % 4);
      void Check(string what, object? ir, object? expected) {
        if (!Equals(ir, expected) && !(ir is string[] parts && expected is string[] wanted && parts.SequenceEqual(wanted)))
          mismatches.Add($"{what} of {Escape(text)}, {Escape(needle)}: {ir} against {expected}");
      }

      Check("Length", Run(Op(IrIntrinsicOp.StrLength, IrType.I32, S(text))), text.Length);
      Check("Equals", Run(Op(IrIntrinsicOp.StrEquals, IrType.Bool, S(text), S(other))), string.Equals(text, other, StringComparison.Ordinal));
      Check("CompareOrdinal", Run(Op(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, S(text), S(other))), Math.Sign(string.CompareOrdinal(text, other)));
      Check("IndexOf", Run(Op(IrIntrinsicOp.StrIndexOf, IrType.I32, S(text), S(needle), I(0))), text.IndexOf(needle, StringComparison.Ordinal));
      Check("StartsWith", Run(Op(IrIntrinsicOp.StrStartsWith, IrType.Bool, S(text), S(needle))), text.StartsWith(needle, StringComparison.Ordinal));
      Check("EndsWith", Run(Op(IrIntrinsicOp.StrEndsWith, IrType.Bool, S(text), S(needle))), text.EndsWith(needle, StringComparison.Ordinal));
      Check("Contains", Run(Op(IrIntrinsicOp.StrContains, IrType.Bool, S(text), S(needle))), text.Contains(needle, StringComparison.Ordinal));
      Check("Concat", Run(Op(IrIntrinsicOp.StrConcat, _str, S(text), S(other))), text + other);
      Check("Trim", Run(Op(IrIntrinsicOp.StrTrim, _str, S(text), StringOperations.Set(WhitespaceSet.DotNet))), text.Trim());
      Check("TrimStart", Run(Op(IrIntrinsicOp.StrTrimStart, _str, S(text), StringOperations.Set(WhitespaceSet.DotNet))), text.TrimStart());
      Check("TrimEnd", Run(Op(IrIntrinsicOp.StrTrimEnd, _str, S(text), StringOperations.Set(WhitespaceSet.DotNet))), text.TrimEnd());
      if (needle.Length > 0) {
        Check("ReplaceAll", Run(Op(IrIntrinsicOp.StrReplaceAll, _str, S(text), S(needle), S(other))), text.Replace(needle, other, StringComparison.Ordinal));
        Check("Split", Run(Op(IrIntrinsicOp.StrSplit, IrType.Array(_str), S(text), S(needle), I(-1))), text.Split(needle));
        Check("LastIndexOf", Run(Op(IrIntrinsicOp.StrLastIndexOf, IrType.I32, S(text), S(needle), I(text.Length))), text.LastIndexOf(needle, StringComparison.Ordinal));
      }

      if (text.Length > 0) {
        var index = random.Next(text.Length);
        Check("CodeUnitAt", Run(Op(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, S(text), I(index))), (ushort)text[index]);
        Check("NextCodePoint", Run(Op(IrIntrinsicOp.StrNextCodePoint, IrType.I32, S(text), I(index))), char.IsSurrogatePair(text, index) ? 2 : 1);
        Check("Substring", Run(Op(IrIntrinsicOp.StrSubstring, _str, S(text), I(index), I(text.Length - index))), text[index..]);
      }
    }

    Assert.That(mismatches, Is.Empty);
  }

  private static string Escape(string text) => string.Concat(text.Select(unit => unit is >= ' ' and <= '~' ? unit.ToString() : $"\\u{(int)unit:X4}"));

  /// <summary>A random string of up to 8 code units of a class: 0 ASCII, 1 Latin-1, 2 the BMP, 3 surrogates and spaces.</summary>
  internal static string RandomText(Random random, int kind) {
    var units = new char[random.Next(9)];
    for (var index = 0; index < units.Length; ++index)
      units[index] = kind switch {
        0 => (char)random.Next(0x20, 0x7F),
        1 => (char)random.Next(0x00, 0x100),
        2 => (char)random.Next(0x0000, 0xD800),
        _ => random.Next(4) switch {
          0 => (char)random.Next(0xD800, 0xDC00),
          1 => (char)random.Next(0xDC00, 0xE000),
          2 => "\u0085\uFEFF \u00A0\u2028"[random.Next(5)],
          _ => (char)random.Next(0x41, 0x44)
        }
      };

    return new string(units);
  }

}
