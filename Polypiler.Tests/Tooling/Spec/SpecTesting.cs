using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Core;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Spec;

namespace Polypiler.Tests.Tooling.Spec;

/// <summary>
/// A throw-away repository for the spec tests: a directory under the temporary folder with a
/// <c>.git</c> marker, the demo family's schema at <c>spec/schemas/demo.schema.json</c> and a test file
/// at <c>tests/widget.feature</c>; documents are written below <c>spec/</c>.
/// </summary>
public sealed class SpecRepository : IDisposable {

  /// <summary>The test path every generated document names.</summary>
  public const string TestPath = "tests/widget.feature";

  /// <summary>Creates the repository.</summary>
  public SpecRepository() {
    this.Root = Path.Combine(Path.GetTempPath(), "polypiler-spec-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this.Root, ".git"));
    this.Write("spec/schemas/demo.schema.json", File.ReadAllText(Path.Combine(PrototypeFixture.RepositoryRoot, "spec", "schemas", "demo.schema.json")));
    this.Write(TestPath, "Feature: widget\n");
  }

  /// <summary>The repository root.</summary>
  public string Root { get; }

  /// <summary>The spec root, <c>spec</c> in the repository.</summary>
  public string SpecRoot => Path.Combine(this.Root, "spec");

  /// <summary>Writes a file relative to the repository root.</summary>
  public SpecRepository Write(string path, string text) {
    var full = Path.Combine(this.Root, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, text);
    return this;
  }

  /// <summary>Writes a document relative to the spec root.</summary>
  public SpecRepository Document(string path, string text) => this.Write("spec/" + path, text);

  /// <summary>Loads the spec tree of the repository.</summary>
  public SpecTree Tree() => SpecTree.Load(this.SpecRoot, this.Root);

  /// <summary>Validates the tree with every family of the tool.</summary>
  public SpecValidation Validate(string? family = null) => new SpecValidator(SpecFamilies.All).Run(this.Tree(), family);

  /// <summary>The diagnostics of <see cref="Validate"/> as text lines.</summary>
  public string[] Lines(string? family = null) => [.. this.Validate(family).Diagnostics.Select(DiagnosticRenderer.ToText)];

  /// <inheritdoc />
  public void Dispose() {
    if (Directory.Exists(this.Root))
      Directory.Delete(this.Root, true);
  }

}

/// <summary>Spec documents for the tests, built from a valid normative demo document with keys replaced or removed.</summary>
public static class SpecText {

  /// <summary>The keys of the valid document, in order, with their values as YAML.</summary>
  public static IReadOnlyList<(string Key, string Value)> ValidKeys { get; } = [
    ("id", "DEMO.widget"),
    ("title", "Widget"),
    ("version", "2"),
    ("status", "normative"),
    ("owner", "Hawkynt"),
    ("issue", "SPEC-META"),
    ("tests", $"[{SpecRepository.TestPath}]"),
    ("introducedIn", "P0"),
    ("compatibility", "additive"),
    ("reviewedRevision", "2"),
    ("normative", "A widget MUST name each of its parts once."),
    ("spec", "\n  parts:\n    - name: gear\n    - name: spring"),
  ];

  /// <summary>The valid document with some keys given other values (<see langword="null"/> removes a key); a key the document lacks is inserted before <c>spec</c>.</summary>
  public static string Document(params (string Key, string? Value)[] changes) {
    var keys = ValidKeys.Select(pair => (pair.Key, Value: (string?)pair.Value)).ToList();
    foreach (var (key, value) in changes) {
      var index = keys.FindIndex(pair => pair.Key == key);
      if (index < 0)
        keys.Insert(keys.Count - 1, (key, value));
      else
        keys[index] = (key, value);
    }

    return string.Concat(keys.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}: {pair.Value}\n"));
  }

}
