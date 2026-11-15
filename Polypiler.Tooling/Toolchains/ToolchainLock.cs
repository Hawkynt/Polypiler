using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Polypiler.Tooling.Toolchains;

/// <summary>
/// <c>tooling/toolchains.lock</c>: the one place an exact toolchain version is pinned. Its format is
/// <c>spec/schemas/toolchains.schema.json</c>; on top of the schema the loader requires the keys in
/// ordinal order, each at most once, every <c>versionPattern</c> with a group <c>version</c> and every
/// package naming a host toolchain that is not a package itself.
/// </summary>
public sealed class ToolchainLock {

  /// <summary>The only schema version this loader reads.</summary>
  public const int SchemaVersion = 1;

  /// <summary>The lock's path below the repository root.</summary>
  public const string RepositoryPath = "tooling/toolchains.lock";

  /// <summary>
  /// The toolchains every lock of the repository pins (<c>backlog/README.md</c>, "Toolchains"). A key
  /// outside this list is optional: <c>probe</c> reports it only when asked for it.
  /// </summary>
  public static IReadOnlyList<string> CoreKeys { get; } = [
    "clang", "dosbox-x", "dotnet-sdk", "fbc", "fpc", "gcc", "go", "ilspy-cli", "ilverify", "jdk", "kotlinc", "llvm", "nasm",
    "node", "pb-compiler", "perl", "php", "python", "qemu-user", "ruby", "rust", "typescript", "vice", "wabt", "wasmtime", "z3"
  ];

  /// <summary>The platforms an entry may name.</summary>
  public static IReadOnlyList<string> KnownPlatforms { get; } = ["linux", "windows", "macos"];

  private static readonly Regex _key = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

  private static readonly string[] _required = ["version", "probe", "versionPattern", "platforms", "licence", "source"];

  private static readonly string[] _optional = ["home", "oracleOnly", "kind", "requires", "roots"];

  private static readonly Regex _variable = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

  private static readonly Dictionary<string, ToolchainKind> _kinds = new(StringComparer.Ordinal) {
    ["tool"] = ToolchainKind.Tool,
    ["package"] = ToolchainKind.Package,
    ["nuget"] = ToolchainKind.NuGet,
    ["external"] = ToolchainKind.External
  };

  private readonly Dictionary<string, ToolchainEntry> _byKey;

  private ToolchainLock(string file, IReadOnlyList<ToolchainEntry> entries, int line, int column) {
    this.File = file;
    this.Entries = entries;
    this.Line = line;
    this.Column = column;
    this._byKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
  }

  /// <summary>The lock file as it was named.</summary>
  public string File { get; }

  /// <summary>The toolchains, in ordinal key order.</summary>
  public IReadOnlyList<ToolchainEntry> Entries { get; }

  /// <summary>The line of the <c>toolchains</c> property.</summary>
  public int Line { get; }

  /// <summary>The column of the <c>toolchains</c> property.</summary>
  public int Column { get; }

  /// <summary>The toolchain under a key.</summary>
  /// <exception cref="ToolchainLockException">The lock has no such key (<c>PLY0020</c>).</exception>
  public ToolchainEntry this[string key]
    => this.TryGet(key, out var entry) ? entry : throw new ToolchainLockException([this.UnknownKey(key)]);

  /// <summary>Looks a toolchain up by its key.</summary>
  public bool TryGet(string key, out ToolchainEntry entry) => this._byKey.TryGetValue(key, out entry!);

  /// <summary>The diagnostic for a key the lock does not have.</summary>
  public ToolchainDiagnostic UnknownKey(string key)
    => new(this.File, 0, 0, ToolchainDiagnostic.LockInvalid, $"unknown toolchain key '{key}'");

  /// <summary>The core toolchains the lock lacks, each as a <c>PLY0020</c> diagnostic.</summary>
  public IReadOnlyList<ToolchainDiagnostic> MissingCoreKeys()
    => [.. CoreKeys.Where(key => !this._byKey.ContainsKey(key))
      .Select(key => new ToolchainDiagnostic(this.File, this.Line, this.Column, ToolchainDiagnostic.LockInvalid, $"missing core toolchain '{key}'"))];

  /// <summary>Reads and checks a lock file.</summary>
  /// <param name="path">The lock file.</param>
  /// <exception cref="ToolchainLockException">The file cannot be read (<c>PLY0004</c>) or is not a valid lock (<c>PLY0020</c>).</exception>
  public static ToolchainLock Load(string path) {
    string text;
    try {
      text = System.IO.File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new ToolchainLockException([new(path, 0, 0, ToolchainDiagnostic.Unreadable, $"cannot read the toolchain lock: {exception.Message}")]);
    }

    return Parse(text, path);
  }

  /// <summary>Checks the text of a lock.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name diagnostics give the file.</param>
  /// <exception cref="ToolchainLockException">The text is not a valid lock (<c>PLY0020</c>).</exception>
  public static ToolchainLock Parse(string text, string file) {
    Node root;
    try {
      root = ReadDocument(new Parser(new StringReader(text)));
    } catch (YamlException exception) {
      throw new ToolchainLockException([new(file, LineOf(exception.Start), ColumnOf(exception.Start), ToolchainDiagnostic.LockInvalid, $"not a YAML mapping: {exception.Message}")]);
    }

    return new Checker(file).Check(root);
  }

  private static int LineOf(Mark mark) => (int)mark.Line;

  private static int ColumnOf(Mark mark) => (int)mark.Column;

  private static Node ReadDocument(Parser parser) {
    parser.Consume<StreamStart>();
    if (parser.Accept<StreamEnd>(out _))
      return new AliasNode(1, 1);

    parser.Consume<DocumentStart>();
    var root = ReadNode(parser);
    parser.Consume<DocumentEnd>();
    if (!parser.Accept<StreamEnd>(out _)) {
      var start = parser.Current!.Start;
      throw new YamlException(start, start, "the lock holds more than one YAML document");
    }

    return root;
  }

  private static Node ReadNode(Parser parser) {
    var start = parser.Current!.Start;
    var (line, column) = (LineOf(start), ColumnOf(start));
    if (parser.TryConsume<Scalar>(out var scalar))
      return new ScalarNode(line, column, scalar.Value, scalar.Style == ScalarStyle.Plain);

    if (parser.TryConsume<SequenceStart>(out _)) {
      var items = new List<Node>();
      while (!parser.TryConsume<SequenceEnd>(out _))
        items.Add(ReadNode(parser));
      return new SequenceNode(line, column, items);
    }

    if (parser.TryConsume<MappingStart>(out _)) {
      var pairs = new List<(Node Key, Node Value)>();
      while (!parser.TryConsume<MappingEnd>(out _))
        pairs.Add((ReadNode(parser), ReadNode(parser)));
      return new MappingNode(line, column, pairs);
    }

    parser.Consume<AnchorAlias>();
    return new AliasNode(line, column);
  }

  private abstract record Node(int Line, int Column);

  private sealed record ScalarNode(int Line, int Column, string Value, bool Plain) : Node(Line, Column);

  private sealed record SequenceNode(int Line, int Column, IReadOnlyList<Node> Items) : Node(Line, Column);

  private sealed record MappingNode(int Line, int Column, IReadOnlyList<(Node Key, Node Value)> Pairs) : Node(Line, Column);

  private sealed record AliasNode(int Line, int Column) : Node(Line, Column);

  /// <summary>Walks the document and collects every problem before it gives up.</summary>
  private sealed class Checker(string file) {

    private readonly List<ToolchainDiagnostic> _problems = [];

    public ToolchainLock Check(Node root) {
      if (root is not MappingNode mapping)
        throw new ToolchainLockException([this.Problem(root, "not a YAML mapping")]);

      var properties = this.Properties(mapping, "the lock", ["schema", "toolchains"], []);
      if (properties.TryGetValue("schema", out var schema) && schema.Value is not ScalarNode { Plain: true, Value: "1" })
        this.Report(schema.Value, $"unsupported schema {Text(schema.Value)}");

      var entries = new List<ToolchainEntry>();
      var (line, column) = (mapping.Line, mapping.Column);
      if (properties.TryGetValue("toolchains", out var toolchains)) {
        (line, column) = (toolchains.Key.Line, toolchains.Key.Column);
        if (toolchains.Value is MappingNode table)
          entries = this.Entries(table);
        else
          this.Report(toolchains.Value, "property 'toolchains' must be a mapping");
      }

      if (this._problems.Count > 0)
        throw new ToolchainLockException(this._problems);
      return new(file, entries, line, column);
    }

    private List<ToolchainEntry> Entries(MappingNode table) {
      var entries = new List<ToolchainEntry>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      string? previous = null;
      foreach (var (keyNode, value) in table.Pairs) {
        var key = Text(keyNode);
        if (keyNode is not ScalarNode || !_key.IsMatch(key)) {
          this.Report(keyNode, $"invalid key '{key}'");
          continue;
        }

        if (!seen.Add(key)) {
          this.Report(keyNode, $"duplicate key '{key}'");
          continue;
        }

        if (previous != null && string.CompareOrdinal(previous, key) > 0)
          this.Report(keyNode, $"keys are not in ordinal order: '{key}' after '{previous}'");
        previous = key;

        if (value is MappingNode body) {
          if (this.Entry(key, keyNode, body) is { } entry)
            entries.Add(entry);
        } else
          this.Report(value, $"toolchain '{key}' must be a mapping");
      }

      foreach (var entry in entries)
        this.CheckHost(entry, entries);
      entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
      return entries;
    }

    private void CheckHost(ToolchainEntry entry, List<ToolchainEntry> entries) {
      if (entry.Requires == null) {
        if (entry.Kind is ToolchainKind.Package or ToolchainKind.NuGet)
          this.Report(entry.Line, entry.Column, $"'{entry.Key}' is a package but names no host toolchain");
        return;
      }

      var host = entries.Find(candidate => candidate.Key == entry.Requires);
      if (host == null)
        this.Report(entry.Line, entry.Column, $"unknown toolchain key '{entry.Requires}'");
      else if (host.Kind is ToolchainKind.Package or ToolchainKind.NuGet)
        this.Report(entry.Line, entry.Column, $"'{entry.Key}' requires '{host.Key}', which is itself a package");
    }

    private ToolchainEntry? Entry(string key, Node keyNode, MappingNode body) {
      var before = this._problems.Count;
      var properties = this.Properties(body, $"'{key}'", _required, _optional);
      foreach (var name in _required.Where(name => !properties.ContainsKey(name)))
        this.Report(keyNode, $"missing property '{name}' in '{key}'");

      var version = this.String(properties, "version", key);
      var probe = this.Strings(properties, "probe", key);
      var pattern = this.String(properties, "versionPattern", key);
      var platforms = this.Strings(properties, "platforms", key);
      var licence = this.String(properties, "licence", key);
      var source = this.String(properties, "source", key);
      var home = this.String(properties, "home", key);
      var requires = this.String(properties, "requires", key);
      var roots = this.Strings(properties, "roots", key);
      foreach (var variable in roots?.Where(variable => !_variable.IsMatch(variable)) ?? [])
        this.Report(properties["roots"].Value, $"root '{variable}' of '{key}' is not the name of an environment variable");
      var oracleOnly = false;
      var kind = ToolchainKind.Tool;

      if (pattern != null)
        this.CheckPattern(properties["versionPattern"].Value, pattern, key);

      foreach (var platform in platforms?.Where(platform => !KnownPlatforms.Contains(platform)) ?? [])
        this.Report(properties["platforms"].Value, $"unknown platform '{platform}' in '{key}'");

      if (properties.TryGetValue("oracleOnly", out var flag)) {
        if (flag.Value is ScalarNode { Plain: true, Value: "true" or "false" } truth)
          oracleOnly = truth.Value == "true";
        else
          this.Report(flag.Value, $"property 'oracleOnly' of '{key}' must be true or false");
      }

      if (this.String(properties, "kind", key) is { } kindName && !_kinds.TryGetValue(kindName, out kind))
        this.Report(properties["kind"].Value, $"unknown kind '{kindName}' in '{key}'");

      if (this._problems.Count > before)
        return null;

      return new(key, version!, probe!, pattern!, platforms!, licence!, source!) {
        Home = home,
        OracleOnly = oracleOnly,
        Kind = kind,
        Requires = requires,
        Roots = (IReadOnlyList<string>?)roots ?? [],
        Line = keyNode.Line,
        Column = keyNode.Column
      };
    }

    private void CheckPattern(Node node, string pattern, string key) {
      try {
        if (!new Regex(pattern, RegexOptions.CultureInvariant).GetGroupNames().Contains("version"))
          this.Report(node, $"versionPattern of '{key}' has no group 'version'");
      } catch (ArgumentException exception) {
        this.Report(node, $"versionPattern of '{key}' is not a regular expression: {exception.Message}");
      }
    }

    private Dictionary<string, (Node Key, Node Value)> Properties(MappingNode mapping, string owner, string[] required, string[] optional) {
      var properties = new Dictionary<string, (Node Key, Node Value)>(StringComparer.Ordinal);
      foreach (var (keyNode, value) in mapping.Pairs) {
        var name = Text(keyNode);
        if (!required.Contains(name) && !optional.Contains(name))
          this.Report(keyNode, $"unknown property '{name}' in {owner}");
        else if (!properties.TryAdd(name, (keyNode, value)))
          this.Report(keyNode, $"duplicate key '{name}'");
      }

      if (owner == "the lock")
        foreach (var name in required.Where(name => !properties.ContainsKey(name)))
          this.Report(mapping, $"missing property '{name}' in the lock");
      return properties;
    }

    private string? String(Dictionary<string, (Node Key, Node Value)> properties, string name, string key) {
      if (!properties.TryGetValue(name, out var property))
        return null;
      if (IsString(property.Value))
        return ((ScalarNode)property.Value).Value;

      this.Report(property.Value, $"property '{name}' of '{key}' must be a string");
      return null;
    }

    private List<string>? Strings(Dictionary<string, (Node Key, Node Value)> properties, string name, string key) {
      if (!properties.TryGetValue(name, out var property))
        return null;
      if (property.Value is SequenceNode { Items.Count: > 0 } list && list.Items.All(IsString))
        return [.. list.Items.Cast<ScalarNode>().Select(item => item.Value)];

      this.Report(property.Value, $"property '{name}' of '{key}' must be a list of at least one string");
      return null;
    }

    private static bool IsString(Node node)
      => node is ScalarNode { Value.Length: > 0 } scalar && !(scalar.Plain && scalar.Value is "~" or "null");

    private static string Text(Node node) => node is ScalarNode scalar ? scalar.Value : "?";

    private ToolchainDiagnostic Problem(Node node, string reason)
      => new(file, node.Line, node.Column, ToolchainDiagnostic.LockInvalid, reason);

    private void Report(Node node, string reason) => this._problems.Add(this.Problem(node, reason));

    private void Report(int line, int column, string reason)
      => this._problems.Add(new(file, line, column, ToolchainDiagnostic.LockInvalid, reason));

  }

}
