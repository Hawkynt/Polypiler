using System.Collections.Generic;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The rows of the collections of #1328: <c>List&lt;T&gt;</c>, which every profile has, and the helpers
/// that write what it lacks of JavaScript's arrays - <c>pop</c>, and <c>indexOf</c> on floating-point
/// numbers, whose <c>==</c> no method of a list compares with.
/// </summary>
internal static class CSharpCollectionRows {

  /// <summary>Every row of the collections.</summary>
  public static IEnumerable<CSharpCapability> All => [
    CSharpCapability.OfApi("List<T>", CSharpProfile.Net20),
    CSharpCapability.OfApi("Array.prototype.pop of ECMA-262 on a List<T> of values", CSharpProfile.Net20, CSharpEmitter.PopValue),
    CSharpCapability.OfApi("Array.prototype.pop of ECMA-262 on a List<T> of references", CSharpProfile.Net20, CSharpEmitter.PopReference),
    CSharpCapability.OfApi("Array.prototype.includes of ECMA-262 on a List<T> of references", CSharpProfile.Net20, CSharpEmitter.ContainsReference),
    CSharpCapability.OfApi("Array.prototype.indexOf of ECMA-262 on a List<double>", CSharpProfile.Net20, CSharpEmitter.IndexOfStrictDouble),
    CSharpCapability.OfApi("Array.prototype.indexOf of ECMA-262 on a List<float>", CSharpProfile.Net20, CSharpEmitter.IndexOfStrictSingle)
  ];

}
