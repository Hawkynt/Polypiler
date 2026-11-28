using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.Frontends.Python.Syntax;
using Polypiler.Tests.Frontends.Python.Support;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The frontend's own tokenizer, parser and compile-time checks: every case of
/// <c>tests/fixtures/python/syntax</c> - the versioned constructs and the snippets of CPython's own tests,
/// valid ones and every kind of error - is decided as the case states, which the Python layer holds
/// to CPython; trees, the source as bytes, and the Unicode tables beside.
/// </summary>
[TestFixture]
public sealed class PythonSyntaxTests {

  private static PythonSyntaxCase[] Cases => [.. PythonSyntaxCases.All];

  private static PythonTreeCase[] Trees => [.. PythonSyntaxCases.Trees];

  private static string Outcome(PythonCheckResult result) => result.Compiled ? "valid" : result.Diagnostics.Single().Message;

  /// <summary>Each case is valid, or refused with CPython's class and message, for its version.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Case_IsDecidedAsCPythonDecidesIt(PythonSyntaxCase @case) {
    var result = PythonFrontend.Check("case.py", @case.Source, new PythonImportOptions { Version = @case.Version });
    Assert.That(result.Decided, Is.True);
    Assert.That(Outcome(result), Is.EqualTo(@case.Want));
    Assert.That(result.Tree is not null, Is.EqualTo(result.Parsed));
  }

  /// <summary>The cases cover the versions of the frontend and every stage: parsing, its second pass, compiling.</summary>
  [Test]
  public void Cases_CoverEveryVersionAndStage() {
    Assert.That(Cases.Select(@case => @case.Version).Distinct(), Is.SupersetOf(Enumerable.Range(PythonFrontend.MinimumVersion, PythonFrontend.LatestVersion - PythonFrontend.MinimumVersion + 1)));
    Assert.That(Cases.Count(@case => @case.Want == "valid"), Is.GreaterThan(300));
    Assert.That(Cases.Select(@case => @case.Want).Distinct().Count(), Is.GreaterThan(300));
  }

  /// <summary>The tree of a source is CPython's, every field and position included.</summary>
  [TestCaseSource(nameof(Trees))]
  public void Tree_IsCPythons(PythonTreeCase @case)
    => Assert.That(PythonFrontend.Check("tree.py", @case.Source).Tree, Is.EqualTo(@case.Tree));

  /// <summary>The source as bytes: the BOM and an encoding declaration on one of the first two lines count; newlines of every kind are one.</summary>
  [TestCase(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)'=', (byte)'1' }, "valid")]
  [TestCase(new byte[] { (byte)'x', (byte)'=', (byte)'1', (byte)'\r', (byte)'\n', (byte)'y', (byte)'=', (byte)'2', (byte)'\r', (byte)'z', (byte)'=', (byte)'3' }, "valid")]
  [TestCase(new byte[] { (byte)'x', (byte)'=', (byte)'"', 0xFF, (byte)'"' }, "SyntaxError: Non-UTF-8 code starting with '\\xff' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)'=', (byte)'1', 0 }, "SyntaxError: source code string cannot contain null bytes")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)' ', (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n', 0xE9, (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe9' on line 2, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe9' on line 2, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe9' on line 2, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'\n', (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)' ', (byte)' ', (byte)'\n', (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)'-', (byte)'v', (byte)'a', (byte)'r', (byte)'i', (byte)'a', (byte)'n', (byte)'t', (byte)'-', (byte)'x', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xC3, 0xA9, (byte)'\'', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'L', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'_', (byte)'1', (byte)'_', (byte)'E', (byte)'x', (byte)'t', (byte)'e', (byte)'n', (byte)'d', (byte)'e', (byte)'d', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xC0, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xc0' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xC1, 0xBF, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xc1' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE0, 0x80, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe0' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xED, 0xA0, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xed' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xF0, 0x80, 0x80, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xf0' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xF4, 0x90, 0x80, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xf4' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xF5, 0x80, 0x80, 0x80, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xf5' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE2, 0x82, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe2' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\n', (byte)'y', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE2, 0x82, 0xAC, 0xFF, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xff' on line 2, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xF0, 0x9F, 0x98, 0x80, (byte)'\'', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE2 }, "SyntaxError: Non-UTF-8 code starting with '\\xe2' on line 1, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  [TestCase(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\n' }, "valid")]
  [TestCase(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'s', (byte)'h', (byte)'\n', (byte)'#', (byte)'\n', (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n', (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0xE9, (byte)'\'', (byte)'\n' }, "SyntaxError: Non-UTF-8 code starting with '\\xe9' on line 4, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  public void Bytes_AreDecodedAsCPythonDecodesThem(byte[] source, string want)
    => Assert.That(Outcome(PythonFrontend.Check("bytes.py", source)), Is.EqualTo(want));

  [TestCase("# -*- coding: latin-1 -*-\nx = \"\u00e9\"\n", "iso-8859-1", "\u00e9")]
  [TestCase("#!/usr/bin/python\n# coding=koi8-r\nx = \"\u0430\"\n", "koi8-r", "\u0430")]
  [TestCase("# coding: cp1252\nx = \"\u20ac\"\n", "windows-1252", "\u20ac")]
  [TestCase("\n# vim: set fileencoding=iso-8859-15 :\nx = \"\u20ac\"\n", "iso-8859-15", "\u20ac")]
  [TestCase("# coding: utf-8\nx = \"\u00e9\"\n", "utf-8", "\u00e9")]
  [TestCase("# coding: latin-1-foo\nx = \"\u00e9\"\n", "iso-8859-1", "\u00e9")]
  [TestCase("# coding: UTF_8_SIG\nx = \"\u00e9\"\n", "utf-8", "\u00e9")]
  public void DeclaredEncoding_DecodesTheSource(string text, string encoding, string value) {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var result = PythonFrontend.Check("declared.py", Encoding.GetEncoding(encoding).GetBytes(text));
    Assert.That(result.Compiled, Is.True, string.Join("\n", result.Diagnostics));
    Assert.That(result.Tree, Does.Contain($"Constant(value=\"\\u{(int)value[0]:x4}\""));
  }

  [TestCase(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'-', (byte)'1', (byte)'\n' },
    "SyntaxError: encoding problem: iso-8859-1 with BOM")]
  [TestCase(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)'\n', (byte)'x', (byte)'=', 0xE9, (byte)'\n' },
    "SyntaxError: 'ascii' codec can't decode byte 0xe9 in position 18: ordinal not in range(128)")]
  [TestCase(new byte[] { (byte)'#', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)'=', (byte)'c', (byte)'p', (byte)'1', (byte)'2', (byte)'5', (byte)'2', (byte)'\n', (byte)'x', (byte)'=', 0x81, (byte)'\n' },
    "SyntaxError: 'charmap' codec can't decode byte 0x81 in position 17: character maps to <undefined>")]
  [TestCase(new byte[] { (byte)'x', (byte)'=', (byte)'1', (byte)'\n', (byte)'#', (byte)' ', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g', (byte)':', (byte)' ', (byte)'l', (byte)'a', (byte)'t', (byte)'i', (byte)'n', (byte)'1', (byte)'\n', 0xE9, (byte)'\n' },
    "SyntaxError: Non-UTF-8 code starting with '\\xe9' on line 3, but no encoding declared; see https://peps.python.org/pep-0263/ for details")]
  public void BadEncoding_IsRefusedAsCPythonRefusesIt(byte[] source, string want)
    => Assert.That(Outcome(PythonFrontend.Check("encoding.py", source)), Is.EqualTo(want));

  /// <summary>An encoding the frontend has no decoder for gives no verdict at all.</summary>
  [TestCase("# coding: euc-jp\nx = 1\n")]
  [TestCase("# coding: uft-8\nx = 1\n")]
  public void UnknownEncoding_GivesNoVerdict(string text) {
    var result = PythonFrontend.Check("unknown.py", Encoding.ASCII.GetBytes(text));
    Assert.That((result.Decided, result.Parsed, result.Tree), Is.EqualTo((false, false, (string?)null)));
    Assert.That(result.Diagnostics.Single().Message, Does.Contain("is not supported by the Python frontend"));
  }

  /// <summary>A str with a lone surrogate is no UTF-8, as <c>ast.parse(str)</c> finds; a declaration in a str is ignored.</summary>
  [Test]
  public void Text_IsTakenAsDecoded() {
    Assert.That(Outcome(PythonFrontend.Check("text.py", "x = '\ud800'\n")), Is.EqualTo("UnicodeEncodeError: 'utf-8' codec can't encode character '\\ud800' in position 5: surrogates not allowed"));
    Assert.That(Outcome(PythonFrontend.Check("text.py", "# coding: ascii\nx = '\u00e9'\n")), Is.EqualTo("valid"));
    Assert.That(Outcome(PythonFrontend.Check("text.py", "x = '\ud83d\ude00'\n")), Is.EqualTo("valid"));
  }

  /// <summary>The limits of the tokenizer and the parser: indentation, brackets, nesting, digits.</summary>
  [Test]
  public void Limits_AreCPythons() {
    string Nested(string open, string close, int depth) => string.Concat(Enumerable.Repeat(open, depth)) + "1" + string.Concat(Enumerable.Repeat(close, depth)) + "\n";
    Assert.That(Outcome(PythonFrontend.Check("limits.py", Nested("(", ")", 200))), Is.EqualTo("valid"));
    Assert.That(Outcome(PythonFrontend.Check("limits.py", Nested("(", ")", 201))), Is.EqualTo("SyntaxError: too many nested parentheses"));
    Assert.That(Outcome(PythonFrontend.Check("limits.py", string.Concat(Enumerable.Repeat("-", 20000)) + "1\n")), Is.EqualTo("MemoryError: Parser stack overflowed - Python source too complex to parse"));
    var indented = new StringBuilder();
    for (var i = 0; i < 100; i++)
      indented.Append(new string(' ', i)).Append("if x:\n");

    Assert.That(Outcome(PythonFrontend.Check("limits.py", indented + new string(' ', 100) + "pass\n")), Is.EqualTo("IndentationError: too many levels of indentation"));
    Assert.That(Outcome(PythonFrontend.Check("limits.py", "x = " + new string('7', PyLiterals.MaxStrDigits) + "\n")), Is.EqualTo("valid"));
    Assert.That(Outcome(PythonFrontend.Check("limits.py", "x = " + new string('7', PyLiterals.MaxStrDigits + 1) + "\n")), Does.StartWith("SyntaxError: Exceeds the limit (4300 digits)"));
    Assert.That(Outcome(PythonFrontend.Check("limits.py", "x = 0x" + new string('f', 5000) + "\n")), Is.EqualTo("valid"));
  }

  /// <summary>The checks of the code generator that the snippets of CPython's tests do not reach.</summary>
  [TestCase("for x in y:\n    try:\n        pass\n    except* E:\n        break\n", "SyntaxError: 'break', 'continue' and 'return' cannot appear in an except* block")]
  [TestCase("def f[T = int, U](): pass\n", "SyntaxError: non-default type parameter 'U' follows default type parameter")]
  [TestCase("match x:\n    case {1: a, True: b}: pass\n", "SyntaxError: mapping pattern checks duplicate key (True)")]
  [TestCase("match x:\n    case {-0.0: a, 0: b}: pass\n", "SyntaxError: mapping pattern checks duplicate key (0)")]
  [TestCase("match x:\n    case {1j: a, -1-2j: b, 1j: c}: pass\n", "SyntaxError: mapping pattern checks duplicate key (1j)")]
  [TestCase("match x:\n    case {b'a': a, 'a': b, None: c, None: d}: pass\n", "SyntaxError: mapping pattern checks duplicate key (None)")]
  [TestCase("match x:\n    case {f'a': a}: pass\n", "SyntaxError: mapping pattern keys may only match literals and attribute lookups")]
  [TestCase("match x:\n    case f'a': pass\n", "SyntaxError: patterns may only match literals and attribute lookups")]
  [TestCase("match x:\n    case C(a=1, a=2): pass\n", "SyntaxError: attribute name repeated in class pattern: a")]
  [TestCase("match x:\n    case [a, *b, *c]: pass\n", "SyntaxError: multiple starred names in sequence pattern")]
  [TestCase("match x:\n    case (a, a): pass\n", "SyntaxError: multiple assignments to name 'a' in pattern")]
  [TestCase("match x:\n    case a | b: pass\n", "SyntaxError: name capture 'a' makes remaining patterns unreachable")]
  [TestCase("match x:\n    case _:\n        pass\n    case 1:\n        pass\n", "SyntaxError: wildcard makes remaining patterns unreachable")]
  [TestCase("match x:\n    case [a] | (b,): pass\n", "SyntaxError: alternative patterns bind different names")]
  [TestCase("match x:\n    case [a, (a | a)]: pass\n", "SyntaxError: name capture 'a' makes remaining patterns unreachable")]
  [TestCase("match x:\n    case [a, (1 as a) | (2 as a)]: pass\n", "SyntaxError: multiple assignments to name 'a' in pattern")]
  [TestCase("*a, *b = c\n", "SyntaxError: multiple starred expressions in assignment")]
  [TestCase("*a = b\n", "SyntaxError: starred assignment target must be in a list or tuple")]
  [TestCase("x = f(**a, b=1, b=2)\n", "SyntaxError: keyword argument repeated: b")]
  [TestCase("class C(a=1, a=2): pass\n", "SyntaxError: keyword argument repeated: a")]
  [TestCase("try:\n    pass\nexcept:\n    pass\nexcept E:\n    pass\n", "SyntaxError: default 'except:' must be last")]
  [TestCase("def f():\n    '\\ud800 \\ud801'\n", "UnicodeEncodeError: 'utf-8' codec can't encode character '\\ud800' in position 0: surrogates not allowed")]
  [TestCase("class C:\n    '\tx\\udc00\\udc01'\n", "UnicodeEncodeError: 'utf-8' codec can't encode characters in position 9-10: surrogates not allowed")]
  [TestCase("from __future__ import annotations\ndef f(x: g(a=1, a=2)): pass\n", "valid")]
  [TestCase("def f(x: g(a=1, a=2)): pass\n", "SyntaxError: keyword argument repeated: a")]
  [TestCase("x: g(a=1, a=2)\n", "SyntaxError: keyword argument repeated: a")]
  [TestCase("def f():\n    x: g(a=1, a=2)\n", "valid")]
  [TestCase("def f(*a: *T): pass\n", "valid")]
  [TestCase("def f(*a: *[*b]): pass\n", "valid")]
  [TestCase("[*a, *b] = c\n", "SyntaxError: multiple starred expressions in assignment")]
  [TestCase("type T[U: {*a for b in c}] = int\n", "SyntaxError: iterable unpacking cannot be used in comprehension")]
  [TestCase("type T = {*() for x in y}\n", "SyntaxError: iterable unpacking cannot be used in comprehension")]
  public void CompileCheck_IsCPythons(string source, string want)
    => Assert.That(Outcome(PythonFrontend.Check("compile.py", source)), Is.EqualTo(want));

  /// <summary>An identifier outside ASCII is normalized to NFKC; one that normalizes to a constant is refused, as CPython refuses it.</summary>
  [Test]
  public void Identifiers_AreNormalized() {
    Assert.That(PythonFrontend.Check("names.py", "\ufb01 = 1\n").Tree, Does.Contain("Name(id=\"fi\""));
    Assert.That(Outcome(PythonFrontend.Check("names.py", "\uff34rue = 1\n")), Is.EqualTo("ValueError: identifier field can't represent 'True' constant"));
    Assert.That(Outcome(PythonFrontend.Check("names.py", "a\u20acb = 1\n")), Is.EqualTo("SyntaxError: invalid character '\u20ac' (U+20AC)"));
    Assert.That(Outcome(PythonFrontend.Check("names.py", "a\u00a0b = 1\n")), Is.EqualTo("SyntaxError: invalid non-printable character U+00A0"));
  }

  /// <summary>NFKC, the names of <c>\N{...}</c>, XID and printability against the Unicode Character Database.</summary>
  [TestCase("\ufb01", "fi")]
  [TestCase("e\u0301", "\u00e9")]
  [TestCase("\u1100\u1161\u11a8", "\uac01")]
  [TestCase("\uac00\u11a8", "\uac01")]
  [TestCase("\u2460", "1")]
  [TestCase("a\u0323\u0307", "\u1ea1\u0307")]
  [TestCase("\u0071\u0307\u0323", "q\u0323\u0307")]
  [TestCase("\u212b", "\u00c5")]
  [TestCase("\ud835\udc00", "A")]
  public void Nfkc_IsUnicodes(string text, string normalized) => Assert.That(PyUnicode.Nfkc(text), Is.EqualTo(normalized));

  [TestCase("LATIN SMALL LETTER A", 0x61)]
  [TestCase("latin small letter a", 0x61)]
  [TestCase("HANGUL SYLLABLE GAG", 0xAC01)]
  [TestCase("HANGUL SYLLABLE A", 0xC544)]
  [TestCase("CJK UNIFIED IDEOGRAPH-4E00", 0x4E00)]
  [TestCase("CJK UNIFIED IDEOGRAPH-20000", 0x20000)]
  [TestCase("TANGUT IDEOGRAPH-17000", 0x17000)]
  [TestCase("EGYPTIAN HIEROGLYPH-13460", 0x13460)]
  [TestCase("NUSHU CHARACTER-1B170", 0x1B170)]
  [TestCase("CJK COMPATIBILITY IDEOGRAPH-F900", 0xF900)]
  [TestCase("LINE FEED", 0x0A)]
  [TestCase("BYTE ORDER MARK", 0xFEFF)]
  public void Lookup_FindsTheCharacter(string name, int point) => Assert.That(PyUnicode.Lookup(name), Is.EqualTo(point));

  [TestCase("NO SUCH CHARACTER")]
  [TestCase("HANGUL SYLLABLE GAGX")]
  [TestCase("CJK UNIFIED IDEOGRAPH-04E00")]
  [TestCase("CJK UNIFIED IDEOGRAPH-4E0")]
  [TestCase("CJK UNIFIED IDEOGRAPH-1234567")]
  [TestCase("CJK UNIFIED IDEOGRAPH-110000")]
  [TestCase("CJK UNIFIED IDEOGRAPH-0041")]
  [TestCase("CJK UNIFIED IDEOGRAPH-ZZZZ")]
  [TestCase("TANGUT IDEOGRAPH-4E00")]
  [TestCase("EGYPTIAN HIEROGLYPH-0041")]
  [TestCase("EGYPTIAN HIEROGLYPH-013460")]
  [TestCase("LATIN CAPITAL LETTER A WITH MACRON AND GRAVE")]
  public void Lookup_RefusesWhatIsNoCharacter(string name) => Assert.That(PyUnicode.Lookup(name), Is.Null);

  [Test]
  public void Lookup_RefusesAnOverlongName() => Assert.That(PyUnicode.Lookup(new string('A', 300)), Is.Null);

  [Test]
  public void Properties_AreUnicodes() {
    Assert.That((PyUnicode.IsXidStart('_'), PyUnicode.IsXidStart('a'), PyUnicode.IsXidStart('1'), PyUnicode.IsXidStart(0x00E9), PyUnicode.IsXidStart(0x0300)), Is.EqualTo((true, true, false, true, false)));
    Assert.That((PyUnicode.IsXidContinue('1'), PyUnicode.IsXidContinue('-'), PyUnicode.IsXidContinue(0x0300), PyUnicode.IsXidContinue(0x20AC)), Is.EqualTo((true, false, true, false)));
    Assert.That((PyUnicode.IsPrintable(' '), PyUnicode.IsPrintable('a'), PyUnicode.IsPrintable(0xA0), PyUnicode.IsPrintable(0xD800), PyUnicode.IsPrintable(0x0B)), Is.EqualTo((true, true, false, false, false)));
    Assert.That(PyUnicode.Text([0xD800, 0x41, 0x1F600]), Is.EqualTo("\ud800A\ud83d\ude00"));
  }

}
