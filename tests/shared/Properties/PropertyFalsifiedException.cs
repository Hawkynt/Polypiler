using NUnit.Framework;

namespace Polypiler.Tests.Properties;

/// <summary>
/// A falsified property. It is an NUnit assertion failure, so the test is reported as failed with
/// the PLY0071 message, and it carries the facts of the failure record for tests of the harness.
/// </summary>
internal sealed class PropertyFalsifiedException(string message, PropertyFailure failure) : AssertionException(message) {

  /// <summary>What the failure record holds.</summary>
  public PropertyFailure Failure { get; } = failure;

}

/// <summary>The facts of a falsified property, as written to its failure record.</summary>
/// <param name="Test">The name of the test that stated the property.</param>
/// <param name="Seed">The seed the inputs were made from.</param>
/// <param name="DeclaredSeed">The seed the test declared; differs from <paramref name="Seed"/> when the environment replaced it.</param>
/// <param name="Index">The zero-based index of the first failing input.</param>
/// <param name="Original">The first failing input, formatted.</param>
/// <param name="Shrunk">The minimal failing input shrinking reached, formatted.</param>
/// <param name="ShrinkSteps">How many shrink steps led from the original to the shrunk input.</param>
/// <param name="Exception">The type of the exception the predicate threw on the shrunk input, if it threw.</param>
/// <param name="RecordPath">Where the record was written; null when it could not be.</param>
internal sealed record PropertyFailure(string Test, ulong Seed, ulong DeclaredSeed, int Index, string Original, string Shrunk, int ShrinkSteps, string? Exception, string? RecordPath);
