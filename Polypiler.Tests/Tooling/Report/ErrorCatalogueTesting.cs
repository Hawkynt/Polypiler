using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Polypiler.Prototype.Testing;
using Polypiler.Tooling.Diagnostics;
using Polypiler.Tooling.Report;

namespace Polypiler.Tests.Tooling.Report;

/// <summary>What the error-catalogue tests share: the fixtures, registries written as text and the generated files by name.</summary>
internal static class ErrorCatalogueTesting {

  /// <summary>The folder of the fixtures.</summary>
  public static string FixtureFolder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "infra", "docs-errors");

  /// <summary>The path of a fixture.</summary>
  public static string Fixture(string name) => Path.Combine(FixtureFolder, name.Replace('/', Path.DirectorySeparatorChar));

  /// <summary>The blocks every registry text of the tests carries.</summary>
  public const string Blocks = """
    blocks:
      - { first: PLY0001, last: PLY0099, area: infrastructure }
      - { first: PLY0100, last: PLY0199, area: ir }
      - { first: PLY1000, last: PLY2999, area: languages }
      - { first: PLY1000, last: PLY1099, area: csharp }

    """;

  /// <summary>One code of a registry text; <paramref name="extra"/> adds lines such as <c>strict: true</c>.</summary>
  public static string Entry(string code, string severity = "error", string? exit = "EnvironmentError", string message = "a message", string area = "infrastructure", string extra = "")
    => $"""
        - code: {code}
          severity: {severity}
          title: a title
          message: '{message.Replace("'", "''", StringComparison.Ordinal)}'
          area: {area}
          owner: TEST-OWNER

      """
      + (exit is null ? "" : $"    exit: {exit}\n")
      + string.Concat(extra.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"    {line}\n"));

  /// <summary>A registry text of the given codes.</summary>
  public static string RegistryText(params string[] entries)
    => "schema: 1\n" + Blocks + (entries.Length == 0 ? "codes: []\n" : "codes:\n" + string.Concat(entries));

  /// <summary>Reads a registry text, which must be valid.</summary>
  public static DiagnosticRegistry Registry(params string[] entries) {
    var registry = DiagnosticRegistryValidator.Validate(RegistryText(entries), "test.yaml");
    if (registry.Problems.Count > 0)
      throw new InvalidOperationException(string.Join("\n", registry.Problems));
    return registry;
  }

  /// <summary>The files generated from a registry without notes, by name.</summary>
  public static IReadOnlyDictionary<string, string> Generate(DiagnosticRegistry registry, IReadOnlyDictionary<string, ErrorNote>? notes = null)
    => ErrorCatalogueWriter.Generate(registry, notes ?? new Dictionary<string, ErrorNote>()).ToDictionary(file => file.Path, file => file.Content);

  /// <summary>The files of a directory with their text, by name.</summary>
  public static Dictionary<string, string> Snapshot(string directory)
    => Directory.Exists(directory)
      ? Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllText, StringComparer.Ordinal)
      : [];

  /// <summary>A text with CRLF read as LF, for fixtures a Windows checkout may have converted.</summary>
  public static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

}
