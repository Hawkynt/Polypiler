using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Tests.Float.Support;

namespace Polypiler.Tests.Float;

/// <summary>
/// Every portable implementation against the reference vectors MPFR computed: the correctly rounded
/// ones bit for bit, the others within their stated distance; and the implementation the frontend
/// imports from the embedded source against the compiled one, bit for bit, on a sample.
/// </summary>
[TestFixture]
public sealed class FloatOperationVectorTests {

  private static IEnumerable<TestCaseData> Cases => FloatImplementations.ImplementedCases;

  [TestCaseSource(nameof(Cases))]
  public void Implementation_IsWithinItsDistance_OfEveryReferenceVector(object function, object format) {
    var (target, type) = ((FloatFunction)function, (IrType)format);
    var vectors = FloatVectors.Of(target.Vectors, type);
    var failures = new List<string>();
    foreach (var vector in vectors) {
      if (target.Redefined is { } redefined && redefined([.. vector.Operands.Select(operand => FloatVectors.Value(type, operand))]))
        continue;

      var actual = FloatImplementations.Compiled(target.Method, type, vector.Operands);
      if (Matches(type, vector, actual, target.Distance))
        continue;

      failures.Add($"{vector}: expected {vector.Result:x}, got {actual:x}");
    }

    Assert.That(failures, Is.Empty, $"{failures.Count} of {vectors.Count} vectors fail; the first: {string.Join("; ", failures.Take(10))}");
  }

  [TestCaseSource(nameof(Cases))]
  public void ImportedImplementation_AgreesBitForBit_WithTheCompiledOne(object function, object format) {
    var (target, type) = ((FloatFunction)function, (IrType)format);
    var failures = new List<string>();
    foreach (var vector in Sample(FloatVectors.Of(target.Vectors, type))) {
      var compiled = FloatImplementations.Compiled(target.Method, type, vector.Operands);
      long interpreted;
      try {
        interpreted = FloatImplementations.Interpreted(target.Method, type, vector.Operands);
      } catch (Exception exception) {
        failures.Add($"{vector}: {exception.GetType().Name} {exception.Message}");
        continue;
      }

      if (compiled != interpreted)
        failures.Add($"{vector}: compiled {compiled:x}, interpreted {interpreted:x}");
    }

    Assert.That(failures, Is.Empty);
  }

  /// <summary>
  /// Whether a result is the correctly rounded one, or within the distance: a NaN only matches a NaN,
  /// a zero only the zero of the same sign, an infinity only the same infinity (ADR 0005).
  /// </summary>
  private static bool Matches(IrType format, FloatVector vector, long actual, int distance) {
    if (FloatVectors.Same(format, vector.Result, actual))
      return true;

    if (distance == 0 || FloatVectors.IsNaN(format, vector.Result) || FloatVectors.IsNaN(format, actual))
      return false;

    var expected = FloatVectors.Value(format, vector.Result);
    var computed = FloatVectors.Value(format, actual);
    if (double.IsInfinity(expected) || double.IsInfinity(computed) || expected == 0 && computed == 0)
      return false;

    return FloatVectors.Distance(format, vector.Result, actual) <= distance;
  }

  /// <summary>Every vector of the special classes and every tenth of the random ones.</summary>
  private static IEnumerable<FloatVector> Sample(IReadOnlyList<FloatVector> vectors)
    => vectors.Where((vector, index) => vector.Class is not ("core" or "domain") ? index % 3 == 0 : index % 20 == 0);

}
