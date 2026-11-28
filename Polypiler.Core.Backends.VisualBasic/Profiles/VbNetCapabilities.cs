using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.VisualBasic;

/// <summary>
/// A construct of Visual Basic .NET, the first language version and the first framework that have it,
/// and what a profile without it writes instead.
/// </summary>
/// <param name="Name">The construct, as the emitter asks for it.</param>
/// <param name="Language">The first language version that has it, or <see langword="null"/> where every version has it.</param>
/// <param name="Framework">The first framework that has it, or <see langword="null"/> where every framework has it.</param>
/// <param name="Replacement">What a profile without it writes instead, or <see langword="null"/> where the construct is refused.</param>
public sealed record VbNetCapability(string Name, Version? Language, VisualBasicFramework? Framework, string? Replacement = null);

/// <summary>
/// What each language version and target framework of Visual Basic .NET has, from the language's
/// "What's new for Visual Basic" history and the framework API reference: the emitter asks the table
/// for every construct whose form depends on the profile. The table is one value, so a test can hand
/// the emitter another one.
/// </summary>
public sealed class VbNetCapabilities {

  private readonly Dictionary<string, VbNetCapability> _byName;

  /// <summary>Creates a table.</summary>
  /// <param name="entries">The entries, each name once.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public VbNetCapabilities(IEnumerable<VbNetCapability> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    this.Entries = [.. entries];
    this._byName = new(StringComparer.Ordinal);
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary>
  /// A statement continued on the next line after a comma or an operator without <c> _</c>: Visual
  /// Basic 10. Below, the long initializer of a table ends each of its lines with <c> _</c>.
  /// </summary>
  public const string ImplicitLineContinuation = "ImplicitLineContinuation";

  /// <summary>
  /// <c>BitConverter.SingleToInt32Bits</c> and <c>Int32BitsToSingle</c>: .NET Standard 2.1. Below,
  /// the bits go through <c>BitConverter.GetBytes</c>.
  /// </summary>
  public const string SingleBits = "SingleBits";

  /// <summary>
  /// <c>Math.Min</c> and <c>Math.Max</c> that order -0 before +0, as IEEE 754-2019 and the IR do:
  /// .NET Core 3.0. Below, the helpers <c>PlyMin</c> and <c>PlyMax</c>.
  /// </summary>
  public const string SignedZeroMinMax = "SignedZeroMinMax";

  /// <summary>
  /// <c>Double.ToString</c> giving the shortest text that reads back as the same number: .NET Core
  /// 3.0. Below, the text of a floating-point number is refused.
  /// </summary>
  public const string ShortestFloatText = "ShortestFloatText";

  /// <summary>
  /// <c>String.Trim</c>, <c>TrimStart</c> and <c>TrimEnd</c> without arguments removing what
  /// <c>Char.IsWhiteSpace</c> accepts: .NET Framework 4. Below, a trim of .NET's white space is refused,
  /// as those frameworks kept a list of their own.
  /// </summary>
  public const string WhiteSpaceTrim = "WhiteSpaceTrim";

  /// <summary>The table of the language versions and frameworks as their references state them.</summary>
  public static VbNetCapabilities Standard { get; } = new([
    new(ImplicitLineContinuation, new Version(10, 0), null, "an explicit ' _' continuation"),
    new(SingleBits, null, VisualBasicFramework.NetStandard21, "BitConverter.GetBytes"),
    new(SignedZeroMinMax, null, VisualBasicFramework.NetCoreApp31, "PlyMin, PlyMax"),
    new(ShortestFloatText, null, VisualBasicFramework.NetCoreApp31),
    new(WhiteSpaceTrim, null, VisualBasicFramework.Net46)
  ]);

  /// <summary>The entries, in the order given.</summary>
  public IReadOnlyList<VbNetCapability> Entries { get; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public VbNetCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability '{name}' is not in the table.", nameof(name));

  /// <summary>Whether a profile has a construct: its language version and its framework are both at least the entry's.</summary>
  /// <param name="name">The construct.</param>
  /// <param name="profile">A Visual Basic .NET profile.</param>
  /// <returns>Whether the profile has it.</returns>
  /// <exception cref="ArgumentException">The table has no such entry, or the profile is a classic one.</exception>
  public bool Has(string name, VisualBasicProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    if (!profile.IsNet)
      throw new ArgumentException($"The profile '{profile.Name}' is no Visual Basic .NET profile.", nameof(profile));

    var entry = this[name];
    return (entry.Language is null || profile.Version >= entry.Language) && (entry.Framework is null || profile.Framework!.Rank >= entry.Framework.Rank);
  }

  /// <summary>The names of the constructs a profile lacks, in table order.</summary>
  /// <param name="profile">A Visual Basic .NET profile.</param>
  /// <returns>The names.</returns>
  public IEnumerable<string> Missing(VisualBasicProfile profile) => this.Entries.Where(entry => !this.Has(entry.Name, profile)).Select(entry => entry.Name);

}
