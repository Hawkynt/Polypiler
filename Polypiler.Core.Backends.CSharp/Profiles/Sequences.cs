using System;
using System.Collections.Generic;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the lazy sequences of #1327: iterator methods with <c>yield return</c> and
/// <c>yield break</c>, which C# has had since version 2, and <c>IEnumerable&lt;T&gt;</c>, which every
/// framework of the table has had since .NET Framework 2.0 - no profile lacks either, so no construct
/// of a sequence is ever inexpressible. A single-pass sequence is the helper <c>SinglePass</c>.
/// </summary>
internal static class CSharpSequenceRows {

  /// <summary>The name of the row of iterator methods.</summary>
  public const string Iterator = "iterator (yield)";

  /// <summary>The name of the row of <c>IEnumerable&lt;T&gt;</c>.</summary>
  public const string Enumerable = "IEnumerable<T>";

  /// <summary>The name of the row of the single-pass sequence.</summary>
  public const string SinglePass = "generator object of ECMA-262 as a single-pass IEnumerable<T>";

  /// <summary>Every row of the sequences.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfLanguage(Iterator, CSharpFeature.Iterator, new Version(2, 0)),
    CSharpCapability.OfApi(Enumerable, CSharpProfile.Net20),
    CSharpCapability.OfApi(SinglePass, CSharpProfile.Net20, CSharpEmitter.SinglePass)
  ];

}
