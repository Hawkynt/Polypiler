// SPDX-License-Identifier: MIT
using System;

namespace Polypiler.Core.Backends.JavaScript;

internal static partial class JavaScriptHelpers {

  /// <summary>A helper whose text is the same on every level.</summary>
  private static (string, Func<Func<string, bool>, string>) Same(string name, string text) => (name, _ => text);

  /// <summary>A helper with one text for 64-bit integers as <c>BigInt</c>s and one for them as pairs of halves.</summary>
  private static (string, Func<Func<string, bool>, string>) Wide(string name, string big, string halves) => (name, native => native("BigInt") ? big : halves);

}
