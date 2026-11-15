using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Corpus;

namespace Polypiler.Tests.Tooling.Licensing;

/// <summary>
/// The synthetic trees of <c>tests/fixtures/infra/license-effect</c>, copied where a test needs them.
/// A package tree gets its <c>polypiler.manifest.json</c> from the real <see cref="ManifestWriter"/>,
/// and the two trees translated from <c>gpl-nested/sub/x.c</c> a copy of that tree of
/// <c>tests/fixtures/infra/licenses</c>. A notice is committed with a leading <c>_</c>, which the copy
/// drops, so that <c>licenses inventory</c> does not take the synthetic notices for the repository's
/// own. Shared with the acceptance tests as source.
/// </summary>
public static class LicenceEffectTrees {

  /// <summary>The folder of the trees.</summary>
  public static string Fixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "license-effect");

  /// <summary>The pinned upstream file of <c>pkg-lgpl</c>.</summary>
  public static (string Repository, string Commit, string Path) PbCompilerArith { get; } = ("Hawkynt/PB-Compiler", "0d73acbea185a2597357d53f7ce9acb44758ba76", "tests/ARITH.BAS");

  /// <summary>The licence each package tree's manifest states.</summary>
  public static IReadOnlyDictionary<string, string> Stated { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
    ["pkg-gpl"] = "GPL-3.0-or-later",
    ["pkg-understated"] = "LGPL-3.0-or-later",
    ["pkg-lgpl"] = "LGPL-3.0-or-later",
    ["pkg-reference-only"] = "GPL-3.0-or-later"
  };

  /// <summary>Copies a tree to a folder, dropping the leading <c>_</c> of a notice's name.</summary>
  /// <param name="tree">The tree's name.</param>
  /// <param name="destination">The folder the tree's files go to.</param>
  public static void Copy(string tree, string destination) => CopyFolder(Path.Combine(Fixtures, tree), destination, true);

  /// <summary>Copies a package tree to a folder and writes its manifest there.</summary>
  /// <param name="tree">A key of <see cref="Stated"/>.</param>
  /// <param name="destination">The package folder.</param>
  public static void Package(string tree, string destination) {
    Copy(tree, destination);
    if (tree is "pkg-gpl" or "pkg-understated")
      CopyFolder(Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "licenses", "gpl-nested"), Path.Combine(destination, "gpl-nested"), false);

    var inputs = tree switch {
      "pkg-lgpl" => [new ManifestInput(PbCompilerArith.Path, PinnedSha256(), new(PbCompilerArith.Repository, PbCompilerArith.Commit, PbCompilerArith.Path))],
      "pkg-reference-only" => [Local(destination, "src/old.c")],
      _ => new[] { Local(destination, "gpl-nested/sub/x.c") }
    };
    Write(destination, Stated[tree], inputs, []);
  }

  /// <summary>Writes a manifest of a translation with inputs, helpers and a stated licence to a folder.</summary>
  /// <param name="folder">The package folder.</param>
  /// <param name="stated">The licence the manifest states.</param>
  /// <param name="inputs">The inputs.</param>
  /// <param name="helpers">The helpers.</param>
  public static void Write(string folder, string stated, IReadOnlyList<ManifestInput> inputs, IReadOnlyList<ManifestHelper> helpers)
    => ManifestWriter.Write(new Manifest {
      Compiler = new("polypiler", "0.1.0", 1),
      Command = ["translate", "x.c", "--from", "c", "--to", "javascript", "--output", "."],
      Inputs = inputs,
      Frontend = new("c", null),
      Target = new("javascript", "es2022, esm", "node", "any"),
      Outputs = [],
      Api = [],
      Intrinsics = [],
      Helpers = helpers,
      NativeDependencies = [],
      Licensing = new(stated, [], []),
      Caveats = [],
      Optimization = new("default", [], []),
      Warnings = [],
      Toolchains = [],
      Commands = new("polypiler translate x.c --from c --to javascript --output .", null)
    }, folder);

  /// <summary>A local input of a package by its path relative to the package, hashed when it exists.</summary>
  /// <param name="folder">The package folder.</param>
  /// <param name="path">The input's path relative to it.</param>
  public static ManifestInput Local(string folder, string path) {
    var file = Path.Combine(folder, path);
    return new(path, ManifestWriter.Sha256(File.Exists(file) ? File.ReadAllBytes(file) : []), null);
  }

  /// <summary>A helper of a licence.</summary>
  /// <param name="symbol">Its symbol.</param>
  /// <param name="licence">Its licence.</param>
  public static ManifestHelper Helper(string symbol, string licence) => new(symbol, licence, "polypiler", 1, 1, []);

  private static string PinnedSha256()
    => CorpusLock.Load(Path.Combine(PrototypeFixture.RepositoryRoot, CorpusLock.RepositoryPath)).Entries
      .Single(entry => entry.Repository == PbCompilerArith.Repository && entry.Commit == PbCompilerArith.Commit && entry.Path == PbCompilerArith.Path).Sha256;

  private static void CopyFolder(string source, string destination, bool notices) {
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(source, file);
      var name = Path.GetFileName(relative);
      if (notices && name.StartsWith("_THIRD", StringComparison.Ordinal))
        relative = Path.Combine(Path.GetDirectoryName(relative)!, name[1..]);
      var target = Path.Combine(destination, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(file, target, true);
    }
  }

}
