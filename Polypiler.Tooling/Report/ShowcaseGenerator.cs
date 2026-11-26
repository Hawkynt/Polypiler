using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tooling.Report;

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

/// <summary>The outcome of translating one example to one backend.</summary>
/// <param name="Backend">The backend name passed to <c>--to</c>.</param>
/// <param name="Succeeded">Whether <c>polypiler translate</c> exited 0.</param>
/// <param name="Profile">The profile recorded in <c>polypiler.manifest.json</c>, if any.</param>
/// <param name="Files">The rendered output files when <paramref name="Succeeded"/> is true.</param>
/// <param name="DiagnosticCode">The first <c>PLY####</c> code when refused, if present.</param>
/// <param name="DiagnosticMessage">Concise refusal message when refused.</param>
public sealed record ShowcaseOutcome(
  string Backend,
  bool Succeeded,
  string? Profile,
  IReadOnlyList<ShowcaseFile> Files,
  string? DiagnosticCode,
  string? DiagnosticMessage);

/// <summary>Generates the cross-backend C# translation showcase Markdown by invoking the latest <c>polypiler</c> binary.</summary>
public static partial class ShowcaseGenerator {

  private static readonly TimeSpan _commandTimeout = TimeSpan.FromMinutes(2);

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

  /// <summary>Discovers all supported target backends from <c>polypiler translate --help</c>.</summary>
  public static IReadOnlyList<string> DiscoverBackends(IProcessRunner runner, IReadOnlyList<string> polypiler) {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(polypiler);
    var result = runner.Run([.. polypiler, "translate", "--help"], _commandTimeout);
    if (result.TimedOut || result.ExitCode != 0)
      throw new InvalidOperationException($"polypiler translate --help failed (exit {result.ExitCode}): {result.StandardError.Trim()}");

    var match = ToOptionPattern().Match(result.StandardOutput);
    if (!match.Success)
      throw new InvalidOperationException("could not discover target backends from 'polypiler translate --help'");

    return [.. match.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
  }

  /// <summary>Translates all examples across all backends and renders the complete Markdown document.</summary>
  public static string Generate(
    IProcessRunner runner,
    IReadOnlyList<string> polypiler,
    IReadOnlyList<ShowcaseExample> examples,
    IReadOnlyList<string> backends,
    string relativeExamplesDirectory = "showcase") {
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(polypiler);
    ArgumentNullException.ThrowIfNull(examples);
    ArgumentNullException.ThrowIfNull(backends);

    var matrix = new ShowcaseOutcome[examples.Count, backends.Count];
    var pairs = new List<(int Example, int Backend)>(examples.Count * backends.Count);
    for (var e = 0; e < examples.Count; ++e)
      for (var b = 0; b < backends.Count; ++b)
        pairs.Add((e, b));

    Parallel.ForEach(pairs, pair => {
      matrix[pair.Example, pair.Backend] = TranslateOne(runner, polypiler, examples[pair.Example], backends[pair.Backend]);
    });

    return RenderMarkdown(examples, backends, matrix, relativeExamplesDirectory);
  }

  /// <summary>Renders a precomputed translation matrix to deterministic LF Markdown.</summary>
  public static string RenderMarkdown(
    IReadOnlyList<ShowcaseExample> examples,
    IReadOnlyList<string> backends,
    ShowcaseOutcome[,] matrix,
    string relativeExamplesDirectory = "showcase") {
    var sb = new StringBuilder();
    sb.AppendLine("<!-- generated by \"polypiler-tooling report showcase\"; do not edit -->");
    sb.AppendLine("# Cross-Backend Translation Showcase");
    sb.AppendLine();
    sb.AppendLine("Live translations of high-level C# constructs generated directly by the latest `polypiler translate` across every supported backend. Binary targets (`.NET MSIL`, `JVM bytecode`) are disassembled from the real emitted `.dll` and `.jar` artifacts into readable assembler-like text mnemonics. Constructs not yet implemented in the C# frontend or a target backend report their exact `PLY####` diagnostic rather than fabricating code.");
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
    foreach (var backend in backends)
      sb.Append(' ').Append(DisplayBackendName(backend)).Append(" |");
    sb.AppendLine();
    sb.Append("| --- | --- |");
    for (var b = 0; b < backends.Count; ++b)
      sb.Append(" :---: |");
    sb.AppendLine();

    for (var e = 0; e < examples.Count; ++e) {
      var example = examples[e];
      sb.Append("| [").Append(example.Title).Append("](#").Append(Anchor(example.Title)).Append(") | ")
        .Append(example.Category).Append(" |");
      for (var b = 0; b < backends.Count; ++b) {
        var outcome = matrix[e, b];
        sb.Append(' ').Append(outcome.Succeeded ? "✅" : $"`{outcome.DiagnosticCode ?? "refused"}`").Append(" |");
      }
      sb.AppendLine();
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

      var succeeded = new List<ShowcaseOutcome>();
      var refused = new List<ShowcaseOutcome>();
      for (var b = 0; b < backends.Count; ++b) {
        var outcome = matrix[e, b];
        if (outcome.Succeeded)
          succeeded.Add(outcome);
        else
          refused.Add(outcome);
      }

      foreach (var item in succeeded) {
        var fileSummary = string.Join(", ", item.Files.Select(f => f.Disassembled ? $"`{f.Path}` (disassembled)" : $"`{f.Path}`"));
        var profileLabel = string.IsNullOrEmpty(item.Profile) ? "" : $"`{item.Profile}` — ";
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.Append("<summary><strong>").Append(DisplayBackendName(item.Backend)).Append("</strong> (")
          .Append(profileLabel).Append(fileSummary).AppendLine(")</summary>");
        foreach (var file in item.Files) {
          sb.AppendLine();
          if (item.Files.Count > 1 || file.Disassembled) {
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
        sb.AppendLine();
        sb.AppendLine("</details>");
      }

      if (refused.Count > 0) {
        sb.AppendLine();
        if (succeeded.Count == 0 && refused.Select(r => (r.DiagnosticCode, r.DiagnosticMessage)).Distinct().Count() == 1) {
          var first = refused[0];
          sb.Append("> **Frontend refusal (all backends):** `").Append(first.DiagnosticCode ?? "error")
            .Append("`: ").AppendLine(first.DiagnosticMessage);
        } else {
          var notes = refused.Select(r => $"`{r.Backend}` (`{r.DiagnosticCode ?? "error"}`: {r.DiagnosticMessage})");
          sb.Append("> **Refused by:** ").AppendLine(string.Join("; ", notes));
        }
      }
    }

    return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
  }

  private static ShowcaseOutcome TranslateOne(IProcessRunner runner, IReadOnlyList<string> polypiler, ShowcaseExample example, string backend) {
    var workDir = Path.Combine(Path.GetTempPath(), "polypiler-showcase-" + Guid.NewGuid().ToString("N"));
    try {
      var result = runner.Run([.. polypiler, "translate", example.FilePath, "--from", "csharp", "--to", backend, "--output", workDir], _commandTimeout);
      if (result.TimedOut)
        return new(backend, false, null, [], "TIMEOUT", "translation timed out");
      if (result.ExitCode != 0) {
        var (code, message) = ParseDiagnostic(result.StandardError, example.FilePath);
        return new(backend, false, null, [], code, message);
      }

      var profile = ReadProfile(workDir);
      var rendered = new List<ShowcaseFile>();
      if (Directory.Exists(workDir)) {
        var files = Directory.GetFiles(workDir, "*", SearchOption.AllDirectories)
          .Select(full => (Full: full, Relative: Path.GetRelativePath(workDir, full).Replace('\\', '/')))
          .Where(item => item.Relative != "polypiler.manifest.json" && !item.Relative.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
          .OrderBy(item => item.Relative, StringComparer.Ordinal);

        foreach (var (full, relative) in files)
          rendered.Add(RenderOutputFile(relative, File.ReadAllBytes(full)));
      }

      return new(backend, true, profile, rendered, null, null);
    } finally {
      if (Directory.Exists(workDir))
        Directory.Delete(workDir, recursive: true);
    }
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
      sb.Append(offset.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append(':');
      var end = Math.Min(offset + 16, limit);
      for (var i = offset; i < end; ++i)
        sb.Append(' ').Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
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
    var firstLine = stderr.Replace("\r\n", "\n", StringComparison.Ordinal)
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .FirstOrDefault(line => line.Contains("error PLY", StringComparison.Ordinal))
      ?? stderr.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
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
    "batch" => "Batch",
    "msil" => "MSIL",
    "jvm" => "JVM",
    _ => backend
  };

  private static string FenceForExtension(string extension) => extension switch {
    ".cs" => "csharp",
    ".js" or ".mjs" or ".cjs" => "javascript",
    ".py" => "python",
    ".rs" => "rust",
    ".toml" => "toml",
    ".pm" or ".pl" or ".t" => "perl",
    ".hpp" or ".cpp" or ".h" => "cpp",
    ".vbs" => "vbscript",
    ".bat" or ".cmd" => "bat",
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

  [GeneratedRegex(@"--to\s+<([^>]+)>", RegexOptions.CultureInvariant)]
  private static partial Regex ToOptionPattern();

  [GeneratedRegex(@"error\s+(PLY\d{4}):\s*(.+)$", RegexOptions.CultureInvariant)]
  private static partial Regex DiagnosticLinePattern();

}
