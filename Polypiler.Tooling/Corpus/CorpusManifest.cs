using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tooling.Corpus;

/// <summary>An upstream repository the corpus reads from, pinned to one commit.</summary>
/// <param name="Name">The repository as <c>owner/repo</c>.</param>
/// <param name="Url">Where the commit is fetched from.</param>
/// <param name="Commit">The pinned commit: 40 lower-case hexadecimal digits.</param>
/// <param name="Licence">The repository's licence as an SPDX expression.</param>
/// <param name="LicenceFiles">The licence files imported with every fixture of the repository.</param>
public sealed record CorpusRepository(string Name, string Url, string Commit, string Licence, IReadOnlyList<string> LicenceFiles) {

  /// <summary>The folder below <c>corpus/upstream</c> the repository's files are written to: <c>owner/repo@commit</c>.</summary>
  public string Folder => $"{this.Name}@{this.Commit}";

}

/// <summary>Where a fixture's expected values come from.</summary>
/// <param name="Kind"><c>embedded</c> (inside the fixture's file), <c>file</c> (a file of the same commit) or <c>published</c> (a published document).</param>
/// <param name="Source">What names them: a path for <c>file</c>, a description or reference otherwise.</param>
public sealed record CorpusVectors(string Kind, string Source);

/// <summary>One upstream file the tests read, with everything needed to run and check it (PRD §11).</summary>
/// <param name="Id">The fixture ID, e.g. <c>cipher.checksum.adler32</c>.</param>
/// <param name="Repository">The <see cref="CorpusRepository.Name"/> the file comes from.</param>
/// <param name="Path">The file, relative to the repository root.</param>
/// <param name="Export">The symbol of the file the fixture is about.</param>
/// <param name="Dependencies">Further files of the same commit the file needs.</param>
/// <param name="Run">The command line that runs the file; <c>{file}</c> stands for its local path.</param>
/// <param name="Vectors">Where the expected values come from.</param>
/// <param name="Licence">The file's licence as an SPDX expression.</param>
/// <param name="Determinism"><c>deterministic</c> or <c>seeded</c>.</param>
/// <param name="Tolerance"><c>exact</c>, <c>declared</c> or a policy <c>VERIFICATION.tolerance.&lt;name&gt;</c>.</param>
/// <param name="Profiles">The target profiles the fixture is checked for.</param>
public sealed record CorpusFixtureEntry(
  string Id,
  string Repository,
  string Path,
  string Export,
  IReadOnlyList<string> Dependencies,
  IReadOnlyList<string> Run,
  CorpusVectors Vectors,
  string Licence,
  string Determinism,
  string Tolerance,
  IReadOnlyList<string> Profiles
) {

  /// <summary>Every file of the commit the fixture needs: its path, its dependencies and a vector file.</summary>
  public IEnumerable<string> Files {
    get {
      yield return this.Path;
      foreach (var dependency in this.Dependencies)
        yield return dependency;
      if (this.Vectors.Kind == "file")
        yield return this.Vectors.Source;
    }
  }

}

/// <summary>
/// <c>tooling/corpus/corpus.yaml</c>: the pinned repositories and the fixtures taken from them. Its
/// format is <c>spec/schemas/corpus.schema.json</c>; on top of the schema the loader requires every
/// fixture to name a declared repository, every fixture ID and repository name once, and every path
/// relative and free of <c>..</c>.
/// </summary>
public sealed class CorpusManifest {

  /// <summary>The manifest's path below the repository root.</summary>
  public const string RepositoryPath = "tooling/corpus/corpus.yaml";

  private static readonly Regex _name = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
  private static readonly Regex _commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
  private static readonly Regex _id = new(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant);
  private static readonly Regex _tolerance = new(@"^(exact|declared|VERIFICATION\.tolerance\.[A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant);

  private static readonly string[] _repositoryFields = ["name", "url", "commit", "licence", "licenceFiles"];
  private static readonly string[] _fixtureFields = ["id", "repository", "path", "export", "dependencies", "run", "vectors", "licence", "determinism", "tolerance", "profiles"];

  private CorpusManifest(string file, IReadOnlyList<CorpusRepository> repositories, IReadOnlyList<CorpusFixtureEntry> fixtures) {
    this.File = file;
    this.Repositories = repositories;
    this.Fixtures = fixtures;
  }

  /// <summary>The manifest file as it was named.</summary>
  public string File { get; }

  /// <summary>The repositories, in file order.</summary>
  public IReadOnlyList<CorpusRepository> Repositories { get; }

  /// <summary>The fixtures, in file order.</summary>
  public IReadOnlyList<CorpusFixtureEntry> Fixtures { get; }

  /// <summary>The repository of a name, or <see langword="null"/>.</summary>
  public CorpusRepository? Repository(string name) => this.Repositories.FirstOrDefault(repository => repository.Name == name);

  /// <summary>The fixture of an ID, or <see langword="null"/>.</summary>
  public CorpusFixtureEntry? Fixture(string id) => this.Fixtures.FirstOrDefault(fixture => fixture.Id == id);

  /// <summary>Reads and checks a manifest file.</summary>
  /// <exception cref="CorpusException">The file cannot be read or is invalid (<c>PLY0026</c>).</exception>
  public static CorpusManifest Load(string path) {
    string text;
    try {
      text = System.IO.File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new CorpusException(CorpusDiagnostics.Manifest($"cannot read '{path}': {exception.Message}"));
    }

    return Parse(text, path);
  }

  /// <summary>Checks the text of a manifest.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The name diagnostics give the file.</param>
  /// <exception cref="CorpusException">The text is not a valid manifest (<c>PLY0026</c>).</exception>
  public static CorpusManifest Parse(string text, string file) {
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new CorpusException(CorpusDiagnostics.Manifest($"not YAML: {exception.Message}", file, (int)exception.Start.Line, (int)exception.Start.Column));
    }

    if (stream.Documents.Count != 1)
      throw new CorpusException(CorpusDiagnostics.Manifest("the manifest must be exactly one YAML document", file));

    return new Checker(file).Check(stream.Documents[0].RootNode);
  }

  /// <summary>Why a path cannot name a file of a repository, or <see langword="null"/> when it can.</summary>
  /// <param name="path">The path as the manifest gives it.</param>
  public static string? PathProblem(string path) {
    if (path.Length == 0)
      return "is empty";
    if (path[0] == '/' || path[0] == '\\' || (path.Length > 1 && path[1] == ':'))
      return "is absolute";
    if (path.Contains('\\'))
      return "contains a backslash";

    var segments = path.Split('/');
    if (segments.Contains(".."))
      return "contains '..'";
    return segments.Any(segment => segment is "" or ".") ? "has an empty or '.' segment" : null;
  }

  /// <summary>Walks the document and collects every problem before it gives up; YamlDotNet already refuses a duplicate key.</summary>
  private sealed class Checker(string file) {

    private readonly List<Diagnostic> _problems = [];

    public CorpusManifest Check(YamlNode root) {
      if (root is not YamlMappingNode mapping)
        throw new CorpusException(this.Problem(root, "the manifest is not a YAML mapping"));

      var properties = this.Properties(mapping, "the manifest", ["schema", "repositories", "fixtures"]);
      if (properties.TryGetValue("schema", out var schema) && schema is not YamlScalarNode { Style: ScalarStyle.Plain, Value: "1" })
        this.Report(schema, $"unsupported schema {Text(schema)}");

      var repositories = new List<CorpusRepository>();
      foreach (var node in this.Items(properties, "repositories", "the manifest")) {
        if (this.Repository(node) is { } repository) {
          if (repositories.Any(other => other.Name == repository.Name))
            this.Report(node, $"repository '{repository.Name}' is declared twice");
          else
            repositories.Add(repository);
        }
      }

      var fixtures = new List<CorpusFixtureEntry>();
      foreach (var node in this.Items(properties, "fixtures", "the manifest")) {
        if (this.Fixture(node) is not { } fixture)
          continue;
        if (fixtures.Any(other => other.Id == fixture.Id))
          this.Report(node, $"fixture ID '{fixture.Id}' is duplicated");
        else if (repositories.All(repository => repository.Name != fixture.Repository))
          this.Report(node, $"fixture '{fixture.Id}' names undeclared repository '{fixture.Repository}'");
        else
          fixtures.Add(fixture);
      }

      if (this._problems.Count > 0)
        throw new CorpusException(this._problems);
      return new(file, repositories, fixtures);
    }

    private CorpusRepository? Repository(YamlNode node) {
      if (node is not YamlMappingNode body) {
        this.Report(node, "a repository must be a mapping");
        return null;
      }

      var before = this._problems.Count;
      var owner = $"repository '{Text(body, "name")}'";
      var properties = this.Properties(body, owner, _repositoryFields);
      var name = this.String(properties, "name", owner, _name, "owner/repo");
      var url = this.String(properties, "url", owner);
      var commit = this.String(properties, "commit", owner, _commit, "40 lower-case hexadecimal digits");
      var licence = this.String(properties, "licence", owner);
      var licenceFiles = this.Paths(properties, "licenceFiles", owner);
      return this._problems.Count > before ? null : new(name!, url!, commit!, licence!, licenceFiles!);
    }

    private CorpusFixtureEntry? Fixture(YamlNode node) {
      if (node is not YamlMappingNode body) {
        this.Report(node, "a fixture must be a mapping");
        return null;
      }

      var before = this._problems.Count;
      var owner = $"fixture '{Text(body, "id")}'";
      var properties = this.Properties(body, owner, _fixtureFields);
      var id = this.String(properties, "id", owner, _id, @"[a-z0-9]+(\.[a-z0-9-]+)+");
      var repository = this.String(properties, "repository", owner);
      var path = this.Path(properties, "path", owner);
      var export = this.String(properties, "export", owner);
      var dependencies = this.Paths(properties, "dependencies", owner);
      var run = this.Strings(properties, "run", owner);
      var vectors = this.Vectors(properties, owner);
      var licence = this.String(properties, "licence", owner);
      var determinism = this.String(properties, "determinism", owner, new("^(deterministic|seeded)$"), "deterministic or seeded");
      var tolerance = this.String(properties, "tolerance", owner, _tolerance, "exact, declared or VERIFICATION.tolerance.<name>");
      var profiles = this.Strings(properties, "profiles", owner);
      if (run is { Count: 0 })
        this.Report(properties["run"], $"property 'run' of {owner} must not be empty");
      else if (run is not null && !run.Any(argument => argument.Contains("{file}", StringComparison.Ordinal)))
        this.Report(properties["run"], $"property 'run' of {owner} does not name '{{file}}'");

      return this._problems.Count > before
        ? null
        : new(id!, repository!, path!, export!, dependencies!, run!, vectors!, licence!, determinism!, tolerance!, profiles!);
    }

    private CorpusVectors? Vectors(Dictionary<string, YamlNode> properties, string owner) {
      if (!properties.TryGetValue("vectors", out var node))
        return null;
      if (node is not YamlMappingNode body) {
        this.Report(node, $"property 'vectors' of {owner} must be a mapping");
        return null;
      }

      var vectors = this.Properties(body, $"the vectors of {owner}", ["kind", "source"]);
      var kind = this.String(vectors, "kind", owner, new("^(embedded|file|published)$"), "embedded, file or published");
      var source = kind == "file" ? this.Path(vectors, "source", owner) : this.String(vectors, "source", owner);
      return kind is null || source is null ? null : new(kind, source);
    }

    private Dictionary<string, YamlNode> Properties(YamlMappingNode mapping, string owner, string[] names) {
      var properties = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
      foreach (var (key, value) in mapping.Children) {
        var name = Text(key);
        if (names.Contains(name))
          properties[name] = value;
        else
          this.Report(key, $"unknown property '{name}' in {owner}");
      }

      foreach (var name in names.Where(name => !properties.ContainsKey(name)))
        this.Report(mapping, $"missing property '{name}' in {owner}");
      return properties;
    }

    private IEnumerable<YamlNode> Items(Dictionary<string, YamlNode> properties, string name, string owner) {
      if (!properties.TryGetValue(name, out var node))
        return [];
      if (node is YamlSequenceNode list)
        return list.Children;

      this.Report(node, $"property '{name}' of {owner} must be a list");
      return [];
    }

    private string? String(Dictionary<string, YamlNode> properties, string name, string owner, Regex? pattern = null, string? expected = null) {
      if (!properties.TryGetValue(name, out var node))
        return null;
      if (!IsString(node)) {
        this.Report(node, $"property '{name}' of {owner} must be a non-empty string");
        return null;
      }

      var value = ((YamlScalarNode)node).Value!;
      if (pattern is null || pattern.IsMatch(value))
        return value;

      this.Report(node, $"property '{name}' of {owner} is '{value}', expected {expected}");
      return null;
    }

    private string? Path(Dictionary<string, YamlNode> properties, string name, string owner) {
      var path = this.String(properties, name, owner);
      if (path is not null && PathProblem(path) is { } problem) {
        this.Report(properties[name], $"path '{path}' of {owner} {problem}");
        return null;
      }

      return path;
    }

    private List<string>? Strings(Dictionary<string, YamlNode> properties, string name, string owner) {
      if (!properties.TryGetValue(name, out var node))
        return null;
      if (node is YamlSequenceNode list && list.Children.All(IsString))
        return [.. list.Children.Cast<YamlScalarNode>().Select(item => item.Value!)];

      this.Report(node, $"property '{name}' of {owner} must be a list of non-empty strings");
      return null;
    }

    private List<string>? Paths(Dictionary<string, YamlNode> properties, string name, string owner) {
      var paths = this.Strings(properties, name, owner);
      var invalid = false;
      foreach (var path in paths ?? []) {
        if (PathProblem(path) is { } problem) {
          this.Report(properties[name], $"path '{path}' of {owner} {problem}");
          invalid = true;
        }
      }

      return invalid ? null : paths;
    }

    private static bool IsString(YamlNode node)
      => node is YamlScalarNode { Value.Length: > 0 } scalar && !(scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null");

    private static string Text(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value! : "?";

    private static string Text(YamlMappingNode mapping, string name)
      => mapping.Children.TryGetValue(new YamlScalarNode(name), out var value) ? Text(value) : "?";

    private Diagnostic Problem(YamlNode node, string reason) => CorpusDiagnostics.Manifest(reason, file, (int)node.Start.Line, (int)node.Start.Column);

    private void Report(YamlNode node, string reason) => this._problems.Add(this.Problem(node, reason));

  }

}
