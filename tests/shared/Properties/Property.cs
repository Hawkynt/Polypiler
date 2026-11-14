using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FsCheck;

namespace Polypiler.Tests.Properties;

/// <summary>
/// States a property over generated inputs. Every property names its seed — there is no default, so
/// a property without one does not compile — and a failure is shrunk to a minimal input, recorded
/// under <c>artifacts/properties/</c> and reported as PLY0071 with the seed that replays it.
/// </summary>
internal static class Property {

  /// <summary>
  /// Checks <paramref name="predicate"/> on the first <paramref name="count"/> inputs that
  /// <paramref name="inputs"/> makes from the seed; a failing input is shrunk greedily with
  /// <paramref name="shrink"/>. A predicate that throws counts as falsified.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
  /// <exception cref="PropertyFalsifiedException">An input falsified the property.</exception>
  public static void ForAll<T>(ulong seed, int count, Func<ulong, IEnumerable<T>> inputs, Func<T, IEnumerable<T>> shrink, Func<T, bool> predicate, [CallerMemberName] string test = "")
    => PropertyRunner.Default.ForAll(seed, count, inputs, shrink, predicate, test);

  /// <summary>
  /// Checks <paramref name="predicate"/> on <paramref name="count"/> values of an FsCheck
  /// <paramref name="arbitrary"/>, generated from the seed and shrunk with the arbitrary's own shrinker.
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
  /// <exception cref="PropertyFalsifiedException">An input falsified the property.</exception>
  public static void ForAll<T>(ulong seed, int count, Arbitrary<T> arbitrary, Func<T, bool> predicate, [CallerMemberName] string test = "")
    => PropertyRunner.Default.ForAll(seed, count, arbitrary, predicate, test);

}
