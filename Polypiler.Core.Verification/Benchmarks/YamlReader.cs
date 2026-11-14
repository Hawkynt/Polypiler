using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>
/// Reads the YAML files of the benchmark harness field by field, so that every problem names the
/// field and its position: an unknown or missing property, a value of the wrong kind or out of range.
/// </summary>
internal sealed class YamlReader(string file) {

  /// <summary>The root mapping of a document.</summary>
  public YamlMappingNode Root(string text) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new BenchmarkFileException(file, (int)exception.Start.Line, (int)exception.Start.Column, $"not valid YAML: {exception.InnerException?.Message ?? exception.Message}");
    }

    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
      throw new BenchmarkFileException(file, 1, 1, "the file must be a mapping");
    return root;
  }

  /// <summary>An exception naming a node's position.</summary>
  public BenchmarkFileException Error(YamlNode node, string reason) => new(file, (int)node.Start.Line, (int)node.Start.Column, reason);

  /// <summary>The properties of a mapping by name; a name outside the allowed ones is an error.</summary>
  public Dictionary<string, YamlNode> Properties(YamlMappingNode mapping, string where, params string[] allowed) {
    var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
    foreach (var (key, value) in mapping.Children) {
      var name = key is YamlScalarNode { Value: { } text } ? text : throw this.Error(key, $"{where}: property names must be plain text");
      if (!allowed.Contains(name))
        throw this.Error(key, $"{where}: unknown property '{name}'");
      properties[name] = value;
    }

    return properties;
  }

  /// <summary>A required property.</summary>
  public YamlNode Required(Dictionary<string, YamlNode> properties, YamlNode owner, string name)
    => properties.TryGetValue(name, out var node) ? node : throw this.Error(owner, $"missing property '{name}'");

  /// <summary>A text value.</summary>
  public string Text(YamlNode node, string field)
    => node is YamlScalarNode { Value: { } text } scalar && (scalar.Style != ScalarStyle.Plain || text is not ("" or "~" or "null"))
      ? text
      : throw this.Error(node, $"'{field}' must be text");

  /// <summary>An integer value within a range.</summary>
  public long Integer(YamlNode node, string field, long minimum, long maximum = long.MaxValue)
    => node is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } text } && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
      ? value >= minimum && value <= maximum ? value : throw this.Error(node, $"'{field}' must be at least {minimum}" + (maximum == long.MaxValue ? "" : $" and at most {maximum}") + $", not {value}")
      : throw this.Error(node, $"'{field}' must be an integer");

  /// <summary>A non-negative decimal number.</summary>
  public decimal Number(YamlNode node, string field)
    => node is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } text } && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
      ? value >= 0 ? value : throw this.Error(node, $"'{field}' must not be negative, not {text}")
      : throw this.Error(node, $"'{field}' must be a number");

  /// <summary>A list of text values.</summary>
  public string[] TextList(YamlNode node, string field)
    => node is YamlSequenceNode sequence ? [.. sequence.Children.Select(item => this.Text(item, field))] : throw this.Error(node, $"'{field}' must be a list");

  /// <summary>A list of mappings.</summary>
  public YamlMappingNode[] Mappings(YamlNode node, string field)
    => node is YamlSequenceNode sequence
      ? [.. sequence.Children.Select(item => item as YamlMappingNode ?? throw this.Error(item, $"'{field}' must be a list of mappings"))]
      : throw this.Error(node, $"'{field}' must be a list");

  /// <summary>A mapping of text to text.</summary>
  public Dictionary<string, string> TextMap(YamlNode node, string field) {
    if (node is not YamlMappingNode mapping)
      throw this.Error(node, $"'{field}' must be a mapping");

    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (key, value) in mapping.Children)
      map[this.Text(key, field)] = this.Text(value, $"{field}.{((YamlScalarNode)key).Value}");
    return map;
  }

}
