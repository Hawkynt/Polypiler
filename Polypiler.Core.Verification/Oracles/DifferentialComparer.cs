using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Polypiler.Core.Verification.Oracles;

/// <summary>The element of a vector that a tolerance rejected.</summary>
/// <param name="Index">The element's index in the output, from 0.</param>
/// <param name="ReferenceHex">The reference element's bits, most significant first.</param>
/// <param name="ActualHex">The actual element's bits, most significant first.</param>
/// <param name="Reason"><c>&lt;distance&gt; exceeds &lt;mode&gt; &lt;bound&gt;</c>, or <c>special values differ under &lt;rule&gt;</c>.</param>
public sealed record ElementMismatch(int Index, string ReferenceHex, string ActualHex, string Reason);

/// <summary>How one vector compared.</summary>
/// <param name="Name">The vector's name.</param>
/// <param name="Passed">Whether the actual output matches the reference under the policy.</param>
/// <param name="FirstDifferingOffset">The first byte at which the two outputs differ, or where the first incomplete element starts; <see langword="null"/> when they are equal byte for byte.</param>
/// <param name="ReferenceHex">The whole reference output.</param>
/// <param name="ActualHex">The whole actual output.</param>
/// <param name="UlpDistance">The largest comparison distance of an element of a floating-point output that passed, or of the element that failed; <see langword="null"/> for a byte output or when no element was a pair of numbers. Saturates at <see cref="long.MaxValue"/>.</param>
public sealed record VectorOutcome(string Name, bool Passed, int? FirstDifferingOffset, string ReferenceHex, string ActualHex, long? UlpDistance) {

  /// <summary>The element a tolerance rejected; <see langword="null"/> unless the vector failed in a tolerance mode on an element (<c>PLY0052</c>).</summary>
  public ElementMismatch? Mismatch { get; init; }

  /// <summary>Whether the two outputs are identical byte for byte.</summary>
  public bool Identical => this.FirstDifferingOffset is null;

}

/// <summary>
/// The comparison of every vector of a fixture, in vector order. A report with no vectors verified
/// nothing: it is never <see cref="Verified"/>.
/// </summary>
/// <param name="Total">The number of vectors.</param>
/// <param name="Passed">The number of vectors that passed.</param>
/// <param name="Outcomes">One outcome per vector compared, in vector order; empty when nothing was compared.</param>
public sealed record ComparisonReport(int Total, int Passed, IReadOnlyList<VectorOutcome> Outcomes) {

  /// <summary>The policy the vectors were compared under; <see langword="null"/> when nothing was compared.</summary>
  public ExactnessPolicy? Policy { get; init; }

  /// <summary>Where the bound of the policy comes from: none, a tolerance document, or the fixture.</summary>
  public BoundKind BoundKind => this.Policy?.BoundKind ?? BoundKind.None;

  /// <summary>Whether there was at least one vector and every vector passed.</summary>
  public bool Verified => this.Total > 0 && this.Passed == this.Total;

  /// <summary>A report of vectors that were not compared because the reference failed: none of them passed.</summary>
  /// <param name="total">The number of vectors.</param>
  public static ComparisonReport NotCompared(int total) => new(total, 0, []);

  /// <summary>The <c>PLY0051</c> or <c>PLY0052</c> of every vector that failed, in vector order.</summary>
  /// <param name="fixture">The fixture's ID, as the messages name it.</param>
  public IReadOnlyList<Diagnostic> Diagnostics(string fixture)
    => [.. this.Outcomes.Where(outcome => !outcome.Passed).Select(outcome => outcome.Mismatch is { } mismatch
      ? Diagnostic.Create(OracleDiagnostics.ToleranceExceeded, null, fixture, outcome.Name, mismatch.Index, mismatch.ReferenceHex, mismatch.ActualHex, mismatch.Reason)
      : Diagnostic.Create(OracleDiagnostics.ExactMismatch, null, fixture, outcome.Name, outcome.ReferenceHex, outcome.ActualHex, outcome.FirstDifferingOffset ?? 0))];

}

/// <summary>
/// Compares the outputs of a candidate with the reference's under an <see cref="ExactnessPolicy"/>,
/// every vector even after one failed, and reports where each one differs.
/// </summary>
public static class DifferentialComparer {

  /// <summary>Compares every vector.</summary>
  /// <param name="vectors">The vectors, in file order.</param>
  /// <param name="reference">The reference output of each vector.</param>
  /// <param name="actual">The candidate's output of each vector.</param>
  /// <param name="policy">How the outputs are compared.</param>
  /// <returns>The report.</returns>
  /// <exception cref="ArgumentException">The three lists differ in length.</exception>
  public static ComparisonReport Compare(IReadOnlyList<TestVector> vectors, IReadOnlyList<byte[]> reference, IReadOnlyList<byte[]> actual, ExactnessPolicy policy) {
    ArgumentNullException.ThrowIfNull(vectors);
    ArgumentNullException.ThrowIfNull(reference);
    ArgumentNullException.ThrowIfNull(actual);
    ArgumentNullException.ThrowIfNull(policy);
    if (reference.Count != vectors.Count || actual.Count != vectors.Count)
      throw new ArgumentException($"{vectors.Count} vectors need as many outputs; the reference has {reference.Count}, the candidate {actual.Count}", nameof(actual));

    var outcomes = new VectorOutcome[vectors.Count];
    for (var index = 0; index < vectors.Count; ++index)
      outcomes[index] = CompareOne(vectors[index].Name, reference[index], actual[index], policy);

    return new(outcomes.Length, outcomes.Count(outcome => outcome.Passed), outcomes) { Policy = policy };
  }

  private static VectorOutcome CompareOne(string name, byte[] reference, byte[] actual, ExactnessPolicy policy) {
    var common = reference.AsSpan().CommonPrefixLength(actual);
    int? offset = common == reference.Length && common == actual.Length ? null : common;
    var outcome = new VectorOutcome(name, offset is null, offset, Hex.Format(reference), Hex.Format(actual), null);
    if (policy.ElementFormat is not { } format)
      return outcome;

    var size = ExactnessPolicy.SizeOf(format);
    if (reference.Length != actual.Length || reference.Length % size != 0)
      return outcome with { Passed = false, FirstDifferingOffset = offset ?? reference.Length / size * size };

    ulong? largest = null;
    for (var element = 0; element < reference.Length / size; ++element) {
      var start = element * size;
      var referenceBits = Read(reference, start, size);
      var actualBits = Read(actual, start, size);
      var verdict = FloatComparison.Compare(format, referenceBits, actualBits, policy);
      if (verdict.Distance is { } distance)
        largest = Math.Max(largest ?? 0, distance);
      if (verdict.Passed)
        continue;

      var failed = outcome with { Passed = false, UlpDistance = Saturate(verdict.Distance) };
      if (policy is not ExactnessPolicy.Tolerance tolerance)
        return failed;

      var reason = verdict.Failure == ElementFailure.SpecialValues
        ? $"special values differ under {ExactnessPolicy.Spelling(policy.SpecialValues)}"
        : $"{verdict.Excess} exceeds {tolerance.Mode} {tolerance.BoundText}";
      return failed with { Mismatch = new(element, Bits(referenceBits, size), Bits(actualBits, size), reason) };
    }

    return outcome with { Passed = true, UlpDistance = Saturate(largest) };
  }

  private static ulong Read(byte[] bytes, int start, int size)
    => size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start, 4)) : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(start, 8));

  private static string Bits(ulong bits, int size) => bits.ToString(size == 4 ? "x8" : "x16", CultureInfo.InvariantCulture);

  private static long? Saturate(ulong? distance) => distance is { } value ? (long)Math.Min(value, long.MaxValue) : null;

}
