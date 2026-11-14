using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;

namespace Polypiler.Tooling.Corpus;

/// <summary>
/// Checks the workspace against the lock without touching the network: every locked file is re-hashed,
/// every lock entry must name a declared repository at its pin, and every file under
/// <c>corpus/upstream</c> outside a <c>.git</c> folder must be in the lock.
/// </summary>
public static class CorpusVerifier {

  /// <summary>Every problem of the workspace below a repository root; empty when it matches the lock.</summary>
  public static IReadOnlyList<Diagnostic> Verify(string root, CorpusManifest manifest, CorpusLock lockFile) {
    var problems = new List<Diagnostic>();
    foreach (var entry in lockFile.Entries) {
      var repository = manifest.Repository(entry.Repository);
      if (repository is null)
        problems.Add(CorpusDiagnostics.Manifest($"lock entry '{entry.Location}' names undeclared repository '{entry.Repository}'"));
      else if (repository.Commit != entry.Commit)
        problems.Add(CorpusDiagnostics.Manifest($"lock entry '{entry.Location}' is not at the pin {repository.Commit}"));
      else if (VerifyFile(root, entry) is { } mismatch)
        problems.Add(mismatch);
    }

    var upstream = Path.Combine(root, CorpusImporter.UpstreamPath);
    if (Directory.Exists(upstream)) {
      var locked = lockFile.Entries.Select(entry => entry.Location).ToHashSet(StringComparer.Ordinal);
      foreach (var file in Directory.EnumerateFiles(upstream, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(upstream, file).Replace(Path.DirectorySeparatorChar, '/');
        if (!relative.Split('/').Contains(".git") && !locked.Contains(relative))
          problems.Add(CorpusDiagnostics.Manifest($"'{CorpusImporter.UpstreamPath}/{relative}' is on disk but not in the lock"));
      }
    }

    return problems;
  }

  /// <summary>The <c>PLY0023</c> of a locked file whose bytes differ from the lock or that is missing, or <see langword="null"/>.</summary>
  public static Diagnostic? VerifyFile(string root, CorpusLockEntry entry) {
    var path = CorpusImporter.FilePath(root, entry.Repository, entry.Commit, entry.Path);
    byte[] content;
    try {
      content = File.ReadAllBytes(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return CorpusDiagnostics.Mismatch($"corpus file '{entry.Name}' cannot be read, the lock records {entry.Sha256}: {exception.Message}");
    }

    return Compare(entry, CorpusLockEntry.Hash(content), content.LongLength);
  }

  /// <summary>The <c>PLY0023</c> of bytes whose hash or length differs from a lock entry, or <see langword="null"/>.</summary>
  public static Diagnostic? Compare(CorpusLockEntry locked, string sha256, long bytes) {
    if (locked.Sha256 == sha256 && locked.Bytes == bytes)
      return null;

    var message = $"corpus file '{locked.Name}' has SHA-256 {sha256}, the lock records {locked.Sha256}";
    return CorpusDiagnostics.Mismatch(bytes == locked.Bytes ? message : $"{message} ({bytes} bytes, the lock records {locked.Bytes} bytes)");
  }

}
