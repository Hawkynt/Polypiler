using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.Verification.Running;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Core.Verification.Benchmarks;

/// <summary>How a benchmark is measured.</summary>
public enum BenchmarkKind {

  /// <summary>A program run by the cross-language runner; each run is one sample of wall time, CPU time and peak memory.</summary>
  Process,

  /// <summary>A BenchmarkDotNet benchmark of <c>Polypiler.Benchmarks</c>, run once; its iterations are the samples, with allocations.</summary>
  DotNet

}

/// <summary>One way of running a benchmark, for example one optimization level.</summary>
/// <param name="Name">The variant's name, which names its result file.</param>
/// <param name="Environment">Variables added to the environment of the build and run steps.</param>
/// <param name="Arguments">Arguments appended to the run step's.</param>
public sealed record BenchmarkVariant(string Name, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Arguments) {

  /// <summary>The name of the one variant of a definition without <c>variants</c>.</summary>
  public const string DefaultName = "default";

}

/// <summary>Which files' byte sizes a result records.</summary>
/// <param name="Source">Paths relative to the repository root, summed as <c>sourceBytes</c>.</param>
/// <param name="Binary">Paths relative to the run's working directory, read after the run, summed as <c>binaryBytes</c>; a directory counts every file below it.</param>
public sealed record BenchmarkSizes(IReadOnlyList<string> Source, IReadOnlyList<string> Binary);

/// <summary>
/// A benchmark definition, <c>tests/benchmarks/&lt;id&gt;.bench.yaml</c> (schema
/// <c>spec/schemas/bench.schema.json</c>): what to build and run on which toolchain, how often, in
/// which variants, what it must print, and which sizes to record.
/// </summary>
/// <param name="Id">The benchmark's ID, which names its file and its results.</param>
/// <param name="Description">What it measures.</param>
/// <param name="Kind">How it is measured.</param>
/// <param name="Toolchain">The key of <c>tooling/toolchains.lock</c> whose executable the build and run steps start.</param>
/// <param name="Sources">Files relative to the repository root, copied into the run's directory under the same relative names.</param>
/// <param name="Build">The arguments of the build step; <see langword="null"/> without one.</param>
/// <param name="Run">The arguments of the run step.</param>
/// <param name="Stdin">A file relative to the repository root that the run reads on standard input; <see langword="null"/> for none.</param>
/// <param name="ExpectStdout">The exact output a run must print; <see langword="null"/> when the output is not checked.</param>
/// <param name="Variants">The variants; at least one.</param>
/// <param name="Warmup">The runs before the measured ones, whose samples are discarded.</param>
/// <param name="Runs">The measured runs.</param>
/// <param name="Sizes">The files whose sizes are recorded.</param>
public sealed partial record BenchmarkDefinition(
  string Id,
  string Description,
  BenchmarkKind Kind,
  string Toolchain,
  IReadOnlyList<string> Sources,
  IReadOnlyList<string>? Build,
  IReadOnlyList<string> Run,
  string? Stdin,
  string? ExpectStdout,
  IReadOnlyList<BenchmarkVariant> Variants,
  int Warmup,
  int Runs,
  BenchmarkSizes Sizes
) {

  /// <summary>The extension of a definition file.</summary>
  public const string Extension = ".bench.yaml";

  /// <summary>Where the definitions of the repository are, relative to its root.</summary>
  public const string RepositoryFolder = "tests/benchmarks";

  /// <summary>The warm-up runs of a definition without <c>warmup</c>.</summary>
  public const int DefaultWarmup = 1;

  /// <summary>The measured runs of a definition without <c>runs</c>.</summary>
  public const int DefaultRuns = 5;

  /// <summary>Whether a text is an ID or a variant name: lower-case letters, digits and hyphens, starting with a letter or digit.</summary>
  public static bool IsName(string text) => NamePattern().IsMatch(text);

  /// <summary>The variant of a name.</summary>
  /// <exception cref="ArgumentException">The definition has no such variant.</exception>
  public BenchmarkVariant Variant(string name)
    => this.Variants.FirstOrDefault(variant => variant.Name == name) ?? throw new ArgumentException($"benchmark '{this.Id}' has no variant '{name}'", nameof(name));

  /// <summary>Reads a definition file.</summary>
  /// <param name="path">The file.</param>
  /// <exception cref="BenchmarkFileException">The file cannot be read or does not follow the schema.</exception>
  public static BenchmarkDefinition Load(string path) {
    string text;
    try {
      text = File.ReadAllText(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      throw new BenchmarkFileException(path, 0, 0, $"cannot be read: {exception.Message}");
    }

    return Parse(text, path);
  }

  /// <summary>Reads the text of a definition file.</summary>
  /// <param name="text">The text.</param>
  /// <param name="file">The file's name, for the messages.</param>
  /// <exception cref="BenchmarkFileException">The text does not follow the schema.</exception>
  public static BenchmarkDefinition Parse(string text, string file) {
    var reader = new YamlReader(file);
    var root = reader.Root(text);
    var fields = reader.Properties(root, "definition", "id", "description", "kind", "toolchain", "sources", "build", "run", "stdin", "expectStdout", "variants", "warmup", "runs", "sizes");
    var id = reader.Text(reader.Required(fields, root, "id"), "id");
    if (!IsName(id))
      throw reader.Error(fields["id"], $"'id' must be lower-case letters, digits and hyphens, not '{id}'");

    var kindText = reader.Text(reader.Required(fields, root, "kind"), "kind");
    var kind = kindText switch {
      "process" => BenchmarkKind.Process,
      "dotnet" => BenchmarkKind.DotNet,
      _ => throw reader.Error(fields["kind"], $"'kind' must be 'process' or 'dotnet', not '{kindText}'")
    };

    var run = reader.TextList(reader.Required(fields, root, "run"), "run");
    if (run.Length == 0)
      throw reader.Error(fields["run"], "'run' needs at least one argument");

    return new(
      id,
      reader.Text(reader.Required(fields, root, "description"), "description"),
      kind,
      reader.Text(reader.Required(fields, root, "toolchain"), "toolchain"),
      fields.TryGetValue("sources", out var sources) ? RelativePaths(reader, sources, "sources") : [],
      fields.TryGetValue("build", out var build) ? reader.TextList(build, "build") : null,
      run,
      fields.TryGetValue("stdin", out var stdin) ? RelativePath(reader, stdin, "stdin") : null,
      fields.TryGetValue("expectStdout", out var expect) ? reader.Text(expect, "expectStdout") : null,
      fields.TryGetValue("variants", out var variants) ? ReadVariants(reader, variants) : [new(BenchmarkVariant.DefaultName, new Dictionary<string, string>(), [])],
      fields.TryGetValue("warmup", out var warmup) ? (int)reader.Integer(warmup, "warmup", 0, 1000) : DefaultWarmup,
      fields.TryGetValue("runs", out var runs) ? (int)reader.Integer(runs, "runs", 1, 1000) : DefaultRuns,
      fields.TryGetValue("sizes", out var sizes) ? ReadSizes(reader, sizes) : new([], []));
  }

  private static BenchmarkVariant[] ReadVariants(YamlReader reader, YamlNode node) {
    var variants = new List<BenchmarkVariant>();
    foreach (var mapping in reader.Mappings(node, "variants")) {
      var fields = reader.Properties(mapping, "variant", "name", "env", "args");
      var name = reader.Text(reader.Required(fields, mapping, "name"), "variants.name");
      if (!IsName(name))
        throw reader.Error(fields["name"], $"'variants.name' must be lower-case letters, digits and hyphens, not '{name}'");
      if (variants.Any(variant => variant.Name == name))
        throw reader.Error(fields["name"], $"'variants.name' '{name}' is given more than once");

      variants.Add(new(name,
        fields.TryGetValue("env", out var env) ? reader.TextMap(env, "variants.env") : new Dictionary<string, string>(),
        fields.TryGetValue("args", out var args) ? reader.TextList(args, "variants.args") : []));
    }

    return variants.Count > 0 ? [.. variants] : throw reader.Error(node, "'variants' needs at least one variant when given");
  }

  private static BenchmarkSizes ReadSizes(YamlReader reader, YamlNode node) {
    if (node is not YamlMappingNode mapping)
      throw reader.Error(node, "'sizes' must be a mapping");

    var fields = reader.Properties(mapping, "sizes", "source", "binary");
    return new(
      fields.TryGetValue("source", out var source) ? RelativePaths(reader, source, "sizes.source") : [],
      fields.TryGetValue("binary", out var binary) ? RelativePaths(reader, binary, "sizes.binary") : []);
  }

  private static string[] RelativePaths(YamlReader reader, YamlNode node, string field)
    => node is YamlSequenceNode sequence ? [.. sequence.Children.Select(item => RelativePath(reader, item, field))] : throw reader.Error(node, $"'{field}' must be a list");

  private static string RelativePath(YamlReader reader, YamlNode node, string field) {
    var path = reader.Text(node, field);
    return RunPlan.IsRelativeName(path) ? path : throw reader.Error(node, $"'{field}' must name a relative path without '..', not '{path}'");
  }

  [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
  private static partial Regex NamePattern();

}
