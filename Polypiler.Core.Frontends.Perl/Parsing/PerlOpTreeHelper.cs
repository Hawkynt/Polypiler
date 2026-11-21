using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Polypiler.Core.Frontends.Perl.Parsing;

/// <summary>
/// Compiles a Perl source file with the installed 64-bit Perl interpreter using the embedded
/// <c>B::PolypilerDump</c> compiler backend and returns the JSON op-tree dump.
/// </summary>
internal static class PerlOpTreeHelper {

  public const string InterpreterVariable = "POLYPILER_PERL";
  public const string ParserRelativePath = "B/PolypilerDump.pm";

  public static byte[] ParserSource { get; } = ReadResource();

  public static IReadOnlyList<string> Candidates(string? named) {
    if (!string.IsNullOrWhiteSpace(named))
      return [named];
    var list = new List<string>();
    var env = Environment.GetEnvironmentVariable(InterpreterVariable);
    if (!string.IsNullOrWhiteSpace(env))
      list.Add(env);
    list.Add("perl");
    return list;
  }

  public static (byte[]? Dump, string? Error, int Line) Compile(string file, string source, string? interpreter) {
    var failures = new List<string>();
    foreach (var candidate in Candidates(interpreter)) {
      var (dump, error, line, started) = RunWith(candidate, file, source);
      if (started)
        return (dump, error, line);
      failures.Add(error!);
    }
    return (null, $"no Perl interpreter could be started ({string.Join("; ", failures)}); set {InterpreterVariable} to one", 1);
  }

  private static (byte[]? Dump, string? Error, int Line, bool Started) RunWith(string perl, string file, string source) {
    var temp = Directory.CreateTempSubdirectory("polypiler-perl-");
    try {
      var dumpDir = Path.Combine(temp.FullName, "B");
      Directory.CreateDirectory(dumpDir);
      File.WriteAllBytes(Path.Combine(dumpDir, "PolypilerDump.pm"), ParserSource);
      var inputPath = Path.Combine(temp.FullName, "polypiler_input.pl");
      File.WriteAllBytes(inputPath, new UTF8Encoding(false).GetBytes(source));

      var start = new ProcessStartInfo(perl) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = temp.FullName,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
      };
      start.ArgumentList.Add("-I" + temp.FullName);
      start.ArgumentList.Add("-MO=PolypilerDump");
      start.ArgumentList.Add(inputPath);

      Process process;
      try {
        process = Process.Start(start)!;
      } catch (Win32Exception ex) {
        return (null, $"{perl}: {ex.Message}", 1, false);
      }

      using (process) {
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();
        if (process.ExitCode != 0) {
          var err = stderrTask.Result.Trim().Replace(inputPath, file, StringComparison.Ordinal);
          var match = Regex.Match(err, @"\bline ([0-9]+)\b", RegexOptions.CultureInvariant);
          var line = match.Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 1;
          return (null, err.Length > 0 ? err : $"{perl} exited with {process.ExitCode}", line, true);
        }

        return (Encoding.UTF8.GetBytes(stdoutTask.Result), null, 1, true);
      }
    } finally {
      try { temp.Delete(recursive: true); } catch (IOException) { }
    }
  }

  private static byte[] ReadResource() {
    using var stream = typeof(PerlOpTreeHelper).Assembly.GetManifestResourceStream("Polypiler.Core.Frontends.Perl.PolypilerDump.pm")
      ?? throw new InvalidOperationException("the embedded B::PolypilerDump resource is missing");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

}
