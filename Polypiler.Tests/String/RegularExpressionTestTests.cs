using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.Backends.JavaScript.Printing;
using Polypiler.Core.Backends.Python;
using Polypiler.Core.Backends.Python.Printing;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// <c>RegExp.prototype.test</c> of a regular-expression literal (#1399): the conversion of an ECMAScript
/// pattern to .NET's syntax that the interpreter and the C# backend share, checked on a table of
/// patterns and subjects against what node answered; the IR node, its verifier and its text format;
/// the frontend, which lowers the test of a literal and refuses what is outside the subset by name; and
/// the backends, C# compiled in every profile and run on the table, JavaScript as the literal again.
/// </summary>
[TestFixture]
public sealed class RegularExpressionTestTests {

  /// <summary>The subjects of the table: empty, line terminators, white space of ECMAScript and of .NET only, non-ASCII digits and letters, the Kelvin sign and the long s, a pair of surrogates.</summary>
  private static readonly string[] _subjects = ["", "0aF9", "0g", "ab\n", "a", "abc", "a\nc", "a\u2028c", "a\rc", " \t", "\u00A0\uFEFF", "\u0085", "\u0663", "\u00E9", "\u212A", "k", "K", "abcd", "abab", "aa", "aaa", "ababc", "x/y", "A", "\b", "\n", "_", "-", "\u017F", "\uD83D\uDE00"];

  /// <summary>
  /// Each pattern with its flags and, per subject of <see cref="_subjects"/>, whether it matches: the
  /// answers node 22.23.3 printed for <c>new RegExp(pattern, flags).test(subject)</c>, 1 for true.
  /// </summary>
  private static readonly (string Pattern, string Flags, string Matches)[] _table = [
    (@"^[0-9A-Fa-f]*$", "", "110011000000000001111101000000"),
    (@"^[0-9A-Fa-f]+$", "", "010011000000000001111101000000"),
    (@"[A-Za-z]", "", "011111111000000111111111000000"),
    (@"^a.c$", "", "000001000000000000000000000000"),
    (@"^\s+$", "", "000000000110000000000000010000"),
    (@"^\d+$", "", "000000000000000000000000000000"),
    (@"^\w+$", "", "011011000000000111111101001000"),
    (@"[^a-c]", "", "011100111111111111000011111111"),
    (@"^(?:ab|cd){2}$", "", "000000000000000001100000000000"),
    (@"^a{2,3}?$", "", "000000000000000000011000000000"),
    (@"^(a|b)*c$", "", "000001000000000000000100000000"),
    (@"^K$", "i", "000000000000000110000000000000"),
    (@"^[a-c]+$", "i", "000011000000000000111101000000"),
    (@"^\W$", "i", "000000000001111000000000110110"),
    (@"a$", "", "000010000000000000011000000000"),
    (@"^\S\D$", "", "001000000000000000010000000001"),
    (@"x*", "", "111111111111111111111111111111"),
    (@"[]", "", "000000000000000000000000000000"),
    (@"[^]", "", "011111111111111111111111111111"),
    (@"\u00e9|\x41", "", "000000000000010000000001000000"),
    (@"^[\b]$", "", "000000000000000000000000100000"),
    (@"^\cJ$", "", "000000000000000000000000010000"),
    (@"\/", "", "000000000000000000000010000000"),
    (@"a{2,}", "", "000000000000000000011000000000"),
    (@"^[\d-]$", "", "000000000000000000000000000100"),
    (@"^[^\s]$", "", "000010000001111110000001101110"),
    (@"^.$", "", "000010000001111110000001101110"),
    (@"^[^k]$", "i", "000010000001111000000001111110"),
    (@"^\uD83D", "", "000000000000000000000000000001"),
    (@"^$", "", "100000000000000000000000000000"),
    (@"b+?", "", "000101000000000001100100000000"),
    (@"^(?:a|)$", "", "100010000000000000000000000000"),
    (@"^[-a]$", "", "000010000000000000000000000100"),
    (@"^[\-_]$", "", "000000000000000000000000001100"),
    (@"\.", "", "000000000000000000000000000000"),
    (@"^a?b??$", "", "100010000000000000000000000000")
  ];

  private static readonly IrLocal _s = new("s", IrType.String);

  /// <summary>One public function <c>T&lt;row&gt;(s)</c> per row of the table, each the test of its pattern.</summary>
  private static IrModule TableModule() => new("Table", [.. _table.Select((row, index) => new IrFunction($"T{index}", true, IrType.Bool, [_s], [],
    new IrBlock([new IrReturn(RegexOperations.Test(new IrLocalRef(_s), row.Pattern, row.Flags))])))]);

  private static IEnumerable<TestCaseData> Rows() => _table.Select(row => new TestCaseData(row.Pattern, row.Flags, row.Matches).SetName($"Matches as node: /{row.Pattern}/{row.Flags}"));

  /// <summary>Test design 1 to 4: the shared conversion matches what node matches, for every pattern and subject.</summary>
  [TestCaseSource(nameof(Rows))]
  public void Conversion_MatchesWhatNodeMatches(string pattern, string flags, string matches) {
    var actual = string.Concat(_subjects.Select(subject => RegexOperations.Test(subject, pattern, flags) ? '1' : '0'));
    Assert.That(actual, Is.EqualTo(matches), RegexOperations.ToDotNet(pattern, flags));
  }

  [Test]
  public void Interpreter_TestsWithTheSharedConversion() {
    var module = TableModule();
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    for (var row = 0; row < _table.Length; ++row)
      for (var subject = 0; subject < _subjects.Length; ++subject)
        Assert.That(IrInterpreter.Call(module, $"T{row}", [_subjects[subject]]), Is.EqualTo(_table[row].Matches[subject] == '1'), $"/{_table[row].Pattern}/ on subject {subject}");
  }

  [Test]
  public void Interpreter_TrapsForANullSubject() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [], [], new IrBlock([new IrReturn(RegexOperations.Test(new IrNull(IrType.String), "a", ""))]))]);
    Assert.That(() => IrInterpreter.Call(module, "F", []), Throws.InstanceOf<IrTrapException>());
  }

  [TestCase(@"^[0-9A-Fa-f]*$", "", @"^[0-9A-Fa-f]*\z")]
  [TestCase("a.b", "", @"a[\u0000-\u0009\u000B\u000C\u000E-\u2027\u202A-\uFFFF]b")]
  [TestCase(@"\d\w", "", @"[0-9][0-9A-Z_a-z]")]
  [TestCase("k", "i", "[Kk]")]
  [TestCase("[^x]", "i", @"[\u0000-WY-wy-\uFFFF]")]
  [TestCase("(a)(?:b)c{2,3}?|d+", "", "(a)(?:b)c{2,3}?|d+")]
  [TestCase("[]", "", @"[^\u0000-\uFFFF]")]
  [TestCase("[ab]", "", "[ab]")]
  [TestCase(@"\0\t", "", @"\u0000\u0009")]
  public void Conversion_WritesEverySetOut(string pattern, string flags, string expected)
    => Assert.That(RegexOperations.ToDotNet(pattern, flags), Is.EqualTo(expected));

  /// <summary>Test design 5: every flag but i, every construct outside the subset, and every pattern that is no pattern, named.</summary>
  [TestCase("a", "g", "the flag g")]
  [TestCase("a", "y", "the flag y")]
  [TestCase("a", "m", "the flag m")]
  [TestCase("a", "s", "the flag s")]
  [TestCase("a", "u", "the flag u")]
  [TestCase("a", "v", "the flag v")]
  [TestCase("a", "d", "the flag d")]
  [TestCase("a", "ii", "the flag i")]
  [TestCase(@"(a)\1", "", "a backreference")]
  [TestCase(@"\00", "", "an octal escape")]
  [TestCase(@"[\1]", "", "an octal escape")]
  [TestCase("(?<=a)b", "", "a lookbehind")]
  [TestCase("(?<!a)b", "", "a lookbehind")]
  [TestCase("a(?=b)", "", "a lookahead")]
  [TestCase("a(?!b)", "", "a lookahead")]
  [TestCase("(?<n>a)", "", "a named group")]
  [TestCase("(?i)a", "", "a group (?i")]
  [TestCase("(?", "", "an unclosed (")]
  [TestCase(@"\b", "", @"a word boundary \b")]
  [TestCase(@"\B", "", @"a word boundary \B")]
  [TestCase(@"\k<n>", "", "a named backreference")]
  [TestCase(@"\p{L}", "", @"a property escape \p")]
  [TestCase(@"\P{L}", "", @"a property escape \P")]
  [TestCase(@"\q", "", @"the escape \q")]
  [TestCase(@"\c1", "", @"the escape \c")]
  [TestCase(@"\x4", "", @"an escape \x without 2 hexadecimal digits")]
  [TestCase(@"\u12g4", "", @"an escape \u without 4 hexadecimal digits")]
  [TestCase("\\", "", @"a \ at the end")]
  [TestCase("a{", "", "a { that starts no quantifier")]
  [TestCase("a{2,1}", "", "a { that starts no quantifier")]
  [TestCase("a{1,2,3}", "", "a { that starts no quantifier")]
  [TestCase("a{,2}", "", "a { that starts no quantifier")]
  [TestCase("a{1,x}", "", "a { that starts no quantifier")]
  [TestCase("a{99999999999}", "", "a { that starts no quantifier")]
  [TestCase("*a", "", "a quantifier * without anything to repeat")]
  [TestCase("^*", "", "a quantifier * without anything to repeat")]
  [TestCase("{1}", "", "a quantifier { without anything to repeat")]
  [TestCase("(a", "", "an unclosed (")]
  [TestCase("a)", "", "an unmatched )")]
  [TestCase("[a", "", "an unclosed [")]
  [TestCase("[z-a]", "", "a range out of order")]
  [TestCase(@"[\d-z]", "", "a range of a class escape")]
  [TestCase(@"[a-\d]", "", "a range of a class escape")]
  [TestCase("\u00E9", "i", "the flag i and the character U+00E9 outside ASCII")]
  [TestCase("[a-\u00E9]", "i", "the flag i and the character U+00E9 outside ASCII")]
  public void Refusal_NamesWhatIsOutsideTheSubset(string pattern, string flags, string what) {
    Assert.That(RegexOperations.Refusal(pattern, flags), Is.EqualTo(what));
    Assert.That(() => RegexOperations.ToDotNet(pattern, flags), Throws.ArgumentException.With.Message.EqualTo(what));
  }

  [TestCase("a", "")]
  [TestCase("\u00E9", "")]
  [TestCase("[\u00E9]", "")]
  [TestCase("a", "i")]
  [TestCase("", "")]
  public void Refusal_IsNullWithinTheSubset(string pattern, string flags) => Assert.That(RegexOperations.Refusal(pattern, flags), Is.Null);

  [Test]
  public void Operations_AreTheOneTest() {
    Assert.That(RegexOperations.Operations, Is.EqualTo(new[] { IrIntrinsicOp.RegexTest }));
    Assert.That(RegexOperations.IsRegex(IrIntrinsicOp.RegexTest), Is.True);
    Assert.That(RegexOperations.IsRegex(IrIntrinsicOp.StrContains), Is.False);
    Assert.That(StringOperations.IsString(IrIntrinsicOp.RegexTest), Is.False);
  }

  [Test]
  public void Typing_IsThreeStringsToBool() {
    IrType[] strings = [IrType.String, IrType.String, IrType.String];
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.RegexTest, IrType.Bool, strings), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.RegexTest, IrType.I32, strings), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.RegexTest, IrType.Bool, [IrType.String, IrType.String]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.RegexTest, IrType.Bool, [IrType.I32, IrType.String, IrType.String]), Is.False);
  }

  private static IReadOnlyList<string> Verified(IrExpr pattern, IrExpr flags)
    => IrVerifier.Verify(new IrModule("M", [new IrFunction("F", true, IrType.Bool, [_s], [], new IrBlock([
      new IrReturn(new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [new IrLocalRef(_s), pattern, flags]))]))])).Select(problem => problem.Message).ToList();

  [Test]
  public void Verifier_RequiresConstantsWithinTheSubset() {
    Assert.That(Verified(new IrStrConst("a"), new IrStrConst("i")), Is.Empty);
    Assert.That(Verified(new IrLocalRef(_s), new IrStrConst("")), Has.Some.Contains("the pattern and the flags of RegexTest are not string constants"));
    Assert.That(Verified(new IrStrConst("a"), new IrLocalRef(_s)), Has.Some.Contains("the pattern and the flags of RegexTest are not string constants"));
    Assert.That(Verified(new IrStrConst("a"), new IrStrConst("g")), Has.Some.Contains("RegexTest of a regular expression with the flag g"));
    Assert.That(RegexOperations.Problem(new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [new IrLocalRef(_s)])), Is.Null);
    Assert.That(RegexOperations.Problem(new IrIntrinsic(IrIntrinsicOp.RegexTest, IrType.Bool, [new IrLocalRef(_s)])), Is.Null);
  }

  [Test]
  public void Verifier_AcceptsATestInTheInitializerOfAGlobal() {
    var global = new IrGlobal("G", IrType.Bool, RegexOperations.Test(new IrStrConst("ab"), "b", ""));
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrGlobalRef(global))]))]) { Globals = [global] };
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    Assert.That(IrInterpreter.Call(module, "F", []), Is.True);
  }

  [Test]
  public void TextFormat_KeepsPatternAndFlags() {
    var module = TableModule();
    var read = IrModuleFormat.Read(IrModuleFormat.Write(module));
    var tests = read.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrIntrinsic>().Select(RegexOperations.Of);
    Assert.That(tests, Is.EqualTo(_table.Select(row => (row.Pattern, row.Flags))));
    Assert.That(IrModuleFormat.Write(read), Is.EqualTo(IrModuleFormat.Write(module)));
  }

  /// <summary>Test design 3: the C# backend writes a static field per pattern that compiles in every profile and matches what node matches.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void CSharp_CompilesInEveryProfile(CSharpProfile profile) {
    var module = TableModule();
    Assert.That(LegalizedCSharp.Check(module, profile), Is.Empty);
    var text = LegalizedCSharp.Emit(module, profile)[0].Content;
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    Assert.That(text, Does.Contain("private static readonly global::System.Text.RegularExpressions.Regex Pattern1 = new global::System.Text.RegularExpressions.Regex(\"^[0-9A-Fa-f]*\\\\z\", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);"));
    Assert.That(text, Does.Contain("Pattern1.IsMatch(s)"));
  }

  [Test]
  public void CSharp_RunsTheTableAsNodeDoes() {
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(TableModule())[0].Content, true);
    for (var row = 0; row < _table.Length; ++row) {
      var method = build.Method($"T{row}");
      var actual = string.Concat(_subjects.Select(subject => (bool)method.Invoke(null, [subject])! ? '1' : '0'));
      Assert.That(actual, Is.EqualTo(_table[row].Matches), $"/{_table[row].Pattern}/{_table[row].Flags}");
    }
  }

  [Test]
  public void CSharp_SharesTheFieldOfAPatternAndKeepsItsNameFromOthers() {
    var pattern1 = new IrLocal("Pattern1", IrType.Bool);
    IrExpr Test(string pattern) => RegexOperations.Test(new IrLocalRef(_s), pattern, "");
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.Bool, [_s], [pattern1], new IrBlock([
        new IrDeclare(pattern1, Test("a")),
        new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd, new IrLocalRef(pattern1), new IrBinary(IrBinaryOp.LogicalAnd, Test("b"), Test("a"))))])),
      new IrFunction("Pattern2", true, IrType.Bool, [], [], new IrBlock([new IrReturn(new IrConst(IrType.Bool, 1))]))
    ]);
    var text = LegalizedCSharp.Emit(module)[0].Content;
    var build = GeneratedCSharp.Compile(text, true);
    Assert.That(text.Split("new global::System.Text.RegularExpressions.Regex(").Length - 1, Is.EqualTo(2), text);
    Assert.That(build.Method("F").Invoke(null, ["ab"]), Is.True);
    Assert.That(build.Method("F").Invoke(null, ["a"]), Is.False);
  }

  [Test]
  public void CSharp_ReachesTheFieldFromAClassOfTheModule() {
    const string source = """
      export class Checker {
        /** @param {string} s @returns {boolean} */
        hex(s) { return /^[0-9a-f]+$/.test(s); }
      }
      /** @param {string} s @returns {boolean} */
      export function f(s) { return new Checker().hex(s); }
      """;
    var text = LegalizedCSharp.Emit(Imported(source))[0].Content;
    var build = GeneratedCSharp.Compile(text, true);
    Assert.That(build.Method("f").Invoke(null, ["0a"]), Is.True, text);
    Assert.That(build.Method("f").Invoke(null, ["0g"]), Is.False, text);
  }

  [TestCase("es5")]
  [TestCase("es2022")]
  public void JavaScript_WritesTheLiteralBack(string level) {
    var profile = JavaScriptProfile.Parse(level);
    var text = LegalizedJavaScript.Emit(TableModule(), profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    Assert.That(text, Does.Contain("return /^[0-9A-Fa-f]*$/.test(s);"));
    Assert.That(text, Does.Contain("return /^K$/i.test(s);"));
    Assert.That(text, Does.Contain("return /\\//.test(s);"));
  }

  [TestCase("", "(?:)")]
  [TestCase("a/b", @"a\/b")]
  [TestCase(@"a\/b", @"a\/b")]
  [TestCase("[/]/", @"[/]\/")]
  [TestCase(@"[\]/]/", @"[\]/]\/")]
  [TestCase("a\nb\u2028", @"a\u000Ab\u2028")]
  [TestCase("\\\u00E9", @"\u00E9")]
  [TestCase("\u00E9", @"\u00E9")]
  public void JavaScript_EscapesWhatALiteralCannotHold(string pattern, string body)
    => Assert.That(JavaScriptLiterals.RegExpBody(pattern), Is.EqualTo(body));

  /// <summary>The literal the JavaScript backend writes for each row computes in node what the table says.</summary>
  [Test]
  [Category("EndToEnd")]
  public void JavaScript_RunsTheTableAsNodeDoes() {
    var directory = Directory.CreateTempSubdirectory("regex-test-");
    try {
      File.WriteAllText(Path.Combine(directory.FullName, "Table.mjs"), LegalizedJavaScript.Emit(TableModule()).Single().Content);
      var subjects = "[" + string.Join(", ", _subjects.Select(subject => "\"" + JavaScriptLiterals.Escape(subject) + "\"")) + "]";
      var calls = string.Join("\n", _table.Select((_, row) => $"console.log({subjects}.map(s => t.T{row}(s) ? 1 : 0).join(\"\"));"));
      File.WriteAllText(Path.Combine(directory.FullName, "run.mjs"), $"import * as t from \"./Table.mjs\";\n{calls}\n");
      using var node = Process.Start(new ProcessStartInfo("node", [Path.Combine(directory.FullName, "run.mjs")]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
      var output = node.StandardOutput.ReadToEnd();
      node.WaitForExit();
      Assert.That(output.Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(_table.Select(row => row.Matches)), node.StandardError.ReadToEnd());
    } finally {
      directory.Delete(true);
    }
  }

  private static IrModule Imported(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Not.Null, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static string Refused(string source) {
    var result = JavaScriptFrontend.Import("snippet.js", source);
    Assert.That(result.Module, Is.Null);
    return string.Join("; ", result.Diagnostics);
  }

  /// <summary>Scenario: hex validation agrees with node.</summary>
  [TestCase("0aF9", true)]
  [TestCase("", true)]
  [TestCase("0g", false)]
  [TestCase("ab\n", false)]
  public void HexValidation_TranslatedToCSharp_AgreesWithNode(string s, bool r) {
    var module = Imported("function f(s) { return /^[0-9A-Fa-f]*$/.test(s); }\n/** @param {string} s */ export function g(s) { return f(s); }");
    var build = GeneratedCSharp.Compile(LegalizedCSharp.Emit(module)[0].Content, true);
    Assert.That(build.Method("g").Invoke(null, [s]), Is.EqualTo(r));
    Assert.That(IrInterpreter.Call(module, "g", [s]), Is.EqualTo(r));
  }

  /// <summary>Scenario: a global regular expression is refused.</summary>
  [Test]
  public void GlobalRegularExpression_IsRefusedNamingTheFlag()
    => Assert.That(Refused("/** @param {string} s */ export function f(s) { return /a/g.test(s); }"), Does.Contain("PLY1102: 'a regular expression with the flag g' is not supported yet"));

  [TestCase("return /(a)\\1/.test(s);", "a regular expression with a backreference")]
  [TestCase("return /(?<=a)b/.test(s);", "a regular expression with a lookbehind")]
  [TestCase("return /a/y.test(s);", "a regular expression with the flag y")]
  [TestCase("return /a/.test(s, s);", "test with 2 arguments")]
  [TestCase("return /a/.test();", "test with 0 arguments")]
  [TestCase("return /a/.exec(s) !== null;", "a call of /a/.exec")]
  [TestCase("const r = /a/; return r.test(s);", "a regular expression")]
  public void Frontend_RefusesByName(string body, string what)
    => Assert.That(Refused($"/** @param {{string}} s */ export function f(s) {{ {body} }}"), Does.Contain($"'{what}'"));

  /// <summary>test converts its argument to text as JavaScript does: a number by Number::toString, null as "null".</summary>
  [Test]
  public void Frontend_TestsTheSubjectAsText() {
    var module = Imported("""
      /** @param {number} n @returns {boolean} */
      export function f(n) { return /^1\.5$/.test(n); }
      /** @param {string|null} s @returns {boolean} */
      export function g(s) { return /^nul/.test(s); }
      """);
    Assert.That(IrInterpreter.Call(module, "f", [1.5]), Is.True);
    Assert.That(IrInterpreter.Call(module, "f", [2.0]), Is.False);
    Assert.That(IrInterpreter.Call(module, "g", [null!]), Is.True);
    Assert.That(IrInterpreter.Call(module, "g", ["x"]), Is.False);
  }

  [TestCase(@"^[0-9A-Fa-f]*$", "", @"^[0-9A-Fa-f]*\Z")]
  [TestCase("a.b", "", "a[\\x00-\\x09\\x0B\\x0C\\x0E-\u2027\u202A-\uFFFF]b")]
  [TestCase(@"\d\w", "", @"[0-9][0-9A-Z_a-z]")]
  [TestCase("k", "i", "[Kk]")]
  [TestCase("[]", "", "(?!)")]
  [TestCase(@"\0\t", "", @"\x00\x09")]
  public void ToPython_WritesEverySetOut(string pattern, string flags, string expected)
    => Assert.That(RegexOperations.ToPython(pattern, flags), Is.EqualTo(expected));

  [Test]
  public void ToProgram_CompilesSubsetAndCapsHugeRepetitions() {
    foreach (var (pattern, flags, _) in _table) {
      var program = RegexOperations.ToProgram(pattern, flags);
      Assert.That(program, Is.Not.Null, $"/{pattern}/{flags}");
      Assert.That(program!.Instructions[^1].Op, Is.EqualTo(0));
    }

    Assert.That(RegexOperations.ToProgram("(?:){100}", ""), Is.Not.Null);
    Assert.That(RegexOperations.ToProgram("(?:){0,5}", ""), Is.Not.Null);
    Assert.That(RegexOperations.ToProgram("a{5000}", ""), Is.Null);
    Assert.That(RegexOperations.ToProgram("(?:a|b){2000}", ""), Is.Null);
    Assert.That(RegexOperations.ToProgram("a{0,5000}", ""), Is.Null);
    Assert.That(RegexOperations.ToProgram("(?:a|b){0,2000}", ""), Is.Null);
    Assert.That(RegexOperations.ToProgram("a+", "", 2), Is.Null);
    Assert.That(RegexOperations.ToProgram("a*", "", 2), Is.Null);
    Assert.That(RegexOperations.ToProgram("ab", "", 2), Is.Null);
    Assert.That(RegexOperations.ToProgram("abc", "", 2), Is.Null);
    Assert.That(RegexOperations.ToProgram("a|b", "", 2), Is.Null);
    Assert.That(() => RegexOperations.ToProgram("a", "g"), Throws.ArgumentException);
  }

  [TestCaseSource(typeof(PythonProfile), nameof(PythonProfile.All))]
  public void Python_WritesCompiledRegexesInEveryProfile(PythonProfile profile) {
    var translation = PythonFixtures.Translate(TableModule(), profile);
    Assert.That(translation.Problems, Is.Empty);
    var text = translation.Source!;
    Assert.That(text, Does.Contain("import re\n"));
    Assert.That(text, Does.Contain("_PATTERN_1 = re.compile(\"^[0-9A-Fa-f]*\\\\Z\")"));
    Assert.That(text, Does.Contain("return _PATTERN_1.search(s) is not None"));
    var parsed = PythonSyntaxReader.ReadModule(text);
    Assert.That(PythonPrinter.Print(PythonSyntaxReader.ReadModule(PythonPrinter.Print(parsed))), Is.EqualTo(PythonPrinter.Print(parsed)));
  }

  [Test]
  public void Python_DeduplicatesPatternsAndSupportsGlobalInitializer() {
    var global = new IrGlobal("G", IrType.Bool, RegexOperations.Test(new IrStrConst("ab"), "a", ""));
    var module = new IrModule("M", [
      new IrFunction("F", true, IrType.Bool, [_s], [], new IrBlock([
        new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd, RegexOperations.Test(new IrLocalRef(_s), "a", ""), RegexOperations.Test(new IrLocalRef(_s), "a", "")))]))
    ]) { Globals = [global] };
    var translation = PythonFixtures.Translate(module);
    Assert.That(translation.Problems, Is.Empty);
    Assert.That(translation.Source!.Split("re.compile(").Length - 1, Is.EqualTo(1), translation.Source);
  }

  [Test]
  [Category("EndToEnd")]
  public void Python_RunsTheTableAsNodeDoes() {
    var directory = Directory.CreateTempSubdirectory("regex-py-test-");
    try {
      var translation = PythonFixtures.Translate(TableModule());
      Assert.That(translation.Problems, Is.Empty);
      File.WriteAllText(Path.Combine(directory.FullName, "table.py"), translation.Source!);
      var subjects = "[" + string.Join(", ", _subjects.Select(PythonPrinter.Quote)) + "]";
      var calls = string.Join("\n", _table.Select((_, row) => $"print(\"\".join(\"1\" if table.t{row}(s) else \"0\" for s in subjects))"));
      File.WriteAllText(Path.Combine(directory.FullName, "run.py"), $"import table\nsubjects = {subjects}\n{calls}\n");
      using var python = Process.Start(new ProcessStartInfo("python3", [Path.Combine(directory.FullName, "run.py")]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
      var output = python.StandardOutput.ReadToEnd();
      var error = python.StandardError.ReadToEnd();
      python.WaitForExit();
      Assert.That(python.ExitCode, Is.EqualTo(0), error);
      Assert.That(output.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(_table.Select(row => row.Matches)), error);
    } finally {
      directory.Delete(true);
    }
  }

  [TestCaseSource(typeof(RustProfile), nameof(RustProfile.All))]
  public void Rust_WritesNfaTablesInEveryProfile(RustProfile profile) {
    foreach (var policy in new[] { RustTrapPolicy.Panic, RustTrapPolicy.Result }) {
      var configured = profile.WithTrapPolicy(policy);
      var module = TableModule();
      Assert.That(RustCrates.Check(module, configured), Is.Empty);
      var files = RustCrates.Emit(module, configured);
      var text = RustCrates.ModuleText(files);
      Assert.That(text, Does.Contain("fn ply_regex_test("));
      Assert.That(text, Does.Contain("static PLY_REGEX_OPS_1:"));
      Assert.That(text, Does.Contain("static PLY_REGEX_RANGES_1:"));
      Assert.That(text, Does.Contain("&[]"));
      Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
    }
  }

  [Test]
  public void Rust_DeduplicatesPatternsAndRefusesOversizedRegexByName() {
    var duplicate = new IrModule("M", [
      new IrFunction("F", true, IrType.Bool, [_s], [], new IrBlock([
        new IrReturn(new IrBinary(IrBinaryOp.LogicalAnd, RegexOperations.Test(new IrLocalRef(_s), "a", ""), RegexOperations.Test(new IrLocalRef(_s), "a", "")))]))
    ]);
    var text = RustCrates.ModuleText(RustCrates.Emit(duplicate));
    Assert.That(text.Split("PLY_REGEX_OPS_").Length - 1, Is.EqualTo(3), text);

    var huge = new IrModule("Huge", [
      new IrFunction("F", true, IrType.Bool, [_s], [], new IrBlock([new IrReturn(RegexOperations.Test(new IrLocalRef(_s), "a{5000}", ""))]))
    ]);
    var problems = RustCrates.Check(huge);
    Assert.That(problems, Has.Some.Matches<Polypiler.Core.Diagnostic>(diagnostic => diagnostic.Code == RustEmitter.Inexpressible && diagnostic.Message.Contains("a regular expression with too many states", StringComparison.Ordinal)));
  }

  [Test]
  [Category("EndToEnd")]
  public void Rust_RunsTheTableAsNodeDoes() {
    var directory = Directory.CreateTempSubdirectory("regex-rs-test-");
    try {
      var files = RustCrates.Emit(TableModule());
      foreach (var file in files) {
        var full = Path.Combine(directory.FullName, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, file.Content);
      }

      var subjectLiterals = string.Join(",\n    ", _subjects.Select(subject => $"&[{string.Join(", ", subject.Select(unit => $"{(ushort)unit}u16"))}]"));
      var assertions = string.Join("\n    ", _table.Select((row, index) =>
        $"assert_eq!(subjects.iter().map(|s| if table::table::t{index}(s) {{ '1' }} else {{ '0' }}).collect::<String>(), \"{row.Matches}\", \"row {index}\");"));
      Directory.CreateDirectory(Path.Combine(directory.FullName, "tests"));
      File.WriteAllText(Path.Combine(directory.FullName, "tests", "regex_table.rs"),
        $"#[test]\nfn table_matches_node() {{\n    let subjects: &[&[u16]] = &[\n    {subjectLiterals}\n    ];\n    {assertions}\n}}\n");

      using var cargo = Process.Start(new ProcessStartInfo("cargo", ["test", "--offline"]) {
        WorkingDirectory = directory.FullName, RedirectStandardOutput = true, RedirectStandardError = true
      })!;
      var stdout = cargo.StandardOutput.ReadToEnd();
      var stderr = cargo.StandardError.ReadToEnd();
      cargo.WaitForExit();
      Assert.That(cargo.ExitCode, Is.EqualTo(0), stdout + "\n" + stderr);
    } finally {
      directory.Delete(true);
    }
  }

}
