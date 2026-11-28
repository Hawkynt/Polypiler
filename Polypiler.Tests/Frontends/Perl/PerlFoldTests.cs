using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>
/// The constant folding of the Perl frontend (#1561), which perl does while it compiles and the
/// lowering now does itself: each expression is the value of a sub whose result the interpreter gives.
/// </summary>
[TestFixture]
public sealed class PerlFoldTests {

  private static object? Value(string expression, string type, string prelude = "") {
    var result = PerlFrontend.Import("fold.pl", $"{prelude}\nsub f {{ return {expression}; }}\n", new PerlImportOptions { Contracts = $"f.return: {type}" });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    var body = result.Module!.Functions.Single(fn => fn.Name == "f").Body.Statements;
    Assert.That(body[0], Is.InstanceOf<IrReturn>());
    Assert.That(IrTree.Expressions(body[0]).Any(e => e is IrBinary or IrIntrinsic or IrUnary), Is.False, "folded into a constant");
    return IrInterpreter.Call(result.Module, "f", []);
  }

  [TestCase("1 + 2 * 3", 7L)]
  [TestCase("10 - 2 - 3", 5L)]
  [TestCase("9223372036854775807 + 0", long.MaxValue)]
  [TestCase("6 / 3", 2L)]
  [TestCase("7 % 3", 1L)]
  [TestCase("-7 % 3", 2L)]
  [TestCase("7 % -3", -2L)]
  [TestCase("2 ** 10", 1024L)]
  [TestCase("6 & 3", 2L)]
  [TestCase("6 | 3", 7L)]
  [TestCase("6 ^ 3", 5L)]
  [TestCase("1 << 4", 16L)]
  [TestCase("256 >> 4", 16L)]
  [TestCase("-5", -5L)]
  [TestCase("- -5", 5L)]
  [TestCase("1 <=> 2", -1L)]
  [TestCase("2.5 <=> 1", 1L)]
  [TestCase("'a' cmp 'b'", -1L)]
  [TestCase("length 'abc'", 3L)]
  [TestCase("ord 'A'", 65L)]
  [TestCase("ord ''", 0L)]
  [TestCase("abs(-4)", 4L)]
  [TestCase("int(7)", 7L)]
  [TestCase("int(-7.9)", -7L)]
  [TestCase("0 ? 3 : 4", 4L)]
  [TestCase("1 && 5", 5L)]
  [TestCase("0 && 5", 0L)]
  [TestCase("0 || 6", 6L)]
  [TestCase("7 || 6", 7L)]
  [TestCase("8 // 6", 8L)]
  [TestCase("undef // 6", 6L)]
  [TestCase("A + B", 3L, "use constant { A => 1, B => 2 };")]
  [TestCase("C * 2", 14L, "sub C () { 7 }")]
  [TestCase("ord ''", 0L)]
  [TestCase("length 'abc'", 3L)]
  [TestCase("-9223372036854775808", long.MinValue)]
  [TestCase("int(9223372036854775807)", long.MaxValue)]
  [TestCase("+5", 5L)]
  public void Integers_FoldAsPerlFoldsThem(string expression, long expected, string prelude = "")
    => Assert.That(System.Convert.ToInt64(Value(expression, "i64", prelude)), Is.EqualTo(expected));

  [TestCase("9223372036854775807 + 1", 9223372036854775808UL)]
  [TestCase("~0", ulong.MaxValue)]
  [TestCase("18446744073709551615 & 1", 1UL)]
  [TestCase("2 ** 63", 9223372036854775808UL)]
  [TestCase("-9223372036854775808 / -1", 9223372036854775808UL)]
  [TestCase("- -9223372036854775808", 9223372036854775808UL)]
  [TestCase("18446744073709551615 - 1", 18446744073709551614UL)]
  public void Unsigned_FoldsToUVs(string expression, ulong expected)
    => Assert.That(System.Convert.ToUInt64(Value(expression, "u64")), Is.EqualTo(expected));

  [TestCase("1 / 4", 0.25)]
  [TestCase("1.5 + 2", 3.5)]
  [TestCase("1.5 * 2 - 0.5", 2.5)]
  [TestCase("2 ** -1", 0.5)]
  [TestCase("2 ** 70", 1180591620717411303424.0)]
  [TestCase("abs(-1.5)", 1.5)]
  [TestCase("-1.5", -1.5)]
  [TestCase("18446744073709551615 * 2", 36893488147419103230.0)]
  [TestCase("18446744073709551615 + 1", 18446744073709551616.0)]
  [TestCase("-9223372036854775808 - 1", -9223372036854775809.0)]
  [TestCase("3 ** 41", 36472996377170786403.0)]
  public void Floats_FoldToNVs(string expression, double expected)
    => Assert.That(System.Convert.ToDouble(Value(expression, "f64")), Is.EqualTo(expected));

  [TestCase("'a' . 'b'", "ab")]
  [TestCase("'a' . 1 . 2", "a12")]
  [TestCase("1 . 2", "12")]
  [TestCase("'ab' x 3", "ababab")]
  [TestCase("'ab' x 0", "")]
  [TestCase("'ab' x -1", "")]
  [TestCase("chr 65", "A")]
  [TestCase("lc 'AB'", "ab")]
  [TestCase("uc 'ab'", "AB")]
  [TestCase("lcfirst 'AB'", "aB")]
  [TestCase("ucfirst 'ab'", "Ab")]
  [TestCase("ucfirst ''", "")]
  [TestCase("lcfirst ''", "")]
  [TestCase("-'foo'", "-foo")]
  [TestCase("\"x\" . NAME", "xN", "use constant NAME => 'N';")]
  [TestCase("'v' . 1.5", "v1.5")]
  [TestCase("18446744073709551615 . ''", "18446744073709551615")]
  [TestCase("-'_x'", "-_x")]
  public void Strings_FoldAsPerlFoldsThem(string expression, string expected, string prelude = "")
    => Assert.That(Value(expression, "str", prelude), Is.EqualTo(expected));

  [TestCase("1 == 1", true)]
  [TestCase("1 != 1", false)]
  [TestCase("1 < 2", true)]
  [TestCase("2 <= 1", false)]
  [TestCase("1.5 > 1", true)]
  [TestCase("1 >= 2", false)]
  [TestCase("'a' eq 'a'", true)]
  [TestCase("'a' ne 'a'", false)]
  [TestCase("'a' lt 'b'", true)]
  [TestCase("'a' gt 'b'", false)]
  [TestCase("'a' le 'a'", true)]
  [TestCase("'b' ge 'c'", false)]
  [TestCase("!1", false)]
  [TestCase("!0", true)]
  [TestCase("not 0", true)]
  [TestCase("!''", true)]
  [TestCase("!'0'", true)]
  [TestCase("!'x'", false)]
  [TestCase("!0.0", true)]
  [TestCase("!!(1 == 1)", true)]
  public void Comparisons_FoldToBooleans(string expression, bool expected)
    => Assert.That(Value(expression, "bool"), Is.EqualTo(expected));

  [TestCase("use integer; my $x = 7 / 2; return $x;", 3L)]
  [TestCase("use integer; my $x = -7 % 3; return $x;", -1L)]
  [TestCase("use integer; my $x = 9223372036854775807 + 1; return $x;", long.MinValue)]
  [TestCase("use integer; my $x = 1 << 2; return $x;", 4L)]
  [TestCase("use integer; my $x = -8 >> 1; return $x;", -4L)]
  [TestCase("use integer; my $x = ~0; return $x;", -1L)]
  [TestCase("use integer; my $x = 6 & 3; return $x;", 2L)]
  public void UseInteger_FoldsWithIVs(string body, long expected) {
    var result = PerlFrontend.Import("fold.pl", $"sub f {{ {body} }}\n", new PerlImportOptions { Contracts = "f.return: i64" });
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    Assert.That(System.Convert.ToInt64(IrInterpreter.Call(result.Module!, "f", [])), Is.EqualTo(expected));
  }

  [TestCase("1 / 0")]
  [TestCase("1 % 0")]
  [TestCase("1 << -1")]
  [TestCase("'a' x 'b'")]
  [TestCase("chr -1")]
  [TestCase("lc 'Ä'")]
  [TestCase("1.5 % 2")]
  [TestCase("1.5 & 1")]
  [TestCase("-'1'")]
  [TestCase("~1.5")]
  [TestCase("'a' < 1")]
  [TestCase("'a' eq 1")]
  [TestCase("abs('x')")]
  [TestCase("int('x')")]
  [TestCase("ord 1")]
  [TestCase("length 1")]
  public void WhatPerlLeavesToRunTime_IsNotFolded(string expression) {
    var result = PerlFrontend.Import("fold.pl", $"sub f {{ my ($x) = @_; my $r = {expression}; return 1; }}\n");
    var body = result.Module?.Functions.Single(fn => fn.Name == "f").Body.Statements;
    Assert.That(result.Module is null || body!.OfType<IrDeclare>().Any(d => d.Initializer is not (IrConst or IrStrConst)), Is.True, string.Join("\n", result.Diagnostics));
  }

}
