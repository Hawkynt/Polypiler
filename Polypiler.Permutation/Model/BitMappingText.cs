using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Polypiler.Core;

namespace Polypiler.Permutation.Model;

/// <summary>
/// The compact text notation of a <see cref="BitMapping"/>: <c>&lt;inputWidth&gt;:[&lt;entry&gt;,…]</c>,
/// entries least significant first. An entry is <c>7</c> (input bit 7), <c>~7</c> (input bit 7
/// inverted), <c>=0</c> or <c>=1</c> (a constant), <c>?</c> (a don't-care), or <c>a..b</c>, a run of
/// consecutive plain sources from <c>a</c> to <c>b</c>, ascending or descending. <c>8:[7..0]</c> is the
/// byte reversal <c>8:[7,6,5,4,3,2,1,0]</c>. Blanks around the parts are allowed.
/// </summary>
public static partial class BitMappingText {

  /// <summary>The shortest run of consecutive plain sources <see cref="Format"/> writes as a range.</summary>
  public const int MinimumRange = 4;

  /// <summary>Reads a mapping text.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  /// <exception cref="PermutationModelException">The text is malformed (<c>PLY0802</c>) or describes no valid mapping (<c>PLY0800</c>, <c>PLY0801</c>).</exception>
  public static BitMapping Parse(string text) {
    var diagnostics = new DiagnosticBag();
    return TryParse(text, diagnostics, out var mapping) ? mapping : throw new PermutationModelException(diagnostics.Items[0]);
  }

  /// <summary>Reads a mapping text, or reports why it is not a mapping.</summary>
  /// <param name="text">The text.</param>
  /// <param name="diagnostics">Receives one of <c>PLY0800</c>–<c>PLY0802</c> when the text is refused.</param>
  /// <param name="mapping">The mapping, or <see langword="null"/> when it is refused.</param>
  /// <returns>Whether the text is a mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
  public static bool TryParse(string text, DiagnosticBag diagnostics, [NotNullWhen(true)] out BitMapping? mapping) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(diagnostics);
    mapping = null;
    var problem = Read(text, out var width, out var outputs);
    if (problem is not null) {
      diagnostics.Report(problem);
      return false;
    }
    return BitMapping.TryCreate(width, outputs, diagnostics, out mapping);
  }

  /// <summary>Splits a text into width and entries; returns the diagnostic of the first defect, or <see langword="null"/>.</summary>
  private static Diagnostic? Read(string text, out int width, out List<BitSource> outputs) {
    width = 0;
    outputs = [];
    var shape = Shape().Match(text);
    if (!shape.Success)
      return Malformed(text, text.TrimStart().StartsWith('[') ? "missing width" : "expected '<inputWidth>:[<entry>,...]'");

    var widthText = shape.Groups["width"].Value.Trim();
    if (widthText.Length == 0)
      return Malformed(text, "missing width");
    if (!Digits().IsMatch(widthText))
      return Malformed(text, $"width '{widthText}' is not a number");
    if (!int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out width))
      return Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, widthText);

    var body = shape.Groups["entries"].Value;
    if (body.Trim().Length == 0)
      return null;

    var parts = body.Split(',');
    for (var position = 0; position < parts.Length; ++position) {
      var entry = parts[position].Trim();
      var problem = entry switch {
        "" => Malformed(text, $"entry {position + 1} is empty"),
        "?" => Add(outputs, BitSource.DontCare),
        "=0" => Add(outputs, BitSource.Zero),
        "=1" => Add(outputs, BitSource.One),
        _ when entry.StartsWith('=') => Malformed(text, $"unknown entry '{entry}': a constant is =0 or =1"),
        _ when Digits().IsMatch(entry) => Index(outputs, entry, width, BitSource.From),
        _ when entry.StartsWith('~') && Digits().IsMatch(entry[1..]) => Index(outputs, entry[1..], width, BitSource.Not),
        _ when Range().Match(entry) is { Success: true } range => Run(outputs, range.Groups["from"].Value, range.Groups["to"].Value, width),
        _ => Malformed(text, $"unknown entry '{entry}'")
      };
      if (problem is not null)
        return problem;
    }
    return null;
  }

  private static Diagnostic? Add(List<BitSource> outputs, BitSource entry) {
    outputs.Add(entry);
    return null;
  }

  /// <summary>Adds an input bit; an index too large for an <see cref="int"/> is reported as lying outside the input.</summary>
  private static Diagnostic? Index(List<BitSource> outputs, string digits, int width, Func<int, BitSource> entry) {
    if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
      return Diagnostic.Create(PermutationDiagnostics.SourceOutOfRange, null, outputs.Count, digits, width);

    outputs.Add(entry(index));
    return null;
  }

  /// <summary>Adds the run <c>from..to</c>; a run that would make the mapping wider than 4096 bits is refused before it is expanded.</summary>
  private static Diagnostic? Run(List<BitSource> outputs, string fromText, string toText, int width) {
    if (!int.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out var from))
      return Diagnostic.Create(PermutationDiagnostics.SourceOutOfRange, null, outputs.Count, fromText, width);
    if (!int.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out var to))
      return Diagnostic.Create(PermutationDiagnostics.SourceOutOfRange, null, outputs.Count, toText, width);

    var count = outputs.Count + Math.Abs((long)to - from) + 1;
    if (!BitMapping.IsWidth(count))
      return Diagnostic.Create(PermutationDiagnostics.WidthOutOfRange, null, count);

    var step = to >= from ? 1 : -1;
    for (var index = from; index != to + step; index += step)
      outputs.Add(BitSource.From(index));
    return null;
  }

  private static Diagnostic Malformed(string text, string reason) => Diagnostic.Create(PermutationDiagnostics.Malformed, null, "text", text, reason);

  /// <summary>Writes a mapping in the notation; a run of <see cref="MinimumRange"/> or more consecutive plain sources becomes a range.</summary>
  /// <param name="mapping">The mapping.</param>
  /// <returns>The text, which <see cref="Parse"/> reads back to an equal mapping.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is <see langword="null"/>.</exception>
  public static string Format(BitMapping mapping) {
    ArgumentNullException.ThrowIfNull(mapping);
    var outputs = mapping.Outputs;
    var text = new StringBuilder().Append(mapping.InputWidth.ToString(CultureInfo.InvariantCulture)).Append(":[");
    for (var bit = 0; bit < outputs.Count;) {
      if (bit > 0)
        text.Append(',');

      var length = RunLength(outputs, bit);
      if (length >= MinimumRange) {
        text.Append(outputs[bit].Index.ToString(CultureInfo.InvariantCulture)).Append("..").Append(outputs[bit + length - 1].Index.ToString(CultureInfo.InvariantCulture));
        bit += length;
      } else {
        text.Append(outputs[bit].ToString());
        ++bit;
      }
    }
    return text.Append(']').ToString();
  }

  /// <summary>How many plain sources from <paramref name="start"/> on step by the same +1 or -1; 1 for a lone entry.</summary>
  private static int RunLength(IReadOnlyList<BitSource> outputs, int start) {
    if (outputs[start].Kind != BitSourceKind.Source || start + 1 >= outputs.Count || outputs[start + 1].Kind != BitSourceKind.Source)
      return 1;

    var step = outputs[start + 1].Index - outputs[start].Index;
    if (step is not (1 or -1))
      return 1;

    var end = start + 1;
    while (end + 1 < outputs.Count && outputs[end + 1].Kind == BitSourceKind.Source && outputs[end + 1].Index - outputs[end].Index == step)
      ++end;
    return end - start + 1;
  }

  [GeneratedRegex(@"^\s*(?<width>[^:\[\]]*):\s*\[(?<entries>[^\[\]]*)\]\s*$")]
  private static partial Regex Shape();

  [GeneratedRegex("^[0-9]+$")]
  private static partial Regex Digits();

  [GeneratedRegex(@"^(?<from>[0-9]+)\.\.(?<to>[0-9]+)$")]
  private static partial Regex Range();

}
