using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Core.Verification.Evidence;

/// <summary>
/// Writes the evidence bundle of a scenario: <c>&lt;root&gt;/&lt;feature-slug&gt;/&lt;scenario-slug&gt;/</c>
/// with <see cref="FileName"/> and a byte-for-byte copy of every captured file. The directory is
/// deleted and rewritten on every write. A scenario whose slug another scenario of the same feature
/// already took in this process gets <c>-2</c>, <c>-3</c>, …; writing the same collector again
/// rewrites its own directory. Hashes are those of the files on disk when the bundle is written; a
/// file that is gone by then is recorded with a <see langword="null"/> hash and not copied, which
/// <see cref="EvidenceValidator"/> reports.
/// </summary>
public static class EvidenceWriter {

  /// <summary>The file that describes a bundle.</summary>
  public const string FileName = "evidence.json";

  /// <summary>The comparison's failed vectors, one line each.</summary>
  public const string DiffFile = "diff.txt";

  /// <summary>The optimization diagnostics.</summary>
  public const string OptReportFile = "opt-report.txt";

  /// <summary>The <see cref="CiLink"/> outside GitHub Actions.</summary>
  public const string LocalCiLink = "local";

  private static readonly UTF8Encoding _utf8 = new(false);

  private static readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Writes the bundle of a scenario.</summary>
  /// <param name="c">What the scenario collected.</param>
  /// <param name="id">The scenario.</param>
  /// <param name="outcome">How it ended.</param>
  /// <param name="root">The directory of all bundles, such as <c>artifacts/evidence</c>; created when missing.</param>
  /// <returns>The full path of the bundle directory.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="IOException">The bundle cannot be written.</exception>
  /// <exception cref="UnauthorizedAccessException">The bundle cannot be written.</exception>
  public static string Write(EvidenceCollector c, ScenarioIdentity id, ScenarioOutcome outcome, string root) {
    ArgumentNullException.ThrowIfNull(c);
    ArgumentNullException.ThrowIfNull(id);
    ArgumentNullException.ThrowIfNull(root);
    var fullRoot = Path.GetFullPath(root);
    var directory = c.BundleUnder(fullRoot, () => Claim(Path.Combine(fullRoot, id.FeatureSlug), id.ScenarioSlug));
    Delete(directory);
    Directory.CreateDirectory(directory);

    var comparison = c.Comparison;
    var tree = new JsonObject {
      ["schemaVersion"] = 1,
      ["feature"] = id.Feature,
      ["scenario"] = id.Scenario,
      ["tags"] = new JsonArray([.. id.Tags.Select(tag => (JsonNode)tag)]),
      ["outcome"] = outcome == ScenarioOutcome.Passed ? "passed" : "failed",
      ["sources"] = Copies(directory, "source", c.Sources),
      ["targetCode"] = Copies(directory, "target", c.TargetCode),
      ["manifest"] = c.Manifest is { } manifest ? Manifest(directory, manifest) : null,
      ["toolVersions"] = Map(c.ToolVersions),
      ["fixtureValues"] = Map(c.FixtureValues),
      ["processes"] = new JsonArray([.. c.Processes.Select(process => (JsonNode)Process(directory, process))]),
      ["reference"] = comparison is { } referenced ? Results(referenced.Report, outcome => outcome.ReferenceHex) : null,
      ["target"] = comparison is { } targeted ? Results(targeted.Report, outcome => outcome.ActualHex) : null,
      ["diff"] = comparison is { } compared ? Diff(directory, compared.Report) : null,
      ["exactness"] = comparison is { } policed ? Exactness(policed.Policy) : null,
      ["optDiagnostics"] = c.OptDiagnostics is { } opt ? Text(directory, OptReportFile, opt) : null,
      ["files"] = Others(directory, c.Files),
      ["ciLink"] = CiLink(Environment.GetEnvironmentVariable)
    };
    File.WriteAllBytes(Path.Combine(directory, FileName), _utf8.GetBytes(CanonicalJson.Serialize(tree) + "\n"));
    return directory;
  }

  /// <summary>
  /// The link to the CI run: <c>$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID</c>
  /// when all three are set and not empty, otherwise <see cref="LocalCiLink"/>.
  /// </summary>
  /// <param name="environment">Reads an environment variable; <see langword="null"/> when it is not set.</param>
  /// <returns>The link.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
  public static string CiLink(Func<string, string?> environment) {
    ArgumentNullException.ThrowIfNull(environment);
    var server = environment("GITHUB_SERVER_URL");
    var repository = environment("GITHUB_REPOSITORY");
    var run = environment("GITHUB_RUN_ID");
    return string.IsNullOrEmpty(server) || string.IsNullOrEmpty(repository) || string.IsNullOrEmpty(run)
      ? LocalCiLink
      : $"{server.TrimEnd('/')}/{repository}/actions/runs/{run}";
  }

  /// <summary>The first free directory of a slug under a feature's directory: the slug, then <c>-2</c>, <c>-3</c>, ….</summary>
  private static string Claim(string feature, string slug) {
    lock (_claimed)
      for (var number = 1; ; ++number) {
        var candidate = Path.Combine(feature, number == 1 ? slug : $"{slug}-{number.ToString(CultureInfo.InvariantCulture)}");
        if (_claimed.Add(candidate))
          return candidate;
      }
  }

  private static void Delete(string directory) {
    if (!Directory.Exists(directory))
      return;

    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
      File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(directory, true);
  }

  private static JsonArray Copies(string directory, string folder, IReadOnlyList<string> paths) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    return new([.. paths.Select(path => (JsonNode)Copy(directory, $"{folder}/{Unique(names, Path.GetFileName(path))}", path))]);
  }

  /// <summary>Copies a file into the bundle; a missing file is recorded with a <see langword="null"/> hash and not copied.</summary>
  private static JsonObject Copy(string directory, string relative, string source) {
    string? hash = null;
    if (File.Exists(source)) {
      var bytes = File.ReadAllBytes(source);
      hash = ManifestWriter.Sha256(bytes);
      var target = Path.Combine(directory, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllBytes(target, bytes);
    }

    return new JsonObject { ["path"] = relative, ["sha256"] = hash };
  }

  private static JsonObject Manifest(string directory, string path) {
    var entry = Copy(directory, Manifests.Manifest.FileName, path);
    var state = entry["sha256"] is null
      ? null
      : ManifestReader.Read(Path.Combine(directory, Manifests.Manifest.FileName), new DiagnosticBag()) is { } manifest
        ? ManifestStateRule.Name(manifest.State)
        : null;
    entry["state"] = state;
    return entry;
  }

  private static JsonObject Map(IReadOnlyList<KeyValuePair<string, string>> pairs) {
    var map = new JsonObject();
    foreach (var (key, value) in pairs)
      map[key] = value;

    return map;
  }

  private static JsonObject Process(string directory, EvidenceProcess process) => new() {
    ["label"] = process.Label,
    ["exitCode"] = process.ExitCode,
    ["stdout"] = Text(directory, $"process/{process.Label}.stdout.txt", process.Stdout),
    ["stderr"] = Text(directory, $"process/{process.Label}.stderr.txt", process.Stderr)
  };

  private static JsonObject Results(ComparisonReport report, Func<VectorOutcome, string> hex) {
    var results = new JsonObject();
    foreach (var outcome in report.Outcomes)
      results[outcome.Name] = hex(outcome);

    return results;
  }

  /// <summary>Writes <see cref="DiffFile"/>: <c>&lt;name&gt;: expected &lt;hex&gt; actual &lt;hex&gt; @&lt;offset&gt;</c> per failed vector.</summary>
  private static string Diff(string directory, ComparisonReport report) {
    var text = new StringBuilder();
    foreach (var outcome in report.Outcomes.Where(outcome => !outcome.Passed))
      text.Append(CultureInfo.InvariantCulture, $"{outcome.Name}: expected {outcome.ReferenceHex} actual {outcome.ActualHex} @{outcome.FirstDifferingOffset ?? 0}\n");

    return Text(directory, DiffFile, text.ToString());
  }

  /// <summary>The policy as a vector file declares it.</summary>
  private static JsonObject Exactness(ExactnessPolicy policy) {
    var exactness = new JsonObject { ["mode"] = policy.Mode };
    if (policy is ExactnessPolicy.Tolerance tolerance) {
      exactness["bound"] = tolerance switch {
        ExactnessPolicy.Ulp ulp => ulp.Bound,
        ExactnessPolicy.Relative relative => relative.Bound,
        _ => ((ExactnessPolicy.Absolute)tolerance).Bound
      };
      if (tolerance.Policy is { } name)
        exactness["policy"] = name;
      if (tolerance.Reason is { } reason)
        exactness["reason"] = reason;
    }

    if (policy.ElementFormat is { } format)
      exactness["format"] = format == FloatFormat.F32 ? "f32" : "f64";
    exactness["specialValues"] = ExactnessPolicy.Spelling(policy.SpecialValues);
    return exactness;
  }

  private static JsonArray Others(string directory, IReadOnlyList<KeyValuePair<string, string>> files) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    return new([.. files.Select(file => {
      var entry = Copy(directory, Unique(names, $"files/{file.Key}/{Path.GetFileName(file.Value)}"), file.Value);
      entry.Insert(0, "role", file.Key);
      return (JsonNode)entry;
    })]);
  }

  private static string Text(string directory, string relative, string content) {
    var target = Path.Combine(directory, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllBytes(target, _utf8.GetBytes(content));
    return relative;
  }

  private static string Unique(HashSet<string> names, string name) {
    var extension = Path.GetExtension(name);
    var stem = name[..^extension.Length];
    var candidate = name;
    for (var number = 2; !names.Add(candidate); ++number)
      candidate = $"{stem}-{number.ToString(CultureInfo.InvariantCulture)}{extension}";

    return candidate;
  }

}
