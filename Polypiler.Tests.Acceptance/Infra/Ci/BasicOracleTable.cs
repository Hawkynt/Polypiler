using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Acceptance.Infra.Ci;

/// <summary>Where an oracle of the BASIC/PB layer runs.</summary>
public enum OracleAvailability {

  /// <summary>It runs in the job <c>layer-basic</c>, its toolchain probed at its pin.</summary>
  Ci,

  /// <summary>It exists but cannot run on a hosted runner; only recorded evidence files that name their origin count.</summary>
  External,

  /// <summary>No such oracle is available.</summary>
  None

}

/// <summary>One row of <c>tooling/ci/layers/basic.oracles.yaml</c>.</summary>
/// <param name="Oracle">The oracle's name.</param>
/// <param name="Toolchain">Its key in <c>tooling/toolchains.lock</c>; <see langword="null"/> for an oracle the lock cannot install.</param>
/// <param name="Availability">Where it runs.</param>
public sealed record BasicOracle(string Oracle, string? Toolchain, OracleAvailability Availability) {

  /// <summary>Whether a test checked by this oracle can pass: only an oracle that runs in the job can; the others are reported only.</summary>
  public bool CanPass => this.Availability == OracleAvailability.Ci;

  /// <summary>The availability as the file and the table spell it.</summary>
  public string AvailabilityText => BasicOracleTable.Spell(this.Availability);

}

/// <summary>
/// The oracle availability table of the BASIC/PB layer (PRD §7.3): read from
/// <c>tooling/ci/layers/basic.oracles.yaml</c>, rendered as the Markdown table the job prints into its
/// summary, and compared with <c>tests/fixtures/infra/ci/basic/oracles.expected.txt</c>.
/// </summary>
public static class BasicOracleTable {

  /// <summary>The oracle file, relative to the repository root.</summary>
  public const string RepositoryPath = "tooling/ci/layers/basic.oracles.yaml";

  /// <summary>The expected rendering, relative to the repository root.</summary>
  public const string ExpectedPath = "tests/fixtures/infra/ci/basic/oracles.expected.txt";

  private const string Header = "| Oracle | Toolchain | Availability |\n| --- | --- | --- |\n";

  private static readonly Dictionary<string, OracleAvailability> _availabilities = new(StringComparer.Ordinal) {
    ["ci"] = OracleAvailability.Ci,
    ["external"] = OracleAvailability.External,
    ["none"] = OracleAvailability.None
  };

  /// <summary>The spelling of an availability in the file and the table.</summary>
  /// <param name="availability">The availability.</param>
  public static string Spell(OracleAvailability availability) => _availabilities.Single(pair => pair.Value == availability).Key;

  /// <summary>Reads the text of an oracle file.</summary>
  /// <param name="text">The YAML text.</param>
  /// <returns>The rows in file order.</returns>
  /// <exception cref="FormatException">The text is not YAML, has no <c>oracles</c> list, or a row lacks its oracle or has an unknown availability.</exception>
  public static IReadOnlyList<BasicOracle> Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var stream = new YamlStream();
    try {
      stream.Load(new StringReader(text));
    } catch (YamlException exception) {
      throw new FormatException($"{RepositoryPath}: not valid YAML: {exception.Message}", exception);
    }

    if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root || !root.Children.TryGetValue(new YamlScalarNode("oracles"), out var node) || node is not YamlSequenceNode rows)
      throw new FormatException($"{RepositoryPath}: no 'oracles' list");

    return [.. rows.Select((row, index) => Row(row, index + 1))];
  }

  /// <summary>Reads the oracle file of a repository.</summary>
  /// <param name="root">The repository root.</param>
  public static IReadOnlyList<BasicOracle> Load(string root) => Parse(File.ReadAllText(Path.Combine(root, RepositoryPath)));

  /// <summary>The table as Markdown, one row per oracle in file order, <c>-</c> for an oracle without a toolchain; lines end in LF.</summary>
  /// <param name="oracles">The rows.</param>
  public static string Render(IEnumerable<BasicOracle> oracles) {
    var text = new StringBuilder(Header);
    foreach (var oracle in oracles)
      text.Append("| ").Append(oracle.Oracle).Append(" | ").Append(oracle.Toolchain ?? "-").Append(" | ").Append(oracle.AvailabilityText).Append(" |\n");
    return text.ToString();
  }

  /// <summary>The lines in which a rendering differs from the expectation, each naming the line, the expected row and the actual one; empty when they agree.</summary>
  /// <param name="actual">The rendering of the file.</param>
  /// <param name="expected">The expectation; CR LF counts as LF.</param>
  public static IReadOnlyList<string> Differences(string actual, string expected)
    => BasicUpstream.LineDifferences(BasicUpstream.Lines(actual), BasicUpstream.Lines(expected));

  private static BasicOracle Row(YamlNode node, int number) {
    if (node is not YamlMappingNode row)
      throw new FormatException($"{RepositoryPath}: row {number} is not a mapping");

    string? Field(string name) => row.Children.TryGetValue(new YamlScalarNode(name), out var value) && value is YamlScalarNode { Value: { Length: > 0 } text } ? text : null;

    var oracle = Field("oracle") ?? throw new FormatException($"{RepositoryPath}: row {number} has no oracle");
    var availability = Field("availability");
    if (availability is null || !_availabilities.TryGetValue(availability, out var parsed))
      throw new FormatException($"{RepositoryPath}: oracle '{oracle}' has the availability '{availability}', not one of {string.Join(", ", _availabilities.Keys)}");

    return new(oracle, Field("toolchain"), parsed);
  }

}
