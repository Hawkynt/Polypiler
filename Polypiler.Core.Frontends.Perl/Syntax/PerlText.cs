using System;
using System.Collections.Generic;

namespace Polypiler.Core.Frontends.Perl.Syntax;

/// <summary>
/// A text the lexer reads: the file, or a part cut out of it - the body of a quote with its delimiter
/// escapes removed, a heredoc body - which keeps the line it started on so that tokens inside it
/// report the line of the file.
/// </summary>
internal sealed class PerlText {

  private readonly int[] _newlines;

  public PerlText(string text, int baseLine) {
    this.Value = text;
    this.BaseLine = baseLine;
    var newlines = new List<int>();
    for (var i = 0; i < text.Length; ++i)
      if (text[i] == '\n')
        newlines.Add(i);
    this._newlines = [.. newlines];
  }

  /// <summary>The characters.</summary>
  public string Value { get; }

  /// <summary>The line of the file the text starts on.</summary>
  public int BaseLine { get; }

  /// <summary>The number of characters.</summary>
  public int Length => this.Value.Length;

  /// <summary>The character at a position, or <c>'\0'</c> beyond the end.</summary>
  public char this[int index] => (uint)index < (uint)this.Value.Length ? this.Value[index] : '\0';

  /// <summary>The line of the file a position of the text is on.</summary>
  public int LineAt(int position) {
    var index = Array.BinarySearch(this._newlines, position);
    return this.BaseLine + (index >= 0 ? index : ~index);
  }

}
