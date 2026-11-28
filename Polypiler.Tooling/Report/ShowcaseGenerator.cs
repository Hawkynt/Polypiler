using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Report;

/// <summary>A target backend together with all of its supported dialect profiles.</summary>
/// <param name="Backend">The backend name passed to <c>--to</c>.</param>
/// <param name="Profiles">The dialect profile names passed to <c>--profile</c>, in canonical order.</param>
public sealed record ShowcaseTarget(string Backend, IReadOnlyList<string> Profiles);

/// <summary>A curated C# showcase example read from <c>docs/showcase/</c>.</summary>
/// <param name="FileName">The file name, e.g. <c>04-structs-and-records.cs</c>.</param>
/// <param name="FilePath">The absolute path passed to <c>polypiler translate</c>.</param>
/// <param name="Title">The human-readable heading.</param>
/// <param name="Category">The grouping label shown in the summary matrix.</param>
/// <param name="Summary">A one-line summary of the constructs demonstrated.</param>
/// <param name="DisplaySource">The C# source with leading metadata comments removed.</param>
public sealed record ShowcaseExample(
  string FileName,
  string FilePath,
  string Title,
  string Category,
  string Summary,
  string DisplaySource);

/// <summary>One rendered file produced by a backend for a showcase example.</summary>
/// <param name="Path">Portable relative path of the emitted file.</param>
/// <param name="Fence">Markdown fenced code block language identifier.</param>
/// <param name="Content">Verbatim text or disassembled assembler-like text.</param>
/// <param name="Disassembled">Whether <paramref name="Content"/> was disassembled from a binary output.</param>
public sealed record ShowcaseFile(string Path, string Fence, string Content, bool Disassembled);

/// <summary>The outcome of translating one example to one backend dialect.</summary>
/// <param name="Backend">The backend name passed to <c>--to</c>.</param>
/// <param name="RequestedProfile">The dialect profile name passed to <c>--profile</c>.</param>
/// <param name="Succeeded">Whether <c>polypiler translate</c> exited 0.</param>
/// <param name="Profile">The profile recorded in <c>polypiler.manifest.json</c>, if any.</param>
/// <param name="Files">The rendered output files when <paramref name="Succeeded"/> is true.</param>
/// <param name="DiagnosticCode">The first <c>PLY####</c> code when refused, if present.</param>
/// <param name="DiagnosticMessage">Concise refusal message when refused.</param>
public sealed record ShowcaseOutcome(
  string Backend,
  string RequestedProfile,
  bool Succeeded,
  string? Profile,
  IReadOnlyList<ShowcaseFile> Files,
  string? DiagnosticCode,
  string? DiagnosticMessage);

/// <summary>Generates the cross-backend, dialect-grouped C# translation showcase Markdown by invoking the latest <c>polypiler</c> binary.</summary>
public static partial class ShowcaseGenerator {

  private static readonly TimeSpan _discoveryTimeout = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan _batchTimeout = TimeSpan.FromMinutes(10);
  private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

  /// <summary>Reads all <c>*.cs</c> showcase examples from <paramref name="directory"/> in deterministic file-name order.</summary>
  public static IReadOnlyList<ShowcaseExample> LoadExamples(string directory) {
    ArgumentNullException.ThrowIfNull(directory);
    var files = Directory.GetFiles(directory, "*.cs").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
    var result = new List<ShowcaseExample>(files.Count);
    foreach (var file in files) {
      var raw = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
      var lines = raw.Split('\n');
      var title = Path.GetFileNameWithoutExtension(file);
      var category = "General";
      var summary = "";
      var bodyStart = 0;

      while (bodyStart < lines.Length && lines[bodyStart].StartsWith("// ", StringComparison.Ordinal)) {
        var line = lines[bodyStart];
        if (line.StartsWith("// Showcase:", StringComparison.Ordinal))
          title = line["// Showcase:".Length..].Trim();
        else if (line.StartsWith("// Category:", StringComparison.Ordinal))
          category = line["// Category:".Length..].Trim();
        else if (line.StartsWith("// Summary:", StringComparison.Ordinal))
          summary = line["// Summary:".Length..].Trim();
        else
          break;
        ++bodyStart;
      }

      var display = string.Join('\n', lines[bodyStart..]).Trim();
      result.Add(new(Path.GetFileName(file), Path.GetFullPath(file), title, category, summary, display));
    }

    return result;
  }

  /// <summary>Discovers all supported target backends and their dialect profiles from <c>polypiler targets</c>.</summary>
  public static IReadOnlyList<ShowcaseTarget> DiscoverTargets(IProcessRunner runner, IReadOnlyList<string> polypiler) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(polypiler);
    var result = runner.Run([.. polypiler, "targets"], _discoveryTimeout);
    if (result.TimedOut || result.ExitCode != 0)
      throw new InvalidOperationException($"polypiler targets failed (exit {result.ExitCode}): {result.StandardError.Trim()}");

    List<ShowcaseTarget>? targets;
    try {
      targets = JsonSerializer.Deserialize<List<ShowcaseTarget>>(result.StandardOutput, _json);
    } catch (JsonException exception) {
      throw new InvalidOperationException($"could not parse 'polypiler targets' output: {exception.Message}", exception);
    }

    if (targets is null || targets.Count == 0)
      throw new InvalidOperationException("could not discover target backends from 'polypiler targets'");

    return targets;
  }

  /// <summary>Translates all examples across all discovered backends and dialects and renders the complete Markdown document.</summary>
  public static string Generate(
    IProcessRunner runner,
    IReadOnlyList<string> polypiler,
    IReadOnlyList<ShowcaseExample> examples,
    IReadOnlyList<ShowcaseTarget> targets,
    string relativeExamplesDirectory = "showcase") {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(polypiler);
    ArgumentNullException.ThrowIfNull(examples);
    ArgumentNullException.ThrowIfNull(targets);

    var matrix = new IReadOnlyList<ShowcaseOutcome>[examples.Count, targets.Count];
    var workRoot = Path.Combine(Path.GetTempPath(), "polypiler-showcase-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(workRoot);
      var jobs = new List<BatchJobSpec>();
      var jobMap = new Dictionary<string, (int Example, int Target, int Profile, string OutputDir)>(StringComparer.Ordinal);

      for (var e = 0; e < examples.Count; ++e)
        for (var t = 0; t < targets.Count; ++t) {
          var target = targets[t];
          for (var p = 0; p < target.Profiles.Count; ++p) {
            var id = $"{e}:{t}:{p}";
            var outDir = Path.Combine(workRoot, $"{e}-{t}-{p}");
            var profile = target.Profiles[p];
            jobs.Add(new(id, [examples[e].FilePath, "--from", "csharp", "--to", target.Backend, "--profile", profile, "--output", outDir]));
            jobMap[id] = (e, t, p, outDir);
          }
        }

      var perCell = new ShowcaseOutcome[examples.Count, targets.Count][];
      for (var e = 0; e < examples.Count; ++e)
        for (var t = 0; t < targets.Count; ++t)
          perCell[e, t] = new ShowcaseOutcome[targets[t].Profiles.Count];

      if (jobs.Count > 0) {
        var batchPath = Path.Combine(workRoot, "jobs.json");
        File.WriteAllText(batchPath, JsonSerializer.Serialize(jobs, _json), new UTF8Encoding(false));
        var run = runner.Run([.. polypiler, "targets", "--run-batch", batchPath], _batchTimeout);
        if (run.TimedOut || run.ExitCode != 0)
          throw new InvalidOperationException($"polypiler targets --run-batch failed (exit {run.ExitCode}): {run.StandardError.Trim()}");

        var results = JsonSerializer.Deserialize<BatchJobOutcome[]>(run.StandardOutput, _json)
          ?? throw new InvalidOperationException("could not parse 'polypiler targets --run-batch' output");

        foreach (var item in results) {
          var (e, t, p, outDir) = jobMap[item.Id];
          var backend = targets[t].Backend;
          var requestedProfile = targets[t].Profiles[p];
          perCell[e, t][p] = ReadOutcome(examples[e], backend, requestedProfile, item.ExitCode, item.Stderr, outDir);
        }
      }

      for (var e = 0; e < examples.Count; ++e)
        for (var t = 0; t < targets.Count; ++t)
          matrix[e, t] = perCell[e, t];

      return RenderMarkdown(examples, targets, matrix, relativeExamplesDirectory);
    } finally {
      if (Directory.Exists(workRoot))
        Directory.Delete(workRoot, recursive: true);
    }
  }

  /// <summary>Renders a precomputed backend/dialect translation matrix to deterministic LF Markdown.</summary>
  public static string RenderMarkdown(
    IReadOnlyList<ShowcaseExample> examples,
    IReadOnlyList<ShowcaseTarget> targets,
    IReadOnlyList<ShowcaseOutcome>[,] matrix,
    string relativeExamplesDirectory = "showcase") {
    var sb = new StringBuilder();
    sb.AppendLine("<!-- generated by \"polypiler-tooling report showcase\"; do not edit -->");
    sb.AppendLine("# Cross-Backend Translation Showcase");
    sb.AppendLine();
    sb.AppendLine("Live translations of high-level C# constructs generated directly by the latest `polypiler translate` across every supported backend and target dialect. Dialects are grouped under their parent backend so you can compare how features lower across versions (such as `es5` vs `es2022`, `net2.0` vs `net10.0`, `py3.8` vs `py3.14`, `perl/5.6` vs `perl/5`, `cpp/cpp17` vs `cpp/cpp23`, `cmd` vs `dos622`, `visualbasic/vb6` vs `visualbasic/vba7-64` vs `vbnet/vb16.9`, `jvm/8` vs `jvm/21`, `msil/net8` vs `msil/net10`, `go1.18` vs `go1.27`, and `kotlin2.0` vs `kotlin2.4-jvm21`). Structurally identical dialect outputs under the same backend are collapsed together while every distinct lowering is shown verbatim. Binary targets (`.NET MSIL`, `JVM bytecode`) are disassembled from the real emitted `.dll` and `.jar` artifacts into readable assembler-like text mnemonics. Constructs not yet implemented report their exact `PLY####` diagnostic rather than fabricating code.");
    sb.AppendLine();
    sb.AppendLine("Regenerate locally with:");
    sb.AppendLine();
    sb.AppendLine("```bash");
    sb.AppendLine("dotnet build Polypiler.Cli/Polypiler.Cli.csproj -c Release");
    sb.AppendLine("dotnet run --project Polypiler.Tooling/Polypiler.Tooling.csproj -c Release -- report showcase");
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("## Summary matrix");
    sb.AppendLine();
    sb.Append("| Construct | Category |");
    foreach (var target in targets)
      sb.Append(' ').Append(DisplayBackendName(target.Backend)).Append(" |");
    sb.AppendLine();
    sb.Append("| --- | --- |");
    for (var t = 0; t < targets.Count; ++t)
      sb.Append(" :---: |");
    sb.AppendLine();

    for (var e = 0; e < examples.Count; ++e) {
      var example = examples[e];
      sb.Append("| [").Append(example.Title).Append("](#").Append(Anchor(example.Title)).Append(") | ")
        .Append(example.Category).Append(" |");
      for (var t = 0; t < targets.Count; ++t)
        sb.Append(' ').Append(FormatOverviewCell(matrix[e, t])).Append(" |");
      sb.AppendLine();
    }

    var multiDialectTargets = targets.Select((target, index) => (Target: target, Index: index))
      .Where(item => item.Target.Profiles.Count > 1)
      .ToList();
    if (multiDialectTargets.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("## Dialect support by backend");
      foreach (var (target, t) in multiDialectTargets) {
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.Append("<summary><strong>").Append(DisplayBackendName(target.Backend)).Append("</strong> (")
          .Append(target.Profiles.Count.ToString(CultureInfo.InvariantCulture)).Append(" dialects: ")
          .Append(string.Join(", ", target.Profiles.Select(p => $"<code>{p}</code>"))).AppendLine(")</summary>");
        sb.AppendLine();
        sb.Append("| Construct |");
        foreach (var profile in target.Profiles)
          sb.Append(" `").Append(profile).Append("` |");
        sb.AppendLine();
        sb.Append("| --- |");
        for (var p = 0; p < target.Profiles.Count; ++p)
          sb.Append(" :---: |");
        sb.AppendLine();
        for (var e = 0; e < examples.Count; ++e) {
          var example = examples[e];
          sb.Append("| [").Append(example.Title).Append("](#").Append(Anchor(example.Title)).Append(") |");
          foreach (var outcome in matrix[e, t])
            sb.Append(' ').Append(outcome.Succeeded ? "✅" : $"`{outcome.DiagnosticCode ?? "refused"}`").Append(" |");
          sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
      }
    }

    for (var e = 0; e < examples.Count; ++e) {
      var example = examples[e];
      sb.AppendLine();
      sb.Append("## ").AppendLine(example.Title);
      sb.AppendLine();
      if (example.Summary.Length > 0)
        sb.Append(example.Summary).Append(' ');
      sb.Append("Source: [`").Append(example.FileName).Append("`](").Append(relativeExamplesDirectory.TrimEnd('/')).Append('/').Append(example.FileName).AppendLine(").");
      sb.AppendLine();
      sb.AppendLine("```csharp");
      sb.AppendLine(example.DisplaySource);
      sb.AppendLine("```");

      var fullyRefusedBackends = new List<(string Backend, IReadOnlyList<ShowcaseOutcome> Outcomes)>();
      var anyBackendSucceeded = false;

      for (var t = 0; t < targets.Count; ++t) {
        var outcomes = matrix[e, t];
        var succeeded = outcomes.Where(o => o.Succeeded).ToList();
        var refused = outcomes.Where(o => !o.Succeeded).ToList();
        if (succeeded.Count == 0) {
          fullyRefusedBackends.Add((targets[t].Backend, refused));
          continue;
        }

        anyBackendSucceeded = true;
        var groups = GroupByStructuralOutput(succeeded);
        RenderBackendSection(sb, targets[t].Backend, outcomes.Count, groups, refused);
      }

      if (fullyRefusedBackends.Count > 0) {
        sb.AppendLine();
        var allRefusals = fullyRefusedBackends.SelectMany(b => b.Outcomes).ToList();
        if (!anyBackendSucceeded && allRefusals.Select(r => (r.DiagnosticCode, r.DiagnosticMessage)).Distinct().Count() == 1) {
          var first = allRefusals[0];
          sb.Append("> **Frontend refusal (all backends & dialects):** `").Append(first.DiagnosticCode ?? "error")
            .Append("`: ").AppendLine(first.DiagnosticMessage);
        } else {
          var notes = fullyRefusedBackends.Select(entry => FormatRefusedBackend(entry.Backend, entry.Outcomes));
          sb.Append("> **Refused by:** ").AppendLine(string.Join("; ", notes));
        }
      }
    }

    return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
  }

  private static void RenderBackendSection(
    StringBuilder sb,
    string backend,
    int totalDialects,
    IReadOnlyList<DialectGroup> groups,
    IReadOnlyList<ShowcaseOutcome> refused) {
    var displayName = DisplayBackendName(backend);
    sb.AppendLine();
    sb.AppendLine("<details>");

    if (groups.Count == 1 && refused.Count == 0) {
      var only = groups[0];
      var dialectList = string.Join(", ", only.Dialects.Select(d => $"<code>{d}</code>"));
      var fileSummary = FormatFileSummary(only.Representative.Files);
      var qualifier = only.Dialects.Count > 1 ? "identical output — " : "";
      sb.Append("<summary><strong>").Append(displayName).Append("</strong> — ")
        .Append(dialectList).Append(" (").Append(qualifier).Append(fileSummary).AppendLine(")</summary>");
      RenderFiles(sb, only.Representative.Files);
      sb.AppendLine();
      sb.AppendLine("</details>");
      return;
    }

    var passedCount = groups.Sum(g => g.Dialects.Count);
    var statusPart = refused.Count == 0
      ? $"{totalDialects.ToString(CultureInfo.InvariantCulture)} dialects"
      : $"{passedCount.ToString(CultureInfo.InvariantCulture)}/{totalDialects.ToString(CultureInfo.InvariantCulture)} dialects";
    var variantPart = groups.Count == 1
      ? "1 output"
      : $"{groups.Count.ToString(CultureInfo.InvariantCulture)} distinct outputs";
    sb.Append("<summary><strong>").Append(displayName).Append("</strong> — ")
      .Append(statusPart).Append(" (").Append(variantPart).AppendLine(")</summary>");

    foreach (var group in groups) {
      var label = group.Dialects.Count == 1 ? "Dialect" : "Dialects";
      var dialectList = string.Join(", ", group.Dialects.Select(d => $"<code>{d}</code>"));
      sb.AppendLine();
      sb.AppendLine("<details>");
      sb.Append("<summary>").Append(label).Append(": ").Append(dialectList)
        .Append(" (").Append(FormatFileSummary(group.Representative.Files)).AppendLine(")</summary>");
      RenderFiles(sb, group.Representative.Files);
      sb.AppendLine();
      sb.AppendLine("</details>");
    }

    if (refused.Count > 0) {
      var groupedRefusals = refused
        .GroupBy(r => (r.DiagnosticCode, r.DiagnosticMessage))
        .Select(g => $"{string.Join(", ", g.Select(x => $"`{x.RequestedProfile}`"))} (`{g.Key.DiagnosticCode ?? "error"}`: {g.Key.DiagnosticMessage})");
      sb.AppendLine();
      sb.Append("> **Refused dialects:** ").AppendLine(string.Join("; ", groupedRefusals));
    }

    sb.AppendLine();
    sb.AppendLine("</details>");
  }

  private static void RenderFiles(StringBuilder sb, IReadOnlyList<ShowcaseFile> files) {
    foreach (var file in files) {
      sb.AppendLine();
      if (files.Count > 1 || file.Disassembled) {
        sb.Append("**`").Append(file.Path).Append("`**");
        if (file.Disassembled)
          sb.Append(" *(disassembled mnemonics)*");
        sb.AppendLine();
        sb.AppendLine();
      }
      sb.Append("```").AppendLine(file.Fence);
      sb.AppendLine(file.Content.TrimEnd());
      sb.AppendLine("```");
    }
  }

  private static string FormatFileSummary(IReadOnlyList<ShowcaseFile> files)
    => string.Join(", ", files.Select(f => f.Disassembled ? $"<code>{f.Path}</code> (disassembled)" : $"<code>{f.Path}</code>"));

  private static string FormatOverviewCell(IReadOnlyList<ShowcaseOutcome> outcomes) {
    var passed = outcomes.Count(o => o.Succeeded);
    if (passed == outcomes.Count)
      return "✅";
    if (passed > 0)
      return $"⚠️ {passed.ToString(CultureInfo.InvariantCulture)}/{outcomes.Count.ToString(CultureInfo.InvariantCulture)}";

    var codes = outcomes.Select(o => o.DiagnosticCode ?? "refused").Distinct(StringComparer.Ordinal).ToList();
    return string.Join("/", codes.Select(code => $"`{code}`"));
  }

  private static string FormatRefusedBackend(string backend, IReadOnlyList<ShowcaseOutcome> refused) {
    var distinct = refused.GroupBy(r => (r.DiagnosticCode, r.DiagnosticMessage)).ToList();
    if (distinct.Count == 1) {
      var first = distinct[0].Key;
      return $"`{backend}` (`{first.DiagnosticCode ?? "error"}`: {first.DiagnosticMessage})";
    }

    var perDialect = distinct.Select(g => $"{string.Join("/", g.Select(x => $"`{x.RequestedProfile}`"))}: `{g.Key.DiagnosticCode ?? "error"}`: {g.Key.DiagnosticMessage}");
    return $"`{backend}` ({string.Join("; ", perDialect)})";
  }

  private static IReadOnlyList<DialectGroup> GroupByStructuralOutput(IReadOnlyList<ShowcaseOutcome> succeeded) {
    var groups = new List<DialectGroup>();
    var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var outcome in succeeded) {
      var key = StructuralKey(outcome.Files);
      if (indexByKey.TryGetValue(key, out var existing)) {
        groups[existing].Dialects.Add(outcome.RequestedProfile);
      } else {
        indexByKey[key] = groups.Count;
        groups.Add(new([outcome.RequestedProfile], outcome));
      }
    }
    return groups;
  }

  /// <summary>
  /// Builds a comparison key across emitted files while ignoring pure profile-banner metadata lines
  /// so dialects that emit structurally identical code collapse cleanly under one heading.
  /// </summary>
  internal static string StructuralKey(IReadOnlyList<ShowcaseFile> files) {
    var sb = new StringBuilder();
    foreach (var file in files) {
      sb.Append(file.Path).Append('\0');
      var isCargoToml = file.Path.EndsWith("Cargo.toml", StringComparison.Ordinal);
      foreach (var line in file.Content.Split('\n')) {
        if (IsProfileBannerLine(line, isCargoToml))
          continue;
        sb.Append(line).Append('\n');
      }
      sb.Append('\0');
    }
    return sb.ToString();
  }

  private static bool IsProfileBannerLine(string line, bool isCargoToml) {
    if (line.StartsWith("// Profile:", StringComparison.Ordinal)
      || line.StartsWith("// JavaScript profile:", StringComparison.Ordinal)
      || line.StartsWith("# Python profile:", StringComparison.Ordinal)
      || line.StartsWith("//! Rust profile:", StringComparison.Ordinal)
      || line.StartsWith("// Kotlin profile:", StringComparison.Ordinal)
      || line.StartsWith("// classfile version ", StringComparison.Ordinal))
      return true;
    return isCargoToml && (line.StartsWith("edition = ", StringComparison.Ordinal) || line.StartsWith("rust-version = ", StringComparison.Ordinal));
  }

  private static ShowcaseOutcome ReadOutcome(
    ShowcaseExample example,
    string backend,
    string requestedProfile,
    int exitCode,
    string stderr,
    string outputDirectory) {
    if (exitCode != 0) {
      var (code, message) = ParseDiagnostic(stderr, example.FilePath);
      return new(backend, requestedProfile, false, null, [], code, message);
    }

    var profile = ReadProfile(outputDirectory);
    var rendered = new List<ShowcaseFile>();
    if (Directory.Exists(outputDirectory)) {
      var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
        .Select(full => (Full: full, Relative: Path.GetRelativePath(outputDirectory, full).Replace('\\', '/')))
        .Where(item => item.Relative != "polypiler.manifest.json" && !item.Relative.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
        .OrderBy(item => item.Relative, StringComparer.Ordinal);

      foreach (var (full, relative) in files)
        rendered.Add(RenderOutputFile(relative, File.ReadAllBytes(full)));
    }

    return new(backend, requestedProfile, true, profile, rendered, null, null);
  }

  /// <summary>Converts one emitted file (text or binary) into a Markdown-ready <see cref="ShowcaseFile"/>.</summary>
  public static ShowcaseFile RenderOutputFile(string relativePath, byte[] bytes) {
    var extension = Path.GetExtension(relativePath).ToLowerInvariant();
    if (extension is ".dll" or ".exe")
      return new(relativePath, "il", SafeDisassemble(() => MsilDisassembler.Disassemble(bytes), bytes), true);
    if (extension == ".jar")
      return new(relativePath, "text", SafeDisassemble(() => JvmDisassembler.DisassembleJar(bytes), bytes), true);
    if (extension == ".class")
      return new(relativePath, "text", SafeDisassemble(() => JvmDisassembler.DisassembleClass(bytes), bytes), true);
    if (bytes.Contains((byte)0))
      return new(relativePath, "text", FormatHexSummary(bytes), true);

    var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
    return new(relativePath, FenceForExtension(extension), text, false);
  }

  private static string SafeDisassemble(Func<string> disassemble, byte[] bytes) {
    try {
      return disassemble();
    } catch (Exception) {
      return FormatHexSummary(bytes);
    }
  }

  private static string FormatHexSummary(byte[] bytes) {
    var sb = new StringBuilder();
    sb.Append("; binary artifact (").Append(bytes.Length).AppendLine(" bytes)");
    var limit = Math.Min(bytes.Length, 64);
    for (var offset = 0; offset < limit; offset += 16) {
      sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append(':');
      var end = Math.Min(offset + 16, limit);
      for (var i = offset; i < end; ++i)
        sb.Append(' ').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
      sb.AppendLine();
    }
    return sb.ToString();
  }

  private static string? ReadProfile(string outputDirectory) {
    var manifestPath = Path.Combine(outputDirectory, "polypiler.manifest.json");
    if (!File.Exists(manifestPath))
      return null;

    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
    return document.RootElement.TryGetProperty("target", out var target) && target.TryGetProperty("profile", out var profile)
      ? profile.GetString()
      : null;
  }

  private static (string? Code, string Message) ParseDiagnostic(string stderr, string sourcePath) {
    var lines = stderr.Replace("\r\n", "\n", StringComparison.Ordinal)
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var firstLine = lines.FirstOrDefault(line => line.Contains("error PLY", StringComparison.Ordinal))
      ?? lines.FirstOrDefault()
      ?? "translation failed";

    var match = DiagnosticLinePattern().Match(firstLine);
    if (match.Success)
      return (match.Groups[1].Value, match.Groups[2].Value.Trim());

    var normalized = firstLine.Replace(sourcePath, Path.GetFileName(sourcePath), StringComparison.Ordinal);
    return (null, normalized);
  }

  private static string DisplayBackendName(string backend) => backend switch {
    "csharp" => "C#",
    "javascript" => "JavaScript",
    "python" => "Python",
    "rust" => "Rust",
    "perl" => "Perl",
    "cpp" => "C++",
    "vbscript" => "VBScript",
    "visualbasic" => "Visual Basic (VB6/VBA, VB.NET)",
    "batch" => "Batch",
    "msil" => "MSIL",
    "jvm" => "JVM",
    "go" => "Go",
    "kotlin" => "Kotlin",
    _ => backend
  };

  private static string FenceForExtension(string extension) => extension switch {
    ".cs" => "csharp",
    ".js" or ".mjs" or ".cjs" => "javascript",
    ".py" => "python",
    ".rs" => "rust",
    ".kt" or ".kts" => "kotlin",
    ".toml" => "toml",
    ".pm" or ".pl" or ".t" => "perl",
    ".hpp" or ".cpp" or ".h" => "cpp",
    ".vbs" => "vbscript",
    ".bas" => "vb",
    ".bat" or ".cmd" => "bat",
    ".go" => "go",
    _ => "text"
  };

  private static string Anchor(string title) {
    var sb = new StringBuilder(title.Length);
    foreach (var ch in title.ToLowerInvariant()) {
      if (char.IsLetterOrDigit(ch) || ch == '-')
        sb.Append(ch);
      else if (ch == ' ')
        sb.Append('-');
    }
    return sb.ToString();
  }

  private sealed record DialectGroup(List<string> Dialects, ShowcaseOutcome Representative);

  private sealed record BatchJobSpec(string Id, string[] Args);

  private sealed record BatchJobOutcome(string Id, int ExitCode, string Stderr);

  [GeneratedRegex(@"error\s+(PLY\d{4}):\s*(.+)$", RegexOptions.CultureInvariant)]
  private static partial Regex DiagnosticLinePattern();

}
