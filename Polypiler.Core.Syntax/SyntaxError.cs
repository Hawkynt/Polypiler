using System;

namespace Polypiler.Core.Syntax;

/// <summary>A syntax error a generated parser reports: where, and the reference compiler's message.</summary>
/// <param name="Position">Where the error starts.</param>
/// <param name="Message">The message, without position or class.</param>
public sealed record SyntaxError(SourcePosition Position, string Message) {

  /// <summary>Where the error ends, where the rule that raises it knows.</summary>
  public SourcePosition? End { get; init; }

  /// <inheritdoc />
  public override string ToString() => $"{this.Position}: {this.Message}";

}

/// <summary>Carries a <see cref="SyntaxError"/> out of a parse: the actions of the error rules raise it.</summary>
public sealed class SyntaxErrorException : Exception {

  /// <summary>Wraps an error.</summary>
  /// <param name="error">The error.</param>
  public SyntaxErrorException(SyntaxError error) : base((error ?? throw new ArgumentNullException(nameof(error))).ToString())
    => this.Error = error;

  /// <summary>The error.</summary>
  public SyntaxError Error { get; }

}

/// <summary>A source nests rules deeper than the parser's limit (<see cref="ParserBase{TToken, TKind}.MaxNesting"/>).</summary>
public sealed class SyntaxNestingException : Exception {

  /// <summary>Records where the limit was passed.</summary>
  /// <param name="position">The start of the token the rule that passed the limit began at.</param>
  /// <param name="limit">The limit.</param>
  public SyntaxNestingException(SourcePosition position, int limit) : base($"{position}: nesting deeper than {limit}") {
    this.Position = position;
    this.Limit = limit;
  }

  /// <summary>The start of the token the rule that passed the limit began at.</summary>
  public SourcePosition Position { get; }

  /// <summary>The limit that was passed.</summary>
  public int Limit { get; }

}
