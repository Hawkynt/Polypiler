using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// A construct the emitted Kotlin may use, the language version and the JVM release that have it, and
/// what stands in for it before.
/// </summary>
/// <param name="Name">The construct, as the emitter asks for it.</param>
/// <param name="KotlinMinor">The minor version of Kotlin 2 that has it.</param>
/// <param name="Jvm">The JVM release whose class library has it; 8 for what every target has.</param>
/// <param name="Replacement">What a profile without it writes instead.</param>
public sealed record KotlinCapability(string Name, int KotlinMinor, int Jvm, string Replacement);

/// <summary>
/// What each Kotlin profile has, from the Kotlin language documentation ("What's new in Kotlin 2.x")
/// and the Java SE API specification of each release: the emitter asks the table for every construct
/// whose form depends on the profile and writes the replacement where the profile lacks it. Every row
/// in use here is a method of <c>java.lang.Math</c> or <c>java.lang.Integer</c>/<c>Long</c> that a
/// later JVM brought; the language versions 2.0 to 2.4 differ in nothing the emitter writes. The table
/// is one value, so that a test can hand the emitter another one.
/// </summary>
public sealed class KotlinCapabilities {

  /// <summary><c>Math.fma</c>, the fused multiply-add rounded once (Java 9).</summary>
  public const string Fma = "Math.fma";

  /// <summary><c>Math.multiplyHigh</c>, the high 64 bits of the signed 128-bit product (Java 9).</summary>
  public const string MultiplyHigh = "Math.multiplyHigh";

  /// <summary><c>Math.unsignedMultiplyHigh</c>, the high 64 bits of the unsigned 128-bit product (Java 18).</summary>
  public const string UnsignedMultiplyHigh = "Math.unsignedMultiplyHigh";

  /// <summary><c>Integer.compress</c>/<c>expand</c> and the <c>Long</c> pair, the parallel bit extract and deposit (Java 19).</summary>
  public const string CompressExpand = "Integer.compress";

  private readonly Dictionary<string, KotlinCapability> _byName;

  /// <summary>Creates a table.</summary>
  /// <param name="entries">The entries, each name once.</param>
  /// <exception cref="ArgumentException">A name is listed twice.</exception>
  public KotlinCapabilities(IEnumerable<KotlinCapability> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    this.Entries = [.. entries];
    this._byName = [];
    foreach (var entry in this.Entries)
      if (!this._byName.TryAdd(entry.Name, entry))
        throw new ArgumentException($"The capability '{entry.Name}' is listed twice.", nameof(entries));
  }

  /// <summary>The table of the language and the class library as their documentation states them.</summary>
  public static KotlinCapabilities Standard { get; } = new([
    new(Fma, 0, 9, "the portable implementation FusedMultiplyAdd of the IR library"),
    new(MultiplyHigh, 0, 9, "the helper MulHighSigned64 of the IR library"),
    new(UnsignedMultiplyHigh, 0, 18, "the helper MulHighUnsigned64 of the IR library"),
    new(CompressExpand, 0, 19, "the helpers ParallelBitExtract and ParallelBitDeposit of the IR library")
  ]);

  /// <summary>The entries, in the order given.</summary>
  public IReadOnlyList<KotlinCapability> Entries { get; }

  /// <summary>The entry of a name.</summary>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public KotlinCapability this[string name]
    => this._byName.TryGetValue(name, out var entry) ? entry : throw new ArgumentException($"The capability '{name}' is not in the table.", nameof(name));

  /// <summary>Whether a profile has a construct.</summary>
  /// <param name="name">The construct.</param>
  /// <param name="profile">The profile.</param>
  /// <returns><see langword="true"/> when both the language version and the JVM target of the profile reach the construct's.</returns>
  /// <exception cref="ArgumentException">The table has no such entry.</exception>
  public bool Has(string name, KotlinProfile profile) {
    ArgumentNullException.ThrowIfNull(profile);
    var entry = this[name];
    return entry.KotlinMinor <= profile.Minor && entry.Jvm <= profile.Jvm;
  }

  /// <summary>The names of the constructs a profile lacks, in table order.</summary>
  /// <param name="profile">The profile.</param>
  /// <returns>The names.</returns>
  public IEnumerable<string> Missing(KotlinProfile profile) => this.Entries.Where(entry => !this.Has(entry.Name, profile)).Select(entry => entry.Name);

}
