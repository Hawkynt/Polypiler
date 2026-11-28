using System;

namespace Polypiler.Core.Syntax;

/// <summary>What a generated parser reads of a token: its kind, its text and its extent.</summary>
/// <typeparam name="TKind">The token kinds of the language.</typeparam>
public interface IToken<TKind> where TKind : struct, Enum {

  /// <summary>The kind.</summary>
  TKind Kind { get; }

  /// <summary>The text keywords and operators are matched by.</summary>
  string Text { get; }

  /// <summary>Where the token starts.</summary>
  SourcePosition Start { get; }

  /// <summary>Where the token ends: just after its last character.</summary>
  SourcePosition End { get; }

}

/// <summary>A token of a language whose token source needs nothing more.</summary>
/// <typeparam name="TKind">The token kinds of the language.</typeparam>
/// <param name="Kind">The kind.</param>
/// <param name="Start">Where the token starts.</param>
/// <param name="End">Where the token ends.</param>
/// <param name="Text">The text.</param>
public sealed record Token<TKind>(TKind Kind, SourcePosition Start, SourcePosition End, string Text) : IToken<TKind> where TKind : struct, Enum;

/// <summary>
/// Gives the tokens of a source one at a time, as a parser asks for them. After the last token, the
/// end of the source, it keeps giving that token. An error of the source is thrown from
/// <see cref="Next"/>, and the parser lets it pass unchanged.
/// </summary>
/// <typeparam name="TToken">The token type.</typeparam>
public interface ITokenSource<out TToken> {

  /// <summary>The next token.</summary>
  TToken Next();

}
