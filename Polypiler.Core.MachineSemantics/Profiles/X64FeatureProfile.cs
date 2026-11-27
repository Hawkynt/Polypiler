using System.Collections.Immutable;

namespace Polypiler.Core.MachineSemantics;

/// <summary>The cumulative CPU and operating-system requirements of an x86-64 psABI level.</summary>
public sealed class X64FeatureProfile {

  private static readonly X64FeatureProfile _v1 = new("x86-64-v1",
    ["cmov", "cx8", "fpu", "fxsr", "mmx", "osfxsr", "sce", "sse", "sse2"], 0);

  private static readonly X64FeatureProfile _v2 = new("x86-64-v2", _v1.RequiredFeatures.AddRange(
    "cx16", "lahf-sahf", "popcnt", "sse3", "sse4.1", "sse4.2", "ssse3"), 0);

  private static readonly X64FeatureProfile _v3 = new("x86-64-v3", _v2.RequiredFeatures.AddRange(
    "avx", "avx2", "bmi1", "bmi2", "f16c", "fma", "lzcnt", "movbe", "osxsave"), 0x06);

  private static readonly X64FeatureProfile _v4 = new("x86-64-v4", _v3.RequiredFeatures.AddRange(
    "avx512f", "avx512bw", "avx512cd", "avx512dq", "avx512vl"), 0xE6);

  private X64FeatureProfile(string id, ImmutableArray<string> requiredFeatures, ulong requiredXcr0Bits) {
    this.Id = id;
    this.RequiredFeatures = requiredFeatures;
    this.RequiredXcr0Bits = requiredXcr0Bits;
  }

  /// <summary>The canonical profile name, from <c>x86-64-v1</c> through <c>x86-64-v4</c>.</summary>
  public string Id { get; }

  /// <summary>The required feature names, including operating-system enables and inherited requirements.</summary>
  public ImmutableArray<string> RequiredFeatures { get; }

  /// <summary>The XCR0 bits the operating system must enable in addition to the named features.</summary>
  public ulong RequiredXcr0Bits { get; }

  /// <summary>Finds a level by its exact name. <c>x86-64</c> is the baseline alias.</summary>
  /// <param name="id">The case-sensitive name.</param>
  /// <returns>The profile, or <see langword="null"/> for an absent or unknown name.</returns>
  public static X64FeatureProfile? Find(string? id) => id switch {
    "x86-64" or "x86-64-v1" => _v1,
    "x86-64-v2" => _v2,
    "x86-64-v3" => _v3,
    "x86-64-v4" => _v4,
    _ => null
  };

}
