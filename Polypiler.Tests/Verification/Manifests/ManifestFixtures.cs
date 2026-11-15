using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Polypiler.Core;
using Polypiler.Core.Verification.Manifests;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Verification.Manifests;

/// <summary>The manifest fixtures of <c>tests/fixtures/infra/manifest/</c>, and manifests made from them.</summary>
internal static class ManifestFixtures {

  /// <summary>The fixture directory, relative to the repository.</summary>
  public const string Folder = "tests/fixtures/infra/manifest";

  /// <summary>SHA-256 of the three bytes <c>abc</c>, FIPS 180-4's published example.</summary>
  public const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

  /// <summary>SHA-256 of the empty message, FIPS 180-4's published example.</summary>
  public const string Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

  /// <summary>The four fixtures that are valid, one per state.</summary>
  public static readonly string[] Valid = ["generated.json", "built.json", "executed.json", "verified.json"];

  /// <summary>The full path of a fixture.</summary>
  public static string PathOf(string name) => Path.Combine(PrototypeFixture.RepositoryRoot, Folder, name);

  /// <summary>The text of a fixture, with LF line ends whatever the checkout made of them.</summary>
  public static string Text(string name) => File.ReadAllText(PathOf(name)).ReplaceLineEndings("\n");

  /// <summary>A fixture as a tree to change.</summary>
  public static JsonObject Tree(string name) => JsonNode.Parse(Text(name))!.AsObject();

  /// <summary>The schema of the repository's <c>spec/schemas</c>.</summary>
  public static ManifestSchema RepositorySchema => ManifestSchema.Load(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas"));

  /// <summary>Reads a tree as a manifest text; the diagnostics as <c>code pointer-and-reason</c> lines.</summary>
  public static (Manifest? Manifest, Diagnostic[] Diagnostics) Parse(JsonNode tree, ManifestSchema? schema = null) => Parse(tree.ToJsonString(), schema);

  /// <summary>Reads a manifest text.</summary>
  public static (Manifest? Manifest, Diagnostic[] Diagnostics) Parse(string text, ManifestSchema? schema = null) {
    var bag = new DiagnosticBag();
    var manifest = ManifestReader.Parse(text, "m.json", bag, schema ?? ManifestSchema.Default);
    return (manifest, [.. bag.Items]);
  }

  /// <summary>The model of a valid fixture.</summary>
  public static Manifest Model(string name) {
    var (manifest, diagnostics) = Parse(Text(name));
    return manifest ?? throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.Message)));
  }

  /// <summary>A small manifest built in code: nothing but what every manifest must have.</summary>
  public static Manifest Minimal => new() {
    Compiler = new("polypiler", "0.1.0", 1),
    Command = ["translate", "a.cs", "--from", "csharp", "--to", "javascript", "--output", "."],
    Inputs = [],
    Frontend = new("csharp", null),
    Target = new("javascript", "es2022, esm", "node", "any"),
    Outputs = [],
    Api = [],
    Intrinsics = [],
    Helpers = [],
    NativeDependencies = [],
    Licensing = new("NOASSERTION", [], []),
    Caveats = [],
    Optimization = new("default", [], []),
    Warnings = [],
    Toolchains = [],
    Commands = new("polypiler translate a.cs --from csharp --to javascript --output .", null)
  };

}
