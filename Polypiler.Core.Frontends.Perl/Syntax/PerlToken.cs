using System;
using System.Collections.Generic;
using Polypiler.Core.Frontends.Perl.Ast;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>The kinds of token the lexer gives.</summary>
internal enum PerlTokenKind {
  /// <summary>The end of the text, or <c>__END__</c>/<c>__DATA__</c>.</summary>
  End,
  /// <summary>An identifier, possibly qualified (<c>Foo::bar</c>), a keyword or a word operator.</summary>
  Word,
  /// <summary>A numeric literal.</summary>
  Number,
  /// <summary>A version string, <c>v5.36.0</c> or <c>5.36.0</c>.</summary>
  Version,
  /// <summary>A named variable: <see cref="PerlToken.Sigil"/> and <see cref="PerlToken.Text"/>; the sigil <c>#</c> is <c>$#name</c>.</summary>
  Variable,
  /// <summary>A sigil that starts a dereference (<c>$$x</c>, <c>@{...}</c>, <c>$#{...}</c>): <see cref="PerlToken.Text"/> is the sigil.</summary>
  Cast,
  /// <summary>A quote: <c>'...'</c>, <c>"..."</c>, <c>q</c>, <c>qq</c>, <c>qw</c>, backticks, <c>qx</c>.</summary>
  Quote,
  /// <summary><c>m</c>, <c>qr</c> or a bare <c>/.../</c>.</summary>
  Match,
  /// <summary><c>s/.../.../</c>.</summary>
  Substitute,
  /// <summary><c>tr/.../.../</c> or <c>y/.../.../</c>.</summary>
  Transliterate,
  /// <summary>A heredoc marker; its body is read when the token is taken.</summary>
  Heredoc,
  /// <summary><c>&lt;FH&gt;</c>, <c>&lt;$fh&gt;</c>, <c>&lt;&gt;</c>, <c>&lt;&lt;&gt;&gt;</c>.</summary>
  ReadLine,
  /// <summary><c>&lt;*.c&gt;</c>.</summary>
  Glob,
  /// <summary>A file test such as <c>-e</c>.</summary>
  FileTest,
  /// <summary>Punctuation or a symbolic operator.</summary>
  Operator
}

/// <summary>The body of a quote: its characters (delimiter escapes removed for non-bracket delimiters) and delimiters.</summary>
internal sealed record PerlQuote(PerlText Text, char Open, char Close) {

  /// <summary>The characters between the delimiters.</summary>
  public string Value => this.Text.Value;

}

/// <summary>A token: its kind, text, extent in the lexer's text, line and the payload of its kind.</summary>
internal sealed record PerlToken(PerlTokenKind Kind, string Text, int Start, int End, int Line) {

  /// <summary>Whether whitespace or a comment precedes the token.</summary>
  public bool SpaceBefore { get; init; }

  /// <summary>The value of a <see cref="PerlTokenKind.Number"/>.</summary>
  public PerlNumeric Number { get; init; }

  /// <summary>The sigil of a <see cref="PerlTokenKind.Variable"/>.</summary>
  public char Sigil { get; init; }

  /// <summary>The quote keyword (<c>q qq qw qx m qr s tr y</c>, or <c>'</c>, <c>"</c>, <c>`</c>, <c>/</c>) of a quote-like token.</summary>
  public string Keyword { get; init; } = "";

  /// <summary>The first part of a quote-like token.</summary>
  public PerlQuote? Body { get; init; }

  /// <summary>The second part of <c>s</c> and <c>tr</c>.</summary>
  public PerlQuote? Replacement { get; init; }

  /// <summary>The modifiers after a quote-like token.</summary>
  public string Flags { get; init; } = "";

  /// <summary>Whether a word is followed by <c>=&gt;</c> and so is a string.</summary>
  public bool AutoQuoted { get; init; }

  /// <summary>Whether a heredoc interpolates.</summary>
  public bool Interpolates { get; init; }

  /// <summary>Whether a heredoc is indented (<c>&lt;&lt;~</c>).</summary>
  public bool Indented { get; init; }

  /// <summary>Whether the token is the operator or word <paramref name="text"/>.</summary>
  public bool Is(string text) => this.Kind is PerlTokenKind.Operator or PerlTokenKind.Word && this.Text == text;

  /// <summary>Whether the token is a word (not autoquoted) <paramref name="text"/>.</summary>
  public bool IsWord(string text) => this.Kind == PerlTokenKind.Word && !this.AutoQuoted && this.Text == text;

}

/// <summary>A syntax error: perl would refuse the file.</summary>
internal sealed class PerlSyntaxException(string message, int line) : Exception(message) {

  /// <summary>The line the error is on.</summary>
  public int Line { get; } = line;

}

/// <summary>A construct the parser does not decide: the file is neither accepted nor refused.</summary>
internal sealed class PerlUnsupportedException(string construct, int line) : Exception(construct) {

  /// <summary>The construct.</summary>
  public string Construct { get; } = construct;

  /// <summary>The line it is on.</summary>
  public int Line { get; } = line;

}

/// <summary>What the lexer needs to know of the parser's state.</summary>
internal interface IPerlLexerHost {

  /// <summary>Whether <c>'</c> separates package names (the feature <c>apostrophe_as_package_separator</c>).</summary>
  bool ApostropheSeparates { get; }

  /// <summary>Whether <c>use utf8</c> is in effect, so letters beyond ASCII are identifier characters.</summary>
  bool Utf8 { get; }

}
