using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>
/// The quote-likes of the Perl parser (#1561): escapes, the forms of interpolation, subscripts and
/// quantifiers after a variable in a pattern, delimiters and the modifiers of patterns and
/// transliterations - every verdict as <c>perl -c</c> 5.42 gives it.
/// </summary>
[TestFixture]
public sealed class PerlQuoteTests {

  private static PerlParseResult Parse(string source) => PerlFrontend.Parse("quotes.pl", source);

  [TestCase("my $s = \"a\\Ub\\Ec\\LD\\Ee\";")]
  [TestCase("my $s = \"a\\Qb.c\\E\";")]
  [TestCase("my $s = \"\\o{12}\\08\\777\\1234\";")]
  [TestCase("my $s = \"\\x{}\\x\\x4\";")]
  [TestCase("my $s = \"\\c[\\c?\\cA\";")]
  [TestCase("our $a; my $s = \"$a::b $a:: $a::\";")]
  [TestCase("my $a; my $r = qr/$a$/;")]
  [TestCase("my $s = \"$1 $10 ${^NAME} $^W $^\";")]
  [TestCase("my $s = \"${^WARNING_BITS}\";")]
  [TestCase("my $s = \"${ 1; 2 }\";")]
  [TestCase("my $s = \"@{ 1; [2] }\";")]
  [TestCase("my @x; my $s = \"${$x[0]}\";")]
  [TestCase("my @x; my $s = \"@{$x[0]}\";")]
  [TestCase("my $s = \"$_ $+ $-[0] $+{a} $-{a}\";")]
  [TestCase("my $x; use v5.24; my $s = \"$x->@* $x->$* $x->%*\";")]
  [TestCase("my $x; my $s = \"$x->@*\";")]
  [TestCase("my $x; my $s = \"$x->[0] $x->{a} $x->foo $x->[0][1]{a}\";")]
  [TestCase("my %x; my $s = \"$x{-a} $x{ a } $x{'a'}\";")]
  [TestCase("my $s = \"@{^CAPTURE}\";")]
  [TestCase("my @x; my $s = \"@{x}[0] @x[0, 1] @x{a}\";")]
  [TestCase("my $x; my $s = \"$$x{a} $$x[0] $$x\";")]
  [TestCase("my $x; my $s = \"${x}[0] ${x}{a}\";")]
  [TestCase("my $x; my $s = \"$x\\[0] $x\\{a}\";")]
  [TestCase("my @x; my $r = qr/$x[0-9]/;")]
  [TestCase("my @x; my $r = qr/$x[$1]/;")]
  [TestCase("my @x; my $r = qr/$x[-1]/;")]
  [TestCase("my %x; my $r = qr/$x{3}/;")]
  [TestCase("my %x; my $r = qr/$x{3,}/;")]
  [TestCase("my %x; my $r = qr/$x{a}/;")]
  [TestCase("my $s = \"a${\\ join ',', 1}b\";")]
  [TestCase("my $s = \"@{[ map { $_ } 1 ]}\";")]
  [TestCase("my $s = \"${ \\\"a\\\" }\";")]
  [TestCase("my $s = \"@\";")]
  [TestCase("my $s = \"x$ y\";")]
  [TestCase("my $s = \"x@ y\";")]
  [TestCase("my $s = \"a\\N{U+41}\";")]
  [TestCase("my $s = \"\\x{263A}\\x{zz}\";")]
  [TestCase("my $x; my $s = \"$x::\";")]
  [TestCase("my $x; my $s = \"@x::y\";")]
  [TestCase("my $s = \"@::x\";")]
  [TestCase("my $s = \"$::x\";")]
  [TestCase("my $x = 'a\\\\b\\'c';")]
  [TestCase("my $x = q{a{b}c};")]
  [TestCase("my $x = q<a<b>c>;")]
  [TestCase("my $x = qq(a(b)c);")]
  [TestCase("my $x = q#a#;")]
  [TestCase("my $x = q x a x;")]
  [TestCase("my $x = qw/a b/;")]
  [TestCase("my @x = qw(a b) [0];")]
  [TestCase("my $x = 1; $x =~ y/a-z//d;")]
  [TestCase("my $x = 1; $x =~ tr/a-z/A-Z/r;")]
  [TestCase("my $x = 1; $x =~ s/a/b/e;")]
  [TestCase("my $x = 1; $x =~ s/a/$1/g;")]
  [TestCase("my $x = 1; $x =~ s{a} {b}g;")]
  [TestCase("my $x = 1; $x =~ s{a}/b/;")]
  [TestCase("my $x = 1; $x =~ m?a?;")]
  [TestCase("my $x = 1; $x =~ m'a$b';")]
  [TestCase("my $x = 1; $x =~ /a/xx;")]
  [TestCase("my $x = 1; $x =~ /a/aa;")]
  [TestCase("my $x = 1; $x =~ s/a/b/rr;")]
  [TestCase("my $x = 1; $x =~ tr/a/b/cc;")]
  [TestCase("my $x = 1; $x =~ /a/xxx;")]
  [TestCase("my %x; my $r = qr/$x{ 2 , 3 }/;")]
  [TestCase("my %x; my $r = qr/$x{2,a}/;")]
  [TestCase("my %x; my $r = qr/$x{ ,3}/;")]
  [TestCase("my @x; my $r = qr/$x[^a/;")]
  [TestCase("my @x; my $r = qr/$x[]/;")]
  [TestCase("my $x = 1; $x =~ m?a?g;")]
  public void ValidQuotes(string source) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Valid), string.Join("\n", result.Diagnostics));
  }

  [TestCase("my $s = \"\\o{}\";")]
  [TestCase("my $a; my $r = qr/$a{$/;")]
  [TestCase("my @x; my $s = \"$x[1\";")]
  [TestCase("my @x; my $r = qr/$x[1/;")]
  [TestCase("my @x; my $s = \"$x[]\";")]
  [TestCase("my %x; my $s = \"$x{a\";")]
  [TestCase("my $s = \"$\";")]
  [TestCase("my $s = \"a\\N{U+zz}\";")]
  [TestCase("my $x = 1; $x =~ /a/q;")]
  [TestCase("my $x = 1; $x =~ tr/a/b/q;")]
  [TestCase("my $x = 1; $x =~ m/a/qx;")]
  [TestCase("my $x = 1; $x =~ /a/au;")]
  [TestCase("my $x = 1; $x =~ /a/aaa;")]
  [TestCase("my $x = 1; $x =~ /a/ll;")]
  [TestCase("my $x = 1; my $r = qr/a/g;")]
  [TestCase("my $s = \"\\o{81}\\o{ }\";")]
  [TestCase("my %x; my $r = qr/$x{}/;")]
  [TestCase("my %x; my $r = qr/$x{,}/;")]
  [TestCase("my %x; my $r = qr/$x{2/;")]
  [TestCase("my @x; my $r = qr/$x[/;")]
  public void InvalidQuotes(string source) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Invalid), string.Join("\n", result.Diagnostics));
  }

  [TestCase("my $x; my $s = \"@$x{a} @$x[0]\";")]
  [TestCase("my $s = \"a\\N{LATIN SMALL LETTER A}\";")]
  [TestCase("my $s = \"a\\N\";")]
  [TestCase("my $x = 1; $x =~ s/a/b/ee;")]
  public void UnsupportedQuotes(string source) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Unsupported), string.Join("\n", result.Diagnostics));
  }

}
