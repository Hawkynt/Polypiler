using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Tooling.Corpus;

/// <summary>
/// Materializes the pinned upstream files under <c>corpus/upstream/&lt;owner&gt;/&lt;repo&gt;@&lt;commit&gt;/</c>:
/// each repository's commit comes from a local checkout given as a source, or else is fetched shallowly
/// into <c>.git</c> of that folder; only the files the fixtures name and the repository's licence files
/// are written, byte for byte and read-only. Every file is read, and compared with the lock unless the
/// lock is being rewritten, before the first one is written; files are written under a temporary name
/// and renamed only when all of them were written, so a failed import leaves no partial file and never
/// touches the lock.
/// </summary>
public sealed class CorpusImporter {

  /// <summary>The workspace folder below the repository root.</summary>
  public const string UpstreamPath = "corpus/upstream";

  private const string _temporarySuffix = ".corpus-import";

  private readonly GitProcess _git;
  private readonly string _root;
  private readonly IReadOnlyList<string> _sources;
  private readonly Action<string, byte[]> _writeFile;

  /// <summary>Creates an importer for a repository root.</summary>
  /// <param name="git">Runs git.</param>
  /// <param name="root">The repository root: <c>corpus/upstream</c> and the lock are below it.</param>
  /// <param name="sources">Local checkouts searched for a pinned commit before it is fetched.</param>
  /// <param name="writeFile">Writes a file; <see cref="File.WriteAllBytes(string, byte[])"/> unless a test replaces it.</param>
  public CorpusImporter(GitProcess git, string root, IReadOnlyList<string> sources, Action<string, byte[]>? writeFile = null) {
    this._git = git;
    this._root = root;
    this._sources = sources;
    this._writeFile = writeFile ?? File.WriteAllBytes;
  }

  /// <summary>The folder imported files of a repository go to.</summary>
  public static string Folder(string root, CorpusRepository repository)
    => Path.Combine(root, UpstreamPath, repository.Name.Replace('/', Path.DirectorySeparatorChar) + "@" + repository.Commit);

  /// <summary>The local path of a file of a repository at its pin.</summary>
  public static string FilePath(string root, string repository, string commit, string path)
    => Path.Combine(root, UpstreamPath, repository.Replace('/', Path.DirectorySeparatorChar) + "@" + commit, path.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>
  /// The directory of a git repository holding the pinned commit: the first source that holds it, or
  /// else the workspace folder, after a shallow fetch unless an earlier one left the commit there.
  /// </summary>
  /// <exception cref="CorpusException">The commit cannot be fetched (<c>PLY0025</c>) or git cannot be started (<c>PLY0021</c>).</exception>
  public string Locate(CorpusRepository repository) {
    foreach (var source in this._sources.Where(source => this._git.HasCommit(source, repository.Commit)))
      return source;

    var folder = Folder(this._root, repository);
    if (Directory.Exists(Path.Combine(folder, ".git")) && this._git.HasCommit(folder, repository.Commit))
      return folder;

    if (this._git.Fetch(folder, repository.Url, repository.Commit) is { } failure)
      throw new CorpusException(CorpusDiagnostics.Path($"{repository.Name} at {repository.Commit} cannot be fetched from {repository.Url}", failure.Error));
    return folder;
  }

  /// <summary>Imports the fixtures and the licence files of their repositories.</summary>
  /// <param name="manifest">The manifest.</param>
  /// <param name="lockFile">The lock the files are compared with.</param>
  /// <param name="fixtureIds">The fixtures to import; empty for all, and then every repository's licence files.</param>
  /// <param name="updateLock">Whether to record the files in a new lock instead of comparing them with this one.</param>
  /// <returns>The lock after the import: <paramref name="lockFile"/>, or the rewritten lock.</returns>
  /// <exception cref="CorpusException">Anything kept the import from completing; nothing was written then.</exception>
  public CorpusImportResult Import(CorpusManifest manifest, CorpusLock lockFile, IReadOnlyList<string> fixtureIds, bool updateLock) {
    var unknown = fixtureIds.Where(id => manifest.Fixture(id) is null).Select(id => CorpusDiagnostics.Manifest($"no fixture '{id}' in {manifest.File}")).ToList();
    if (unknown.Count > 0)
      throw new CorpusException(unknown);

    var fixtures = fixtureIds.Count == 0 ? manifest.Fixtures : [.. manifest.Fixtures.Where(fixture => fixtureIds.Contains(fixture.Id))];
    var repositories = fixtureIds.Count == 0
      ? manifest.Repositories
      : [.. manifest.Repositories.Where(repository => fixtures.Any(fixture => fixture.Repository == repository.Name))];

    var files = new List<(CorpusRepository Repository, string Path, byte[] Content)>();
    var problems = new List<Diagnostic>();
    foreach (var repository in repositories) {
      var directory = this.Locate(repository);
      var paths = repository.LicenceFiles
        .Concat(fixtures.Where(fixture => fixture.Repository == repository.Name).SelectMany(fixture => fixture.Files))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal);
      foreach (var path in paths) {
        var result = this._git.ReadFile(directory, repository.Commit, path);
        if (result.Succeeded)
          files.Add((repository, path, result.Output));
        else
          problems.Add(CorpusDiagnostics.Path($"'{path}' does not exist in {repository.Name} at {repository.Commit}", result.Error));
      }
    }

    if (problems.Count > 0)
      throw new CorpusException(problems);

    var entries = files.Select(file => CorpusLockEntry.For(file.Repository, file.Path, file.Content)).ToList();
    if (!updateLock) {
      foreach (var entry in entries) {
        if (lockFile.Find(entry.Repository, entry.Commit, entry.Path) is not { } locked)
          problems.Add(CorpusDiagnostics.Manifest($"'{entry.Location}' is not in the lock; import it with --update-lock"));
        else if (CorpusVerifier.Compare(locked, entry.Sha256, entry.Bytes) is { } mismatch)
          problems.Add(mismatch);
      }

      if (problems.Count > 0)
        throw new CorpusException(problems);
    }

    this.Write([.. files.Select(file => (FilePath(this._root, file.Repository.Name, file.Repository.Commit, file.Path), file.Content))]);
    if (!updateLock)
      return new(lockFile, files.Count);

    var kept = fixtureIds.Count == 0 ? [] : lockFile.Entries.Where(entry => entries.All(imported => imported.Location != entry.Location));
    var updated = new CorpusLock(kept.Concat(entries));
    this.Write([(Path.Combine(this._root, CorpusLock.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)), System.Text.Encoding.UTF8.GetBytes(updated.ToJson()))], readOnly: false);
    return new(updated, files.Count);
  }

  /// <summary>Writes every file under a temporary name, then renames them all; on a failure deletes what it wrote.</summary>
  private void Write(IReadOnlyList<(string Path, byte[] Content)> files, bool readOnly = true) {
    var written = new List<(string Temporary, string Target)>();
    try {
      foreach (var (path, content) in files) {
        var temporary = path + _temporarySuffix;
        written.Add((temporary, path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        this._writeFile(temporary, content);
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      foreach (var (temporary, _) in written)
        File.Delete(temporary);
      throw new CorpusException(CorpusDiagnostics.Path($"corpus path '{written[^1].Target}' cannot be written", exception.Message));
    }

    foreach (var (temporary, target) in written) {
      if (readOnly)
        File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.ReadOnly);
      if (File.Exists(target))
        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
      File.Move(temporary, target, true);
    }
  }

}

/// <summary>What an import did.</summary>
/// <param name="Lock">The lock after the import.</param>
/// <param name="Files">How many files were written.</param>
public sealed record CorpusImportResult(CorpusLock Lock, int Files);
