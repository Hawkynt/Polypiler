using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

namespace Polypiler.Core.BinaryFormats;

/// <summary>How a routine uses a buffer it is given.</summary>
public enum BufferAccess {

  /// <summary>It only reads it.</summary>
  Read,

  /// <summary>It only writes it.</summary>
  Write,

  /// <summary>It reads and writes it.</summary>
  ReadWrite

}

/// <summary>
/// A parameter of an entry that points at a buffer, and how long the buffer is: the value of another
/// parameter, or a constant number of bytes. Exactly one of the two lengths is given.
/// </summary>
/// <param name="Parameter">The 0-based index of the pointer parameter.</param>
/// <param name="LengthParameter">The 0-based index of the parameter that holds the length, or <see langword="null"/>.</param>
/// <param name="LengthBytes">The length in bytes, or <see langword="null"/>.</param>
/// <param name="Access">How the routine uses it.</param>
public sealed record BufferContract(int Parameter, int? LengthParameter, ulong? LengthBytes, BufferAccess Access);

/// <summary>
/// What a caller may rely on at an entry: its convention, the types of its parameters and result, and
/// the buffers it takes. The convention is a name only; resolving it to registers and stack slots is
/// the calling-convention model's work.
/// </summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Address">Its address.</param>
/// <param name="Convention">The calling convention, for example <c>cdecl</c>.</param>
/// <param name="Parameters">The types of the parameters, for example <c>u32</c>.</param>
/// <param name="Result">The type of the result, or <see langword="null"/> when it returns none.</param>
/// <param name="Buffers">The buffer parameters.</param>
public sealed record EntryContract(string Name, ulong Address, string Convention, ImmutableArray<string> Parameters, string? Result, ImmutableArray<BufferContract> Buffers) {

  /// <inheritdoc/>
  public bool Equals(EntryContract? other)
    => other is not null && this.Name == other.Name && this.Address == other.Address && this.Convention == other.Convention
      && this.Parameters.SequenceEqual(other.Parameters) && this.Result == other.Result && this.Buffers.SequenceEqual(other.Buffers);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Name, this.Address, this.Convention, this.Parameters.Length);

}

/// <summary>
/// The <c>abi</c> text of an entry or import, <c>&lt;convention&gt; (&lt;type&gt;, …) -&gt; &lt;type&gt;</c>:
/// for example <c>cdecl (u32) -&gt; u32</c>. An empty list or <c>void</c> means no parameters; a missing
/// arrow or <c>-&gt; void</c> means no result.
/// </summary>
/// <param name="Convention">The calling convention.</param>
/// <param name="Parameters">The parameter types.</param>
/// <param name="Result">The result type, or <see langword="null"/>.</param>
public sealed record AbiSignature(string Convention, ImmutableArray<string> Parameters, string? Result) {

  /// <summary>The pattern of the text, the same as the schema's.</summary>
  public const string Pattern = @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*([A-Za-z_][A-Za-z0-9_*]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_*]*)*)?\s*\)\s*(?:->\s*([A-Za-z_][A-Za-z0-9_*]*)\s*)?$";

  private static readonly Regex _syntax = new(Pattern, RegexOptions.CultureInvariant);

  /// <summary>Reads an <c>abi</c> text.</summary>
  /// <param name="text">The text.</param>
  /// <returns>The signature, or <see langword="null"/> when the text does not fit the pattern.</returns>
  public static AbiSignature? Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var match = _syntax.Match(text);
    if (!match.Success)
      return null;
    var parameters = match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var result = match.Groups[3].Success && match.Groups[3].Value != "void" ? match.Groups[3].Value : null;
    return new(match.Groups[1].Value, parameters is ["void"] ? [] : [.. parameters], result);
  }

  /// <inheritdoc/>
  public bool Equals(AbiSignature? other)
    => other is not null && this.Convention == other.Convention && this.Parameters.SequenceEqual(other.Parameters) && this.Result == other.Result;

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(this.Convention, this.Parameters.Length, this.Result);

}
