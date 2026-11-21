using System;

namespace Polypiler.Cli.Options;

/// <summary>How the values of a list option are recorded (ADR 0004, "Recorded command line").</summary>
public enum ListOrder {

  /// <summary>The option is no list.</summary>
  None,

  /// <summary>Duplicates collapse and the order carries no meaning: recorded sorted (ordinal), each value once.</summary>
  Set,

  /// <summary>The order is kept, duplicates too.</summary>
  Sequence

}

/// <summary>What a contributor declares about one of its options.</summary>
/// <param name="Name">The option as the command line writes it, such as <c>--demo-level</c>.</param>
/// <param name="Kind">How it is written and how often it may appear.</param>
/// <param name="Order">For a <see cref="OptionKind.List"/>, <see cref="ListOrder.Set"/> or <see cref="ListOrder.Sequence"/>; otherwise <see cref="ListOrder.None"/>.</param>
/// <param name="Recorded">Whether the canonical command line carries it; <see langword="false"/> for an option that changes only how a run reports.</param>
/// <param name="NormalSpelling">One value as typed to its normal spelling; <see cref="Identity"/> for a value that has only one spelling.</param>
public sealed record ContributedOption(string Name, OptionKind Kind, ListOrder Order, bool Recorded, Func<string, string> NormalSpelling) {

  /// <summary>The normal spelling of a value that has only one: the value itself.</summary>
  public static Func<string, string> Identity { get; } = value => value;

}
