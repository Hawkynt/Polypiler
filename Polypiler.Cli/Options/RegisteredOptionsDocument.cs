using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Cli.Options;

/// <summary>
/// <c>docs/cli/registered-options.md</c>: the planned register of contributed options and identifiers,
/// the one list of their owners, followed by what <see cref="Program.CreateContributions()"/> holds
/// today. A test regenerates the document and fails when the committed file differs.
/// </summary>
public static class RegisteredOptionsDocument {

  /// <summary>The path of the generated document, relative to the repository root.</summary>
  public const string DocumentPath = "docs/cli/registered-options.md";

  /// <summary>
  /// The planned register: per row the options or identifier, the commands, the owner and the
  /// purpose. Where another issue names a different owner for an option, this table decides; an issue
  /// that adds an option adds its row.
  /// </summary>
  public static IReadOnlyList<(string Options, string Commands, string Owner, string Purpose)> Planned { get; } = [
    ("`--opt`, `--disable-opt`, `--assume`, `--strict`", "every pipeline command", "#179", "optimization level, switches, value facts, strict mode"),
    ("`--assume-unchecked`, `--assume-file`; contract form of `--assume`", "`translate`, `generate`", "#777", "caller contracts, checked or explicitly unchecked"),
    ("`--specialization-budget`", "`translate`, `generate`", "#492", "limits on specialized versions, growth and guards"),
    ("`--manifest`", "`recover`", "#944", "binary manifest describing image, map and entries"),
    ("`--mode`, `--byte-order`", "`recover`", "#947", "profile of a raw blob without a manifest"),
    ("`--dependency-mode`, `--allow-unresolved`", "`translate`, `recover`", "#250", "how external machine dependencies are satisfied"),
    ("`--asm-policy`", "`translate`, `recover`", "#911", "whether inline assembly is recovered, kept or required"),
    ("`--verify-passes`", "`translate`, `recover`", "#140", "run the protected-effect checker after each pass"),
    ("`--allow-untrusted-decoding`", "`recover`", "#946", "accept decoding that left the trusted subset"),
    ("`--abi`, `--import-header`", "`translate`", "#319", "ABI identity and headers for foreign functions"),
    ("`--native-provider`, `--strategy native-adapter`", "`translate`", "#286", "bind to a native inference provider instead of translating it"),
    ("identifier `bitmapping` (`--from` and `--to`)", "`translate`", "#961", "a bit-mapping document as input or output"),
    ("`--cpu`, `--entry`, `--load-address`, `--memory-map`, `--verify-emulator`", "`recover`, `translate`", "#880", "machine-code input and emulator selection"),
    ("`--target-triple`", "`translate`", "#851", "target triple an `llvm` output is compiled, linked and executed for (PRD §10)"),
    ("`--llvm-version`", "`translate`", "#860", "LLVM version an `llvm` input or output is held to (PRD §10: `--llvm-version pinned`)"),
    ("`--type-report`", "`translate`", "#1590", "a JSON report of how statically typed the translation is: places per lattice level and certainty, the static ratio, why each variant stays one")
  ];

  /// <summary>The text of <see cref="DocumentPath"/> for <see cref="Program.CreateContributions()"/>, with line feeds as line breaks.</summary>
  public static string Markdown() => Markdown(Program.CreateContributions());

  /// <summary>The text of <see cref="DocumentPath"/> for a set of contributions, with line feeds as line breaks.</summary>
  /// <param name="contributions">What is registered today.</param>
  /// <returns>The text.</returns>
  public static string Markdown(OptionContributions contributions) {
    var text = new StringBuilder();
    text.Append("# Registered options\n\n");
    text.Append("Generated from `RegisteredOptionsDocument` and `Program.CreateContributions()` in `Polypiler.Cli`.\n");
    text.Append("Do not edit it by hand: a test regenerates it and fails when this file differs.\n\n");
    text.Append("An area adds an option to a command it does not own, or an identifier to `--from` and `--to`,\n");
    text.Append("by registering one contributor in `Program.CreateContributions()`\n");
    text.Append("([ADR 0004](../adr/0004-cli-design.md), \"Adding a command or an option later\").\n\n");
    text.Append("## Planned register\n\n");
    text.Append("The one list of owners: where another issue names a different owner for an option, this table\n");
    text.Append("decides. An issue that adds an option adds its row and states the option's kind, set or sequence,\n");
    text.Append("default, allowed values, whether it is recorded and the normal spelling of its value.\n\n");
    text.Append("| Option or identifier | Commands | Owner | Purpose |\n");
    text.Append("| --- | --- | --- | --- |\n");
    foreach (var (options, commands, owner, purpose) in Planned)
      text.Append($"| {options} | {commands} | {owner} | {purpose} |\n");

    text.Append("\n## Registered options\n\n");
    var registered = contributions.Describe();
    if (registered.Count == 0) {
      text.Append("No option is contributed yet.\n");
    } else {
      text.Append("| Command | Option | Kind | Order | Recorded | Owner |\n");
      text.Append("| --- | --- | --- | --- | --- | --- |\n");
      foreach (var (command, option, owner) in registered)
        text.Append($"| `{command}` | `{option.Name}` | {option.Kind} | {(option.Kind == OptionKind.List ? option.Order.ToString() : "—")} | {(option.Recorded ? "yes" : "no")} | {owner} |\n");
    }

    text.Append("\n## Registered identifiers\n\n");
    var identifiers = contributions.Identifiers;
    if (identifiers.Count == 0) {
      text.Append("No identifier is contributed yet.\n");
    } else {
      text.Append("| Identifier | Direction | Owner | Description |\n");
      text.Append("| --- | --- | --- | --- |\n");
      foreach (var (identifier, owner) in identifiers)
        text.Append($"| `{identifier.Name}` | {identifier.Direction} | {owner} | {identifier.Description} |\n");
    }

    return text.ToString();
  }

  /// <summary>The option names of the planned register, each with the owners of the rows that name it.</summary>
  /// <returns>Every name written as <c>`--name</c> in a row of options, with its owners in row order.</returns>
  public static IReadOnlyDictionary<string, IReadOnlyList<string>> PlannedOwners()
    => Planned.Where(row => !row.Options.StartsWith("identifier", StringComparison.Ordinal)).SelectMany(row => row.Options.Split('`').Where((_, index) => index % 2 == 1).Select(code => (Name: code.Split(' ')[0], row.Owner)))
      .Where(entry => entry.Name.StartsWith("--", StringComparison.Ordinal))
      .GroupBy(entry => entry.Name, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(entry => entry.Owner)], StringComparer.Ordinal);

}
