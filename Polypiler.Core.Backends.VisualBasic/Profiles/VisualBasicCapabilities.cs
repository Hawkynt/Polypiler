using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>A construct of classic Visual Basic, the dialects that have it, and what stands in for it in the others.</summary>
/// <param name="Name">The construct, as the emitter asks for it.</param>
/// <param name="Dialects">The dialects that have it.</param>
/// <param name="Replacement">What a dialect without it writes instead, or <see langword="null"/> where the construct is refused.</param>
public sealed record VisualBasicCapability(string Name, IReadOnlyList<string> Dialects, string? Replacement = null);

/// <summary>
/// What each classic Visual Basic dialect has, from the language references of Visual Basic 6.0 and of
/// VBA 7 ("64-bit Visual Basic for Applications overview"): the emitter asks the table for every
/// construct whose form depends on the dialect. The table is one value, so a test can hand the emitter
/// another one.
/// </summary>
public sealed class VisualBasicCapabilities {

  private readonly Dictionary<string, VisualBasicCapability> _byName;

  /// <summary>Creates a table.</summary>
  /// <param name="entries">The entries, each name once.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public VisualBasicCapabilities(IEnumerable<VisualBasicCapability> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    this.Entries = [.. entries];
    this._byName = new(StringComparer.Ordinal);
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary><c>LongLong</c>, the 64-bit integer, with <c>CLngLng</c> and the <c>^</c> type character: VBA 7 on a 64-bit host only.</summary>
  public const string LongLong = "LongLong";

  /// <summary><c>LongPtr</c>, the integer of a pointer's width, and <c>PtrSafe</c>: VBA 7.</summary>
  public const string LongPtr = "LongPtr";

  /// <summary>The table of the four dialects as their references state them.</summary>
  public static VisualBasicCapabilities Standard { get; } = new([
    new(LongLong, ["vba7-64"]),
    new(LongPtr, ["vba7", "vba7-64"], "Long")
  ]);

  /// <summary>The entries, in the order given.</summary>
  public IReadOnlyList<VisualBasicCapability> Entries { get; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public VisualBasicCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability '{name}' is not in the table.", nameof(name));

  /// <summary>Whether a profile has a construct.</summary>
  /// <param name="name">The construct.</param>
  /// <param name="profile">The profile.</param>
  /// <returns>Whether the profile's dialect is listed.</returns>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public bool Has(string name, VisualBasicProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    return this[name].Dialects.Contains(profile.Dialect, StringComparer.Ordinal);
  }

  /// <summary>The names of the constructs a profile lacks, in table order.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The names.</returns>
  public IEnumerable<string> Missing(VisualBasicProfile profile) => this.Entries.Where(entry => !this.Has(entry.Name, profile)).Select(entry => entry.Name);

}
