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
    CSharpCapability.OfApi("Array.prototype.indexOf of ECMA-262 on a List<T> of records", CSharpProfile.Net20, CSharpEmitter.IndexOfReference),
    CSharpCapability.OfApi("Array.prototype.indexOf of ECMA-262 on a List<double>", CSharpProfile.Net20, CSharpEmitter.IndexOfStrictDouble),
    CSharpCapability.OfApi("Array.prototype.indexOf of ECMA-262 on a List<float>", CSharpProfile.Net20, CSharpEmitter.IndexOfStrictSingle),
    CSharpCapability.OfApi("Dictionary<TKey,TValue>", CSharpProfile.Net20),
    CSharpCapability.OfApi("HashSet<T>", CSharpProfile.Net35),
    CSharpCapability.OfApi("Dictionary<TKey,TValue>.TryAdd", CSharpProfile.NetStandard21, CSharpEmitter.TryAdd),
    CSharpCapability.OfApi("Dictionary<TKey,TValue>.TryGetValue as an optional value", CSharpProfile.Net20, CSharpEmitter.FindValue),
    CSharpCapability.OfApi("Dictionary<TKey,TValue>.TryGetValue as a reference or null", CSharpProfile.Net20, CSharpEmitter.FindReference),
    CSharpCapability.OfApi("TryGetValue of the slots as an optional value", CSharpProfile.Net20, CSharpEmitter.FindSlotValue),
    CSharpCapability.OfApi("TryGetValue of the slots as a reference or null", CSharpProfile.Net20, CSharpEmitter.FindSlotReference),
    // Remove and Clear leave an enumerator valid from .NET Core 3.0 on; a profile that can run on .NET Framework takes the slots of a nested class.
    CSharpCapability.OfApi("Dictionary<TKey,TValue> enumeration of .NET Core 3.0", CSharpProfile.NetStandard21),
    // .NET Core 3.0 hashes every NaN and both zeros of a double alike; .NET Framework does not, so a nested comparer stands in.
    CSharpCapability.OfApi("EqualityComparer<double>.Default normalising NaN and -0", CSharpProfile.NetStandard21),
    CSharpCapability.OfApi("OrderedDictionary<TKey,TValue>", CSharpProfile.Net100)
  ];

}
