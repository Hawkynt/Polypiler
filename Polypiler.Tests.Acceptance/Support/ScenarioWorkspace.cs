using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Polypiler.Tests.Acceptance.Support;

/// <summary>
/// The directory of one scenario: <c>polypiler-scenario-&lt;random&gt;</c> under the system's temporary
/// folder, created before the scenario and deleted after it. Relative paths of the steps resolve
/// against it; no two scenarios share one.
/// </summary>
public sealed class ScenarioWorkspace {

  /// <summary>The prefix of every workspace directory, for the check that none is left behind.</summary>
  public const string Prefix = "polypiler-scenario-";

  private static readonly ConcurrentQueue<string> _created = new();

  private ScenarioWorkspace(string path) => this.Path = path;

  /// <summary>Every workspace this process created, for the check at the end of the run that none is left.</summary>
  public static IEnumerable<string> Created => _created;

  /// <summary>The full path of the directory.</summary>
  public string Path { get; }

  /// <summary>Creates a fresh, empty workspace.</summary>
  public static ScenarioWorkspace Create() {
    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    _created.Enqueue(path);
    return new ScenarioWorkspace(path);
  }

  /// <summary>The full path of a path relative to the workspace; an absolute path stays as it is.</summary>
  public string Resolve(string path)
    => System.IO.Path.GetFullPath(System.IO.Path.Combine(this.Path, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

  /// <summary>Writes a file into the workspace, creating its directory, and returns its full path.</summary>
  public string Write(string path, string content) {
    var full = this.Resolve(path);
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
    File.WriteAllText(full, content);
    return full;
  }

  /// <summary>Copies a file into the workspace, creating its directory, and returns its full path.</summary>
  public string Copy(string source, string path) {
    var full = this.Resolve(path);
    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
    File.Copy(source, full, true);
    return full;
  }

  /// <summary>Deletes the directory and everything in it; a workspace already gone is left alone.</summary>
  public void Delete() {
    if (Directory.Exists(this.Path))
      Directory.Delete(this.Path, true);
  }

}
