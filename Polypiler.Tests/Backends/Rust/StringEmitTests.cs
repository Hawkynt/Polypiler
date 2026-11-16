using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Strings as their UTF-16 code units (<c>Vec&lt;u16&gt;</c> owned, <c>&amp;[u16]</c> borrowed): each
/// string operation of the IR as a slice method or a helper of the representation, every helper read
/// back from its text.
/// </summary>
[TestFixture]
public sealed class StringEmitTests {

  private static string Emitted(string methods) => RustCrates.ModuleText(RustCrates.Emit(RustCrates.Import(methods), level: OptimizationLevel.None));

  private static string[] Problems(string methods) => [.. RustCrates.Check(RustCrates.Import(methods)).Select(problem => problem.Message)];

  [Test]
  public void Parameter_IsBorrowed_ResultOwned_LiteralItsCodeUnits() {
    var text = Emitted("public static string F(string s) => s + \"!\";");
    Assert.That(text, Does.Contain("pub fn f(s: &[u16]) -> Vec<u16> {\n    [s, &ply_utf16(\"!\")[..]].concat()\n}"));
    Assert.That(text, Does.Contain("fn ply_utf16(text: &str) -> Vec<u16> {\n    text.encode_utf16().collect()\n}"));
  }

  [Test]
  public void LengthIndexAndComparisons_AreSliceMethods() {
    var text = Emitted("public static int F(string s, string t) => s.Length + s[1] + (s == t ? 1 : 0) + (string.CompareOrdinal(s, t) < 0 ? 8 : 0) + (s.StartsWith(\"a\", System.StringComparison.Ordinal) ? 2 : 0) + (s.EndsWith(t, System.StringComparison.Ordinal) ? 4 : 0);");
    Assert.That(text, Does.Contain("s.len() as i32"));
    Assert.That(text, Does.Contain("s[1]"));
    Assert.That(text, Does.Contain("s == t"));
    Assert.That(text, Does.Contain("(s.cmp(t) as i32) < 0"));
    Assert.That(text, Does.Contain("s.starts_with(&ply_utf16(\"a\"))"));
    Assert.That(text, Does.Contain("s.ends_with(t)"));
  }

  [Test]
  public void Searches_ReplacementsAndSplits_AreHelpers() {
    var text = Emitted("public static int F(string s) => s.IndexOf(',') + s.IndexOf(\"ab\", 1, System.StringComparison.Ordinal) + s.LastIndexOf(',') + (s.Contains(\"q\") ? 1 : 0) + s.Replace(\"a\", \"b\").Length + s.Split(',').Length;");
    Assert.That(text, Does.Contain("ply_index_of(s, &[44], 0)"));
    Assert.That(text, Does.Contain("ply_index_of(s, &ply_utf16(\"ab\"), 1)"));
    Assert.That(text, Does.Contain("ply_last_index_of(s, &[44], s.len() as i32)"));
    Assert.That(text, Does.Contain("ply_index_of(s, &ply_utf16(\"q\"), 0) >= 0"));
    Assert.That(text, Does.Contain("ply_replace(s, &ply_utf16(\"a\"), &ply_utf16(\"b\"))"));
    Assert.That(text, Does.Contain("ply_split(s, &[44], -1)"));
    Assert.That(text, Does.Contain("fn ply_position(position: i32, size: usize) -> usize {"));
  }

  [Test]
  public void TrimsPadsRepeatsAndParts_AreHelpersOrSliceMethods() {
    var text = Emitted("public static string F(string s, char c, int n) => s.Trim() + s.TrimStart() + s.TrimEnd() + s.PadLeft(5, '.') + s.PadRight(n) + new string(c, n) + s.Substring(1) + s.Substring(1, 2);");
    Assert.That(text, Does.Contain("ply_trim(s, true, true, &PLY_WHITESPACE_DOTNET)"));
    Assert.That(text, Does.Contain("ply_trim(s, true, false, &PLY_WHITESPACE_DOTNET)"));
    Assert.That(text, Does.Contain("ply_trim(s, false, true, &PLY_WHITESPACE_DOTNET)"));
    Assert.That(text, Does.Contain("ply_pad(s, 5, &[46], true)"));
    Assert.That(text, Does.Contain("ply_pad(s, n, &ply_utf16(\" \"), false)"));
    Assert.That(text, Does.Contain("vec![c; usize::try_from(n).expect(\"IntegerOverflow\")]"));
    Assert.That(text, Does.Contain("s[ply_range(1, (s.len() as i32).wrapping_sub(1), s.len())].to_vec()"));
    Assert.That(text, Does.Contain("s[ply_range(1, 2, s.len())].to_vec()"));
    Assert.That(text, Does.Contain("static PLY_WHITESPACE_DOTNET: [u16; 25] = ["));
  }

  [Test]
  public void JoinsUnitsAndAppends() {
    var text = Emitted("public static string F(string[] parts, char[] units, string t) { var s = string.Join(\";\", parts) + new string(units) + new string(units, 1, 2); s += 'x'; s += t; return s; } public static char[] G(string s) => s.ToCharArray();");
    Assert.That(text, Does.Contain("parts.join(&ply_utf16(\";\")[..])"));
    Assert.That(text, Does.Contain("units.to_vec()"));
    Assert.That(text, Does.Contain("units[ply_range(1, 2, units.len())].to_vec()"));
    Assert.That(text, Does.Contain("s.push(120);"));
    Assert.That(text, Does.Contain("s.extend_from_slice(t);"));
    Assert.That(text, Does.Contain("pub fn g(s: &[u16]) -> Vec<u16> {\n    s.to_vec()\n}"));
  }

  [Test]
  public void StringVariables_OwnTheirValue() {
    var text = Emitted("public static string F(string s, string[] a, bool c) { var t = s; var u = a[0]; var v = c ? t : u; s = v; return s; }");
    Assert.That(text, Does.Contain("let mut s: Vec<u16> = s.to_vec();"));
    Assert.That(text, Does.Contain("let t: Vec<u16> = s.clone();").Or.Contain("let t: Vec<u16> = s.to_vec();"));
    Assert.That(text, Does.Contain("let u: Vec<u16> = a[0].clone();"));
    Assert.That(text, Does.Contain("if c { t.clone() } else { u.clone() }"));
  }

  [Test]
  public void TablesOfStrings_AreTablesOfStr() {
    var text = Emitted("private static readonly string[] Names = { \"add\", \"sub\" }; private const string Digits = \"01\"; public static int F(int i, string w) => Names[i] == w ? Digits.Length : 0;");
    Assert.That(text, Does.Contain("static NAMES: [&str; 2] = [\"add\", \"sub\"];"));
    Assert.That(text, Does.Contain("ply_utf16(NAMES[i as usize]) == w"));
  }

  [Test]
  public void CodePoints_AndUnpairedSurrogates_AreRefusedByName() {
    var s = new IrLocal("s", IrType.String);
    IrModule Module(IrExpr value) => new("Snippet", [new IrFunction("F", true, value.Type, [s], [], new IrBlock([new IrReturn(value)]))]);
    var codePoint = new IrIntrinsic(IrIntrinsicOp.StrCodePointAt, IrType.I32, [new IrLocalRef(s), new IrConst(IrType.I32, 0)]);
    Assert.That(RustEmitter.Check(Module(codePoint), RustProfile.Default).Select(problem => problem.Message), Has.Some.Contain("'StrCodePointAt'"));
    Assert.That(RustEmitter.Check(Module(new IrStrConst("a\uD800")), RustProfile.Default).Select(problem => problem.Message), Has.Some.Contain("'a string with an unpaired surrogate'"));
  }

  [Test]
  public void StringLiterals_EscapeWhatRustCannotHoldBareAndReadBack() {
    var function = new RsFunction("f", "", [], null, new RsBlock([new RsExpressionStatement(new RsMacro("panic", [new RsString("a\"b\\c\nd\re\tf\u0001g\u007fh")]))], null));
    var text = RustPrinter.Print(function);
    Assert.That(text, Does.Contain("panic!(\"a\\\"b\\\\c\\nd\\re\\tf\\u{1}g\\u{7f}h\")"));
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
    Assert.That(((RsString)((RsMacro)((RsExpressionStatement)((RsFunction)RustSyntaxReader.Read(text + "fn g() {\n    panic!(\"\\0\");\n}\n").Items[0]).Body.Statements[0]).Expression).Arguments[0]).Value,
      Is.EqualTo("a\"b\\c\nd\re\tf\u0001g\u007fh"));
  }

}
