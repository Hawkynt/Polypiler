using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using Polypiler.Core.BinaryFormats;
using Polypiler.Core.MachineSemantics;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.BinaryFormats;

/// <summary>The fixtures of <c>tests/fixtures/native/mach-bin-manifest</c> and a scratch directory to vary them in.</summary>
internal sealed class BinaryTesting : IDisposable {

  /// <summary>The fixture directory.</summary>
  public static readonly string Fixtures = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-bin-manifest");

  /// <summary>The bytes of <c>blob.bin</c>.</summary>
  public static readonly ImmutableArray<byte> Blob = [0x8B, 0x44, 0x24, 0x04, 0x0F, 0xC8, 0xC3, 0x90];

  /// <summary>The SHA-256 of <c>blob.bin</c> as <c>sha256sum</c> printed it.</summary>
  public const string BlobSha256 = "614da4cf773be2621d0d57b413ad234f305482cb7738411487cb6e8fb071f498";

  /// <summary>The machine profiles of the repository and of the MachineIR fixtures, which hold <c>x86-32</c>.</summary>
  public static readonly MachineProfileCatalog Catalog = new([
    Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "machine"),
    Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "native", "mach-ir", "profiles")
  ]);

  /// <summary>Creates an empty scratch directory.</summary>
  public BinaryTesting() {
    this.Directory = Path.Combine(Path.GetTempPath(), "polypiler-binary-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(this.Directory);
  }

  /// <summary>The scratch directory.</summary>
  public string Directory { get; }

  /// <summary>A fixture's path.</summary>
  public static string Fixture(string name) => Path.Combine(Fixtures, name);

  /// <summary>A fixture manifest as a JSON tree to vary.</summary>
  public static JsonObject Json(string name) => JsonNode.Parse(File.ReadAllText(Fixture(name)))!.AsObject();

  /// <summary>Writes a file into the scratch directory.</summary>
  public string Write(string name, ReadOnlySpan<byte> bytes) {
    var path = Path.Combine(this.Directory, name);
    File.WriteAllBytes(path, bytes.ToArray());
    return path;
  }

  /// <summary>Writes a text file into the scratch directory.</summary>
  public string Write(string name, string text) {
    var path = Path.Combine(this.Directory, name);
    File.WriteAllText(path, text);
    return path;
  }

  /// <summary>Writes <c>blob.bin</c> and a manifest into the scratch directory and reads the manifest.</summary>
  public BinaryManifest Manifest(JsonObject json, string name = "manifest.json") {
    this.Write("blob.bin", Blob.AsSpan());
    var result = BinaryManifest.Load(this.Write(name, json.ToJsonString()));
    return result.Succeeded ? result.Value : throw new InvalidOperationException(string.Join("; ", result.Problems));
  }

  /// <summary>The manifest of a JSON text, or its first problem.</summary>
  public static BinaryResult<BinaryManifest> Parse(JsonObject json) => BinaryManifest.Parse(json.ToJsonString(), "test.json", Fixtures);

  /// <inheritdoc/>
  public void Dispose() => System.IO.Directory.Delete(this.Directory, recursive: true);

}
