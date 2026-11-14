using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Polypiler.Core.IR;

/// <summary>
/// The source text of <see cref="FloatOperations"/>, one file per family, as this assembly carries it.
/// It is written in the subset of C# the C# frontend imports, so the portable implementations can be
/// translated into every target: a backend that lacks a native function emits the one of the source.
/// </summary>
public static class FloatOperationSources {

  private const string Prefix = "Polypiler.Core.IR.Float.Operations.";

  /// <summary>Every file as its name and its text, in the order of the names.</summary>
  public static IReadOnlyList<(string Name, string Text)> All { get; } = [.. Read()];

  private static IEnumerable<(string Name, string Text)> Read() {
    var assembly = typeof(FloatOperationSources).Assembly;
    var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
    foreach (var resource in names) {
      using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
      yield return (resource[Prefix.Length..], reader.ReadToEnd());
    }
  }

}
