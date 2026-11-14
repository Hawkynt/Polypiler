using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Support;

/// <summary>
/// Opens a fixture of <c>tooling/corpus/corpus.yaml</c> for a test: the local path of its file under
/// <c>corpus/upstream</c>, after every file the fixture needs has been checked against
/// <c>corpus.lock.json</c>. A file that differs from the lock, is missing or is not locked fails the
/// test with its diagnostic; run <c>polypiler-tooling corpus import</c> to materialize the corpus.
/// </summary>
internal static class CorpusFixture {

  /// <summary>The local path of a fixture's file in the repository's corpus.</summary>
  public static string Open(string id) => Open(id, PrototypeFixture.RepositoryRoot);

  /// <summary>The local path of a fixture's file in the corpus below a root.</summary>
  public static string Open(string id, string root) {
    CorpusManifest manifest;
    CorpusLock lockFile;
    try {
      manifest = CorpusManifest.Load(Path.Combine(root, CorpusManifest.RepositoryPath));
      lockFile = CorpusLock.Load(Path.Combine(root, CorpusLock.RepositoryPath));
    } catch (CorpusException exception) {
      throw Failure(exception.Diagnostics.Select(DiagnosticRenderer.ToText).ToArray());
    }

    var fixture = manifest.Fixture(id) ?? throw Failure($"no fixture '{id}' in {CorpusManifest.RepositoryPath}");
    var repository = manifest.Repository(fixture.Repository)!;
    foreach (var path in fixture.Files) {
      var problem = lockFile.Find(repository.Name, repository.Commit, path) is { } entry
        ? CorpusVerifier.VerifyFile(root, entry)
        : CorpusDiagnostics.Manifest($"'{repository.Folder}/{path}' is not in the lock; import it with --update-lock");
      if (problem is not null)
        throw Failure(DiagnosticRenderer.ToText(problem));
    }

    return CorpusImporter.FilePath(root, repository.Name, repository.Commit, fixture.Path);
  }

  private static AssertionException Failure(params string[] lines)
    => new($"corpus fixture unusable (run 'polypiler-tooling corpus import'):\n{string.Join('\n', lines)}");

}
