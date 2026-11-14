using System.Collections.Generic;
using System.Linq;
using System.Text;
using Polypiler.Core;

namespace Polypiler.Cli;

/// <summary>
/// The exit codes of <c>polypiler</c> with the meaning each stands for (PRD §10.1, ADR 0003): the
/// single source of <c>polypiler help exit-codes</c> and of <c>docs/cli/exit-codes.md</c>.
/// </summary>
public static class ExitCodeTable {

  /// <summary>The path of the generated document, relative to the repository root.</summary>
  public const string DocumentPath = "docs/cli/exit-codes.md";

  /// <summary>
  /// One row per <see cref="ExitCode"/> member in ascending order, then
  /// <see cref="ExitPolicy.Cancelled"/>, which no diagnostic gives: the number, the member's name
  /// (<see langword="null"/> for the cancellation) and its meaning.
  /// </summary>
  public static IReadOnlyList<(int Code, string? Name, string Meaning)> Rows { get; } = [
    ((int)ExitCode.Success, nameof(ExitCode.Success), "success; verification passed if it was requested"),
    ((int)ExitCode.EnvironmentError, nameof(ExitCode.EnvironmentError), "input, output, toolchain or repository data unusable"),
    ((int)ExitCode.UsageError, nameof(ExitCode.UsageError), "the command line is not valid"),
    ((int)ExitCode.UnsupportedConstruct, nameof(ExitCode.UnsupportedConstruct), "a construct is outside the supported subset"),
    ((int)ExitCode.InvalidSource, nameof(ExitCode.InvalidSource), "the source is not valid in its language"),
    ((int)ExitCode.SemanticMismatch, nameof(ExitCode.SemanticMismatch), "generated code disagrees with the reference"),
    ((int)ExitCode.TargetBuildFailure, nameof(ExitCode.TargetBuildFailure), "the target toolchain rejected the generated code"),
    ((int)ExitCode.OracleFailure, nameof(ExitCode.OracleFailure), "the reference could not produce a result"),
    ((int)ExitCode.LicenseBlocker, nameof(ExitCode.LicenseBlocker), "a licence forbids the requested output"),
    ((int)ExitCode.MissingDependency, nameof(ExitCode.MissingDependency), "a native library or model is missing"),
    ((int)ExitCode.BudgetExceeded, nameof(ExitCode.BudgetExceeded), "a time, memory, size or proof budget was exceeded"),
    ((int)ExitCode.InternalError, nameof(ExitCode.InternalError), "internal compiler error"),
    (ExitPolicy.Cancelled, null, "cancelled by the user")
  ];

  /// <summary>The lines of <c>polypiler help exit-codes</c>: <c>&lt;code, right-aligned to 3&gt;  &lt;meaning&gt;</c>.</summary>
  public static IEnumerable<string> HelpLines() => Rows.Select(row => $"{row.Code,3}  {row.Meaning}");

  /// <summary>The text of <see cref="DocumentPath"/>, with line feeds as line breaks.</summary>
  public static string Markdown() {
    var text = new StringBuilder();
    text.Append("# Exit codes\n\n");
    text.Append("Generated from `ExitCodeTable` in `Polypiler.Cli`; `polypiler help exit-codes` prints the same table.\n");
    text.Append("Do not edit it by hand: a test regenerates it and fails when this file differs.\n\n");
    text.Append("The exit code of a run is the one of the first error it reported; an internal compiler error\n");
    text.Append("(`PLY0001`) makes it 70 whatever else was reported, and warnings and infos never change it\n");
    text.Append("([ADR 0003](../adr/0003-diagnostics-and-exit-codes.md)). `spec/diagnostics.yaml` names the exit\n");
    text.Append("code of every diagnostic.\n\n");
    text.Append("| Code | `ExitCode` | Meaning |\n");
    text.Append("| ---: | --- | --- |\n");
    foreach (var (code, name, meaning) in Rows)
      text.Append($"| {code} | {(name is null ? "—" : $"`{name}`")} | {meaning} |\n");

    return text.ToString();
  }

}
