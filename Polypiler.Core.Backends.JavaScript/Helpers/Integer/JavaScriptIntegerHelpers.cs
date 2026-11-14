using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of the integer and bit operations, each family in a file of its own, in the notation
/// of the other helpers. A helper of up to 32 bits takes numbers and returns the bits of its result,
/// which the call cuts to the type; one of 64 bits has a body on <c>BigInt</c>s and one on pairs of
/// halves, of which a module gets the one its profile can run.
/// </summary>
internal static partial class JavaScriptHelpers {

  private const string Invalid = "throw new RangeError(\"invalid operation\");";
  private const string Smallest64 = "{ hi: 2147483648, lo: 0 }";
  private const string Largest64 = "{ hi: 2147483647, lo: 4294967295 }";
  private const string AllOnes64 = "{ hi: 4294967295, lo: 4294967295 }";

  /// <summary>Every integer helper with its text for a profile, family by family.</summary>
  private static IEnumerable<(string Name, Func<Func<string, bool>, string> Text)> IntegerHelpers => [
    .. RotateHelpers, .. ReorderingHelpers, .. PowerHelpers, .. WideMultiplyHelpers, .. DivisionHelpers, .. BoundHelpers,
    .. SaturatingHelpers, .. CarryHelpers, .. OverflowHelpers, .. BitFieldHelpers, .. MaskHelpers, .. EndianHelpers
  ];

  /// <summary>The names of the integer helpers, in the order a module lists them, after those of the other helpers.</summary>
  private static IEnumerable<string> IntegerOrder => IntegerHelpers.Select(helper => helper.Name);

  private static readonly Dictionary<string, Func<Func<string, bool>, string>> _integer = IntegerHelpers.ToDictionary(helper => helper.Name, helper => helper.Text);

  /// <summary>The text of an integer helper for the profile, or <see langword="null"/> for a name that is none.</summary>
  private static string? IntegerSource(string name, Func<string, bool> native) => _integer.TryGetValue(name, out var text) ? text(native) : null;

  /// <summary>A helper whose text is the same at every level.</summary>
  private static (string, Func<Func<string, bool>, string>) Same(string name, string text) => (name, _ => text);

  /// <summary>A helper of 64 bits: one text for <c>BigInt</c>s, one for pairs of halves.</summary>
  private static (string, Func<Func<string, bool>, string>) Wide(string name, string big, string halves) => (name, native => native("BigInt") ? big : halves);

}
