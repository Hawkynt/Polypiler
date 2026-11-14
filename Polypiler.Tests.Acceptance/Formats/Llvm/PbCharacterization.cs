using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Support;
using YamlDotNet.RepresentationModel;

namespace Polypiler.Tests.Acceptance.Formats.Llvm;

/// <summary>
/// One fixture of <c>tests/fixtures/formats/llvm/pb/characterization.yaml</c>: what <c>pbc --emit-llvm</c>
/// did with it and what the LLVM tools said about the module.
/// </summary>
/// <param name="Source"><c>owner/repo@commit:path</c> for a corpus file, else a file beside the characterization.</param>
/// <param name="Outcome"><see cref="Characterization.Emitted"/> or <see cref="Characterization.Declined"/>.</param>
/// <param name="DeclinedBecause">For a declined fixture, text standard error contains.</param>
/// <param name="LlvmAs">For an emitted fixture, whether <c>llvm-as</c> accepts the module: pass or fail.</param>
/// <param name="Verify">For an emitted fixture, whether <c>opt -passes=verify</c> accepts the module: pass or fail.</param>
/// <param name="Sha256">For an emitted fixture, the SHA-256 of the module as printed.</param>
/// <param name="Finding">The finding of the audit that explains a fail.</param>
public sealed record CharacterizationEntry(string Source, string Outcome, string? DeclinedBecause, string? LlvmAs, string? Verify, string? Sha256, string? Finding) {

  /// <summary>The file name of the source, which is how a message names the fixture.</summary>
  public string Name => this.Source[(this.Source.LastIndexOfAny([':', '/']) + 1)..];

  /// <inheritdoc />
  public override string ToString() => this.Name;

}

/// <summary>What one fixture did when it ran: <c>pbc</c>, and for a module the two LLVM tools.</summary>
/// <param name="Pbc">The run of <c>pbc --emit-llvm</c>.</param>
/// <param name="LlvmAs">The run of <c>llvm-as</c> on the module, when there was one.</param>
/// <param name="Verify">The run of <c>opt -passes=verify</c> on the module, when there was one.</param>
public sealed record Observation(CommandResult Pbc, CommandResult? LlvmAs, CommandResult? Verify);

/// <summary>The characterization file and the comparison of a recorded entry with a run.</summary>
/// <param name="Pin">The PB-Compiler commit the entries were recorded at.</param>
/// <param name="Llvm">The LLVM version the entries were recorded with.</param>
/// <param name="Fixtures">The entries.</param>
public sealed record Characterization(string Pin, string Llvm, IReadOnlyList<CharacterizationEntry> Fixtures) {

  /// <summary>The outcome of a fixture <c>pbc</c> printed a module for.</summary>
  public const string Emitted = "emitted";

  /// <summary>The outcome of a fixture <c>pbc</c> refused.</summary>
  public const string Declined = "declined";

  /// <summary>A tool accepted the module.</summary>
  public const string Pass = "pass";

  /// <summary>A tool rejected the module.</summary>
  public const string Fail = "fail";

  /// <summary>The folder of the characterization and of the fixtures that are not corpus files.</summary>
  public static string Folder { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "formats", "llvm", "pb");

  /// <summary>The characterization file of the repository.</summary>
  public static string RepositoryFile { get; } = Path.Combine(Folder, "characterization.yaml");

  /// <summary>The repository's characterization.</summary>
  public static Characterization Repository => Load(RepositoryFile);

  /// <summary>Reads a characterization file.</summary>
  /// <param name="path">The file.</param>
  public static Characterization Load(string path) => Parse(File.ReadAllText(path));

  /// <summary>Reads a characterization.</summary>
  /// <param name="text">The YAML text.</param>
  /// <exception cref="FormatException">The text is no characterization: a required field is missing or a value is not one of its kind.</exception>
  public static Characterization Parse(string text) {
    var stream = new YamlStream();
    stream.Load(new StringReader(text));
    if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
      throw new FormatException("the characterization is not a mapping");

    var fixtures = Field(root, "fixtures") as YamlSequenceNode ?? throw new FormatException("'fixtures' is not a list");
    var entries = fixtures.Children.Select(Entry).ToList();
    return new(Scalar(root, "pin") ?? throw Missing("pin"), Scalar(root, "llvm") ?? throw Missing("llvm"), entries);
  }

  private static CharacterizationEntry Entry(YamlNode node) {
    if (node is not YamlMappingNode map)
      throw new FormatException("a fixture is not a mapping");

    var source = Scalar(map, "source") ?? throw Missing("source");
    var entry = new CharacterizationEntry(source, Scalar(map, "outcome") ?? throw Missing("outcome"), Scalar(map, "declinedBecause"),
      Scalar(map, "llvmAs"), Scalar(map, "verify"), Scalar(map, "sha256"), Scalar(map, "finding"));
    var problem = entry.Outcome switch {
      Emitted when entry.LlvmAs is not (Pass or Fail) || entry.Verify is not (Pass or Fail) => "an emitted fixture needs llvmAs and verify, each pass or fail",
      Emitted when entry.Sha256 is not { Length: 64 } => "an emitted fixture needs the 64 hex digits of sha256",
      Emitted when (entry.LlvmAs == Fail || entry.Verify == Fail) && entry.Finding == null => "a fail needs the finding that explains it",
      Declined when string.IsNullOrEmpty(entry.DeclinedBecause) => "a declined fixture needs declinedBecause",
      Declined when entry.LlvmAs != null || entry.Verify != null || entry.Sha256 != null => "a declined fixture has no module to check",
      Emitted or Declined => null,
      _ => $"outcome '{entry.Outcome}' is neither {Emitted} nor {Declined}"
    };
    return problem == null ? entry : throw new FormatException($"{source}: {problem}");
  }

  private static YamlNode? Field(YamlMappingNode map, string name)
    => map.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;

  private static string? Scalar(YamlMappingNode map, string name) => (Field(map, name) as YamlScalarNode)?.Value;

  private static FormatException Missing(string name) => new($"'{name}' is missing");

  /// <summary>The outcome a run of <c>pbc</c> shows: a module, a refusal, or neither.</summary>
  /// <param name="pbc">The run.</param>
  /// <returns><see cref="Emitted"/> for exit code 0 with output, <see cref="Declined"/> for exit code 1 without, else <see langword="null"/>.</returns>
  public static string? OutcomeOf(CommandResult pbc) => pbc switch {
    { ExitCode: 0, Stdout.Length: > 0 } => Emitted,
    { ExitCode: 1, Stdout.Length: 0 } => Declined,
    _ => null
  };

  /// <summary>The SHA-256 of a module as printed, in lower-case hex.</summary>
  /// <param name="module">The module text.</param>
  public static string Hash(string module) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(module)));

  /// <summary>Compares a recorded entry with a run; every difference names the fixture.</summary>
  /// <param name="entry">The recorded entry.</param>
  /// <param name="observed">The run.</param>
  /// <returns>One message per difference; empty when the run reproduces the entry.</returns>
  public static IReadOnlyList<string> Compare(CharacterizationEntry entry, Observation observed) {
    var actual = OutcomeOf(observed.Pbc);
    if (actual == null)
      return [$"{entry.Name}: recorded outcome '{entry.Outcome}', actual neither: pbc exited {observed.Pbc.ExitCode} with {observed.Pbc.Stdout.Length} characters of output: {observed.Pbc.Stderr}"];
    if (actual != entry.Outcome)
      return [$"{entry.Name}: recorded outcome '{entry.Outcome}', actual '{actual}': {(actual == Declined ? observed.Pbc.Stderr : "pbc printed a module")}"];
    if (actual == Declined)
      return observed.Pbc.Stderr.Contains(entry.DeclinedBecause!, StringComparison.Ordinal)
        ? []
        : [$"{entry.Name}: declined, but stderr does not contain '{entry.DeclinedBecause}': {observed.Pbc.Stderr}"];

    var problems = new List<string>();
    var hash = Hash(observed.Pbc.Stdout);
    if (hash != entry.Sha256)
      problems.Add($"{entry.Name}: the module changed: recorded sha256 {entry.Sha256}, actual {hash}");
    Tool("llvm-as", entry.LlvmAs!, observed.LlvmAs);
    Tool("opt -passes=verify", entry.Verify!, observed.Verify);
    return problems;

    void Tool(string tool, string recorded, CommandResult? run) {
      if (run == null) {
        problems.Add($"{entry.Name}: {tool} did not run");
        return;
      }

      var result = run.ExitCode == 0 ? Pass : Fail;
      if (result != recorded)
        problems.Add($"{entry.Name}: {tool}: recorded {recorded}, actual {result} (exit code {run.ExitCode}): {run.Stderr}");
    }
  }

}
