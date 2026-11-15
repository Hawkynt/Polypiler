using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Verification.Generators;
using Polypiler.Core.Verification.Images;
using Polypiler.Tests.Properties;

namespace Polypiler.Tests.Verification.Images;

/// <summary>
/// The round-trip property of the codec: for generated values of every image type,
/// <c>Decode(Encode(v)) = v</c> and <c>Encode(Decode(b)) = b</c> for the image <c>b</c>; 10 000 values
/// per type from seed 64. Slow tier: the count makes each type take most of a second.
/// </summary>
[TestFixture, Category("Slow")]
public sealed class ImageRoundTripPropertyTests {

  private const ulong Seed = 64;
  private const int Count = 10_000;

  private static readonly string[] _types = [
    "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "bool", "f32", "f64", "char16", "bigint", "bytes", "utf8", "utf16",
    "seq<i16>", "opt<u32>", "record{a:u8,b:bigint}", "tuple<bool,f64>", "union<null|i64|utf8>", "enum<i8>",
    "seq<opt<record{a:bigint,b:utf8}>>"
  ];

  private static IEnumerable<string> Types => _types;

  [TestCaseSource(nameof(Types))]
  public void EveryValue_RoundTrips(string text) {
    var type = ImageType.Parse(text);
    var checkedValues = 0;
    Property.ForAll(Seed, Count, seed => Values(type, seed), _ => [], value => {
      ++checkedValues;
      var image = ImageCodec.Encode(type, value);
      var decoded = ImageCodec.Decode(type, image, out var consumed);
      return consumed == image.Length && decoded.Equals(value) && ImageCodec.Encode(type, decoded).AsSpan().SequenceEqual(image);
    }, $"{nameof(this.EveryValue_RoundTrips)}_{text}");
    Assert.That(checkedValues, Is.EqualTo(Count));
  }

  private static IEnumerable<ImageValue> Values(ImageType type, ulong seed) {
    var random = new SeededRandom(seed);
    while (true)
      yield return Value(type, random, 0);
  }

  private static ImageValue Value(ImageType type, SeededRandom random, int depth) {
    switch (type.Kind) {
      case ImageKind.Integer:
      case ImageKind.Enum:
      case ImageKind.Char16: {
        var bits = random.NextBits(8 * type.Width);
        return new ImageValue.Integer(type.Signed ? Signed(bits, type.Width) : new BigInteger(bits));
      }
      case ImageKind.Boolean:
        return new ImageValue.Boolean(random.NextBoolean());
      case ImageKind.Float:
        return new ImageValue.FloatBits(random.NextBits(8 * type.Width));
      case ImageKind.BigInt: {
        var raw = new byte[random.NextBelow(20)];
        for (var i = 0; i < raw.Length; ++i)
          raw[i] = (byte)random.NextBits(8);
        return new ImageValue.Integer(new BigInteger(raw));
      }
      case ImageKind.Bytes:
        return new ImageValue.Bytes([.. Enumerable.Range(0, random.NextBelow(8)).Select(_ => (byte)random.NextBits(8))]);
      case ImageKind.Utf8:
        return new ImageValue.Text(Text(random, wellFormed: true));
      case ImageKind.Utf16:
        return new ImageValue.Text(Text(random, wellFormed: false));
      case ImageKind.Sequence:
        return new ImageValue.Sequence([.. Enumerable.Range(0, depth > 2 ? 0 : random.NextBelow(4)).Select(_ => Value(type.Element!, random, depth + 1))]);
      case ImageKind.Optional:
        return random.NextBoolean() ? ImageValue.Optional.Absent : new ImageValue.Optional(Value(type.Element!, random, depth + 1));
      case ImageKind.Record:
        return new ImageValue.Record([.. type.Fields.Select(field => Value(field.Type, random, depth + 1))]);
      case ImageKind.Tuple:
        return new ImageValue.Tuple([.. type.Fields.Select(field => Value(field.Type, random, depth + 1))]);
      default: {
        var tag = random.NextBelow(type.Fields.Count);
        var alternative = type.Fields[tag].Type;
        return new ImageValue.Union(tag, alternative.Kind == ImageKind.Null ? null : Value(alternative, random, depth + 1));
      }
    }
  }

  private static BigInteger Signed(ulong bits, int width) => width == 8 ? (long)bits : (long)(bits << (64 - 8 * width)) >> (64 - 8 * width);

  /// <summary>A text of up to six units: any unit for UTF-16, which keeps lone surrogates; scalar values only for UTF-8.</summary>
  private static string Text(SeededRandom random, bool wellFormed) {
    var length = random.NextBelow(7);
    if (!wellFormed)
      return new([.. Enumerable.Range(0, length).Select(_ => (char)random.NextBits(16))]);

    var builder = new System.Text.StringBuilder();
    for (var i = 0; i < length; ++i) {
      var scalar = (int)random.NextBits(21) % 0x110000;
      builder.Append(scalar is >= 0xd800 and <= 0xdfff ? "?" : char.ConvertFromUtf32(scalar));
    }

    return builder.ToString();
  }

}
