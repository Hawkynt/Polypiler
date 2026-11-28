#nullable enable
using System;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Go.Syntax;

/// <summary>A node of the generated parser: its rule, its token range and its children.</summary>
internal sealed class GoPegNode(int rule, int start, int end, GoPegNode[] children) {
  public int Rule { get; } = rule;
  public int Start { get; } = start;
  public int End { get; } = end;
  public GoPegNode[] Children { get; } = children;
}

/// <summary>The hand-written half of the generated parser: the token source and the hooks.</summary>
internal sealed partial class GoPegParser {

  private static readonly GoPegNode[] _none = [];
  private readonly GoToken[] _t;
  private readonly List<GoPegNode> _stack = new(256);
  private int _p;
  public int Farthest;
  public GoToken At(int i) => this._t[i];
  private bool _noLit;

  public GoPegParser(GoSource source) {
    var scanner = new GoScanner(source);
    var tokens = new List<GoToken>(source.Bytes.Length / 4 + 16);
    while (true) {
      var token = scanner.Scan();
      if (token.Kind == GoTokenKind.Comment)
        continue;
      tokens.Add(token);
      if (token.Kind == GoTokenKind.Eof)
        break;
    }
    this._t = tokens.ToArray();
    this.InitMemo(this._t.Length + 1);
  }

  /// <summary>Whether the whole file parses.</summary>
  public bool Accepts() => this.Parse() is not null;

  private bool Is(GoTokenKind kind) {
    if (this._t[this._p].Kind != kind) {
      if (this._p > this.Farthest) this.Farthest = this._p;
      return false;
    }
    if (kind != GoTokenKind.Eof)
      ++this._p;
    return true;
  }

  // The hook of the composite-literal restriction of control clauses.
  private bool litok() => !this._noLit;

  private void Truncate(int count) {
    if (this._stack.Count > count)
      this._stack.RemoveRange(count, this._stack.Count - count);
  }

  private GoPegNode[] Slice(int from) {
    var n = this._stack.Count - from;
    if (n == 0)
      return _none;
    var children = new GoPegNode[n];
    this._stack.CopyTo(from, children, 0, n);
    this._stack.RemoveRange(from, n);
    return children;
  }
}
