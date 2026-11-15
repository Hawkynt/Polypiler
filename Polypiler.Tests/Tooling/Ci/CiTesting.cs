using System;
using System.IO;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Tooling.Ci;

/// <summary>The audit trees of <c>tests/fixtures/infra/ci/core</c>, and a writable copy of one of them.</summary>
internal static class CiTesting {

  /// <summary>The folder of the fixtures.</summary>
  public static string Fixtures { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "ci", "core");

  /// <summary>An audit tree of the fixtures.</summary>
  public static string Tree(string name) => Path.Combine(Fixtures, name);

  /// <summary>Copies a folder with everything in it.</summary>
  public static void CopyFolder(string source, string target) {
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
      var copy = Path.Combine(target, Path.GetRelativePath(source, file));
      Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
      File.Copy(file, copy, true);
    }
  }

}

/// <summary>A copy of an audit tree under the temporary folder, deleted when disposed.</summary>
internal sealed class CiTree : IDisposable {

  /// <summary>Copies the tree <c>audit-ok</c>, or another one of the fixtures.</summary>
  public CiTree(string fixture = "audit-ok") => CiTesting.CopyFolder(CiTesting.Tree(fixture), this.Root);

  /// <summary>The root of the copy.</summary>
  public string Root { get; } = Path.Combine(Path.GetTempPath(), "polypiler-ci-" + Guid.NewGuid().ToString("N"));

  /// <summary>Writes a file of the tree, creating its folder.</summary>
  public CiTree Write(string relative, string text) {
    var path = Path.Combine(this.Root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return this;
  }

  /// <summary>Deletes a file of the tree.</summary>
  public CiTree Delete(string relative) {
    File.Delete(Path.Combine(this.Root, relative));
    return this;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (Directory.Exists(this.Root))
      Directory.Delete(this.Root, true);
  }

}
