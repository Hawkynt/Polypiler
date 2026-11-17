using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Runtime.CompilerServices;

namespace Polypiler.Cli;

/// <summary>
/// What the grammar of ADR 0004 knows about an option beyond what System.CommandLine records: the
/// values an option allows and the pairs of options a command declares exclusive. The foundation
/// checks both itself, before the command runs, and reports them with its own messages.
/// </summary>
public static class OptionRules {

  private static readonly ConditionalWeakTable<Option, string[]> _allowed = [];
  private static readonly ConditionalWeakTable<Command, List<(Option First, Option Second)>> _exclusive = [];
  private static readonly ConditionalWeakTable<Option, object> _repeatOnly = [];

  /// <summary>Restricts an option to a set of values; another value is <c>PLY0003</c> naming them in this order.</summary>
  /// <typeparam name="T">The type of the option.</typeparam>
  /// <param name="option">The option; of a list option every item is checked.</param>
  /// <param name="values">The allowed values, matched ordinally.</param>
  /// <returns>The option.</returns>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  /// <exception cref="ArgumentException">No value is given.</exception>
  public static T AcceptOnly<T>(T option, params string[] values) where T : Option {
    ArgumentNullException.ThrowIfNull(option);
    ArgumentNullException.ThrowIfNull(values);
    if (values.Length == 0)
      throw new ArgumentException("an option allows at least one value", nameof(values));

    _allowed.AddOrUpdate(option, [.. values]);
    return option;
  }

  /// <summary>Declares two options of a command exclusive: both on one command line is <c>PLY0011</c>.</summary>
  /// <param name="command">The command both options belong to.</param>
  /// <param name="first">The option named first in the message.</param>
  /// <param name="second">The option named second.</param>
  /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
  public static void Exclusive(Command command, Option first, Option second) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(first);
    ArgumentNullException.ThrowIfNull(second);
    var pairs = _exclusive.GetValue(command, _ => []);
    lock (pairs)
      pairs.Add((first, second));
  }

  /// <summary>
  /// Declares a list option repeat-only (ADR 0004, "Kinds"): one value per occurrence, never split at a
  /// comma, the empty value a value like any other; for an option whose values may hold a comma, such as <c>--assume</c>.
  /// </summary>
  /// <param name="option">The option.</param>
  /// <returns>The option.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="option"/> is <see langword="null"/>.</exception>
  public static Option<string[]> RepeatOnly(Option<string[]> option) {
    ArgumentNullException.ThrowIfNull(option);
    _repeatOnly.AddOrUpdate(option, option);
    return option;
  }

  /// <summary>Whether an option was declared <see cref="RepeatOnly"/>.</summary>
  internal static bool IsRepeatOnly(Option option) => _repeatOnly.TryGetValue(option, out _);

  /// <summary>The values an option allows, or <see langword="null"/> when it takes any.</summary>
  internal static IReadOnlyList<string>? Allowed(Option option) => _allowed.TryGetValue(option, out var values) ? values : null;

  /// <summary>The exclusive pairs of a command, in the order they were declared.</summary>
  internal static IReadOnlyList<(Option First, Option Second)> ExclusivePairs(Command command) {
    if (!_exclusive.TryGetValue(command, out var pairs))
      return [];

    lock (pairs)
      return [.. pairs];
  }

}
