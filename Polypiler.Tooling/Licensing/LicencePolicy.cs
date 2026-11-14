using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tooling.Licensing;

/// <summary>A reviewed per-path licence: files matching the glob carry the licence whatever else they say.</summary>
/// <param name="Glob">The files it applies to; see <see cref="LicencePolicy.Matches"/>.</param>
/// <param name="Licence">The licence as an SPDX expression.</param>
/// <param name="Reason">Why the licence is what it is.</param>
/// <param name="Reviewer">Who reviewed it.</param>
public sealed record LicenceOverride(GlobPattern Glob, string Licence, string Reason, string Reviewer);

/// <summary>A licence somebody asked to be allowed; it stays denied until the owner decides.</summary>
/// <param name="Licence">The SPDX identifier.</param>
/// <param name="RequestedBy">The issue that asked.</param>
/// <param name="Question">What the owner is asked.</param>
public sealed record PendingDecision(string Licence, string RequestedBy, string Question);

/// <summary>
/// <c>tooling/licensing/policy.yaml</c>: which licences are allowed, which are reference-only, which
/// exceptions are accepted, which files may never be redistributed, the reviewed per-path overrides
/// and the requests that await the owner. Every identifier of the three lists needs a row in
/// <c>DECISIONS.md</c> beside the policy, whose last row for it names the same list; an identifier in
/// two lists, or both listed and pending, is refused. Any identifier in no list is denied.
/// </summary>
public sealed class LicencePolicy {

  /// <summary>The policy's path below the repository root.</summary>
  public const string RepositoryPath = "tooling/licensing/policy.yaml";

  /// <summary>The file beside the policy that records the owner's decisions.</summary>
  public const string DecisionsFile = "DECISIONS.md";

  /// <summary>The folder beside the policy that holds the reference licence texts.</summary>
  public const string TextsFolder = "texts";

  private const string What = "licence policy";

  private static readonly string[] _keys = ["schema", "allowed", "referenceOnly", "allowedExceptions", "restrictedAssets", "overrides", "pendingDecisions"];
  private static readonly string[] _lists = ["allowed", "referenceOnly", "allowedExceptions"];
  private static readonly Regex _identifier = new(@"^[A-Za-z0-9.\-]+\+?$", RegexOptions.CultureInvariant);
  private static readonly Regex _quoted = new("`([^`]+)`", RegexOptions.CultureInvariant);

  private readonly HashSet<string> _allowed;
  private readonly HashSet<string> _referenceOnly;
  private readonly HashSet<string> _exceptions;

  private LicencePolicy(IReadOnlyList<string> allowed, IReadOnlyList<string> referenceOnly, IReadOnlyList<string> exceptions, IReadOnlyList<GlobPattern> restricted, IReadOnlyList<LicenceOverride> overrides, IReadOnlyList<PendingDecision> pending) {
    this.Allowed = allowed;
    this.ReferenceOnly = referenceOnly;
    this.AllowedExceptions = exceptions;
    this.RestrictedAssets = restricted;
    this.Overrides = overrides;
    this.PendingDecisions = pending;
    this._allowed = [.. allowed];
    this._referenceOnly = [.. referenceOnly];
    this._exceptions = [.. exceptions];
  }

  /// <summary>The licences that may be sources of shipped code, in file order.</summary>
  public IReadOnlyList<string> Allowed { get; }

  /// <summary>The licences that may serve as oracles only, in file order.</summary>
  public IReadOnlyList<string> ReferenceOnly { get; }

  /// <summary>The exceptions a <c>WITH</c> may name.</summary>
  public IReadOnlyList<string> AllowedExceptions { get; }

  /// <summary>The files that are never redistributed unless an override names them.</summary>
  public IReadOnlyList<GlobPattern> RestrictedAssets { get; }

  /// <summary>The reviewed per-path licences, in file order.</summary>
  public IReadOnlyList<LicenceOverride> Overrides { get; }

  /// <summary>The licences that await an owner decision.</summary>
  public IReadOnlyList<PendingDecision> PendingDecisions { get; }

  /// <summary>
  /// Whether a path matches a glob: a glob without <c>/</c> is matched against the file name alone,
  /// at any depth, as <c>*.bin</c> is meant; any other against the whole path.
  /// </summary>
  public static bool Matches(GlobPattern glob, string path)
    => glob.IsMatch(glob.Text.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..]);

  /// <summary>The status of one licence identifier in a mode: a reference-only licence is denied in a distribution.</summary>
  public LicenceStatus StatusOf(string licence, LicenceMode mode) {
    if (this._allowed.Contains(licence))
      return LicenceStatus.Allowed;
    return this._referenceOnly.Contains(licence) && mode == LicenceMode.Corpus ? LicenceStatus.ReferenceOnly : LicenceStatus.Denied;
  }

  /// <summary>The status of an expression in a mode.</summary>
  public LicenceStatus Evaluate(SpdxExpression expression, LicenceMode mode)
    => expression.Evaluate(licence => this.StatusOf(licence, mode), this._exceptions.Contains);

  /// <summary>The first override whose glob matches a path, or <see langword="null"/>.</summary>
  public LicenceOverride? Override(string path) => this.Overrides.FirstOrDefault(entry => Matches(entry.Glob, path));

  /// <summary>Whether a path is a restricted asset.</summary>
  public bool IsRestricted(string path) => this.RestrictedAssets.Any(glob => Matches(glob, path));

  /// <summary>The pending decision of an identifier, or <see langword="null"/>.</summary>
  public PendingDecision? Pending(string licence) => this.PendingDecisions.FirstOrDefault(entry => entry.Licence == licence);

  /// <summary>Reads a policy file and the <see cref="DecisionsFile"/> beside it.</summary>
  /// <exception cref="CorpusException">A file cannot be read or the policy is invalid (<c>PLY0026</c>).</exception>
  public static LicencePolicy Load(string path) {
    var decisions = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, DecisionsFile);
    return Parse(Read(path), path, File.Exists(decisions) ? Read(decisions) : "");
  }

  private static string Read(string path) {
    try {
      return File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"cannot read '{path}': {exception.Message}"));
    }
  }

  /// <summary>Checks the text of a policy against the text of its decisions.</summary>
  /// <param name="text">The YAML text of the policy.</param>
  /// <param name="file">The name diagnostics give the policy.</param>
  /// <param name="decisions">The Markdown text of <see cref="DecisionsFile"/>; empty when there is none.</param>
  /// <exception cref="CorpusException">The policy is invalid (<c>PLY0026</c>), with every problem found.</exception>
  public static LicencePolicy Parse(string text, string file, string decisions) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new CorpusException(LicenceDiagnostics.Invalid(What, $"not YAML: {exception.Message}", file, (int)exception.Start.Line, (int)exception.Start.Column));
    }

    if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
      throw new CorpusException(LicenceDiagnostics.Invalid(What, "the policy must be one YAML mapping", file));

    return new Checker(file).Check(root, Decisions(decisions));
  }

  /// <summary>The list the last row of each identifier of a decisions table names.</summary>
  private static Dictionary<string, string> Decisions(string text) {
    var decided = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in text.Split('\n')) {
      var cells = line.Trim().Split('|');
      if (cells.Length < 4 || cells[0].Length != 0)
        continue;
      var list = cells[2].Trim();
      foreach (Match match in _quoted.Matches(cells[1]))
        decided[match.Groups[1].Value] = list;
    }

    return decided;
  }

  /// <summary>Walks the document and collects every problem before it gives up.</summary>
  private sealed class Checker(string file) {

    private readonly List<Diagnostic> _problems = [];

    public LicencePolicy Check(YamlMappingNode root, Dictionary<string, string> decided) {
      var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
      foreach (var (key, value) in root.Children) {
        var name = Text(key);
        if (_keys.Contains(name))
          properties[name] = value;
        else
          this.Report(key, $"unknown property '{name}'");
      }

      foreach (var name in _keys.Where(name => !properties.ContainsKey(name)))
        this.Report(root, $"missing property '{name}'");
      if (properties.TryGetValue("schema", out var schema) && Text(schema) != "1")
        this.Report(schema, $"unsupported schema '{Text(schema)}'");

      var lists = _lists.ToDictionary(name => name, name => this.Identifiers(properties, name), StringComparer.Ordinal);
      var restricted = this.Strings(properties, "restrictedAssets").Select(item => GlobPattern.Parse(item.Value)).ToList();
      var overrides = this.Entries(properties, "overrides", ["glob", "licence", "reason", "reviewer"])
        .Select(this.Override).OfType<LicenceOverride>().ToList();
      var pending = this.Entries(properties, "pendingDecisions", ["licence", "requestedBy", "question"])
        .Select(entry => new PendingDecision(entry["licence"].Value, entry["requestedBy"].Value, entry["question"].Value)).ToList();

      var seen = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (list, items) in lists) {
        foreach (var item in items) {
          if (seen.TryGetValue(item.Value, out var other)) {
            this.Report(item.Node, $"'{item.Value}' is listed in both '{other}' and '{list}'");
            continue;
          }

          seen[item.Value] = list;
          if (!decided.TryGetValue(item.Value, out var decision))
            this.Report(item.Node, $"'{item.Value}' in '{list}' has no row in {DecisionsFile}");
          else if (decision != list)
            this.Report(item.Node, $"'{item.Value}' is in '{list}' but {DecisionsFile} decided '{decision}'");
        }
      }

      foreach (var request in pending.Where(request => seen.ContainsKey(request.Licence)))
        this.Report(properties["pendingDecisions"], $"'{request.Licence}' awaits a decision but is listed in '{seen[request.Licence]}'");

      if (this._problems.Count > 0)
        throw new CorpusException(this._problems);
      return new([.. lists["allowed"].Select(item => item.Value)], [.. lists["referenceOnly"].Select(item => item.Value)],
        [.. lists["allowedExceptions"].Select(item => item.Value)], restricted, overrides, pending);
    }

    private LicenceOverride? Override(Dictionary<string, (string Value, YamlNode Node)> entry) {
      var (licence, node) = entry["licence"];
      try {
        SpdxExpression.Parse(licence);
      } catch (FormatException exception) {
        this.Report(node, $"override licence '{licence}' does not parse: {exception.Message}");
        return null;
      }

      return new(GlobPattern.Parse(entry["glob"].Value), licence, entry["reason"].Value, entry["reviewer"].Value);
    }

    private List<(string Value, YamlNode Node)> Identifiers(Dictionary<string, YamlNode> properties, string name) {
      var items = this.Strings(properties, name);
      foreach (var item in items.Where(item => !_identifier.IsMatch(item.Value)))
        this.Report(item.Node, $"'{item.Value}' in '{name}' is not an SPDX identifier");
      return [.. items.Where(item => _identifier.IsMatch(item.Value))];
    }

    private List<(string Value, YamlNode Node)> Strings(Dictionary<string, YamlNode> properties, string name) {
      if (!properties.TryGetValue(name, out var node))
        return [];
      if (node is YamlSequenceNode list && list.Children.All(IsString))
        return [.. list.Children.Select(item => (Text(item), item))];

      this.Report(node, $"'{name}' must be a list of non-empty strings");
      return [];
    }

    private List<Dictionary<string, (string Value, YamlNode Node)>> Entries(Dictionary<string, YamlNode> properties, string name, string[] fields) {
      if (!properties.TryGetValue(name, out var node))
        return [];
      if (node is not YamlSequenceNode list) {
        this.Report(node, $"'{name}' must be a list");
        return [];
      }

      var entries = new List<Dictionary<string, (string Value, YamlNode Node)>>();
      foreach (var item in list.Children) {
        if (item is not YamlMappingNode mapping) {
          this.Report(item, $"an entry of '{name}' must be a mapping");
          continue;
        }

        var entry = new Dictionary<string, (string Value, YamlNode Node)>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping.Children) {
          var field = Text(key);
          if (!fields.Contains(field))
            this.Report(key, $"unknown property '{field}' in an entry of '{name}'");
          else if (!IsString(value))
            this.Report(value, $"'{field}' of an entry of '{name}' must be a non-empty string");
          else
            entry[field] = (Text(value), value);
        }

        var missing = fields.Where(field => !entry.ContainsKey(field)).ToList();
        foreach (var field in missing.Where(field => !mapping.Children.ContainsKey(new YamlScalarNode(field))))
          this.Report(mapping, $"missing property '{field}' in an entry of '{name}'");
        if (missing.Count == 0)
          entries.Add(entry);
      }

      return entries;
    }

    private static bool IsString(YamlNode node)
      => node is YamlScalarNode { Value.Length: > 0 } scalar && !(scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null");

    private static string Text(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value! : "?";

    private void Report(YamlNode node, string reason)
      => this._problems.Add(LicenceDiagnostics.Invalid(What, reason, file, (int)node.Start.Line, (int)node.Start.Column));

  }

}
