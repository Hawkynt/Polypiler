using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Polypiler.Tooling.Corpus;

/// <summary>What git printed and how it ended.</summary>
/// <param name="ExitCode">The exit code; -1 when git outlived its time.</param>
/// <param name="Output">Standard output, byte for byte.</param>
/// <param name="Error">Standard error, trimmed.</param>
public sealed record GitResult(int ExitCode, byte[] Output, string Error) {

  /// <summary>Whether git ended with exit code 0.</summary>
  public bool Succeeded => this.ExitCode == 0;

}

/// <summary>
/// Runs git as a process: arguments passed as an array, never through a shell; standard output kept
/// as bytes; messages in English (<c>LC_ALL=C</c>); no prompt for credentials; no line-ending
/// conversion (<c>core.autocrlf=false</c>). No git library is linked.
/// </summary>
/// <param name="executable">The git executable, <c>git</c> to find it on <c>PATH</c>.</param>
/// <param name="timeout">How long one git command may run.</param>
public sealed class GitProcess(string executable, TimeSpan timeout) {

  /// <summary>How long one git command may run by default; a shallow fetch of a large repository takes minutes.</summary>
  public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

  /// <summary>Git as found on <c>PATH</c>.</summary>
  public GitProcess()
    : this("git", DefaultTimeout) { }

  /// <summary>The executable git is started as.</summary>
  public string Executable => executable;

  /// <summary>Runs git with arguments, in a directory when one is given (<c>git -C &lt;directory&gt;</c>).</summary>
  /// <exception cref="CorpusException">Git cannot be started (<c>PLY0021</c>).</exception>
  public GitResult Run(string? directory, params string[] arguments) {
    var info = new ProcessStartInfo(executable) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    info.Environment["LC_ALL"] = "C";
    info.Environment["GIT_TERMINAL_PROMPT"] = "0";
    info.ArgumentList.Add("-c");
    info.ArgumentList.Add("core.autocrlf=false");
    if (directory is not null) {
      info.ArgumentList.Add("-C");
      info.ArgumentList.Add(directory);
    }

    foreach (var argument in arguments)
      info.ArgumentList.Add(argument);

    Process process;
    try {
      process = Process.Start(info)!;
    } catch (Win32Exception exception) {
      throw new CorpusException(CorpusDiagnostics.GitMissing(executable, exception.Message));
    }

    using (process) {
      process.StandardInput.Close();
      using var output = new MemoryStream();
      var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
      var error = process.StandardError.ReadToEndAsync();
      if (!process.WaitForExit(timeout)) {
        process.Kill(true);
        process.WaitForExit();
        return new(-1, [], $"git {string.Join(' ', arguments)} did not end within {timeout}");
      }

      process.WaitForExit();
      copy.Wait();
      return new(process.ExitCode, output.ToArray(), error.Result.Trim());
    }
  }

  /// <summary>Whether a directory's repository holds a commit.</summary>
  public bool HasCommit(string directory, string commit) => this.Run(directory, "cat-file", "-e", commit + "^{commit}").Succeeded;

  /// <summary>Fetches one commit, shallowly, into a repository it creates when the directory has none.</summary>
  /// <returns>The failing result, or <see langword="null"/> when the commit was fetched.</returns>
  public GitResult? Fetch(string directory, string url, string commit) {
    if (!Directory.Exists(Path.Combine(directory, ".git"))) {
      var init = this.Run(null, "init", "--quiet", directory);
      if (!init.Succeeded)
        return init;
    }

    var fetch = this.Run(directory, "fetch", "--quiet", "--no-tags", "--depth", "1", url, commit);
    return fetch.Succeeded ? null : fetch;
  }

  /// <summary>The bytes of a file at a commit, exactly as committed.</summary>
  public GitResult ReadFile(string directory, string commit, string path) => this.Run(directory, "cat-file", "blob", $"{commit}:{path}");

  /// <summary>Every file of a commit, in the order git lists them.</summary>
  /// <returns>The paths, or <see langword="null"/> with the failing result.</returns>
  public (IReadOnlyList<string>? Files, GitResult Result) ListFiles(string directory, string commit) {
    var result = this.Run(directory, "ls-tree", "-r", "-z", "--name-only", commit);
    if (!result.Succeeded)
      return (null, result);
    return ([.. Encoding.UTF8.GetString(result.Output).Split('\0').Where(path => path.Length > 0)], result);
  }

}
