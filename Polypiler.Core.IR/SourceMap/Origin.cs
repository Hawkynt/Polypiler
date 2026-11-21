using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.IR;

/// <summary>
/// Where a node of the IR came from: a span of a text source (<see cref="SourceSpan"/>), a range of
/// bytes of a binary input (<see cref="AddressSpan"/>), or the pass that made it
/// (<see cref="Synthetic"/>). Origins never change what a node means: two nodes that differ only in
/// their origins are equal and interchangeable for every pass.
/// </summary>
public abstract partial record Origin {

  /// <summary>An origin of one of the three kinds, which are all there are.</summary>
  private protected Origin() { }

  /// <summary>Whether the origin is well formed on its own, without the table of sources: <see langword="null"/> when it is, otherwise why not.</summary>
  public abstract string? Problem { get; }

  /// <summary>The index of the source in <see cref="IrModule.Sources"/> the origin names, or -1 for a synthetic one.</summary>
  public virtual int SourceIndex => -1;

  /// <summary>The text form of the origin: <c>!loc(…)</c>, <c>!addr(…)</c> or <c>!synthetic("…")</c>.</summary>
  /// <returns>The suffix, as <see cref="TryParse"/> reads it back.</returns>
  public abstract string Format();

  /// <summary>The text form of several origins, in order, separated by one space.</summary>
  /// <param name="origins">The origins.</param>
  /// <returns>The suffixes; empty for none.</returns>
  public static string Format(IEnumerable<Origin> origins) => string.Join(" ", origins.Select(origin => origin.Format()));

  /// <summary>
  /// Reads the suffixes of an instruction: any number of <c>!loc(&lt;source&gt;:&lt;line&gt;:&lt;column&gt;-&lt;line&gt;:&lt;column&gt;)</c>,
  /// <c>!addr(&lt;source&gt;:0x&lt;hex offset&gt;+&lt;length&gt;)</c> and <c>!synthetic("&lt;pass&gt;")</c>, separated by white space.
  /// </summary>
  /// <param name="text">The suffixes.</param>
  /// <param name="sources">How many sources the module declares; every location names one of them.</param>
  /// <param name="origins">The origins read, in order; empty when the text is refused.</param>
  /// <param name="diagnostic">
  /// <see cref="SourceMapDiagnostics.MalformedLocation"/> (PLY0116) for a suffix that is not one of the
  /// three forms or describes no span, <see cref="SourceMapDiagnostics.UndeclaredSource"/> (PLY0117) for
  /// a location naming a source the module does not declare; <see langword="null"/> when the text is read.
  /// </param>
  /// <returns>Whether the text was read.</returns>
  public static bool TryParse(string text, int sources, out IReadOnlyList<Origin> origins, out Diagnostic? diagnostic) {
    var read = new List<Origin>();
    origins = [];
    foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
      var origin = Read(token);
      if (origin is null || origin.Problem is not null) {
        diagnostic = Diagnostic.Create(SourceMapDiagnostics.MalformedLocation, null, token);
        return false;
      }

      if (origin.SourceIndex >= sources) {
        diagnostic = Diagnostic.Create(SourceMapDiagnostics.UndeclaredSource, null, origin.SourceIndex, sources);
        return false;
      }

      read.Add(origin);
    }

    origins = read;
    diagnostic = null;
    return true;
  }

  private static Origin? Read(string token) {
    if (LocationPattern().Match(token) is { Success: true } location)
      return Numbers(location, 5) is { } numbers ? new SourceSpan(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]) : null;

    if (AddressPattern().Match(token) is { Success: true } address
        && int.TryParse(address.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var artifact)
        && long.TryParse(address.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset)
        && int.TryParse(address.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
      return new AddressSpan(artifact, offset, length);

    return SyntheticPattern().Match(token) is { Success: true } synthetic ? new Synthetic(synthetic.Groups[1].Value) : null;
  }

  /// <summary>The numbers of the first groups of a match, or <see langword="null"/> when one does not fit an <see langword="int"/>.</summary>
  private static int[]? Numbers(Match match, int count) {
    var numbers = new int[count];
    for (var index = 0; index < count; ++index)
      if (!int.TryParse(match.Groups[index + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
        return null;

    return numbers;
  }

  [GeneratedRegex(@"^!loc\(([0-9]+):([0-9]+):([0-9]+)-([0-9]+):([0-9]+)\)$", RegexOptions.CultureInvariant)]
  private static partial Regex LocationPattern();

  [GeneratedRegex(@"^!addr\(([0-9]+):0x([0-9A-Fa-f]{1,16})\+([0-9]+)\)$", RegexOptions.CultureInvariant)]
  private static partial Regex AddressPattern();

  [GeneratedRegex(@"^!synthetic\(""([^""\s]+)""\)$", RegexOptions.CultureInvariant)]
  private static partial Regex SyntheticPattern();

}

/// <summary>
/// A span of a text source: from a line and column up to, not including, another. Lines and columns
/// count from 1; a column counts UTF-16 code units, so a character outside the Basic Multilingual Plane
/// takes two. A span whose end equals its start is empty, and allowed.
/// </summary>
/// <param name="Artifact">The index of the source in <see cref="IrModule.Sources"/>.</param>
/// <param name="StartLine">The line of the first code unit.</param>
/// <param name="StartColumn">The column of the first code unit.</param>
/// <param name="EndLine">The line of the end.</param>
/// <param name="EndColumn">The column of the first code unit after the span.</param>
public sealed record SourceSpan(int Artifact, int StartLine, int StartColumn, int EndLine, int EndColumn) : Origin {

  /// <inheritdoc/>
  public override string? Problem
    => this.Artifact < 0 ? "the source index is negative"
      : this.StartLine < 1 || this.StartColumn < 1 || this.EndLine < 1 || this.EndColumn < 1 ? "lines and columns count from 1"
      : (this.EndLine, this.EndColumn).CompareTo((this.StartLine, this.StartColumn)) < 0 ? "the span ends before it starts"
      : null;

  /// <inheritdoc/>
  public override int SourceIndex => this.Artifact;

  /// <summary>The span between two offsets of a text, with lines that end at each <c>\n</c> and columns in UTF-16 code units.</summary>
  /// <param name="artifact">The index of the source in <see cref="IrModule.Sources"/>.</param>
  /// <param name="text">The text of the source.</param>
  /// <param name="start">The offset of the first code unit, 0 to the length of the text.</param>
  /// <param name="end">The offset after the last code unit, <paramref name="start"/> to the length of the text.</param>
  /// <returns>The span.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An offset lies outside the text, or the end before the start.</exception>
  public static SourceSpan FromOffsets(int artifact, string text, int start, int end) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentOutOfRangeException.ThrowIfNegative(start);
    ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(end, text.Length);
    (int Line, int Column) Position(int offset) {
      var lineStart = text.LastIndexOf('\n', Math.Max(offset - 1, 0), offset) + 1;
      return (text.AsSpan(0, lineStart).Count('\n') + 1, offset - lineStart + 1);
    }

    var (startLine, startColumn) = Position(start);
    var (endLine, endColumn) = Position(end);
    return new(artifact, startLine, startColumn, endLine, endColumn);
  }

  /// <inheritdoc/>
  public override string Format()
    => string.Create(CultureInfo.InvariantCulture, $"!loc({this.Artifact}:{this.StartLine}:{this.StartColumn}-{this.EndLine}:{this.EndColumn})");

}

/// <summary>A range of bytes of a binary input: the instruction a lifted node came from.</summary>
/// <param name="Artifact">The index of the source in <see cref="IrModule.Sources"/>.</param>
/// <param name="Offset">The offset of the first byte in the file.</param>
/// <param name="Length">How many bytes the range has.</param>
public sealed record AddressSpan(int Artifact, long Offset, int Length) : Origin {

  /// <inheritdoc/>
  public override string? Problem
    => this.Artifact < 0 ? "the source index is negative"
      : this.Offset < 0 || this.Length < 0 ? "offset and length are never negative"
      : null;

  /// <inheritdoc/>
  public override int SourceIndex => this.Artifact;

  /// <inheritdoc/>
  public override string Format()
    => string.Create(CultureInfo.InvariantCulture, $"!addr({this.Artifact}:0x{this.Offset:X}+{this.Length})");

}

/// <summary>A node a pass made that stands for nothing of the source.</summary>
/// <param name="PassId">The ID of the pass, such as <c>O0100</c>; no quotes and no white space.</param>
public sealed record Synthetic(string PassId) : Origin {

  /// <inheritdoc/>
  public override string? Problem
    => string.IsNullOrEmpty(this.PassId) || this.PassId.Any(character => character == '"' || char.IsWhiteSpace(character)) ? "the pass ID is empty or holds a quote or white space" : null;

  /// <inheritdoc/>
  public override string Format() => $"!synthetic(\"{this.PassId}\")";

}
