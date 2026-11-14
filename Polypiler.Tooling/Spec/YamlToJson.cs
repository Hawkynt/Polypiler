using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Polypiler.Tooling.Spec;

/// <summary>
/// Reads one YAML document into a <see cref="JsonNode"/> and records where every value starts, keyed
/// by its JSON pointer (RFC 6901). Plain scalars are typed by the YAML 1.2 core schema — <c>no</c> and
/// <c>on</c> stay strings, <c>"2"</c> stays a string — and quoted scalars are always strings. Anchors,
/// aliases, merge keys, explicit tags and duplicate mapping keys are refused, so a spec document
/// means the same in every YAML library.
/// </summary>
public static partial class YamlToJson {

  /// <summary>Reads the text of one YAML document.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="positions">Receives the 1-based line and column at which each value starts, keyed by JSON pointer; the whole document is <c>""</c>.</param>
  /// <returns>The document, or <see langword="null"/> for a stream without a document or a document that is <c>null</c>.</returns>
  /// <exception cref="YamlToJsonException">The text is not valid YAML or uses a construct refused above.</exception>
  public static JsonNode? Read(string text, out IReadOnlyDictionary<string, (int Line, int Column)> positions) {
    ArgumentNullException.ThrowIfNull(text);
    var reader = new Reader(new Parser(new StringReader(text)));
    try {
      var root = reader.Stream();
      positions = reader.Positions;
      return root;
    } catch (YamlException exception) {
      throw new YamlToJsonException($"not valid YAML: {exception.Message}", (int)exception.Start.Line, (int)exception.Start.Column);
    }
  }

  /// <summary>One segment of a JSON pointer: <c>~</c> becomes <c>~0</c>, <c>/</c> becomes <c>~1</c>.</summary>
  /// <param name="segment">The key or index.</param>
  /// <returns>The escaped segment.</returns>
  public static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

  [GeneratedRegex("^[-+]?[0-9]+$")]
  private static partial Regex Decimal();

  [GeneratedRegex("^0o[0-7]+$")]
  private static partial Regex Octal();

  [GeneratedRegex("^0x[0-9a-fA-F]+$")]
  private static partial Regex Hexadecimal();

  [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
  private static partial Regex Float();

  [GeneratedRegex(@"^([-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
  private static partial Regex NotANumber();

  /// <summary>The value of a plain scalar by the YAML 1.2 core schema.</summary>
  private static JsonNode? Plain(string value, Mark start) {
    switch (value) {
      case "" or "~" or "null" or "Null" or "NULL":
        return null;
      case "true" or "True" or "TRUE":
        return JsonValue.Create(true);
      case "false" or "False" or "FALSE":
        return JsonValue.Create(false);
    }

    if (Decimal().IsMatch(value))
      return Integer(BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    if (Octal().IsMatch(value))
      return Integer(Digits(value[2..], 8));
    if (Hexadecimal().IsMatch(value))
      return Integer(Digits(value[2..], 16));
    if (Float().IsMatch(value))
      return JsonValue.Create(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
    if (NotANumber().IsMatch(value))
      throw new YamlToJsonException($"'{value}' has no JSON value", (int)start.Line, (int)start.Column);
    return JsonValue.Create(value);
  }

  private static BigInteger Digits(string digits, int radix) {
    var number = BigInteger.Zero;
    foreach (var digit in digits)
      number = number * radix + Convert.ToInt32(digit.ToString(), 16);
    return number;
  }

  /// <summary>A number as JSON: a <see cref="long"/> where it fits, the exact decimal digits otherwise.</summary>
  private static JsonNode Integer(BigInteger number)
    => number >= long.MinValue && number <= long.MaxValue
      ? JsonValue.Create((long)number)
      : JsonNode.Parse(number.ToString(CultureInfo.InvariantCulture))!;

  /// <summary>Walks the parser's events and builds the nodes.</summary>
  private sealed class Reader(IParser parser) {

    private readonly Dictionary<string, (int Line, int Column)> _positions = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (int Line, int Column)> Positions => this._positions;

    public JsonNode? Stream() {
      parser.Consume<StreamStart>();
      if (parser.TryConsume<StreamEnd>(out _))
        return null;

      parser.Consume<DocumentStart>();
      var root = this.Node("");
      parser.Consume<DocumentEnd>();
      if (parser.Current is DocumentStart second)
        throw Refused("a spec file holds one YAML document", second.Start);
      parser.Consume<StreamEnd>();
      return root;
    }

    private JsonNode? Node(string pointer) {
      var current = parser.Current!;
      this._positions[pointer] = ((int)current.Start.Line, (int)current.Start.Column);
      switch (current) {
        case AnchorAlias alias:
          throw Refused("aliases are not allowed", alias.Start);
        case NodeEvent { Anchor.IsEmpty: false } anchored:
          throw Refused("anchors are not allowed", anchored.Start);
        case NodeEvent { Tag.IsEmpty: false } tagged:
          throw Refused($"tag '{tagged.Tag.Value}' is not allowed", tagged.Start);
        case Scalar scalar:
          parser.MoveNext();
          return scalar.Style == ScalarStyle.Plain ? Plain(scalar.Value, scalar.Start) : JsonValue.Create(scalar.Value);
        case SequenceStart:
          return this.Sequence(pointer);
        default:
          return this.Mapping(pointer);
      }
    }

    private JsonArray Sequence(string pointer) {
      parser.Consume<SequenceStart>();
      var array = new JsonArray();
      while (!parser.TryConsume<SequenceEnd>(out _))
        array.Add(this.Node($"{pointer}/{array.Count}"));
      return array;
    }

    private JsonObject Mapping(string pointer) {
      parser.Consume<MappingStart>();
      var mapping = new JsonObject();
      while (!parser.TryConsume<MappingEnd>(out _)) {
        if (parser.Current is not Scalar key || key.Tag is { IsEmpty: false } || !key.Anchor.IsEmpty)
          throw Refused("a mapping key must be a plain or quoted scalar", parser.Current!.Start);
        if (key is { Style: ScalarStyle.Plain, Value: "<<" })
          throw Refused("merge keys are not allowed", key.Start);
        if (mapping.ContainsKey(key.Value))
          throw Refused($"duplicate key '{key.Value}'", key.Start);

        parser.MoveNext();
        mapping[key.Value] = this.Node($"{pointer}/{Escape(key.Value)}");
      }

      return mapping;
    }

    private static YamlToJsonException Refused(string message, Mark at) => new(message, (int)at.Line, (int)at.Column);

  }

}

/// <summary>A YAML text that <see cref="YamlToJson"/> cannot or will not read, with the place of the problem.</summary>
/// <param name="message">What is wrong, as the diagnostic says it.</param>
/// <param name="line">The 1-based line.</param>
/// <param name="column">The 1-based column.</param>
public sealed class YamlToJsonException(string message, int line, int column) : Exception(message) {

  /// <summary>The 1-based line of the problem.</summary>
  public int Line { get; } = line;

  /// <summary>The 1-based column of the problem.</summary>
  public int Column { get; } = column;

}
