using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Polypiler.Core.Frontends.Python.AstDump;

/// <summary>
/// Runs <c>polypiler_ast_dump.py</c> in the installed CPython: <c>python -I -B -c &lt;helper&gt; &lt;file&gt;</c>
/// with the source on standard input. <c>-I</c> keeps the user's environment, site directory and the
/// current directory out of the interpreter, <c>-B</c> writes no bytecode. The helper only parses: the
/// source is never compiled, imported or executed.
/// </summary>
internal static class AstHelper {

  /// <summary>The environment variable that names the interpreter, before <c>python3</c> and <c>python</c> on the path.</summary>
  public const string InterpreterVariable = "POLYPILER_PYTHON";

  /// <summary>The text of the helper, as the assembly carries it.</summary>
  public static string Script { get; } = ReadScript();

  /// <summary>The interpreters tried, in order: the one the environment names, then <c>python3</c> and <c>python</c>.</summary>
  public static IReadOnlyList<string> Candidates(string? named) {
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(named))
      candidates.Add(named);

    var configured = Environment.GetEnvironmentVariable(InterpreterVariable);
    if (!string.IsNullOrWhiteSpace(configured))
      candidates.Add(configured);

    candidates.Add("python3");
    candidates.Add("python");
    return candidates;
  }

  /// <summary>Parses a source with the first interpreter that starts.</summary>
  /// <param name="path">The name of the file, which python's messages carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="interpreter">An interpreter to try first, or <see langword="null"/>.</param>
  /// <returns>What the helper printed, or the reason it printed nothing.</returns>
  public static (string? Json, string? Failure) Run(string path, string source, string? interpreter) => Run(path, source, Candidates(interpreter));

  /// <summary>Parses a source with the first of some interpreters that starts.</summary>
  /// <param name="path">The name of the file, which python's messages carry.</param>
  /// <param name="source">The text of the file.</param>
  /// <param name="candidates">The interpreters, in the order they are tried.</param>
  /// <returns>What the helper printed, or the reason it printed nothing.</returns>
  public static (string? Json, string? Failure) Run(string path, string source, IReadOnlyList<string> candidates) {
    var failures = new List<string>();
    foreach (var candidate in candidates) {
      var (json, failure, started) = RunWith(candidate, path, source);
      if (started)
        return (json, failure);

      failures.Add(failure!);
    }

    return (null, $"no Python interpreter could be started ({string.Join("; ", failures)}); set {InterpreterVariable} to one");
  }

  private static (string? Json, string? Failure, bool Started) RunWith(string interpreter, string path, string source) {
    var start = new ProcessStartInfo(interpreter) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8
    };
    foreach (var argument in new[] { "-I", "-B", "-c", Script, Path.GetFileName(path) })
      start.ArgumentList.Add(argument);

    Process process;
    try {
      process = Process.Start(start)!;
    } catch (Win32Exception exception) {
      return (null, $"{interpreter}: {exception.Message}", false);
    }

    using (process) {
      var output = process.StandardOutput.ReadToEndAsync();
      var errors = process.StandardError.ReadToEndAsync();
      using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        input.Write(source);

      Task.WaitAll(output, errors);
      process.WaitForExit();
      return process.ExitCode == 0
        ? (output.Result, null, true)
        : (null, $"{interpreter} exited with {process.ExitCode}: {errors.Result.Trim()}", true);
    }
  }

  private static string ReadScript() {
    using var stream = typeof(AstHelper).Assembly.GetManifestResourceStream("Polypiler.Core.Frontends.Python.polypiler_ast_dump.py")!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

}
