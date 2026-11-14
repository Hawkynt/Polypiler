using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Polypiler.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Architecture;

/// <summary>One broken module rule.</summary>
/// <param name="From">The project that breaks the rule.</param>
/// <param name="To">The referenced project or path; empty when the rule concerns <paramref name="From"/> alone.</param>
/// <param name="Rule">The rule: <c>layer m -> n</c>, <c>same layer</c>, <c>restricted</c>, <c>standalone</c>,
/// <c>cycle</c>, <c>uncovered</c> or <c>missing</c>.</param>
/// <param name="Message">The diagnostic text, starting with <see cref="ModuleRules.Code"/>.</param>
public sealed record RuleViolation(string From, string To, string Rule, string Message);

/// <summary>A project and the projects it references, by name.</summary>
/// <param name="Name">The project name: the file name without its extension.</param>
/// <param name="References">The names of the referenced projects whose files exist.</param>
public sealed record ProjectFile(string Name, IReadOnlyList<string> References) {

  /// <summary>The <c>ProjectReference</c> paths, as written, that lead to no file.</summary>
  public IReadOnlyList<string> MissingReferences { get; init; } = [];

  /// <summary>Reads the project references of one project file.</summary>
  /// <param name="path">The <c>*.csproj</c> file.</param>
  public static ProjectFile Read(string path) {
    var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
    var references = new List<string>();
    var missing = new List<string>();
    var includes = XDocument.Load(path).Descendants()
      .Where(element => element.Name.LocalName == "ProjectReference")
      .Select(element => (string?)element.Attribute("Include"))
      .OfType<string>();
    foreach (var include in includes) {
      var target = include.Replace('\\', '/');
      if (File.Exists(Path.Combine(directory, target)))
        references.Add(Path.GetFileNameWithoutExtension(target));
      else
        missing.Add(include);
    }

    return new(Path.GetFileNameWithoutExtension(path), references) { MissingReferences = missing };
  }

  /// <summary>
  /// Reads every project file below a directory. Build output (<c>bin</c>, <c>obj</c>), directories
  /// whose name starts with a dot and the fixture tree <c>tests/fixtures</c> are not searched: what
  /// lies there is data, not a project of the repository.
  /// </summary>
  /// <param name="root">The directory to search.</param>
  public static IReadOnlyList<ProjectFile> Discover(string root) {
    var fixtures = Path.GetFullPath(Path.Combine(root, "tests", "fixtures"));
    var found = new List<ProjectFile>();
    var pending = new Stack<string>();
    pending.Push(Path.GetFullPath(root));
    while (pending.Count > 0) {
      var directory = pending.Pop();
      found.AddRange(Directory.GetFiles(directory, "*.csproj").Select(Read));
      foreach (var child in Directory.GetDirectories(directory)) {
        var name = Path.GetFileName(child);
        if (!name.StartsWith('.') && name is not ("bin" or "obj") && child != fixtures)
          pending.Push(child);
      }
    }

    return found.OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
  }

}

/// <summary>
/// The module rules of <c>tooling/ci/module-rules.yaml</c>: the layer of every project and which
/// project references they allow (PRD §5.3, §5.4).
/// </summary>
public sealed class ModuleRules {

  /// <summary>The diagnostic code every violation message starts with.</summary>
  public const string Code = "PLY0038";

  /// <summary>The registered diagnostic of <see cref="Code"/>, as <c>spec/diagnostics.yaml</c> records it.</summary>
  [Diagnostic]
  public static readonly DiagnosticDescriptor Descriptor = new(Code, DiagnosticSeverity.Error, ExitCode.EnvironmentError, "{0}", "modules.dependency-direction");

  private readonly string _source;
  private readonly List<(string Text, Regex Match, int Layer)> _layers = [];
  private readonly HashSet<(string From, string To)> _sameLayerExceptions = [];
  private readonly HashSet<string> _standalone = new(StringComparer.Ordinal);
  private readonly List<(Regex Match, HashSet<int> AllowedFromLayers)> _restricted = [];

  private ModuleRules(string source) => this._source = source;

  /// <summary>Loads a rule file.</summary>
  /// <param name="path">The rule file.</param>
  /// <exception cref="FileNotFoundException">The file does not exist.</exception>
  /// <exception cref="InvalidDataException">The file is not a schema 1 rule file; the message names
  /// the file, the line and the offending key.</exception>
  public static ModuleRules Load(string path) {
    if (!File.Exists(path))
      throw new FileNotFoundException($"module rule file not found: {path}", path);

    var rules = new ModuleRules(DisplayName(path));
    var stream = new YamlStream();
    try {
      using var reader = new StreamReader(path);
      stream.Load(reader);
    } catch (YamlException e) {
      throw new InvalidDataException($"{rules._source}:{e.Start.Line}: {e.Message}", e);
    }

    if (stream.Documents.Count == 0)
      throw new InvalidDataException($"{rules._source}:1: missing key 'schema'");

    rules.Parse(stream.Documents[0].RootNode);
    return rules;
  }

  /// <summary>Gets the layer of a project.</summary>
  /// <param name="projectName">The project name.</param>
  /// <returns>The layer; <see langword="null"/> for a standalone project and for one no rule covers.</returns>
  /// <exception cref="InvalidDataException">More than one rule matches the project.</exception>
  public int? LayerOf(string projectName) {
    var matches = this._layers.Where(rule => rule.Match.IsMatch(projectName)).ToArray();
    var names = matches.Select(rule => rule.Text);
    if (this._standalone.Contains(projectName))
      names = names.Append("standalone");
    if (names.Count() > 1)
      throw new InvalidDataException(
        $"{this._source}: ambiguous rule for project {projectName}: matched by {string.Join(", ", names)}");

    return matches.Length == 1 ? matches[0].Layer : null;
  }

  /// <summary>Tells whether a project has a layer or is standalone.</summary>
  /// <param name="projectName">The project name.</param>
  /// <exception cref="InvalidDataException">More than one rule matches the project.</exception>
  public bool Covers(string projectName)
    => this.LayerOf(projectName) is not null || this._standalone.Contains(projectName);

  /// <summary>Checks the references of the given projects against the rules.</summary>
  /// <param name="projects">The projects to check.</param>
  /// <returns>Every violation, ordered by project; reference cycles come last.</returns>
  /// <exception cref="InvalidDataException">More than one rule matches a project.</exception>
  public IReadOnlyList<RuleViolation> Check(IEnumerable<ProjectFile> projects) {
    var files = projects.OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
    var violations = new List<RuleViolation>();
    var uncovered = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in files) {
      this.CheckCovered(file.Name, uncovered, violations);
      foreach (var missing in file.MissingReferences)
        violations.Add(new(file.Name, missing, "missing",
          $"{Code}: {file.Name} references {missing}: reference target not found"));

      foreach (var to in file.References.Distinct().Order(StringComparer.Ordinal)) {
        this.CheckCovered(to, uncovered, violations);
        if (to == file.Name || uncovered.Contains(to) || uncovered.Contains(file.Name))
          continue;

        var rule = this.Refusal(file.Name, to);
        if (rule is not null)
          violations.Add(new(file.Name, to, rule, $"{Code}: {file.Name} must not reference {to} (rule: {rule})"));
      }
    }

    violations.AddRange(Cycles(files));
    return violations;
  }

  private void CheckCovered(string project, HashSet<string> uncovered, List<RuleViolation> violations) {
    if (this.Covers(project) || !uncovered.Add(project))
      return;

    violations.Add(new(project, string.Empty, "uncovered",
      $"{Code}: project {project} is not covered by {this._source}"));
  }

  /// <summary>Names the rule that forbids a reference between two covered projects.</summary>
  private string? Refusal(string from, string to) {
    var fromLayer = this.LayerOf(from);
    if (fromLayer is null)
      return "standalone";

    var toLayer = this.LayerOf(to);
    if (this._restricted.Any(rule => rule.Match.IsMatch(to) && !rule.AllowedFromLayers.Contains(fromLayer.Value)))
      return toLayer is null ? "standalone" : "restricted";

    if (toLayer is null)
      return null;
    if (fromLayer < toLayer)
      return $"layer {fromLayer} -> {toLayer}";

    return fromLayer == toLayer && !this._sameLayerExceptions.Contains((from, to)) ? "same layer" : null;
  }

  /// <summary>Finds reference cycles by depth-first search; each back edge is one cycle.</summary>
  private static List<RuleViolation> Cycles(ProjectFile[] files) {
    var graph = files.GroupBy(file => file.Name, StringComparer.Ordinal).ToDictionary(
      group => group.Key,
      group => group.SelectMany(file => file.References).Distinct().Order(StringComparer.Ordinal).ToArray(),
      StringComparer.Ordinal);
    var finished = new HashSet<string>(StringComparer.Ordinal);
    var path = new List<string>();
    var cycles = new List<RuleViolation>();

    void Visit(string project) {
      path.Add(project);
      foreach (var next in graph[project].Where(graph.ContainsKey)) {
        var start = path.IndexOf(next);
        if (start >= 0) {
          var cycle = path.Skip(start).Append(next).ToArray();
          cycles.Add(new(cycle[0], cycle[1], "cycle", $"{Code}: reference cycle {string.Join(" -> ", cycle)}"));
        } else if (!finished.Contains(next)) {
          Visit(next);
        }
      }

      path.RemoveAt(path.Count - 1);
      finished.Add(project);
    }

    foreach (var project in graph.Keys.Order(StringComparer.Ordinal))
      if (!finished.Contains(project))
        Visit(project);

    return cycles;
  }

  private void Parse(YamlNode root) {
    var top = this.Mapping(root, "schema", "projects", "sameLayerExceptions", "standalone", "restricted");
    var schema = this.Required(top, root, "schema");
    if (this.Text(schema) != "1")
      throw this.Error(schema, $"key 'schema': unsupported value '{this.Text(schema)}' (supported: 1)");

    foreach (var entry in this.Items(top, "projects")) {
      var map = this.Mapping(entry, "name", "pattern", "layer");
      if (map.ContainsKey("name") == map.ContainsKey("pattern"))
        throw this.Error(entry, "expected exactly one of the keys 'name' and 'pattern'");

      var isPattern = map.TryGetValue("pattern", out var selector);
      var text = this.Text(selector ?? map["name"]);
      this._layers.Add((text, Matcher(text, isPattern), this.Layer(this.Required(map, entry, "layer"))));
    }

    foreach (var entry in this.Items(top, "sameLayerExceptions")) {
      var map = this.Mapping(entry, "from", "to");
      this._sameLayerExceptions.Add(
        (this.Text(this.Required(map, entry, "from")), this.Text(this.Required(map, entry, "to"))));
    }

    foreach (var entry in this.Items(top, "standalone"))
      this._standalone.Add(this.Text(entry));

    foreach (var entry in this.Items(top, "restricted")) {
      var map = this.Mapping(entry, "pattern", "allowedFromLayers");
      var layers = this.Items(map, "allowedFromLayers").Select(this.Layer).ToHashSet();
      this._restricted.Add((Matcher(this.Text(this.Required(map, entry, "pattern")), true), layers));
    }
  }

  private Dictionary<string, YamlNode> Mapping(YamlNode node, params string[] keys) {
    if (node is not YamlMappingNode mapping)
      throw this.Error(node, "expected a mapping");

    var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
    foreach (var (key, value) in mapping.Children) {
      var name = this.Text(key);
      if (!keys.Contains(name))
        throw this.Error(key, $"unknown key '{name}'");

      result[name] = value;
    }

    return result;
  }

  private YamlNode Required(Dictionary<string, YamlNode> mapping, YamlNode owner, string key)
    => mapping.TryGetValue(key, out var value) ? value : throw this.Error(owner, $"missing key '{key}'");

  private IEnumerable<YamlNode> Items(Dictionary<string, YamlNode> mapping, string key) {
    if (!mapping.TryGetValue(key, out var value))
      return [];

    return value is YamlSequenceNode sequence ? sequence.Children : throw this.Error(value, $"key '{key}': expected a list");
  }

  private string Text(YamlNode node)
    => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : throw this.Error(node, "expected a single value");

  private int Layer(YamlNode node) {
    var text = this.Text(node);
    return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var layer)
      ? layer
      : throw this.Error(node, $"'{text}' is not a layer (a non-negative whole number)");
  }

  private InvalidDataException Error(YamlNode node, string message)
    => new($"{this._source}:{node.Start.Line}: {message}");

  private static Regex Matcher(string text, bool isPattern) {
    var expression = Regex.Escape(text);
    return new("^" + (isPattern ? expression.Replace("\\*", ".+") : expression) + "$", RegexOptions.CultureInvariant);
  }

  /// <summary>The rule file as messages name it: relative to the repository root when it lies below it.</summary>
  private static string DisplayName(string path) {
    var full = Path.GetFullPath(path);
    for (var directory = Path.GetDirectoryName(full); directory is not null; directory = Path.GetDirectoryName(directory))
      if (File.Exists(Path.Combine(directory, "Polypiler.slnx")))
        return Path.GetRelativePath(directory, full).Replace('\\', '/');

    return full;
  }

}
