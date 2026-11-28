namespace Polypiler.Core.Frontends.Python.Syntax;

/// <summary>The kinds of the tokens of CPython's tokenizer (<c>Grammar/Tokens</c>) the parser sees.</summary>
internal enum PyTokenKind {

  /// <summary>The end of the source.</summary>
  EndMarker,

  /// <summary>An identifier that is no keyword (soft keywords included).</summary>
  Name,

  /// <summary>A keyword; its text is the keyword.</summary>
  Keyword,

  /// <summary>A numeric literal.</summary>
  Number,

  /// <summary>A string or bytes literal with its prefix and quotes.</summary>
  String,

  /// <summary>The end of a logical line.</summary>
  Newline,

  /// <summary>One more level of indentation.</summary>
  Indent,

  /// <summary>One level of indentation less.</summary>
  Dedent,

  /// <summary>An operator or delimiter; its text names it, <c>!=</c> also for <c>&lt;&gt;</c>.</summary>
  Operator,

  /// <summary>A character the grammar has no token for, such as <c>$</c> (CPython's <c>OP</c>).</summary>
  Unknown,

  /// <summary>The prefix and opening quotes of an f-string.</summary>
  FStringStart,

  /// <summary>A literal part of an f-string.</summary>
  FStringMiddle,

  /// <summary>The closing quotes of an f-string.</summary>
  FStringEnd,

  /// <summary>The prefix and opening quotes of a t-string.</summary>
  TStringStart,

  /// <summary>A literal part of a t-string.</summary>
  TStringMiddle,

  /// <summary>The closing quotes of a t-string.</summary>
  TStringEnd,

  /// <summary>What the tokenizer gives where it found an error.</summary>
  Error

}

/// <summary>
/// A token: its kind, its text as the source spells it (UTF-8 decoded), and its range as CPython
/// records it - 1-based lines, 0-based columns counted in bytes of UTF-8.
/// </summary>
internal sealed class PyToken : Polypiler.Core.Syntax.IToken<PyTokenKind> {

  /// <summary>The kind.</summary>
  public required PyTokenKind Kind { get; init; }

  /// <summary>The text: the spelling, a keyword or operator by its canonical text.</summary>
  public required string Text { get; init; }

  /// <summary>The spelling as the source has it, which differs from <see cref="Text"/> only for <c>&lt;&gt;</c>.</summary>
  public required string Source { get; init; }

  /// <summary>The line the token starts on.</summary>
  public int Line { get; init; }

  /// <summary>The byte column the token starts at, or -1 where it has no extent.</summary>
  public int Column { get; init; }

  /// <summary>The line the token ends on.</summary>
  public int EndLine { get; init; }

  /// <summary>The byte column just after the token, or -1.</summary>
  public int EndColumn { get; init; }

  /// <summary>The depth of open brackets when the token was read.</summary>
  public int Level { get; init; }

  /// <summary>The source text of an f-string or t-string expression the token ends, for debug texts and interpolations.</summary>
  public string? Metadata { get; init; }

  /// <summary>Where the token starts: its line and byte column; the offset is not kept.</summary>
  public Polypiler.Core.Syntax.SourcePosition Start => new(this.Line, this.Column, -1);

  /// <summary>Where the token ends: its line and the byte column after it.</summary>
  public Polypiler.Core.Syntax.SourcePosition End => new(this.EndLine, this.EndColumn, -1);

}
