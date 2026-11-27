using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Polypiler.Core.Frontends.Go.AstDump;

/// <summary>
/// Builds <c>main.go</c>, the helper that parses with <c>go/parser</c> and type-checks with
/// <c>go/types</c>, with the installed <c>go</c> once per version of the helper and of Go into the
/// cache, and runs it with the source on standard input. The helper only parses and type-checks: the
/// source is never built or executed. Every build runs with <c>GOTOOLCHAIN=local</c>,
/// <c>GOPROXY=off</c>, <c>GOFLAGS=-mod=mod</c> and <c>CGO_ENABLED=0</c>, so that nothing is fetched.
/// </summary>
internal static class GoHelper {

  /// <summary>The environment variable that names the <c>go</c> command, before <c>go</c> on the path.</summary>
  public const string ToolchainVariable = "POLYPILER_GO";

  /// <summary>The environment variable that names the folder the built helper is kept in; the temporary folder otherwise.</summary>
  public const string CacheVariable = "POLYPILER_GO_CACHE";

  /// <summary>The text of the helper, as the assembly carries it.</summary>
  public static string Source { get; } = ReadSource();

  /// <summary>The <c>go</c> commands tried, in order: the one given alone, otherwise the one the environment names, then <c>go</c>.</summary>
  public static IReadOnlyList<string> Candidates(string? named) {
    if (!string.IsNullOrWhiteSpace(named))
      return [named];

    var candidates = new List<string>();
    var configured = Environment.GetEnvironmentVariable(ToolchainVariable);
    if (!string.IsNullOrWhiteSpace(configured))
      candidates.Add(configured);

    candidates.Add("go");
    return candidates;
  }

  /// <summary>Parses and type-checks a source with the first <c>go</c> that starts.</summary>
  /// <param name="path">The name of the file, which the messages carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="toolchain">A <c>go</c> command to try first, or <see langword="null"/>.</param>
  /// <returns>What the helper printed, or the reason it printed nothing.</returns>
  public static (string? Json, string? Failure) Run(string path, string source, string? toolchain) {
    var failures = new List<string>();
    foreach (var candidate in Candidates(toolchain)) {
      var (exitCode, output, errors, started) = Execute(candidate, ["env", "GOROOT", "GOVERSION"], null, null, null);
      if (!started) {
        failures.Add(errors);
        continue;
      }

      if (exitCode != 0)
        return (null, $"{candidate} env exited with {exitCode}: {errors.Trim()}");

      var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      var root = lines.Length > 0 ? lines[0] : "";
      var (helper, failure) = Built(candidate, output.Trim());
      if (helper is null)
        return (null, failure);

      // The helper resolves the imports from the source of the standard library, which it finds through GOROOT.
      var (code, json, messages, _) = Execute(helper, [Path.GetFileName(path)], source, null, root);
      return code == 0 ? (json, null) : (null, $"the Go helper exited with {code}: {messages.Trim()}");
    }

    return (null, $"no go command could be started ({string.Join("; ", failures)}); set {ToolchainVariable} to one");
  }

  /// <summary>The path of the helper built by a <c>go</c> command, built when it is not yet there.</summary>
  private static (string? Helper, string? Failure) Built(string toolchain, string version) {
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source + "\n" + version)))[..16].ToLowerInvariant();
    var cache = Environment.GetEnvironmentVariable(CacheVariable) is { Length: > 0 } configured ? configured : Path.Combine(Path.GetTempPath(), "polypiler-go-dump");
    var helper = Path.Combine(cache, hash, OperatingSystem.IsWindows() ? "polypiler-go-dump.exe" : "polypiler-go-dump");
    if (File.Exists(helper))
      return (helper, null);

    var work = Path.Combine(cache, hash + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try {
      File.WriteAllText(Path.Combine(work, "go.mod"), "module polypilergodump\n\ngo 1.18\n");
      File.WriteAllText(Path.Combine(work, "main.go"), Source);
      var built = Path.Combine(work, Path.GetFileName(helper));
      var (exitCode, _, errors, _) = Execute(toolchain, ["build", "-trimpath", "-o", built, "."], null, work, null);
      if (exitCode != 0)
        return (null, $"{toolchain} build of the Go helper exited with {exitCode}: {errors.Trim()}");

      Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
      try {
        File.Move(built, helper);
      } catch (IOException) when (File.Exists(helper)) {
        // Another import built the same helper at the same time.
      }

      return (helper, null);
    } finally {
      try {
        Directory.Delete(work, true);
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }
    }
  }

  private static (int ExitCode, string Output, string Errors, bool Started) Execute(string program, IReadOnlyList<string> arguments, string? input, string? directory, string? root) {
    var start = new ProcessStartInfo(program) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
      WorkingDirectory = directory ?? Environment.CurrentDirectory
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    start.Environment["GOTOOLCHAIN"] = "local";
    start.Environment["GOPROXY"] = "off";
    start.Environment["GOFLAGS"] = "-mod=mod";
    start.Environment["CGO_ENABLED"] = "0";
    if (!string.IsNullOrEmpty(root))
      start.Environment["GOROOT"] = root;

    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOCACHE")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME"))
      && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LocalAppData")))
      start.Environment["GOCACHE"] = Path.Combine(Path.GetTempPath(), "polypiler-go-build-cache");

    Process process;
    try {
      process = Process.Start(start)!;
    } catch (Win32Exception exception) {
      return (-1, "", $"{program}: {exception.Message}", false);
    }

    using (process) {
      var output = process.StandardOutput.ReadToEndAsync();
      var errors = process.StandardError.ReadToEndAsync();
      using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        writer.Write(input ?? "");

      Task.WaitAll(output, errors);
      process.WaitForExit();
      return (process.ExitCode, output.Result, errors.Result, true);
    }
  }

  private static string ReadSource() {
    using var stream = typeof(GoHelper).Assembly.GetManifestResourceStream("Polypiler.Core.Frontends.Go.main.go")!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

}
