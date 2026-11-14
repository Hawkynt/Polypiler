using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tooling.Diagnostics;

/// <summary>
/// Reads <c>spec/diagnostics.yaml</c> and checks the rules of ADR 0003: every code well-formed,
/// unique, in ascending order and inside a block of its own area; an exit code exactly on an error and
/// naming an <see cref="ExitCode"/> member; <c>strict</c> only on a warning. Every problem is a
/// <c>PLY0045</c> diagnostic at the place of the file it is about.
/// </summary>
public static class DiagnosticRegistryValidator {

  /// <summary>The code of every problem of the registry.</summary>
  public const string Code = "PLY0045";

  /// <summary>The registered diagnostic of <see cref="Code"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Descriptor = new(Code, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "adr-0003.registry");

  private static readonly string[] _rootKeys = ["schema", "blocks", "codes"];
  private static readonly string[] _blockKeys = ["first", "last", "area"];
  private static readonly string[] _codeKeys = ["code", "severity", "exit", "title", "message", "rule", "area", "owner", "surfaces", "strict", "reserved", "retired"];
  private static readonly string[] _requiredCodeKeys = ["code", "severity", "title", "message", "area", "owner"];

  /// <summary>Reads and checks a registry file.</summary>
  /// <param name="path">The file.</param>
  /// <returns>The registry with what is wrong with it; a file that cannot be read gives an empty registry with one problem.</returns>
  public static DiagnosticRegistry Validate(string path) {
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(path, [], [], [Diagnostic.Create(Descriptor, null, $"cannot read registry '{path}': {exception.Message}")]);
    }

    return Validate(text, path);
  }

  /// <summary>Checks the text of a registry.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name the diagnostics give the file.</param>
  /// <returns>The registry with what is wrong with it.</returns>
  public static DiagnosticRegistry Validate(string text, string file) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      return new(file, [], [], [Diagnostic.Create(Descriptor, At(file, exception.Start), $"cannot read registry '{file}': {exception.Message}")]);
    }

    return new Checker(file).Check(stream.Documents.Count == 1 ? stream.Documents[0].RootNode : null);
  }

  /// <summary>The place of a mark; YamlDotNet counts lines and columns from 1.</summary>
  private static SourceRange At(string file, Mark mark)
    => new(file, (int)mark.Line, (int)mark.Column, (int)mark.Line, (int)mark.Column);

  /// <summary>Walks the document and collects every problem.</summary>
  private sealed class Checker(string file) {

    private readonly List<Diagnostic> _problems = [];

    public DiagnosticRegistry Check(YamlNode? root) {
      if (root is not YamlMappingNode mapping) {
        this.Report(root, $"cannot read registry '{file}': not a YAML mapping");
        return new(file, [], [], this._problems);
      }

      var properties = this.Properties(mapping, "the registry", _rootKeys, _rootKeys);
      if (properties.TryGetValue("schema", out var schema) && Scalar(schema) != "1")
        this.Report(schema, $"unsupported schema '{Scalar(schema)}'");

      var blocks = properties.TryGetValue("blocks", out var blockList) ? this.Blocks(blockList) : [];
      var codes = properties.TryGetValue("codes", out var codeList) ? this.Codes(codeList, blocks) : [];
      return new(file, blocks, codes, this._problems);
    }

    private List<DiagnosticBlock> Blocks(YamlNode node) {
      var blocks = new List<(DiagnosticBlock Block, YamlNode Node)>();
      foreach (var item in this.Items(node, "blocks")) {
        if (item is not YamlMappingNode mapping) {
          this.Report(item, "a block must be a mapping");
          continue;
        }

        var properties = this.Properties(mapping, "a block", _blockKeys, _blockKeys);
        if (properties.Count < _blockKeys.Length)
          continue;

        var (first, last, area) = (Scalar(properties["first"]), Scalar(properties["last"]), Scalar(properties["area"]));
        if (!this.IsCode(first, properties["first"]) | !this.IsCode(last, properties["last"]))
          continue;

        if (string.CompareOrdinal(first, last) > 0) {
          this.Report(mapping, $"block {first}-{last} ends before it starts");
          continue;
        }

        var block = new DiagnosticBlock(first, last, area);
        foreach (var (other, _) in blocks.Where(other => Overlaps(other.Block, block)))
          this.Report(mapping, $"block {block} of '{area}' overlaps block {other} of '{other.Area}'");
        blocks.Add((block, mapping));
      }

      return [.. blocks.Select(block => block.Block)];
    }

    /// <summary>Whether two blocks share a code without one lying inside the other, or are the same range.</summary>
    private static bool Overlaps(DiagnosticBlock a, DiagnosticBlock b) {
      if (string.CompareOrdinal(a.Last, b.First) < 0 || string.CompareOrdinal(b.Last, a.First) < 0)
        return false;

      var aInB = b.Contains(a.First) && b.Contains(a.Last);
      var bInA = a.Contains(b.First) && a.Contains(b.Last);
      return aInB == bInA;
    }

    private List<DiagnosticEntry> Codes(YamlNode node, List<DiagnosticBlock> blocks) {
      var entries = new List<DiagnosticEntry>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      string? previous = null;
      foreach (var item in this.Items(node, "codes")) {
        if (item is not YamlMappingNode mapping) {
          this.Report(item, "a code must be a mapping");
          continue;
        }

        if (this.Entry(mapping, blocks) is not { } entry)
          continue;

        if (!seen.Add(entry.Code))
          this.Report(mapping, $"duplicate code {entry.Code}");
        else if (previous is not null && string.CompareOrdinal(entry.Code, previous) < 0)
          this.Report(mapping, $"{entry.Code} follows {previous}; codes are listed in ascending order");
        else
          entries.Add(entry);

        previous = string.CompareOrdinal(entry.Code, previous) > 0 ? entry.Code : previous;
      }

      return entries;
    }

    private DiagnosticEntry? Entry(YamlMappingNode mapping, List<DiagnosticBlock> blocks) {
      var properties = this.Properties(mapping, "a code", _codeKeys, _requiredCodeKeys);
      if (!properties.TryGetValue("code", out var codeNode) || !this.IsCode(Scalar(codeNode), codeNode))
        return null;

      var code = Scalar(codeNode);
      var count = this._problems.Count;
      foreach (var (key, value) in properties.Where(property => property.Value is not YamlScalarNode))
        this.Report(value, $"{code}: '{key}' must be a scalar");
      if (this._problems.Count > count)
        return null;

      var severity = DiagnosticSeverity.Error;
      if (properties.TryGetValue("severity", out var severityNode) && !TryParseSeverity(Scalar(severityNode), out severity))
        this.Report(severityNode, $"{code}: unknown severity '{Scalar(severityNode)}'");

      ExitCode? exitCode = null;
      if (properties.TryGetValue("exit", out var exitNode)) {
        if (Enum.GetNames<ExitCode>().Contains(Scalar(exitNode), StringComparer.Ordinal))
          exitCode = Enum.Parse<ExitCode>(Scalar(exitNode));
        else
          this.Report(exitNode, $"{code}: '{Scalar(exitNode)}' is not an exit code");
      }

      if (severity == DiagnosticSeverity.Error && exitNode is null)
        this.Report(mapping, $"{code}: an error needs an exit code");
      else if (severity != DiagnosticSeverity.Error && exitNode is not null)
        this.Report(exitNode, $"{code}: only errors carry an exit code");

      var strict = this.Flag(properties, "strict", code);
      if (strict && severity != DiagnosticSeverity.Warning)
        this.Report(properties["strict"], $"{code}: only warnings can be strict");

      var area = properties.TryGetValue("area", out var areaNode) ? Scalar(areaNode) : "";
      var block = blocks.Where(candidate => candidate.Contains(code)).MinBy(candidate => int.Parse(candidate.Last[3..]) - int.Parse(candidate.First[3..]));
      if (block is null)
        this.Report(codeNode, $"{code} lies in no block");
      else if (areaNode is not null && block.Area != area)
        this.Report(areaNode, $"{code} is registered for area '{area}' but lies in the block of '{block.Area}'");

      return new(
        code, severity, exitCode,
        Text(properties, "title"), Text(properties, "message"), properties.TryGetValue("rule", out var rule) ? Scalar(rule) : null,
        area, Text(properties, "owner"), properties.TryGetValue("surfaces", out var surfaces) ? Scalar(surfaces) : null,
        strict, this.Flag(properties, "reserved", code), this.Flag(properties, "retired", code));
    }

    private static bool TryParseSeverity(string text, out DiagnosticSeverity severity) {
      foreach (var candidate in Enum.GetValues<DiagnosticSeverity>())
        if (DiagnosticRenderer.SeverityName(candidate) == text) {
          severity = candidate;
          return true;
        }

      severity = DiagnosticSeverity.Error;
      return false;
    }

    private bool Flag(Dictionary<string, YamlNode> properties, string key, string code) {
      if (!properties.TryGetValue(key, out var node))
        return false;

      switch (Scalar(node)) {
        case "true":
          return true;
        case "false":
          return false;
        default:
          this.Report(node, $"{code}: '{key}' must be true or false");
          return false;
      }
    }

    private bool IsCode(string text, YamlNode node) {
      if (DiagnosticDescriptor.IsCode(text))
        return true;

      this.Report(node, $"'{text}' is not a diagnostic code");
      return false;
    }

    private IEnumerable<YamlNode> Items(YamlNode node, string key) {
      if (node is YamlSequenceNode sequence)
        return sequence.Children;

      this.Report(node, $"property '{key}' must be a list");
      return [];
    }

    private Dictionary<string, YamlNode> Properties(YamlMappingNode mapping, string what, string[] allowed, string[] required) {
      var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
      foreach (var (keyNode, value) in mapping.Children) {
        var key = Scalar(keyNode);
        // The YAML reader already refuses a key given twice.
        if (allowed.Contains(key, StringComparer.Ordinal))
          properties.Add(key, value);
        else
          this.Report(keyNode, $"unknown property '{key}' in {what}");
      }

      foreach (var key in required.Where(key => !properties.ContainsKey(key)))
        this.Report(mapping, $"missing property '{key}' in {what}");
      return properties;
    }

    /// <summary>The text of a scalar, which the YAML reader never leaves without one; "" for a list or a mapping.</summary>
    private static string Scalar(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value! : "";

    private static string Text(Dictionary<string, YamlNode> properties, string key) => properties.TryGetValue(key, out var node) ? Scalar(node) : "";

    private void Report(YamlNode? node, string message)
      => this._problems.Add(Diagnostic.Create(Descriptor, node is null ? null : At(file, node.Start), message));

  }

}
