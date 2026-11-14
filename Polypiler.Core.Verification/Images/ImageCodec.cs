using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Polypiler.Core.Verification.Images;

/// <summary>
/// Encodes values as images and decodes images back, both canonically: one value has exactly one
/// image, so images compare as bytes. Every multi-byte quantity is little-endian; NaN payloads and the
/// sign of zero are kept. Decoding is strict: an image that is not the canonical image of any value
/// is refused.
/// </summary>
public static class ImageCodec {

  /// <summary>The count of a <c>seq</c> that is malformed, never a null sequence.</summary>
  private const uint MalformedCount = uint.MaxValue;

  private static readonly UTF8Encoding _utf8 = new(false, true);

  /// <summary>The image of a value.</summary>
  /// <param name="type">The value's image type.</param>
  /// <param name="value">The value.</param>
  /// <returns>The image.</returns>
  /// <exception cref="ImageException">The value does not fit the type (<c>PLY0064: value &lt;v&gt; does not fit &lt;type&gt;</c>).</exception>
  public static byte[] Encode(ImageType type, ImageValue value) {
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(value);
    var output = new MemoryStream();
    Write(output, type, value);
    return output.ToArray();
  }

  /// <summary>Decodes the image at the start of some bytes.</summary>
  /// <param name="type">The image type.</param>
  /// <param name="bytes">The bytes; those after the image are not read.</param>
  /// <param name="consumed">The length of the image.</param>
  /// <returns>The value.</returns>
  /// <exception cref="ImageException">The bytes do not start with a canonical image of the type.</exception>
  public static ImageValue Decode(ImageType type, ReadOnlySpan<byte> bytes, out int consumed) {
    ArgumentNullException.ThrowIfNull(type);
    consumed = 0;
    return Read(type, bytes, ref consumed, "image");
  }

  /// <summary>Decodes the image at <paramref name="position"/>; messages name the <paramref name="subject"/> and give offsets into <paramref name="bytes"/>.</summary>
  internal static ImageValue Read(ImageType type, ReadOnlySpan<byte> bytes, ref int position, string subject) {
    switch (type.Kind) {
      case ImageKind.Integer:
      case ImageKind.Enum:
      case ImageKind.Char16: {
        var raw = Take(bytes, ref position, type.Width, subject);
        return new ImageValue.Integer(new BigInteger(raw, isUnsigned: !type.Signed));
      }
      case ImageKind.Boolean: {
        var raw = Take(bytes, ref position, 1, subject)[0];
        return raw <= 1 ? new ImageValue.Boolean(raw == 1) : throw new ImageException($"{subject}: byte {raw:x2} is not a valid bool");
      }
      case ImageKind.Float:
        var bits = Take(bytes, ref position, type.Width, subject);
        return new ImageValue.FloatBits(type.Width == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bits) : BinaryPrimitives.ReadUInt64LittleEndian(bits));
      case ImageKind.BigInt: {
        var raw = Take(bytes, ref position, Count(bytes, ref position, subject, 1), subject);
        if (raw.Length > 0 && (raw[^1] == 0 && (raw.Length == 1 || raw[^2] < 0x80) || raw[^1] == 0xff && raw.Length > 1 && raw[^2] >= 0x80))
          throw new ImageException($"{subject}: bigint is not minimal");

        return new ImageValue.Integer(new BigInteger(raw));
      }
      case ImageKind.Bytes:
        return new ImageValue.Bytes(Take(bytes, ref position, Count(bytes, ref position, subject, 1), subject).ToArray());
      case ImageKind.Utf8: {
        var at = position + 4;
        var raw = Take(bytes, ref position, Count(bytes, ref position, subject, 1), subject);
        try {
          return new ImageValue.Text(_utf8.GetString(raw));
        } catch (DecoderFallbackException) {
          throw new ImageException($"{subject}: the utf8 bytes at {at} are not well-formed UTF-8");
        }
      }
      case ImageKind.Utf16: {
        var raw = Take(bytes, ref position, Count(bytes, ref position, subject, 2) * 2, subject);
        var units = new char[raw.Length / 2];
        for (var i = 0; i < units.Length; ++i)
          units[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(raw[(2 * i)..]);

        return new ImageValue.Text(new(units));
      }
      case ImageKind.Sequence: {
        var count = Count(bytes, ref position, subject, 1);
        var elements = new List<ImageValue>();
        for (var i = 0; i < count; ++i)
          elements.Add(Read(type.Element!, bytes, ref position, subject));

        return new ImageValue.Sequence(elements);
      }
      case ImageKind.Optional: {
        var presence = Take(bytes, ref position, 1, subject)[0];
        return presence switch {
          0 => ImageValue.Optional.Absent,
          1 => new ImageValue.Optional(Read(type.Element!, bytes, ref position, subject)),
          _ => throw new ImageException($"{subject}: byte {presence:x2} is not a valid presence byte of {type}")
        };
      }
      case ImageKind.Record:
      case ImageKind.Tuple: {
        var values = new List<ImageValue>();
        foreach (var field in type.Fields)
          values.Add(Read(field.Type, bytes, ref position, subject));

        return type.Kind == ImageKind.Record ? new ImageValue.Record(values) : new ImageValue.Tuple(values);
      }
      case ImageKind.Union: {
        var tag = Take(bytes, ref position, 1, subject)[0];
        if (tag >= type.Fields.Count)
          throw new ImageException($"{subject}: tag {tag} exceeds the {type.Fields.Count} alternatives of {type}");

        var alternative = type.Fields[tag].Type;
        return new ImageValue.Union(tag, alternative.Kind == ImageKind.Null ? null : Read(alternative, bytes, ref position, subject));
      }
      default:
        throw new ArgumentException("'null' is only an alternative of a union", nameof(type));
    }
  }

  /// <summary>
  /// Reads a <c>u32</c> count and checks that what it counts can be there: every counted thing takes
  /// at least <paramref name="unit"/> bytes, so a count beyond the rest of the bytes is a truncation.
  /// </summary>
  private static int Count(ReadOnlySpan<byte> bytes, ref int position, string subject, int unit) {
    var count = BinaryPrimitives.ReadUInt32LittleEndian(Take(bytes, ref position, 4, subject));
    if (count == MalformedCount)
      throw new ImageException($"{subject}: count ffffffff at {position - 4} is malformed");
    if (count > (uint)((bytes.Length - position) / unit))
      throw Truncated(subject, bytes.Length);

    return (int)count;
  }

  private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int position, int length, string subject) {
    if (length > bytes.Length - position)
      throw Truncated(subject, bytes.Length);

    var taken = bytes.Slice(position, length);
    position += length;
    return taken;
  }

  private static ImageException Truncated(string subject, int offset) => new($"{subject} is truncated at byte {offset}");

  private static void Write(MemoryStream output, ImageType type, ImageValue value) {
    switch (type.Kind, value) {
      case (ImageKind.Integer or ImageKind.Enum or ImageKind.Char16, ImageValue.Integer integer): {
        var bits = 8 * type.Width;
        var (minimum, maximum) = type.Signed ? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1) : (BigInteger.Zero, (BigInteger.One << bits) - 1);
        if (integer.Value < minimum || integer.Value > maximum)
          throw DoesNotFit(type, value);

        var span = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)(integer.Value & ulong.MaxValue));
        output.Write(span, 0, type.Width);
        break;
      }
      case (ImageKind.Boolean, ImageValue.Boolean boolean):
        output.WriteByte(boolean.Value ? (byte)1 : (byte)0);
        break;
      case (ImageKind.Float, ImageValue.FloatBits pattern): {
        if (type.Width == 4 && pattern.Bits > uint.MaxValue)
          throw DoesNotFit(type, value);

        var span = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(span, pattern.Bits);
        output.Write(span, 0, type.Width);
        break;
      }
      case (ImageKind.BigInt, ImageValue.Integer integer):
        Counted(output, integer.Value.IsZero ? [] : integer.Value.ToByteArray());
        break;
      case (ImageKind.Bytes, ImageValue.Bytes raw):
        Counted(output, raw.Value);
        break;
      case (ImageKind.Utf8, ImageValue.Text text): {
        byte[] encoded;
        try {
          encoded = _utf8.GetBytes(text.Value);
        } catch (EncoderFallbackException) {
          throw DoesNotFit(type, value);
        }

        Counted(output, encoded);
        break;
      }
      case (ImageKind.Utf16, ImageValue.Text text): {
        WriteCount(output, text.Value.Length);
        foreach (var unit in text.Value) {
          output.WriteByte((byte)unit);
          output.WriteByte((byte)(unit >> 8));
        }

        break;
      }
      case (ImageKind.Sequence, ImageValue.Sequence sequence):
        WriteCount(output, sequence.Elements.Count);
        foreach (var element in sequence.Elements)
          Write(output, type.Element!, element);
        break;
      case (ImageKind.Optional, ImageValue.Optional optional):
        output.WriteByte(optional.Value is null ? (byte)0 : (byte)1);
        if (optional.Value is { } present)
          Write(output, type.Element!, present);
        break;
      case (ImageKind.Record, ImageValue.Record record):
        Members(output, type, record.Fields, value);
        break;
      case (ImageKind.Tuple, ImageValue.Tuple tuple):
        Members(output, type, tuple.Elements, value);
        break;
      case (ImageKind.Union, ImageValue.Union union): {
        if (union.Tag < 0 || union.Tag >= type.Fields.Count || (type.Fields[union.Tag].Type.Kind == ImageKind.Null) != (union.Value is null))
          throw DoesNotFit(type, value);

        output.WriteByte((byte)union.Tag);
        if (union.Value is { } payload)
          Write(output, type.Fields[union.Tag].Type, payload);
        break;
      }
      default:
        throw DoesNotFit(type, value);
    }
  }

  private static void Members(MemoryStream output, ImageType type, IReadOnlyList<ImageValue> values, ImageValue value) {
    if (values.Count != type.Fields.Count)
      throw DoesNotFit(type, value);

    for (var i = 0; i < values.Count; ++i)
      Write(output, type.Fields[i].Type, values[i]);
  }

  private static void Counted(MemoryStream output, byte[] bytes) {
    WriteCount(output, bytes.Length);
    output.Write(bytes);
  }

  /// <summary>Writes a <c>u32</c> count; an <see cref="int"/> never reaches the malformed count.</summary>
  private static void WriteCount(MemoryStream output, int count) {
    Span<byte> span = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)count);
    output.Write(span);
  }

  private static ImageException DoesNotFit(ImageType type, ImageValue value) => new($"value {value} does not fit {type}");

}
