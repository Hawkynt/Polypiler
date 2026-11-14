using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Corpus;

/// <summary>
/// The miniature upstream repository of <c>tests/fixtures/infra/corpus/mini-upstream</c>, built with
/// git in a scratch folder: the first commit holds <c>a.txt</c> and <c>LICENSE</c>, the second adds
/// <c>dir/b.txt</c> and any further files given. Shared as source with the acceptance tests.
/// </summary>
internal sealed class MiniUpstream : IDisposable {

  /// <summary>The fixture folder the files are taken from.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "corpus", "mini-upstream");

  /// <summary>The repository name manifests of the tests give the mini-upstream.</summary>
  public const string Name = "test/mini";

  private MiniUpstream(string directory, string first, string commit) {
    this.Directory = directory;
    this.FirstCommit = first;
    this.Commit = commit;
  }

  /// <summary>The repository's working tree.</summary>
  public string Directory { get; }

  /// <summary>The first commit.</summary>
  public string FirstCommit { get; }

  /// <summary>The second commit: the pin of the tests.</summary>
  public string Commit { get; }

  /// <summary>The repository as a <c>file://</c> URL, which <c>git fetch --depth 1</c> accepts.</summary>
  public string Url => new Uri(this.Directory).AbsoluteUri;

  /// <summary>Builds the repository, with further files in its second commit.</summary>
  public static MiniUpstream Create(params (string Path, string Content)[] extra) {
    var directory = Scratch.Create("polypiler-mini-upstream-");
    var git = new GitProcess();
    Git(git, null, "init", "--quiet", directory);
    Copy(directory, "a.txt", "LICENSE");
    Git(git, directory, "add", "--all");
    CommitAll(git, directory, "first");
    var first = Head(git, directory);
    Copy(directory, "dir/b.txt");
    foreach (var (path, content) in extra) {
      var full = Path.Combine(directory, path);
      System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    }

    Git(git, directory, "add", "--all");
    CommitAll(git, directory, "second");
    return new(directory, first, Head(git, directory));
  }

  /// <summary>A manifest declaring the mini-upstream with <c>LICENSE</c> as its licence file, followed by fixtures.</summary>
  /// <param name="fixtures">YAML list items under <c>fixtures:</c>, or the empty string for <c>fixtures: []</c>.</param>
  /// <param name="url">The URL to fetch from; the repository's own by default.</param>
  /// <param name="commit">The pin; the second commit by default.</param>
  public string Manifest(string fixtures = "", string? url = null, string? commit = null) => $"""
    schema: 1
    repositories:
      - name: {Name}
        url: '{url ?? this.Url}'
        commit: {commit ?? this.Commit}
        licence: LGPL-3.0-or-later
        licenceFiles: [LICENSE]
    fixtures:{(fixtures.Length == 0 ? " []" : "\n" + fixtures)}

    """;

  /// <summary>A fixture list item of the mini-upstream.</summary>
  public static string Fixture(string id, string path, string dependencies = "[]", string vectors = "{ kind: embedded, source: inline }") => $"""
      - id: {id}
        repository: {Name}
        path: {path}
        export: main
        dependencies: {dependencies}
        run: [cat, '{"{file}"}']
        vectors: {vectors}
        licence: LGPL-3.0-or-later
        determinism: deterministic
        tolerance: exact
        profiles: []

    """;

  /// <summary>Deletes the repository.</summary>
  public void Dispose() => Scratch.Delete(this.Directory);

  private static void Copy(string directory, params string[] paths) {
    foreach (var path in paths) {
      var target = Path.Combine(directory, path);
      System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(Path.Combine(FixtureFolder, path), target);
    }
  }

  private static void CommitAll(GitProcess git, string directory, string message)
    => Git(git, directory, "-c", "user.name=Polypiler Tests", "-c", "user.email=tests@polypiler.invalid", "-c", "commit.gpgsign=false",
      "commit", "--quiet", "--no-verify", "--message", message);

  private static string Head(GitProcess git, string directory) => Encoding.UTF8.GetString(Git(git, directory, "rev-parse", "HEAD").Output).Trim();

  private static GitResult Git(GitProcess git, string? directory, params string[] arguments) {
    var result = git.Run(directory, arguments);
    Assert.That(result.ExitCode, Is.Zero, $"git {string.Join(' ', arguments)}: {result.Error}");
    return result;
  }

}

/// <summary>Scratch folders under the system's temporary folder, deleted with their read-only files.</summary>
internal static class Scratch {

  /// <summary>Creates an empty folder whose name starts with a prefix.</summary>
  public static string Create(string prefix) {
    var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
  }

  /// <summary>Deletes a folder and everything in it, read-only files included; a folder already gone is left alone.</summary>
  public static void Delete(string path) {
    if (!Directory.Exists(path))
      return;
    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
      File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(path, true);
  }

  /// <summary>Writes a file below a folder, creating its directory and replacing a read-only file.</summary>
  public static string Write(string root, string path, byte[] content) {
    var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    if (File.Exists(full))
      File.SetAttributes(full, FileAttributes.Normal);
    File.WriteAllBytes(full, content);
    return full;
  }

  /// <summary>Writes a text file below a folder, as UTF-8 without a byte-order mark.</summary>
  public static string Write(string root, string path, string content) => Write(root, path, Encoding.UTF8.GetBytes(content));

  /// <summary>The relative paths of every file below a folder, outside <c>.git</c>, with <c>/</c> separators, in ordinal order.</summary>
  public static IReadOnlyList<string> Files(string root)
    => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
      .Where(file => !file.Split('/').Contains(".git"))
      .Order(StringComparer.Ordinal)];

}
