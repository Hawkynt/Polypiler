// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// The helper of the IR's dynamic type, a pair <c>[tag, payload]</c>: the payload of a tag, which
/// throws the <c>InvalidCastError</c> (a <c>TypeError</c>) where the value has another tag, as the
/// IR's <c>InvalidCast</c> trap.
/// </summary>
internal static partial class JavaScriptHelpers {

  /// <summary>The names of the helpers of the dynamic type, in their order.</summary>
  private static IEnumerable<string> VariantOrder => ["$variant"];

  /// <summary>The text of a helper of the dynamic type, the same at every level; <see langword="null"/> for any other name.</summary>
  private static string? VariantSource(string name) => name == "$variant"
    ? Lines("function $variant(value, tag) {", "  if (value[0] !== tag) throw new InvalidCastError(\"invalid cast\");", "  return value[1];", "}")
    : null;

}
