using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Optimize;
using Polypiler.Core.Options;

namespace Polypiler.Cli;

/// <summary>
/// The validated pipeline options of one command line (ADR 0004): the level of <c>--opt</c>, the
/// optimizations <c>--disable-opt</c> switches off, the facts of <c>--assume</c> in command-line
/// order and whether <c>--strict</c> is set. Every command that runs the pipeline receives one value
/// of this record from <see cref="GlobalOptionSet.Read"/>.
/// </summary>
/// <param name="Opt">The optimization level.</param>
/// <param name="DisabledOptimizations">The IDs of the optimizations that do not run.</param>
/// <param name="Assumptions">The facts the user states, in the order they were typed.</param>
/// <param name="Strict">Whether strict mode refuses everything unproven.</param>
public sealed record GlobalOptions(OptimizationLevel Opt, IReadOnlySet<string> DisabledOptimizations, IReadOnlyList<Assumption> Assumptions, bool Strict) {

  /// <summary>The options of a command line that gives none of them: <c>--opt balanced</c> and nothing else.</summary>
  public static GlobalOptions Default { get; } = new(OptimizationLevel.Balanced, new HashSet<string>(), [], false);

  /// <summary>
  /// The pipeline options in their canonical form, one argument per item: <c>--opt &lt;level&gt;</c>,
  /// written out also for the default; <c>--disable-opt</c> with the IDs ascending and comma-joined if
  /// any; one <c>--assume &lt;text&gt;</c> per fact in ordinal order of its normalized text; <c>--strict</c>
  /// if set. Two equivalent command lines give the same arguments (PRD §23.3).
  /// </summary>
  /// <returns>The arguments, the fact texts unquoted.</returns>
  public IReadOnlyList<string> CanonicalArguments() {
    List<string> arguments = [GlobalOptionSet.Opt, GlobalOptionSet.LevelName(this.Opt)];
    if (this.DisabledOptimizations.Count > 0)
      arguments.AddRange([GlobalOptionSet.DisableOpt, string.Join(',', this.DisabledOptimizations.Order(StringComparer.Ordinal))]);

    foreach (var text in this.Assumptions.Select(assumption => assumption.Text).Order(StringComparer.Ordinal))
      arguments.AddRange([GlobalOptionSet.Assume, text]);

    if (this.Strict)
      arguments.Add(GlobalOptionSet.Strict);

    return arguments;
  }

  /// <summary>
  /// The <see cref="CanonicalArguments"/> as one line, the place the canonical command line of ADR 0004
  /// records them: after the command's own options, before the contributed ones. Every fact is written
  /// in double quotes, as <c>--assume "Sum.n in [1, 31]"</c>.
  /// </summary>
  /// <returns>The line.</returns>
  public string ToCanonicalArguments() {
    var arguments = this.CanonicalArguments();
    return string.Join(' ', arguments.Select((argument, index) => index > 0 && arguments[index - 1] == GlobalOptionSet.Assume ? $"\"{argument}\"" : argument));
  }

}
