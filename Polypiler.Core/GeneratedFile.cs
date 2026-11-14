using System;
using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core;

/// <summary>One file a backend produced. Two files are equal when their paths, their texts and their helpers are.</summary>
/// <param name="Path">The path of the file, relative to the output directory.</param>
/// <param name="Content">The complete text of the file.</param>
public sealed record GeneratedFile(string Path, string Content) {

  /// <summary>The code the backend copied into the file beside the translated program; none by default.</summary>
  public IReadOnlyList<EmittedHelper> Helpers { get; init; } = [];

  /// <inheritdoc/>
  public bool Equals(GeneratedFile? other)
    => other is not null && this.Path == other.Path && this.Content == other.Content && this.Helpers.SequenceEqual(other.Helpers);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Path, this.Content, this.Helpers.Count);

}
