using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Cli;

/// <summary>
/// The canonical command line a manifest stores as <c>requestedConfig</c> (ADR 0004, "Recorded command
/// line"): the command name, the operands as typed with <c>\</c> as <c>/</c>, the command's own options
/// ordered by name, then the pipeline options in their fixed order. Every option is written
/// <c>--name value</c>, a flag when set, a list once with its items sorted, duplicates removed and
/// joined by commas; each argument is quoted by the ADR's rule. Two invocations that request the same
/// thing give the same string.
/// </summary>
public static class RequestedConfig {

  /// <summary>The pipeline options, in the order the ADR fixes for them.</summary>
  public static IReadOnlyList<string> PipelineOrder { get; } = ["--opt", "--disable-opt", "--assume", "--strict"];

  /// <summary>Builds the canonical command line.</summary>
  /// <param name="command">The command name.</param>
  /// <param name="operands">The operands, in the order given.</param>
  /// <param name="options">
  /// The options to record by name: a value for a single-valued option, the items for a list, and an
  /// empty list for a flag that is set. Options of <see cref="PipelineOrder"/> go after the others.
  /// </param>
  /// <param name="lists">The names of the options that are lists.</param>
  /// <returns>The line, without the executable.</returns>
  public static string Build(string command, IEnumerable<string> operands, IReadOnlyDictionary<string, IReadOnlyList<string>> options, IReadOnlySet<string> lists) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(operands);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(lists);
    List<string> arguments = [command, .. operands.Select(operand => operand.Replace('\\', '/'))];
    var own = options.Keys.Where(name => !PipelineOrder.Contains(name)).Order(StringComparer.Ordinal);
    var pipeline = PipelineOrder.Where(options.ContainsKey);
    foreach (var name in own.Concat(pipeline)) {
      var values = options[name];
      arguments.Add(name);
      if (lists.Contains(name))
        arguments.Add(string.Join(',', values.SelectMany(Items).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
      else if (values.Count > 0)
        arguments.Add(values[0]);
    }

    // The invocation's quoting is the ADR's; it names the executable first, which the record leaves out.
    return CliContext.Invocation(arguments)[(Core.ProductInfo.Name.Length + 1)..];
  }

  /// <summary>
  /// The items of one value of a list: split at the commas outside brackets, so that an instantiation
  /// <c>Method[int,long]</c> stays one item.
  /// </summary>
  /// <param name="value">The value as typed.</param>
  /// <returns>The items.</returns>
  public static IEnumerable<string> Items(string value) {
    ArgumentNullException.ThrowIfNull(value);
    var (depth, start) = (0, 0);
    for (var index = 0; index < value.Length; ++index) {
      depth += value[index] switch { '[' => 1, ']' => -1, _ => 0 };
      if (value[index] != ',' || depth != 0)
        continue;

      yield return value[start..index];
      start = index + 1;
    }

    yield return value[start..];
  }

}
