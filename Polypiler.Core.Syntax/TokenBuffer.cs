using System;

namespace Polypiler.Core.Syntax;

/// <summary>
/// The tokens a parse has read so far, read from the source only when a rule asks for one past the
/// last (pegen's <c>_PyPegen_fill_token</c>): an error of the tokenizer surfaces where the parser
/// reaches it, and the tokens after a refused construct are never read.
/// </summary>
/// <typeparam name="TToken">The token type.</typeparam>
public sealed class TokenBuffer<TToken> where TToken : class {

  private readonly ITokenSource<TToken> _source;
  private TToken[] _tokens = new TToken[64];

  /// <summary>Creates an empty buffer over a token source.</summary>
  /// <param name="source">The token source.</param>
  public TokenBuffer(ITokenSource<TToken> source) => this._source = source ?? throw new ArgumentNullException(nameof(source));

  /// <summary>The number of tokens read from the source.</summary>
  public int Count { get; private set; }

  /// <summary>The token at an index; the tokens up to it are read first where they are not yet.</summary>
  /// <param name="index">The index, 0 or more.</param>
  public TToken this[int index] {
    get {
      while (index >= this.Count)
        this.Fill();

      return this._tokens[index];
    }
  }

  /// <summary>The last token read from the source; the first is read where none is yet.</summary>
  public TToken Last => this.Count == 0 ? this[0] : this._tokens[this.Count - 1];

  private void Fill() {
    var token = this._source.Next();
    if (this.Count == this._tokens.Length)
      Array.Resize(ref this._tokens, this._tokens.Length * 2);

    this._tokens[this.Count++] = token;
  }

}
