using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Toolchains;

namespace Polypiler.Tests.Tooling.Toolchains;

/// <summary>What the toolchain tests share: locks written as text, a scripted process runner and a scratch folder.</summary>
internal static class ToolchainTesting {

  /// <summary>The folder of the toolchain fixtures.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "toolchains");

  /// <summary>The repository lock.</summary>
  public static string RepositoryLock { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tooling", "toolchains.lock");

  /// <summary>The path of a fixture.</summary>
  public static string Fixture(string name) => Path.Combine(FixtureFolder, name);

  /// <summary>One entry of a lock, indented as the lock writes it.</summary>
  public static string Entry(
    string key,
    string version = "1.2.3",
    string probe = "[fake, --version]",
    string pattern = @"^fake (?<version>\S+)$",
    string platforms = "[linux, windows, macos]",
    string extra = ""
  ) => $"""
      {key}:
        version: "{version}"
        probe: {probe}
        versionPattern: '{pattern}'
        platforms: {platforms}
        licence: MIT
        source: https://example.org/{key}

    """ + string.Concat(extra.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"    {line}\n"));

  /// <summary>A lock of the given entries.</summary>
  public static string Lock(params string[] entries) => "schema: 1\ntoolchains:\n" + string.Concat(entries);

  /// <summary>Parses a lock of the given entries.</summary>
  public static ToolchainLock Parse(params string[] entries) => ToolchainLock.Parse(Lock(entries), "test.lock");

  /// <summary>The problems a lock text is refused with, as (line, column, reason).</summary>
  public static IReadOnlyList<(int Line, int Column, string Message)> Problems(string text) {
    try {
      ToolchainLock.Parse(text, "test.lock");
    } catch (ToolchainLockException exception) {
      return [.. exception.Diagnostics.Select(diagnostic => (diagnostic.Line, diagnostic.Column, diagnostic.Message))];
    }

    return [];
  }

}

/// <summary>A process runner that answers from a script and records what it was asked to run.</summary>
internal sealed class ScriptedRunner(Func<IReadOnlyList<string>, ProcessResult> answer) : IProcessRunner {

  /// <summary>A runner that prints one line on standard output and exits with 0.</summary>
  public ScriptedRunner(string output) : this(_ => new(0, output + "\n", "", false)) { }

  /// <summary>Every command line run, in order.</summary>
  public List<IReadOnlyList<string>> Calls { get; } = [];

  /// <summary>The time-out of the last run.</summary>
  public TimeSpan LastTimeout { get; private set; }

  /// <inheritdoc />
  public ProcessResult Run(IReadOnlyList<string> argv, TimeSpan timeout) {
    this.Calls.Add(argv);
    this.LastTimeout = timeout;
    return answer(argv);
  }

}

/// <summary>A folder that is deleted again, with empty files standing in for executables.</summary>
internal sealed class ScratchFolder : IDisposable {

  /// <summary>Creates an empty folder under the temporary path.</summary>
  public ScratchFolder() => Directory.CreateDirectory(this.Path);

  /// <summary>The folder.</summary>
  public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "polypiler-toolchains-" + Guid.NewGuid().ToString("N"));

  /// <summary>Creates an empty file below the folder, and the folders on its way, and returns its full path.</summary>
  public string File(string relative) {
    var path = System.IO.Path.Combine(this.Path, relative);
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    System.IO.File.WriteAllText(path, "");
    return path;
  }

  /// <summary>Creates a folder below the folder and returns its full path.</summary>
  public string Folder(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(this.Path, relative)).FullName;

  /// <inheritdoc />
  public void Dispose() => Directory.Delete(this.Path, true);

}
