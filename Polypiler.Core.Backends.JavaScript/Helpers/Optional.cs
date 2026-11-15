using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helpers of optional values: the value of one that traps when it holds none, as the IR's
/// <c>MissingValue</c> - the generated <c>InvalidOperationError</c> with the trap's message, the class
/// of <c>InvalidOperationException</c> (<see cref="ErrorClass"/>) - and the absent
/// result an exported function spells <c>undefined</c>.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>The names of the helpers of optional values, in their order.</summary>
  private static IEnumerable<string> OptionalOrder => ["$unwrap", "$undef"];

  /// <summary>The text of a helper of optional values, the same at every level; <see langword="null"/> for any other name.</summary>
  private static string? OptionalSource(string name) => name switch {
    "$unwrap" => Lines("function $unwrap(value) {", "  if (value == null) throw new InvalidOperationError(\"missing value\");", "  return value;", "}"),
    "$undef" => Lines("function $undef(value) {", "  return value == null ? void 0 : value;", "}"),
    _ => null
  };

}
