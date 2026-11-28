namespace Polypiler.Core.Syntax;

/// <summary>
/// A place in a source: the 1-based line, the column in the unit the frontend counts in (bytes,
/// UTF-16 units or characters, as its reference compiler reports them) and the byte offset.
/// </summary>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The column, in the frontend's unit and from the frontend's base.</param>
/// <param name="Offset">The 0-based byte offset into the source.</param>
public readonly record struct SourcePosition(int Line, int Column, int Offset) {

  /// <inheritdoc />
  public override string ToString() => $"{this.Line}:{this.Column}";

}
