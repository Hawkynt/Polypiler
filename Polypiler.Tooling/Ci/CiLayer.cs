using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Polypiler.Core;
using Polypiler.Tooling.Corpus;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tooling.Ci;

/// <summary>
/// One layer of the CI, read from <c>tooling/ci/layers/&lt;name&gt;.yaml</c> and checked against
/// <c>spec/schemas/ci-layer.schema.json</c>: the toolchains it installs, the runners it uses, the
/// changed paths that select it and whether it runs regardless. The job <c>layer-&lt;name&gt;</c> of
/// <c>.github/workflows/ci.yml</c> runs it.
/// </summary>
/// <param name="Name">The layer's name, which is its file name without <c>.yaml</c>.</param>
/// <param name="File">The file, relative to the repository root with <c>/</c> separators.</param>
/// <param name="Toolchains">The keys of the toolchain lock the layer installs.</param>
/// <param name="Os">The runner labels of its job.</param>
/// <param name="Paths">The globs of changed paths that select it.</param>
/// <param name="Always">Whether it runs whatever changed.</param>
public sealed record CiLayer(string Name, string File, IReadOnlyList<string> Toolchains, IReadOnlyList<string> Os, IReadOnlyList<string> Paths, bool Always) {

  /// <summary>The folder of the layer files, relative to the repository root.</summary>
  public const string Folder = "tooling/ci/layers";

  /// <summary>The name under which the tooling assembly carries <c>spec/schemas/ci-layer.schema.json</c>.</summary>
  public const string SchemaResource = "ci-layer.schema.json";

  /// <summary>The prefix of the job that runs a layer.</summary>
  public const string JobPrefix = "layer-";

  private static readonly Lazy<JsonSchema> _schema = new(() => {
    using var stream = typeof(CiLayer).Assembly.GetManifestResourceStream(SchemaResource)!;
    using var reader = new StreamReader(stream);
    return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
  });

  private static readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List };

  /// <summary>The job of <c>.github/workflows/ci.yml</c> that runs the layer.</summary>
  public string Job => JobPrefix + this.Name;

  /// <summary>Whether a changed path selects the layer: it runs always, or one of its globs matches the path.</summary>
  /// <param name="path">The path, relative to the repository root; <c>\</c> counts as <c>/</c>.</param>
  /// <returns><see langword="true"/> when the layer has to run for the path.</returns>
  public bool Selects(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var normalized = path.Replace('\\', '/');
    return this.Always || this.Paths.Any(glob => GlobPattern.Parse(glob).IsMatch(normalized));
  }

  /// <summary>
  /// Whether a file of <see cref="Folder"/> is a layer file: <c>&lt;name&gt;.yaml</c>. A file whose name
  /// has a further dot, such as <c>basic.oracles.yaml</c>, belongs to a layer without being one.
  /// </summary>
  /// <param name="file">The file name or path.</param>
  public static bool IsLayerFile(string file) {
    var name = Path.GetFileName(file);
    return name.EndsWith(".yaml", StringComparison.Ordinal) && !Path.GetFileNameWithoutExtension(name).Contains('.', StringComparison.Ordinal);
  }

  /// <summary>Reads every layer file of a repository; the companion files of a layer are left out (<see cref="IsLayerFile"/>).</summary>
  /// <param name="root">The repository root.</param>
  /// <returns>The valid layers, sorted by name, and a <c>PLY0075</c> for every file that is not valid; no layer file at all is one <c>PLY0075</c> too.</returns>
  public static (IReadOnlyList<CiLayer> Layers, IReadOnlyList<Diagnostic> Problems) LoadAll(string root) {
    ArgumentNullException.ThrowIfNull(root);
    var folder = Path.Combine(root, Folder);
    var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yaml").Where(IsLayerFile).Order(StringComparer.Ordinal).ToArray() : [];
    if (files.Length == 0)
      return ([], [CiDiagnostics.InconsistentWith($"no layer files found in {Folder}")]);

    var layers = new List<CiLayer>();
    var problems = new List<Diagnostic>();
    foreach (var file in files) {
      problems.AddRange(Parse(System.IO.File.ReadAllText(file), $"{Folder}/{Path.GetFileName(file)}", out var layer));
      if (layer is not null)
        layers.Add(layer);
    }

    return ([.. layers.OrderBy(layer => layer.Name, StringComparer.Ordinal)], problems);
  }

  /// <summary>Reads the text of one layer file.</summary>
  /// <param name="text">The YAML text.</param>
  /// <param name="file">The file, relative to the repository root; its name without <c>.yaml</c> must be the layer's.</param>
  /// <param name="layer">Receives the layer, or <see langword="null"/> when the file is not valid.</param>
  /// <returns>A <c>PLY0075</c> <c>&lt;file&gt;: &lt;reason&gt;</c> for every problem; none for a valid file.</returns>
  public static IReadOnlyList<Diagnostic> Parse(string text, string file, out CiLayer? layer) {
    ArgumentNullException.ThrowIfNull(text);
    ArgumentNullException.ThrowIfNull(file);
    layer = null;
    JsonNode? document;
    try {
      document = YamlToJson.Read(text, out _);
    } catch (YamlToJsonException exception) {
      return [CiDiagnostics.InconsistentWith($"{file}: {exception.Message}")];
    }

    var results = _schema.Value.Evaluate(JsonSerializer.SerializeToElement(document), _evaluation);
    if (!results.IsValid)
      return [.. SchemaErrors(results).Distinct().Select(error => CiDiagnostics.InconsistentWith($"{file}: {error}"))];

    var name = document!["layer"]!.GetValue<string>();
    var expected = Path.GetFileNameWithoutExtension(file);
    if (name != expected)
      return [CiDiagnostics.InconsistentWith($"{file}: layer '{name}' does not match the file name '{expected}'")];

    layer = new(name, file, Strings(document["toolchains"]), Strings(document["os"]), Strings(document["paths"]), document["always"]!.GetValue<bool>());
    return [];
  }

  private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(item => item!.GetValue<string>())];

  /// <summary>The errors of the list output, as <c>&lt;pointer&gt;: &lt;message&gt;</c>; the root is <c>/</c>.</summary>
  private static IEnumerable<string> SchemaErrors(EvaluationResults results) {
    foreach (var detail in new[] { results }.Concat(results.Details ?? []))
      foreach (var error in detail.Errors ?? new Dictionary<string, string>()) {
        var pointer = detail.InstanceLocation.ToString();
        yield return $"{(pointer.Length == 0 ? "/" : pointer)}: {error.Value}";
      }
  }

}
