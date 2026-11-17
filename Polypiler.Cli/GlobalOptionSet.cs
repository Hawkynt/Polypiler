using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core;
using Polypiler.Core.Optimize;
using Polypiler.Core.Options;

namespace Polypiler.Cli;

/// <summary>
/// The four pipeline options, defined once (ADR 0004, "Options"; <c>docs/cli/pipeline-options.md</c>):
/// <c>--opt none|balanced|speed|size|memory</c> (single, default <c>balanced</c>),
/// <c>--disable-opt O####[,O####]…</c> (a list declared a set), <c>--assume "&lt;fact&gt;"</c>
/// (repeat-only) and <c>--strict</c> (a flag). They are command options: a command that runs the
/// pipeline attaches them with <see cref="AddTo"/> and reads them with <see cref="Read"/>; before the
/// command name, or on a command that did not attach them, they are <c>PLY0003</c>.
/// </summary>
public static partial class GlobalOptionSet {

  /// <summary>The name of the option of the optimization level.</summary>
  public const string Opt = "--opt";

  /// <summary>The name of the option of the optimizations switched off.</summary>
  public const string DisableOpt = "--disable-opt";

  /// <summary>The name of the option of a stated fact.</summary>
  public const string Assume = "--assume";

  /// <summary>The name of the flag of strict mode.</summary>
  public const string Strict = "--strict";

  /// <summary>The four names, in the fixed order of the canonical command line.</summary>
  public static IReadOnlyList<string> Names { get; } = [Opt, DisableOpt, Assume, Strict];

  /// <summary>The optimization levels by their names, in the order of the message of <c>PLY0012</c>.</summary>
  private static readonly (string Name, OptimizationLevel Level)[] _levels = [
    ("none", OptimizationLevel.None), ("balanced", OptimizationLevel.Balanced), ("speed", OptimizationLevel.Speed),
    ("size", OptimizationLevel.Size), ("memory", OptimizationLevel.Memory)
  ];

  [GeneratedRegex("^O[0-9]{4}$", RegexOptions.CultureInvariant)]
  private static partial Regex OptimizationId();

  /// <summary>Attaches the four options to a command.</summary>
  /// <param name="command">The command that runs the pipeline.</param>
  /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
  public static void AddTo(Command command) {
    ArgumentNullException.ThrowIfNull(command);
    command.Options.Add(new Option<string>(Opt) { Description = "the optimization level: none, balanced (the default), speed, size or memory", HelpName = "level" });
    command.Options.Add(new Option<string[]>(DisableOpt) { Description = "optimizations not to apply, by their IDs", HelpName = "O####" });
    command.Options.Add(OptionRules.RepeatOnly(new Option<string[]>(Assume) { Description = "a fact the compiler may use without proof, reported as a warning; once per fact", HelpName = "fact" }));
    command.Options.Add(new Option<bool>(Strict) { Description = "refuse everything unproven: a strict warning becomes an error" });
  }

  /// <summary>The name of a level, as <c>--opt</c> takes it.</summary>
  /// <param name="level">The level.</param>
  /// <returns>The name.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The value is no level.</exception>
  public static string LevelName(OptimizationLevel level)
    => _levels.FirstOrDefault(entry => entry.Level == level).Name ?? throw new ArgumentOutOfRangeException(nameof(level), level, "not an optimization level");

  /// <summary>The level of a name, matched ordinally; <see langword="null"/> for a name that is none.</summary>
  /// <param name="name">The value of <c>--opt</c>.</param>
  /// <returns>The level, or <see langword="null"/>.</returns>
  public static OptimizationLevel? LevelOf(string? name)
    => _levels.Where(entry => entry.Name == name).Select(entry => (OptimizationLevel?)entry.Level).FirstOrDefault();

  /// <summary>
  /// Reads and validates the four options of a parsed command line that attached them, and reports
  /// what is wrong in the order the checks run: the level (<c>PLY0012</c>), every item of
  /// <c>--disable-opt</c> (<c>PLY0008</c>), every <c>--assume</c> (<c>PLY0007</c>). For a valid
  /// command line strict mode is set on the context and every fact is reported as <c>PLY0009</c> once,
  /// in command-line order — under <c>--strict</c> as <c>PLY0010</c>.
  /// </summary>
  /// <param name="parse">The parse of a command line the foundation accepted.</param>
  /// <param name="context">The context the diagnostics are reported to.</param>
  /// <param name="optimizations">The IDs of the optimizations the pipeline has.</param>
  /// <returns>The options, or <see langword="null"/> after an error diagnostic.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static GlobalOptions? Read(System.CommandLine.ParseResult parse, CliContext context, IEnumerable<string> optimizations) {
    ArgumentNullException.ThrowIfNull(parse);
    ArgumentNullException.ThrowIfNull(context);
    ArgumentNullException.ThrowIfNull(optimizations);
    var errors = context.Diagnostics.ErrorCount;
    var levelName = parse.GetValue<string>(Opt) ?? "balanced";
    var level = LevelOf(levelName);
    if (level is null)
      context.Report(Diagnostic.Create(PipelineOptionDiagnostics.UnknownLevel, null, levelName));

    var known = optimizations.ToHashSet(StringComparer.Ordinal);
    var disabled = new HashSet<string>(StringComparer.Ordinal);
    foreach (var id in parse.GetValue<string[]>(DisableOpt) ?? []) {
      if (!OptimizationId().IsMatch(id))
        context.Report(Diagnostic.Create(PipelineOptionDiagnostics.UnknownOptimization, null, string.Format(System.Globalization.CultureInfo.InvariantCulture, PipelineOptionDiagnostics.NotAnId, id)));
      else if (!known.Contains(id))
        context.Report(Diagnostic.Create(PipelineOptionDiagnostics.UnknownOptimization, null, string.Format(System.Globalization.CultureInfo.InvariantCulture, PipelineOptionDiagnostics.UnknownId, id)));
      else
        disabled.Add(id);
    }

    var assumptions = new List<Assumption>();
    foreach (var text in parse.GetValue<string[]>(Assume) ?? [])
      if (Fact(text, out var reason) is { } assumption)
        assumptions.Add(assumption);
      else
        context.Report(Diagnostic.Create(PipelineOptionDiagnostics.InvalidAssumption, null, text, reason!));

    if (context.Diagnostics.ErrorCount > errors)
      return null;

    var strict = parse.GetValue<bool>(Strict);
    context.Strict |= strict;
    foreach (var assumption in assumptions)
      context.Report(Diagnostic.Create(PipelineOptionDiagnostics.TrustedAssumption, null, assumption.Text));

    return context.Diagnostics.ErrorCount > errors ? null : new GlobalOptions(level!.Value, disabled, assumptions, strict);
  }

  /// <summary>
  /// The fact of one <c>--assume</c>. A text with a <c>:</c> is a caller contract, whose parser is
  /// not part of this build; any other is a value fact of <see cref="AssumptionParser"/>.
  /// </summary>
  private static Assumption? Fact(string text, out string? reason) {
    if (text.Contains(':', StringComparison.Ordinal)) {
      reason = PipelineOptionDiagnostics.ContractsUnavailable;
      return null;
    }

    AssumptionParser.TryParse(text, out var assumption, out reason);
    return assumption;
  }

}
