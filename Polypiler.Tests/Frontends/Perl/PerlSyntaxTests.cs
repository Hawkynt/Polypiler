using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Perl;

namespace Polypiler.Tests.Frontends.Perl;

/// <summary>
/// The lexer and parser of the Perl frontend (#1561): the term/operator decisions, numbers, quotes,
/// heredocs, interpolation, precedence and the operands of named unary and list operators (as the
/// shape of perl's op tree), and what a file declares (as its outline), with the verdicts perl gives.
/// </summary>
[TestFixture]
public sealed class PerlSyntaxTests {

  private static string Shape(string expression) => PerlFrontend.ExpressionShape(expression);

  private static PerlParseResult Parse(string source) => PerlFrontend.Parse("syntax.pl", source);

  private static string Outline(string source) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Valid), string.Join("\n", result.Diagnostics));
    return result.Outline;
  }

  // ---- precedence and associativity (perlop) ----

  [TestCase("$a + $b * $c", "(add $a (multiply $b $c))")]
  [TestCase("$a / $b / $c", "(divide (divide $a $b) $c)")]
  [TestCase("$a ** $b ** $c", "(pow $a (pow $b $c))")]
  [TestCase("-$a ** 2", "(negate (pow $a 2))")]
  [TestCase("2 ** -$a", "(pow 2 (negate $a))")]
  [TestCase("!$a || $b", "(or (not $a) $b)")]
  [TestCase("not $a || $b", "(not (or $a $b))")]
  [TestCase("not $a and $b", "(and (not $a) $b)")]
  [TestCase("$a or $b and $c", "(or $a (and $b $c))")]
  [TestCase("$a xor $b or $c", "(or (xor $a $b) $c)")]
  [TestCase("$a ? $b : $c ? $d : $e", "(cond_expr $a $b (cond_expr $c $d $e))")]
  [TestCase("$a ? $b = $c : $d", "(cond_expr $a (sassign $c $b) $d)")]
  [TestCase("$a ? $b : $c = $d", "(sassign $d (cond_expr $a $b $c))")]
  [TestCase("$x = $y = 3", "(sassign (sassign 3 $y) $x)")]
  [TestCase("$x += $y *= 2", "(add $x (multiply $y 2))")]
  [TestCase("$a & $b | $c ^ $d", "(bit_xor (bit_or (bit_and $a $b) $c) $d)")]
  [TestCase("$a << $b + $c", "(left_shift $a (add $b $c))")]
  [TestCase("$a < $b == $c", "(eq (lt $a $b) $c)")]
  [TestCase("$a lt $b eq $c", "(seq (slt $a $b) $c)")]
  [TestCase("$a <=> $b || $c cmp $d", "(or (ncmp $a $b) (scmp $c $d))")]
  [TestCase("$a // $b || $c", "(or (dor $a $b) $c)")]
  [TestCase("$a && $b || $c", "(or (and $a $b) $c)")]
  [TestCase("$a x 3 . $b", "(concat (repeat $a 3) $b)")]
  [TestCase("$a++ + ++$b", "(add (postinc $a) (preinc $b))")]
  [TestCase("$a-- - --$b", "(subtract (postdec $a) (predec $b))")]
  [TestCase("~$a & $b", "(bit_and (complement $a) $b)")]
  [TestCase("\\$a", "(srefgen $a)")]
  [TestCase("+$a", "$a")]
  [TestCase("$a =~ /x/ && $b !~ /y/", "(and (match $a) (not (match $b)))")]
  [TestCase("$a =~ s/x/y/", "(subst $a 'y')")]
  [TestCase("$a =~ $b", "(=~ $a $b)")]
  [TestCase("$a .. $b", "(.. $a $b)")]
  [TestCase("($a, $b)", "(list $a $b)")]
  public void Precedence_IsPerlops(string expression, string shape)
    => Assert.That(Shape(expression), Is.EqualTo(shape));

  [TestCase("ref $x eq 'HASH'", "(seq (ref $x) 'HASH')")]
  [TestCase("defined $a || $b", "(or (defined $a) $b)")]
  [TestCase("lc $a . $b", "(lc (concat $a $b))")]
  [TestCase("length $a < 3", "(lt (length $a) 3)")]
  [TestCase("-e $f && -d _", "(and (ftis $f) ftdir)")]
  [TestCase("-e -f $f", "(ftis (ftfile $f))")]
  [TestCase("-s", "ftsize")]
  [TestCase("ref($a) . $b", "(concat (ref $a) $b)")]
  [TestCase("time / 2", "(divide time 2)")]
  [TestCase("time() - $a", "(subtract time $a)")]
  [TestCase("shift // 0", "(dor shift 0)")]
  [TestCase("join ',', $a, $b or $c", "(or (join ',' $a $b) $c)")]
  [TestCase("push @a, $b, $c", "(push @a $b $c)")]
  [TestCase("split /,/, $s", "(split $s 0)")]
  [TestCase("split /,/, $s, 2", "(split $s 2)")]
  [TestCase("scalar(@a)", "@a")]
  [TestCase("wantarray ? 1 : 0", "(cond_expr wantarray 1 0)")]
  [TestCase("CORE::length $a", "(length $a)")]
  public void NamedUnaryAndListOperators_TakeTheirOperands(string expression, string shape)
    => Assert.That(Shape(expression), Is.EqualTo(shape));

  // ---- terms ----

  [TestCase("$h{a}", "(helem %h 'a')")]
  [TestCase("$h{-a}", "(helem %h '-a')")]
  [TestCase("$h{a b}", "(helem %h (method a 'b'))")]
  [TestCase("$h{$k}", "(helem %h $k)")]
  [TestCase("$a[0][1]", "(aelem (rv2av (aelem @a 0)) 1)")]
  [TestCase("$r->[0]{k}", "(helem (rv2hv (aelem (rv2av $r) 0)) 'k')")]
  [TestCase("$r->(1)(2)", "(call (call $r 1) 2)")]
  [TestCase("$$r[0]", "(aelem (rv2av $r) 0)")]
  [TestCase("${$r}{k}", "(helem (rv2hv $r) 'k')")]
  [TestCase("@{$r}[1, 2]", "(aslice (rv2av $r) (list 1 2))")]
  [TestCase("@h{'a', 'b'}", "(hslice %h (list 'a' 'b'))")]
  [TestCase("%h{'a'}", "(kvhslice %h 'a')")]
  [TestCase("%a[0]", "(kvaslice @a 0)")]
  [TestCase("$#a", "(av2arylen @a)")]
  [TestCase("$#{$r}", "(av2arylen (rv2av $r))")]
  [TestCase("$#$r", "(av2arylen (rv2av $r))")]
  [TestCase("$r->@*", "(rv2av $r)")]
  [TestCase("$r->$#*", "(av2arylen (rv2av $r))")]
  [TestCase("$r->%*", "(rv2hv $r)")]
  [TestCase("$r->@[0, 1]", "(aslice (rv2av $r) (list 0 1))")]
  [TestCase("$r->%{a}", "(kvhslice (rv2hv $r) 'a')")]
  [TestCase("&$f(1)", "(call (rv2cv $f) 1)")]
  [TestCase("&{$f}", "(call (rv2cv $f))")]
  [TestCase("&foo(1)", "(foo 1)")]
  [TestCase("&foo", "foo")]
  [TestCase("*STDOUT", "*STDOUT")]
  [TestCase("*{$g}", "(rv2gv $g)")]
  [TestCase("*foo{CODE}", "(helem *foo 'CODE')")]
  [TestCase("$obj->m(1)", "(method m $obj 1)")]
  [TestCase("$obj->$m", "(method $ $obj $m)")]
  [TestCase("$obj->SUPER::m()", "(method SUPER::m $obj)")]
  [TestCase("Foo::Bar->new", "(method new 'Foo::Bar')")]
  [TestCase("Foo::->new", "(method new 'Foo')")]
  [TestCase("[1, 2]", "(anonlist 1 2)")]
  [TestCase("[]", "anonlist")]
  [TestCase("{a => 1}", "(anonhash 'a' 1)")]
  [TestCase("sub { 1 }", "anoncode")]
  [TestCase("do { 1 }", "do")]
  [TestCase("eval { 1 }", "eval")]
  [TestCase("<STDIN>", "(readline 'STDIN')")]
  [TestCase("<$fh>", "(readline '$fh')")]
  [TestCase("<>", "(readline '')")]
  [TestCase("<<>>", "(readline '<<>>')")]
  [TestCase("<*.c>", "(glob '*.c')")]
  [TestCase("`ls`", "(backtick 'ls')")]
  [TestCase("qx'ls $x'", "(backtick 'ls $x')")]
  [TestCase("(1, 2)[1]", "(aslice (list 1 2) 1)")]
  [TestCase("qw(a b)[0]", "(aslice (qw 'a' 'b') 0)")]
  [TestCase("local $x", "(local $x)")]
  [TestCase("my ($a, @b)", "(my $a @b)")]
  [TestCase("my Dog $spot", "(my $spot)")]
  [TestCase("__PACKAGE__", "'main'")]
  [TestCase("__LINE__", "1")]
  [TestCase("__FILE__", "'expression.pl'")]
  public void Terms_HaveTheirShape(string expression, string shape)
    => Assert.That(Shape(expression), Does.StartWith(shape));

  [Test]
  public void VersionStrings_AreTheirCharacters() {
    Assert.That(Shape("v5.36.0"), Is.EqualTo("'\u0005$\0'"));
    Assert.That(Shape("5.36.0"), Is.EqualTo("'\u0005$\0'"));
    Assert.That(Shape("v65"), Is.EqualTo("'A'"));
  }

  [Test]
  public void Escapes_HavePerlsValues() {
    Assert.That(Shape("\"\\x41\\x{263A}\\101\\o{102}\\cA\\e\\N{U+43}\""), Is.EqualTo("'A\u263aAB\u0001\u001bC'"));
    Assert.That(Shape("\"\\n\\r\\f\\b\\a\\0\\7\\x\\x{}\\x{zz}\\q\""), Is.EqualTo("'\n\r\f\b\a\0\u0007\0\0\0q'"));
    Assert.That(Shape("\"\\x{110000}\\x{D800}\""), Is.EqualTo("'\ufffd\ud800'"));
    Assert.That(Shape("\"\\o{19}\""), Is.EqualTo("'\u0001'"));
    Assert.That(Shape("\"caf\u00e9\""), Is.EqualTo("'caf\u00c3\u00a9'"), "a source character beyond ASCII is its UTF-8 bytes");
    Assert.That(Shape("'caf\u00e9'"), Is.EqualTo("'caf\u00c3\u00a9'"));
    Assert.That(Shape("/caf\u00e9\\d/"), Is.EqualTo("match"));
  }

  // ---- numbers ----

  [TestCase("1_000", "1000")]
  [TestCase("0x1F", "31")]
  [TestCase("0b101", "5")]
  [TestCase("0o17", "15")]
  [TestCase("017", "15")]
  [TestCase("1.5", "1.5")]
  [TestCase(".5", "0.5")]
  [TestCase("1.", "1")]
  [TestCase("1e3", "1000")]
  [TestCase("1E-2", "0.01")]
  [TestCase("0x1p4", "16")]
  [TestCase("0x1.8p1", "3")]
  [TestCase("01p3", "8")]
  [TestCase("0b1.1p1", "3")]
  [TestCase("18446744073709551615", "18446744073709551615")]
  [TestCase("18446744073709551616", "1.8446744073709552E+19")]
  [TestCase("0xFFFFFFFFFFFFFFFFF", "2.9514790517935283E+20")]
  [TestCase("0", "0")]
  public void Numbers_HavePerlsValue(string literal, string value)
    => Assert.That(Shape(literal), Is.EqualTo(value));

  [TestCase("1..3", "(.. 1 3)")]
  [TestCase("0x10..0x20", "(.. 16 32)")]
  [TestCase("08", "error")]
  [TestCase("0x", "error")]
  [TestCase("0b12", "error")]
  public void NumberEdges(string expression, string shape)
    => Assert.That(Shape(expression), Does.StartWith(shape));

  // ---- quotes ----

  [TestCase("'it\\'s'", "'it's'")]
  [TestCase("'a\\\\b'", "'a\\b'")]
  [TestCase("'a\\nb'", "'a\\nb'")]
  [TestCase("q{a{b}c}", "'a{b}c'")]
  [TestCase("q(a\\)b)", "'a)b'")]
  [TestCase("q#x#", "'x'")]
  [TestCase("q xyx", "'y'")]
  [TestCase("q #comment\n(x)", "'x'")]
  [TestCase("qq{a}", "'a'")]
  [TestCase("\"a\\tb\"", "'a\tb'")]
  [TestCase("\"\\Uab\\E\"", "(interp (uc 'ab'))")]
  [TestCase("\"\\Qa.\"", "(interp (quotemeta 'a.'))")]
  [TestCase("\"\\uab\"", "'Ab'")]
  [TestCase("\"\\lAB\"", "'aB'")]
  [TestCase("\"\\u$a\"", "(interp (ucfirst $a))")]
  [TestCase("\"$a\"", "(interp $a)")]
  [TestCase("\"a$b c\"", "(interp 'a' $b ' c')")]
  [TestCase("\"${a}b\"", "(interp $a 'b')")]
  [TestCase("\"$h{k}\"", "(interp (helem %h 'k'))")]
  [TestCase("\"$a[1]\"", "(interp (aelem @a 1))")]
  [TestCase("\"$r->[0]{k}\"", "(interp (helem (rv2hv (aelem (rv2av $r) 0)) 'k'))")]
  [TestCase("\"$r->method\"", "(interp $r '->method')")]
  [TestCase("\"@a\"", "(interp (join $\" @a))")]
  [TestCase("\"@a[1,2]\"", "(interp (join $\" (aslice @a (list 1 2))))")]
  [TestCase("\"@{[ 1 + 2 ]}\"", "(interp (join $\" (rv2av (anonlist (add 1 2)))))")]
  [TestCase("\"${\\ $x}\"", "(interp (rv2sv (srefgen $x)))")]
  [TestCase("\"$$r\"", "(interp (rv2sv $r))")]
  [TestCase("\"$$r[0]\"", "(interp (aelem (rv2av $r) 0))")]
  [TestCase("\"$::x\"", "(interp $main::x)")]
  [TestCase("\"$1 $& $_\"", "(interp $1 ' ' $& ' ' $_)")]
  [TestCase("\"$^W\"", "(interp $^W)")]
  [TestCase("\"user@example\"", "(interp 'user' (join $\" @example))")]
  [TestCase("\"a@\"", "'a@'")]
  [TestCase("\"a $ b\"", "'a $ b'")]
  [TestCase("\"a$\"", "error")]
  [TestCase("qw(a b  c)", "(qw 'a' 'b' 'c')")]
  [TestCase("qw/a\\/b/", "(qw 'a/b')")]
  public void Quotes_HavePerlsValue(string literal, string value)
    => Assert.That(Shape(literal), Does.StartWith(value));

  [TestCase("m/a$/", "match")]
  [TestCase("/a$x[1]/", "match")]
  [TestCase("qr{a}i", "qr")]
  [TestCase("s{a}{b}g", "(subst 'b')")]
  [TestCase("s{a} /b/", "(subst 'b')")]
  [TestCase("s/a/$1/e", "subst")]
  [TestCase("tr/a-z/A-Z/", "tr")]
  [TestCase("y/a//d", "tr")]
  [TestCase("s\\a\\b\\", "(subst 'b')")]
  [TestCase("m'$x'", "match")]
  [TestCase("s/x/y/ee", "error: Perl construct outside the parser: s///ee")]
  [TestCase("m/(?{ 1 })/", "error: Perl construct outside the parser: a code block")]
  public void Patterns_AreScannedWithTheirDelimiters(string literal, string shape)
    => Assert.That(Shape(literal), Does.StartWith(shape));

  [TestCase("'abc", "Can't find string terminator")]
  [TestCase("s{a}{b", "Can't find string terminator")]
  [TestCase("\"\\x{41\"", "Missing right brace on \\x{}")]
  [TestCase("\"\\o{1\"", "Missing right brace on \\o{}")]
  [TestCase("\"\\N{U+41\"", "Missing right brace on \\N{}")]
  [TestCase("\"\\c\"", "Missing control char name")]
  [TestCase("\"${a\"", "Missing right curly")]
  [TestCase("\"@{a\"", "Missing right curly")]
  [TestCase("\"$h{a\"", "Missing right curly or square bracket")]
  [TestCase("\"$a[1\"", "Missing right curly or square bracket")]
  public void UnterminatedQuotes_AreSyntaxErrors(string literal, string message)
    => Assert.That(Shape(literal), Does.StartWith("error: Perl syntax error").And.Contain(message));

  [TestCase("s{a}", "Substitution replacement not terminated")]
  [TestCase("tr{a}", "Transliteration replacement not terminated")]
  public void AMissingSecondPart_IsASyntaxError(string source, string message)
    => Assert.That(Parse(source).Diagnostics[0].Message, Does.Contain(message));

  [TestCase("\"\\N{LATIN SMALL LETTER A}\"", "the named character")]
  [TestCase("\"\\N\"", "\\N without braces")]
  [TestCase("\"$$$x[0]\"", "a subscripted dereference")]
  public void UndecidedQuotes_AreOutsideTheParser(string literal, string construct)
    => Assert.That(Shape(literal), Does.StartWith("error: Perl construct outside the parser").And.Contain(construct));

  // ---- heredocs ----

  [Test]
  public void Heredocs_OfEveryForm_HaveTheirBodies() {
    const string source = """
      my $a = <<"A" . <<'B' . <<C . <<\D . << "E";
      one $x
      A
      two $x
      B
      three
      C
      four
      D
      five
      E
      my $f = <<~F;
          indented
            more
          F
      my $g = <<`G`;
      ls
      G
      sub done { 1 }
      """;
    Assert.That(Outline(source), Is.EqualTo("main: my $a my $f my $g\nsub main::done:\n"));
  }

  [TestCase("my $a = <<X;\nbody\n", "Can't find string terminator \"X\"")]
  [TestCase("my $a = <<X;", "Can't find string terminator \"X\"")]
  [TestCase("my $a = <<~X;\n  a\n b\n  X\n", "Indentation on line 2 of here-doc")]
  [TestCase("my $a = <<~;\n", "Use of bare <<~")]
  [TestCase("my $a = <<\"X;\n", "Unterminated delimiter for here document")]
  public void BrokenHeredocs_AreSyntaxErrors(string source, string message) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Invalid));
    Assert.That(result.Diagnostics[0].Message, Does.Contain(message));
  }

  [Test]
  public void AHeredocEndingTheFile_AndAnIndentedOneWithBlankLines_AreRead() {
    Assert.That(Parse("my $a = <<X;\nbody\nX").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("my $a = <<~X;\n  a\n\n  b\n  X\n").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("print \"@{[ <<X ]}\";\n").Verdict, Is.EqualTo(PerlVerdict.Unsupported));
  }

  // ---- statements and the outline ----

  [Test]
  public void Lexicals_BecomeVisibleAfterTheirStatement() {
    // `my $x = $x` reads the outer $x; a foreach variable and a condition variable belong to their statement.
    const string source = """
      use strict;
      my $x = 1;
      { my $x = $x + 1; }
      for my $i (1 .. 3) { my $j = $i; }
      if ((my $c = 1) > 0) { $c++ } elsif ($c) { } else { }
      while (my $line = shift @ARGV) { last }
      until (0) { last }
      for (my $k = 0; $k < 3; $k++) { next }
      foreach (1, 2) { }
      for my ($p, $q) (1, 2) { }
      our @ISA;
      state $s;
      """;
    Assert.That(Outline("use feature 'state';\n" + source),
      Is.EqualTo("main: my $x my $x my $i my $j my $c my $line my $k my $p my $q our @ISA state $s\n"));
  }

  [Test]
  public void Subs_HaveTheirOwnPads_WithAnonymousSubsNestedWhereTheyEnd() {
    const string source = """
      package Foo;
      sub new { my ($class, %args) = @_; my $f = sub { my $y = 1; return $y; }; return $f; }
      sub Bar::baz;
      sub Bar::qux { 1 }
      sub CONST () { 42 }
      sub redefined { 1 }
      sub redefined { my $z; }
      package main;
      my @list = map { my $m = $_; $m } 1 .. 3;
      BEGIN { my $hidden = 1; }
      END { my $also_hidden = 1; }
      AUTOLOAD { 1 }
      DESTROY { 1 }
      """;
    Assert.That(Outline(source), Is.EqualTo(
      "main: my @list my $m\nsub Bar::qux:\nsub Foo::new: my $class my %args my $f { my $y }\nsub Foo::redefined: my $z\nsub main::AUTOLOAD:\nsub main::DESTROY:\n"));
  }

  [Test]
  public void Signatures_AndPrototypes_AndAttributes() {
    const string source = """
      use feature 'signatures';
      sub a ($x, $y = 2, @rest) { }
      sub b ($, $=, %o) { }
      sub c ($x //= 1, $y ||= 2, $z =) { }
      sub d :prototype($$) ($p, $q) { }
      sub e : lvalue method { }
      """;
    Assert.That(Outline(source), Is.EqualTo("main:\nsub main::a: my $x my $y my @rest\nsub main::b: my %o\nsub main::c: my $x my $y my $z\nsub main::d: my $p my $q\nsub main::e:\n"));
    Assert.That(Outline("sub f($$);\nsub f($$) { }\nf 1, 2;\n"), Is.EqualTo("main:\nsub main::f:\n"));
  }

  [Test]
  public void PackagesWithBlocks_TryCatch_Defer_AndLabels() {
    const string source = """
      use v5.40;
      use feature 'defer';
      package Foo 1.2 { sub a { } }
      package Bar { sub b { } }
      try { die "x" } catch ($e) { warn $e } finally { 1 }
      defer { 1 }
      OUTER: for my $i (1 .. 2) { next OUTER; last OUTER; redo; }
      LABEL: ;
      say "x";
      """;
    Assert.That(Outline("no warnings;\n" + source), Does.StartWith("main: state &true"));
  }

  [Test]
  public void PodAndTheEndMarker_AreNotCode() {
    const string source = "=pod\n\nmy $hidden;\n\n=cut\nmy $x;\n\n=head1 X\n\nmore\n=cut\nmy $y;\n__END__\nmy $after;\n";
    Assert.That(Outline(source), Is.EqualTo("main: my $x my $y\n"));
    Assert.That(Outline("my $x;\n__DATA__\n}{\n"), Is.EqualTo("main: my $x\n"));
    Assert.That(Outline("my $x;\n\x04 garbage"), Is.EqualTo("main: my $x\n"));
  }

  [TestCase("{ a => 1 }", "main:\n")]
  [TestCase("{ 'a', 1 }", "main:\n")]
  [TestCase("{ my $x }", "main: my $x\n")]
  [TestCase("{}", "main:\n")]
  [TestCase("my @x = map { $_ => 1 } 1;", "main: my @x\n")]
  [TestCase("my @x = map { +{ a => $_ } } 1;", "main: my @x\n")]
  [TestCase("my @x = map { ; my $y } 1;", "main: my @x my $y\n")]
  [TestCase("my @x = grep { $_ } 1;", "main: my @x\n")]
  [TestCase("my @x = map +($_ => 1), 1;", "main: my @x\n")]
  [TestCase("my @x = sort { $a <=> $b } 1;", "main: my @x\n")]
  [TestCase("my @x = sort by_num 1, 2;", "main: my @x\n")]
  [TestCase("my $c; my @x = sort $c 1, 2;", "main: my $c my @x\n")]
  [TestCase("my @x = map(ucfirst, 1);", "main: my @x\n")]
  public void BlocksAndHashes_AreTold(string source, string outline)
    => Assert.That(Outline(source), Is.EqualTo(outline));

  [TestCase("my @x = map {} 1;")]
  [TestCase("my @x = map { 'a' => 1 } 1;")]
  [TestCase("my @x = map { 'a', 1 } 1;")]
  [TestCase("my @x = map { q(a), 1 } 1;")]
  [TestCase("my @x = map { A, 1 } 1;")]
  public void MapWithAHash_IsAnExpression_WhichNeedsAComma(string source)
    => Assert.That(Parse(source).Verdict, Is.EqualTo(PerlVerdict.Invalid));

  [TestCase("print STDERR 'x';")]
  [TestCase("print STDERR;")]
  [TestCase("print {$fh} 'x';")]
  [TestCase("my $fh; print $fh 'x';")]
  [TestCase("my $fh; print $fh <STDIN>;")]
  [TestCase("my $fh; print $fh ('x');")]
  [TestCase("my $fh; printf $fh \"%s\", 1;")]
  [TestCase("my $x; print $x;")]
  [TestCase("my $x; print $x + 1, 2;")]
  [TestCase("my $x; print $x -1;")]
  [TestCase("print(STDOUT 'x');")]
  [TestCase("system { 'sh' } 'sh', '-c';")]
  [TestCase("open(FH, '<', 'x'); close FH; my $l = <FH>;")]
  [TestCase("my $x; print $x <<EOT;\nbody\nEOT\n")]
  public void FileHandles_OfPrintAndItsKin(string source)
    => Assert.That(Parse(source).Verdict, Is.EqualTo(PerlVerdict.Valid), string.Join("\n", Parse(source).Diagnostics));

  // ---- barewords, known subs and imports ----

  [TestCase("sub f { } f 1, 2;")]
  [TestCase("sub f() { 1 } my $x = f + 1;")]
  [TestCase("sub f($) { } my $x = f 1, 2;")]
  [TestCase("sub f(&@) { } f { 1 } 1, 2;")]
  [TestCase("sub f(&) { } f { 1 };")]
  [TestCase("use constant PI => 3; my $x = PI * 2;")]
  [TestCase("use constant { A => 1, B => 2 }; my $x = A | B;")]
  [TestCase("use constant DEBUG; my $x = DEBUG;")]
  [TestCase("use subs qw(f); f 1;")]
  [TestCase("use List::Util qw(first sum); my $x = first { $_ } 1, 2;")]
  [TestCase("use Scalar::Util 'blessed'; my $x = blessed $y && 1;")]
  [TestCase("use Test::More tests => 1; ok 1, 'x'; is 1, 1;")]
  [TestCase("use Carp; croak 'x' if 0;")]
  [TestCase("use Moose; has 'x' => (is => 'rw');")]
  [TestCase("my $x = Foo::Bar->new;")]
  [TestCase("my $x = new Foo(1);")]
  [TestCase("my $x = new Foo;")]
  [TestCase("my $c; my $x = new $c (1);")]
  [TestCase("my $o; method $o 'a', 'b';")]
  [TestCase("my $x = FOO;")]
  [TestCase("my $x = FOO - 1;")]
  [TestCase("my $x = foo / 2;")]
  [TestCase("my $x = -foo;")]
  [TestCase("my $x = -foo => 1;")]
  [TestCase("my %h = (q => 1, s => 2, y => 3, tr => 4, if => 5);")]
  [TestCase("my $x = $h{s} + $h{y} + $h{q};")]
  [TestCase("BEGIN { require './test.pl'; } plan tests => 2; ok 1; is 1, 1, 'x';")]
  [TestCase("BEGIN { chdir 't' if -d 't'; @INC = '../lib'; }")]
  [TestCase("Internals::SvREADONLY $x, 1;")]
  [TestCase("use builtin qw(true); my $x = true;")]
  [TestCase("my $x = CORE::GLOBAL::foo();")]
  [TestCase("my $x = Foo::bar(1);")]
  [TestCase("use List::Util; my $x = List::Util::reduce { $a + $b } 1, 2;")]
  [TestCase("use Carp; Carp::croak 'x';")]
  public void Calls_ParseByWhatIsKnownOfTheirSub(string source)
    => Assert.That(Parse(source).Verdict, Is.EqualTo(PerlVerdict.Valid), string.Join("\n", Parse(source).Diagnostics));

  [TestCase("use Foo::Bar; frob -1;", "whose parse depends")]
  [TestCase("use Foo::Bar; frob 'x';", "a call only if a module or BEGIN code defines frob")]
  [TestCase("use Foo::Bar qw(:all); frob 'x';", "a call only if")]
  [TestCase("BEGIN { require Foo; } frob 'x';", "a call only if")]
  [TestCase("use List::Util qw(other); other { 1 };", "whose prototype is not known")]
  [TestCase("Foo::bar 'x';", "a call only if")]
  [TestCase("frob { 1 };", "the bareword frob followed by '{'")]
  [TestCase("use Filter::Simple;", "changes the grammar")]
  [TestCase("use Syntax::Keyword::Try;", "changes the grammar")]
  [TestCase("format STDOUT =\n.\n", "format")]
  [TestCase("use feature 'class'; class Foo;", "the class feature")]
  [TestCase("use feature 'switch'; given (1) { }", "the switch feature")]
  [TestCase("CORE::given (1) { }", "the switch feature")]
  [TestCase("my sub f { }", "lexical subs")]
  [TestCase("my \\$x = \\1;", "declared references")]
  [TestCase("for \\my $x (1) { }", "refaliasing")]
  [TestCase("#!perl -n\nprint;\n", "command-line switches")]
  public void Undecided_IsRefusedByName(string source, string construct) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Unsupported));
    Assert.That(result.Diagnostics[0].Code, Is.EqualTo("PLY1701"));
    Assert.That(result.Diagnostics[0].Message, Does.Contain(construct));
  }

  [TestCase("my $x = ;", "expected a term")]
  [TestCase("{", "missing right curly bracket")]
  [TestCase("}", "unmatched right curly bracket")]
  [TestCase("if ($x) { 1 } elsif { 2 }", "expected '('")]
  [TestCase("my 1;", "a variable to declare")]
  [TestCase("my $Foo::x;", "Can't use global $Foo::x in \"my\"")]
  [TestCase("foo 'x';", "bareword \"foo\" followed by a term")]
  [TestCase("1 1;", "expected an operator or ';'")]
  [TestCase("$x = 1 .. 2 .. 3;", "expected an operator")]
  [TestCase("use 6;", "required--this is only v5.42")]
  [TestCase("use v5.99;", "required--this is only v5.42")]
  [TestCase("use feature 'nonesuch';", "Feature \"nonesuch\" is not supported")]
  [TestCase("use feature ':5.99x';", "is not supported by Perl 5.42")]
  [TestCase("use feature 'try'; try { 1 }", "try requires catch or finally")]
  [TestCase("$# = 1;", "$# is no longer supported")]
  [TestCase("my $x = \x01;", "Unrecognized character \\x01")]
  [TestCase("for my $Foo::x (1) { }", "a scalar loop variable")]
  [TestCase("use feature 'signatures'; sub f (1) { }", "a signature parameter")]
  [TestCase("CORE::nope();", "CORE::nope is not a keyword")]
  [TestCase("package;", "package name expected")]
  [TestCase("use;", "a module name or version")]
  [TestCase("my $x = sub;", "a sub body")]
  [TestCase("if 1;", "expected '('")]
  [TestCase("my $x = if;", "unexpected 'if'")]
  [TestCase("my $x = $obj->;", "a method name")]
  [TestCase("my $x = @ ;", "syntax error near \"@ \"")]
  [TestCase("my $x = (1;", "expected ')'")]
  [TestCase("my $x = 'a' . ;", "expected a term")]
  public void SyntaxErrors_ArePerlsRefusals(string source, string message) {
    var result = Parse(source);
    Assert.That(result.Verdict, Is.EqualTo(PerlVerdict.Invalid), string.Join("\n", result.Diagnostics));
    Assert.That(result.Diagnostics[0].Code, Is.EqualTo("PLY1700"));
    Assert.That(result.Diagnostics[0].Message, Does.Contain(message));
  }

  [Test]
  public void FeaturesAndPragmas_ChangeTheGrammarLexically() {
    Assert.That(Parse("say 'x';").Verdict, Is.EqualTo(PerlVerdict.Invalid), "say is a plain word without the feature");
    Assert.That(Parse("use feature 'say'; say 'x';").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("{ use feature 'say'; } say 'x';").Verdict, Is.EqualTo(PerlVerdict.Invalid), "a pragma ends with its block");
    Assert.That(Parse("use v5.10; say 'x'; no feature 'say'; my $x = 1;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use feature ':5.10'; say 'x';").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use feature ':all'; no feature ':all'; my $x;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use experimental 'signatures'; sub f ($x) { }").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("no feature; my $x;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use feature qw(postderef lexical_subs); my $x;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use 5.006; use 5.008_001; use 5; use v5.8.1; my $x;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Parse("use utf8; my $ünï = 1; my $x = $main'x;").Verdict, Is.EqualTo(PerlVerdict.Valid));
    Assert.That(Outline("use v5.42; my $x;"), Does.StartWith("main: state &true state &false"));
    Assert.That(Outline("use builtin ':5.40'; my $x;"), Does.StartWith("main: state &true"));
    Assert.That(Parse("use Foo 1.2 qw(a); use Foo (); no strict 'refs'; use lib '.'; use Foo 1.2;").Verdict, Is.EqualTo(PerlVerdict.Valid));
  }

  [TestCase("$x = $y;\n$ x = 1;")]
  [TestCase("$ {x} = 1;")]
  [TestCase("my @a = (1); my $n = $#a + $#{a} + $#$r + $#-;")]
  [TestCase("my $x = ${^WARNING_BITS} . $^W . $0 . $; . $$ . $; . @- . @+ . $#+;")]
  [TestCase("my $x = $::y + $main::z;")]
  [TestCase("my %h = %::; my $s = $'x;")]
  [TestCase("*X = *-; *Y = *+; *Z = *$ ;")]
  [TestCase("my @x = @{^CAPTURE}; my $n = $#{^CAPTURE};")]
  [TestCase("my $x = &$code; my $y = &{$code}(1);")]
  [TestCase("my $x = $x[0] ? $x{a} : @x{qw(a)};")]
  [TestCase("local $_ = 1; local ($a, $b); local $h{x}; local +($r // 0)->[0];")]
  [TestCase("my ($x, undef, $y) = @_;")]
  [TestCase("my ($x => $y) = @_;")]
  [TestCase("my (@a, , $b);")]
  [TestCase("our $VERSION = '1.0'; our ($A, $B);")]
  [TestCase("goto &f; goto LABEL; goto $x; goto sub { };")]
  [TestCase("last; next; redo; last LABEL; next(); CORE::dump;")]
  [TestCase("return; return 1, 2; return wantarray ? 1 : 2;")]
  [TestCase("do 'file.pl'; eval 'code'; eval; require Foo; require 5.006; require 'x.pl';")]
  [TestCase("my $x = defined &f; my $y = exists $h{a}; delete local $h{b}; undef $x; undef;")]
  [TestCase("my $x = -t STDIN; my $y = -s _;")]
  [TestCase("my $x = 1 x 3; my @y = (1) x 3; $x x= 2;")]
  [TestCase("use feature 'isa'; my $x = $o isa Foo; my $y = $o isa $class;")]
  [TestCase("my $x = $a ~~ $b; my $y = ~~$a;")]
  [TestCase("my $x = $a ^^ $b;")]
  [TestCase("$x ||= 1; $x //= 2; $x &&= 3; $x **= 2; $x .= 'a'; $x <<= 1; $x &.= 'a';")]
  [TestCase("my $x = $y->[0]->{a}->(1)->method->@*;")]
  [TestCase("my $x = (sort { $a <=> $b } @a)[0];")]
  [TestCase("my $f = \\&foo; my $g = \\(my $o);")]
  [TestCase("...;\nsub f { ... }")]
  [TestCase("print 'a' for 1, 2; print 'b' foreach 1; print 'c' while 0; print 'd' until 1; do { 1 } while 0;")]
  [TestCase("1 if 1; 1 unless 0;")]
  [TestCase("while () { last } for (;;) { last } continue_label: { 1 } continue { 1 }")]
  [TestCase("if (1) { } else { }\nunless (1) { } elsif (0) { } else { }\nwhile (1) { } continue { }")]
  [TestCase("$x = <<'A' x 2;\nbody\nA\n")]
  public void AssortedValidCode_Parses(string source)
    => Assert.That(Parse(source).Verdict, Is.EqualTo(PerlVerdict.Valid), string.Join("\n", Parse(source).Diagnostics));

  [Test]
  public void Lines_AreThoseOfTheFile() {
    var result = Parse("my $x = 1;\n\n=pod\n\ntext\n\n=cut\n\nmy $y = <<E;\nbody\nE\nmy $z = 1 1;\n");
    Assert.That(result.Diagnostics[0].Line, Is.EqualTo(12));
  }

}
