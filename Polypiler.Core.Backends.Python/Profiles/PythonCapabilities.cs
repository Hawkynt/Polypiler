using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Python;

/// <summary>A construct of Python, the minor version of Python 3 that brought it, and what stands in for it before.</summary>
/// <param name="Name">The construct, as the emitter asks for it.</param>
/// <param name="Since">The minor version of Python 3 that has it.</param>
/// <param name="Replacement">What a profile below that version writes instead, or <see langword="null"/> when there is nothing.</param>
public sealed record PythonCapability(string Name, int Since, string? Replacement = null);

/// <summary>
/// What each Python profile has (the "What's New in Python" documents of 3.8 to 3.14): the emitter
/// asks the table for every construct whose form depends on the version and writes the replacement a
/// profile below the construct's version takes. The table is one value, so a test can hand the
/// emitter another one.
/// </summary>
public sealed class PythonCapabilities {

  private readonly Dictionary<string, PythonCapability> _byName;

  /// <summary>Creates a table.</summary>
  /// <param name="entries">The entries, each name once.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public PythonCapabilities(IEnumerable<PythonCapability> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    this.Entries = [.. entries];
    this._byName = [];
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary>The construct an assignment expression is written with: <c>(x := value)</c> (PEP 572, Python 3.8).</summary>
  public const string AssignmentExpression = ":=";

  /// <summary>A built-in collection as a generic type in an annotation, <c>list[int]</c> (PEP 585, Python 3.9).</summary>
  public const string BuiltinGenerics = "list[int]";

  /// <summary>The <c>match</c> statement (PEP 634, Python 3.10).</summary>
  public const string Match = "match";

  /// <summary><c>int.bit_count()</c>, the number of one bits of the absolute value (Python 3.10).</summary>
  public const string BitCount = "int.bit_count";

  /// <summary>The <c>struct</c> module, which packs a float into its bits and back.</summary>
  public const string Struct = "struct";

  /// <summary>The table of the language as the documentation of each version states it.</summary>
  public static PythonCapabilities Standard { get; } = new([
    new(AssignmentExpression, 8),
    new(Struct, 8),
    new(BuiltinGenerics, 9, "typing.List[int]"),
    new(Match, 10, "if/elif chain"),
    new(BitCount, 10, "the library's PopCount helper")
  ]);

  /// <summary>The entries, in the order given.</summary>
  public IReadOnlyList<PythonCapability> Entries { get; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public PythonCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability '{name}' is not in the table.", nameof(name));

  /// <summary>Whether a profile has a construct.</summary>
  /// <param name="name">The construct.</param>
  /// <param name="profile">The profile.</param>
  /// <returns><see langword="true"/> when the profile's version is the construct's or later.</returns>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public bool Has(string name, PythonProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    return this[name].Since <= profile.Minor;
  }

  /// <summary>The names of the constructs a profile lacks, in table order.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The names.</returns>
  public IEnumerable<string> Missing(PythonProfile profile) => this.Entries.Where(entry => !this.Has(entry.Name, profile)).Select(entry => entry.Name);

}
