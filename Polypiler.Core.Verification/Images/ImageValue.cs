using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Verification.Images;

/// <summary>
/// A value that travels as an image. The case says what shape the value has; the
/// <see cref="ImageType"/> it is encoded with says its width and spelling. Values compare
/// structurally, floats by their bits.
/// </summary>
public abstract record ImageValue {

  private protected ImageValue() { }

  /// <summary>An integer of any width: the value of a fixed-width integer, an enum, a <c>char16</c> unit or a <c>bigint</c>.</summary>
  /// <param name="Value">The value.</param>
  public sealed record Integer(BigInteger Value) : ImageValue {

    /// <inheritdoc />
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);

  }

  /// <summary>A boolean.</summary>
  /// <param name="Value">The value.</param>
  public sealed record Boolean(bool Value) : ImageValue {

    /// <inheritdoc />
    public override string ToString() => this.Value ? "true" : "false";

  }

  /// <summary>The IEEE 754 bit pattern of an <c>f32</c> (in the low 32 bits) or an <c>f64</c>; NaN payloads and the sign of zero are kept.</summary>
  /// <param name="Bits">The bits.</param>
  public sealed record FloatBits(ulong Bits) : ImageValue {

    /// <summary>The bits of a binary64.</summary>
    /// <param name="value">The value.</param>
    public static FloatBits Of(double value) => new(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>The bits of a binary32.</summary>
    /// <param name="value">The value.</param>
    public static FloatBits Of(float value) => new(BitConverter.SingleToUInt32Bits(value));

    /// <inheritdoc />
    public override string ToString() => $"0x{this.Bits:x}";

  }

  /// <summary>A byte string.</summary>
  /// <param name="Value">The bytes.</param>
  public sealed record Bytes(byte[] Value) : ImageValue {

    /// <summary>The bytes.</summary>
    public byte[] Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc />
    public bool Equals(Bytes? other) => other is not null && this.Value.AsSpan().SequenceEqual(other.Value);

    /// <inheritdoc />
    public override int GetHashCode() => this.Value.Length;

    /// <inheritdoc />
    public override string ToString() => $"bytes[{Convert.ToHexStringLower(this.Value)}]";

  }

  /// <summary>A text: the UTF-16 code units of a <c>utf8</c> or <c>utf16</c> value; a lone surrogate is a unit like any other.</summary>
  /// <param name="Value">The text.</param>
  public sealed record Text(string Value) : ImageValue {

    /// <summary>The text.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc />
    public override string ToString() {
      var builder = new StringBuilder("\"");
      foreach (var unit in this.Value)
        builder.Append(unit is '"' or '\\' ? $"\\{unit}" : char.IsControl(unit) || char.IsSurrogate(unit) ? $"\\u{(int)unit:x4}" : unit.ToString());

      return builder.Append('"').ToString();
    }

  }

  /// <summary>The elements of a sequence.</summary>
  /// <param name="Elements">The elements, in order.</param>
  public sealed record Sequence(IReadOnlyList<ImageValue> Elements) : ImageValue {

    /// <summary>The elements, in order.</summary>
    public IReadOnlyList<ImageValue> Elements { get; } = Checked(Elements);

    /// <inheritdoc />
    public bool Equals(Sequence? other) => other is not null && this.Elements.SequenceEqual(other.Elements);

    /// <inheritdoc />
    public override int GetHashCode() => this.Elements.Count;

    /// <inheritdoc />
    public override string ToString() => $"[{string.Join(", ", this.Elements)}]";

  }

  /// <summary>An optional value: absent, or present with a value.</summary>
  /// <param name="Value">The value; <see langword="null"/> when absent.</param>
  public sealed record Optional(ImageValue? Value) : ImageValue {

    /// <summary>The absent value.</summary>
    public static Optional Absent { get; } = new((ImageValue?)null);

    /// <inheritdoc />
    public override string ToString() => this.Value?.ToString() ?? "absent";

  }

  /// <summary>The fields of a record, in declaration order.</summary>
  /// <param name="Fields">The field values.</param>
  public sealed record Record(IReadOnlyList<ImageValue> Fields) : ImageValue {

    /// <summary>The field values, in declaration order.</summary>
    public IReadOnlyList<ImageValue> Fields { get; } = Checked(Fields);

    /// <inheritdoc />
    public bool Equals(Record? other) => other is not null && this.Fields.SequenceEqual(other.Fields);

    /// <inheritdoc />
    public override int GetHashCode() => this.Fields.Count;

    /// <inheritdoc />
    public override string ToString() => $"{{{string.Join(", ", this.Fields)}}}";

  }

  /// <summary>The elements of a tuple.</summary>
  /// <param name="Elements">The elements, in order.</param>
  public sealed record Tuple(IReadOnlyList<ImageValue> Elements) : ImageValue {

    /// <summary>The elements, in order.</summary>
    public IReadOnlyList<ImageValue> Elements { get; } = Checked(Elements);

    /// <inheritdoc />
    public bool Equals(Tuple? other) => other is not null && this.Elements.SequenceEqual(other.Elements);

    /// <inheritdoc />
    public override int GetHashCode() => this.Elements.Count;

    /// <inheritdoc />
    public override string ToString() => $"({string.Join(", ", this.Elements)})";

  }

  /// <summary>One alternative of a union.</summary>
  /// <param name="Tag">The zero-based index of the alternative.</param>
  /// <param name="Value">Its value; <see langword="null"/> for the alternative <c>null</c>.</param>
  public sealed record Union(int Tag, ImageValue? Value) : ImageValue {

    /// <inheritdoc />
    public override string ToString() => $"#{this.Tag} {this.Value?.ToString() ?? "null"}";

  }

  /// <summary>The outcome of a call that trapped, in place of a result.</summary>
  /// <param name="Class">The trap class.</param>
  public sealed record Trap(TrapClass Class) : ImageValue {

    /// <summary>The trap class.</summary>
    public TrapClass Class { get; } = Class ?? throw new ArgumentNullException(nameof(Class));

    /// <inheritdoc />
    public override string ToString() => $"!{this.Class}";

  }

  private static IReadOnlyList<ImageValue> Checked(IReadOnlyList<ImageValue> values) {
    ArgumentNullException.ThrowIfNull(values);
    foreach (var value in values)
      ArgumentNullException.ThrowIfNull(value, nameof(values));

    return [.. values];
  }

}
