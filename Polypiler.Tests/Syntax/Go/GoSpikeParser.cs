using System.Collections.Generic;
using Polypiler.Core.Frontends.Go.Syntax;
using Polypiler.Core.Syntax;

namespace Polypiler.Tests.Syntax.Go;

/// <summary>A token of the Go scanner as the generated parser reads it.</summary>
internal sealed class GoSpikeToken(GoToken token, SourcePosition start) : IToken<GoTokenKind> {

  /// <inheritdoc />
  public GoTokenKind Kind => token.Kind;

  /// <inheritdoc />
  public string Text => token.Literal.Length > 0 ? token.Literal : token.Kind.Spelling();

  /// <inheritdoc />
  public SourcePosition Start => start;

  /// <inheritdoc />
  public SourcePosition End => start;

}

/// <summary>The Go scanner without its comments, as the token source of the generated parser.</summary>
internal sealed class GoSpikeTokens(GoSource source) : ITokenSource<GoSpikeToken> {

  private readonly GoScanner _scanner = new(source);

  public GoSpikeToken Next() {
    while (true) {
      var token = this._scanner.Scan();
      if (token.Kind == GoTokenKind.Comment)
        continue;

      var (line, column) = source.Position(token.Offset);
      return new(token, new(line, column, token.Offset));
    }
  }

}

/// <summary>
/// The hand-written half of the parser generated from <c>go.pgram</c>: the token source and the hooks
/// of the composite-literal rule of control clauses - <c>nolit(e)</c> parses a clause where a
/// composite literal may not follow a bare type name, <c>lit(e)</c> lifts that again inside
/// brackets, and <c>?litok</c> reads it. The state is part of the memo key.
/// </summary>
internal sealed partial class GoSpikeParser {

  private readonly Stack<bool> _saved = new();
  private bool _noLit;

  /// <summary>Prepares a parse of a source.</summary>
  public GoSpikeParser(GoSource source) : base(new GoSpikeTokens(source)) { }

  /// <summary>How deep the hooks are nested now; 0 between parses.</summary>
  public int HookDepth => this._saved.Count;

  /// <summary>Whether the source parses.</summary>
  public static bool Accepts(string text) {
    try {
      new GoSpikeParser(new GoSource(text)).ParseSourceFile();
      return true;
    } catch (SyntaxErrorException) {
      return false;
    }
  }

  private void Set(bool noLit) {
    this._saved.Push(this._noLit);
    this._noLit = noLit;
    this.MemoState = noLit ? 1 : 0;
  }

  private void Restore() {
    this._noLit = this._saved.Pop();
    this.MemoState = this._noLit ? 1 : 0;
  }

  private void EnterNolit() => this.Set(true);

  private void LeaveNolit() => this.Restore();

  private void EnterLit() => this.Set(false);

  private void LeaveLit() => this.Restore();

  private bool litok() => !this._noLit;

}
