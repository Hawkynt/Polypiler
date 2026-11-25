using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Polypiler.Core;

/// <summary>One file a backend produced. Two files are equal when their paths, their payloads and their helpers are.</summary>
/// <param name="Path">The path of the file, relative to the output directory.</param>
/// <param name="Content">The complete text of the file, or empty when <see cref="BinaryContent"/> carries the payload.</param>
public sealed record GeneratedFile(string Path, string Content) {

  private static readonly UTF8Encoding _utf8 = new(false);

  /// <summary>Optional raw binary payload for compiled artefacts such as .NET assemblies, JVM class files, or JAR archives.</summary>
  public byte[]? BinaryContent { get; init; }

  /// <summary>The code the backend copied into the file beside the translated program; none by default.</summary>
  public IReadOnlyList<EmittedHelper> Helpers { get; init; } = [];

  /// <summary>Returns the bytes to write to disk: <see cref="BinaryContent"/> when present, otherwise UTF-8 encoded <see cref="Content"/>.</summary>
  public byte[] GetBytes() => this.BinaryContent ?? _utf8.GetBytes(this.Content);

  /// <inheritdoc/>
  public bool Equals(GeneratedFile? other)
    => other is not null
      && this.Path == other.Path
      && this.Content == other.Content
      && (this.BinaryContent is null ? other.BinaryContent is null : other.BinaryContent is not null && this.BinaryContent.AsSpan().SequenceEqual(other.BinaryContent))
      && this.Helpers.SequenceEqual(other.Helpers);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Path, this.Content, this.BinaryContent?.Length ?? -1, this.Helpers.Count);

}
