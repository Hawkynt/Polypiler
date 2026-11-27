using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Go;

/// <summary>A construct of Go, the minor version of Go 1 that brought it, and what stands in for it before.</summary>
/// <param name="Name">The construct, as the emitter asks for it.</param>
/// <param name="Since">The minor version of Go 1 that has it.</param>
/// <param name="Replacement">What a profile below that version writes instead, or <see langword="null"/> when there is nothing.</param>
public sealed record GoCapability(string Name, int Since, string? Replacement = null);

/// <summary>
/// What each Go profile has (the Go 1.18 to 1.27 release notes): the emitter asks the table for every
/// construct whose form depends on the version and writes the replacement a profile below the
/// construct's version takes. The table is one value, so a test can hand the emitter another one.
/// </summary>
public sealed class GoCapabilities {

  private readonly Dictionary<string, GoCapability> _byName;

  /// <summary>Creates a table.</summary>
  /// <param name="entries">The entries, each name once.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public GoCapabilities(IEnumerable<GoCapability> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    this.Entries = [.. entries];
    this._byName = [];
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary>Type parameters and the constraint <c>comparable</c> (Go 1.18): the oldest profile has them.</summary>
  public const string Generics = "generics";

  /// <summary>The built-in functions <c>min</c> and <c>max</c> (Go 1.21).</summary>
  public const string MinMax = "min/max";

  /// <summary>The package <c>slices</c> of the standard library, <c>slices.Equal</c> among it (Go 1.21).</summary>
  public const string Slices = "slices";

  /// <summary>The built-in function <c>clear</c> of a slice or map (Go 1.21).</summary>
  public const string Clear = "clear";

  /// <summary><c>for i := range n</c> over an integer (Go 1.22).</summary>
  public const string RangeOverInt = "range-over-int";

  /// <summary>A loop variable of a three-clause <c>for</c> is a new variable in every pass (Go 1.22).</summary>
  public const string PerIterationLoopVariables = "per-iteration-loop-variables";

  /// <summary><c>for x := range f</c> over an iterator function (Go 1.23).</summary>
  public const string RangeOverFunc = "range-over-func";

  /// <summary>The table of the language as the release notes of each version state it.</summary>
  public static GoCapabilities Standard { get; } = new([
    new(Generics, 18),
    new(MinMax, 21, "a helper per type"),
    new(Slices, 21, "a loop"),
    new(Clear, 21, "a loop"),
    new(RangeOverInt, 22, "a three-clause for statement"),
    new(PerIterationLoopVariables, 22, "a copy of the loop variable in the body"),
    new(RangeOverFunc, 23, "a callback")
  ]);

  /// <summary>The entries, in the order given.</summary>
  public IReadOnlyList<GoCapability> Entries { get; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public GoCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability '{name}' is not in the table.", nameof(name));

  /// <summary>Whether a profile has a construct.</summary>
  /// <param name="name">The construct.</param>
  /// <param name="profile">The profile.</param>
  /// <returns><see langword="true"/> when the profile's version is the construct's or later.</returns>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public bool Has(string name, GoProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    return this[name].Since <= profile.Minor;
  }

  /// <summary>The names of the constructs a profile lacks, in table order.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The names.</returns>
  public IEnumerable<string> Missing(GoProfile profile) => this.Entries.Where(entry => !this.Has(entry.Name, profile)).Select(entry => entry.Name);

}
