using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Polypiler.Core.Verification.Oracles;

namespace Polypiler.Core.Verification.Evidence;

/// <summary>A process a scenario ran, as an evidence bundle records it.</summary>
/// <param name="Label">The name of its files: <c>process/&lt;label&gt;.stdout.txt</c> and <c>.stderr.txt</c>.</param>
/// <param name="Stdout">Everything it wrote to standard output.</param>
/// <param name="Stderr">Everything it wrote to standard error.</param>
/// <param name="ExitCode">Its exit code.</param>
public sealed record EvidenceProcess(string Label, string Stdout, string Stderr, int ExitCode);

/// <summary>
/// What one scenario adds to its evidence bundle (PRD §6.6), collected while its steps run and written
/// by <see cref="EvidenceWriter"/> after the last one. Files are named by path and read only when the
/// bundle is written, so the bundle holds what was on disk at the end of the scenario. Steps that run
/// side by side may add at the same time.
/// </summary>
public sealed partial class EvidenceCollector {

  private readonly Lock _lock = new();
  private readonly List<string> _sources = [];
  private readonly List<string> _targetCode = [];
  private readonly List<KeyValuePair<string, string>> _toolVersions = [];
  private readonly List<KeyValuePair<string, string>> _fixtureValues = [];
  private readonly List<EvidenceProcess> _processes = [];
  private readonly List<KeyValuePair<string, string>> _files = [];
  private readonly Dictionary<string, string> _bundles = new(StringComparer.Ordinal);
  private string? _manifest;
  private (ComparisonReport Report, ExactnessPolicy Policy)? _comparison;
  private string? _optDiagnostics;

  [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
  private static partial Regex Name();

  /// <summary>The source files, in the order added.</summary>
  public IReadOnlyList<string> Sources => this.Read(() => (IReadOnlyList<string>)[.. this._sources]);

  /// <summary>The generated files, in the order added.</summary>
  public IReadOnlyList<string> TargetCode => this.Read(() => (IReadOnlyList<string>)[.. this._targetCode]);

  /// <summary>The generated manifest; <see langword="null"/> when none was added.</summary>
  public string? Manifest => this.Read(() => this._manifest);

  /// <summary>The version of each tool by its key, in the order first added.</summary>
  public IReadOnlyList<KeyValuePair<string, string>> ToolVersions => this.Read(() => (IReadOnlyList<KeyValuePair<string, string>>)[.. this._toolVersions]);

  /// <summary>The value of each fixture input by its name, in the order first added.</summary>
  public IReadOnlyList<KeyValuePair<string, string>> FixtureValues => this.Read(() => (IReadOnlyList<KeyValuePair<string, string>>)[.. this._fixtureValues]);

  /// <summary>The processes, in the order added.</summary>
  public IReadOnlyList<EvidenceProcess> Processes => this.Read(() => (IReadOnlyList<EvidenceProcess>)[.. this._processes]);

  /// <summary>The comparison of the target's results with the reference's; <see langword="null"/> when none was added.</summary>
  public (ComparisonReport Report, ExactnessPolicy Policy)? Comparison => this.Read(() => this._comparison);

  /// <summary>The optimization diagnostics, every text added on a line of its own; <see langword="null"/> when none was added.</summary>
  public string? OptDiagnostics => this.Read(() => this._optDiagnostics);

  /// <summary>Further files by role, in the order added.</summary>
  public IReadOnlyList<KeyValuePair<string, string>> Files => this.Read(() => (IReadOnlyList<KeyValuePair<string, string>>)[.. this._files]);

  /// <summary>Adds a source file.</summary>
  /// <param name="path">The file.</param>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  public void AddSource(string path) => this.Add(this._sources, FilePath(path));

  /// <summary>Adds a generated file.</summary>
  /// <param name="path">The file.</param>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  public void AddTargetCode(string path) => this.Add(this._targetCode, FilePath(path));

  /// <summary>Sets the generated manifest; a later call replaces an earlier one.</summary>
  /// <param name="path">The <c>polypiler.manifest.json</c>.</param>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  public void AddManifest(string path) {
    var checkedPath = FilePath(path);
    lock (this._lock)
      this._manifest = checkedPath;
  }

  /// <summary>Records the version of a tool; a later call for the same key replaces the version.</summary>
  /// <param name="key">The tool, by its key in <c>tooling/toolchains.lock</c> where it has one.</param>
  /// <param name="version">The version.</param>
  /// <exception cref="ArgumentException">An argument is empty.</exception>
  public void AddToolVersion(string key, string version) => this.Set(this._toolVersions, Text(key, nameof(key)), Text(version, nameof(version)));

  /// <summary>Records the value of a fixture input; a later call for the same name replaces the value.</summary>
  /// <param name="name">The input's name.</param>
  /// <param name="value">Its value, as text.</param>
  /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
  /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
  public void AddFixtureValue(string name, string value) {
    ArgumentNullException.ThrowIfNull(value);
    this.Set(this._fixtureValues, Text(name, nameof(name)), value);
  }

  /// <summary>Records a process with what it wrote and how it exited.</summary>
  /// <param name="label">The name of its files: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or digit; unique in the scenario.</param>
  /// <param name="stdout">Everything it wrote to standard output.</param>
  /// <param name="stderr">Everything it wrote to standard error.</param>
  /// <param name="exitCode">Its exit code.</param>
  /// <exception cref="ArgumentException"><paramref name="label"/> is not a file name or was already recorded.</exception>
  /// <exception cref="ArgumentNullException"><paramref name="stdout"/> or <paramref name="stderr"/> is <see langword="null"/>.</exception>
  public void AddProcess(string label, string stdout, string stderr, int exitCode) {
    CheckName(label, nameof(label));
    ArgumentNullException.ThrowIfNull(stdout);
    ArgumentNullException.ThrowIfNull(stderr);
    lock (this._lock) {
      if (this._processes.Exists(process => process.Label == label))
        throw new ArgumentException($"the process '{label}' is already recorded", nameof(label));

      this._processes.Add(new(label, stdout, stderr, exitCode));
    }
  }

  /// <summary>Sets the comparison of the target's results with the reference's; a later call replaces an earlier one.</summary>
  /// <param name="report">The comparison.</param>
  /// <param name="policy">The policy it was made under.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public void AddComparison(ComparisonReport report, ExactnessPolicy policy) {
    ArgumentNullException.ThrowIfNull(report);
    ArgumentNullException.ThrowIfNull(policy);
    lock (this._lock)
      this._comparison = (report, policy);
  }

  /// <summary>Adds optimization diagnostics; each text becomes one or more lines of <c>opt-report.txt</c>.</summary>
  /// <param name="text">The diagnostics.</param>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
  public void AddOptDiagnostics(string text) {
    ArgumentNullException.ThrowIfNull(text);
    lock (this._lock)
      this._optDiagnostics = this._optDiagnostics is null ? text : this._optDiagnostics + "\n" + text;
  }

  /// <summary>Adds any other file, kept as <c>files/&lt;role&gt;/&lt;name&gt;</c>.</summary>
  /// <param name="role">What the file is: a file name as for <see cref="AddProcess"/>.</param>
  /// <param name="path">The file.</param>
  /// <exception cref="ArgumentException"><paramref name="role"/> is not a file name or <paramref name="path"/> is empty.</exception>
  public void AddFile(string role, string path) {
    CheckName(role, nameof(role));
    var checkedPath = FilePath(path);
    lock (this._lock)
      this._files.Add(new(role, checkedPath));
  }

  /// <summary>
  /// The bundle directory of this scenario under a root: the one it was given before, or a new one
  /// from <paramref name="claim"/>, so that writing a bundle twice rewrites the same directory.
  /// </summary>
  internal string BundleUnder(string root, Func<string> claim) {
    lock (this._lock) {
      if (!this._bundles.TryGetValue(root, out var directory))
        this._bundles[root] = directory = claim();

      return directory;
    }
  }

  private T Read<T>(Func<T> read) {
    lock (this._lock)
      return read();
  }

  private void Add(List<string> list, string item) {
    lock (this._lock)
      list.Add(item);
  }

  private void Set(List<KeyValuePair<string, string>> list, string key, string value) {
    lock (this._lock) {
      var index = list.FindIndex(pair => pair.Key == key);
      if (index < 0)
        list.Add(new(key, value));
      else
        list[index] = new(key, value);
    }
  }

  private static string FilePath(string path) => Text(path, nameof(path));

  private static string Text(string text, string parameter) {
    ArgumentException.ThrowIfNullOrEmpty(text, parameter);
    return text;
  }

  private static void CheckName(string name, string parameter) {
    ArgumentNullException.ThrowIfNull(name, parameter);
    if (!Name().IsMatch(name))
      throw new ArgumentException($"'{name}' is not a file name of letters, digits, '.', '_' and '-'", parameter);
  }

}
