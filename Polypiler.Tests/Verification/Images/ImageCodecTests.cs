using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;
using Polypiler.Tests.Verification.Images.Support;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="ImageCodec"/>: canonical encoding and strict decoding of every image type (test design 1, 3, 6 to 16).</summary>
[TestFixture]
public sealed class ImageCodecTests {

  private static ImageValue.Integer Int(BigInteger value) => new(value);

  private static string Encode(string type, ImageValue value) => Convert.ToHexStringLower(ImageCodec.Encode(ImageType.Parse(type), value));

  private static ImageValue Decode(string type, string hex) {
    var bytes = Convert.FromHexString(hex);
    var value = ImageCodec.Decode(ImageType.Parse(type), bytes, out var consumed);
    Assert.That(consumed, Is.EqualTo(bytes.Length), "the whole image is consumed");
    return value;
  }

  private static string EncodeProblem(string type, ImageValue value) => Assert.Throws<ImageException>(() => ImageCodec.Encode(ImageType.Parse(type), value))!.Message;

  private static string DecodeProblem(string type, string hex) => Assert.Throws<ImageException>(() => ImageCodec.Decode(ImageType.Parse(type), Convert.FromHexString(hex), out _))!.Message;

  private static IEnumerable<ConformanceRow> Rows => ImageJson.Conformance();

  /// <summary>Test design 1: every row of the conformance file encodes to its hex and decodes back to its value.</summary>
  [TestCaseSource(nameof(Rows))]
  public void ConformanceRow_EncodesAndDecodes(ConformanceRow row) {
    Assert.That(Convert.ToHexStringLower(ImageCodec.Encode(row.Type, row.Value)), Is.EqualTo(row.Hex));
    Assert.That(Decode(row.Type.ToString(), row.Hex), Is.EqualTo(row.Value));
  }

  /// <summary>Every one of the 22 spellings has a row in the conformance file.</summary>
  [Test]
  public void EveryImageType_HasAConformanceRow() {
    var covered = Rows.Select(row => row.Type.ToString()).Select(type => type.Contains('<') || type.Contains('{') ? type[..type.IndexOfAny(['<', '{'])] : type).ToHashSet();
    Assert.That(covered, Is.SupersetOf(new[] { "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "bool", "f32", "f64", "char16", "bigint", "bytes", "utf8", "utf16", "seq", "opt", "record", "tuple", "union", "enum" }));
  }

  /// <summary>Test design 8: each fixed-width integer at its minimum, maximum, and one beyond each.</summary>
  [TestCase("u8", "0", "255")]
  [TestCase("u16", "0", "65535")]
  [TestCase("u32", "0", "4294967295")]
  [TestCase("u64", "0", "18446744073709551615")]
  [TestCase("i8", "-128", "127")]
  [TestCase("i16", "-32768", "32767")]
  [TestCase("i32", "-2147483648", "2147483647")]
  [TestCase("i64", "-9223372036854775808", "9223372036854775807")]
  [TestCase("char16", "0", "65535")]
  [TestCase("enum<i8>", "-128", "127")]
  public void FixedIntegers_AreRangeChecked(string type, string minimum, string maximum) {
    var (low, high) = (BigInteger.Parse(minimum), BigInteger.Parse(maximum));
    Assert.That(Decode(type, Encode(type, Int(low))), Is.EqualTo(Int(low)));
    Assert.That(Decode(type, Encode(type, Int(high))), Is.EqualTo(Int(high)));
    Assert.That(EncodeProblem(type, Int(low - 1)), Is.EqualTo($"value {low - 1} does not fit {type}"));
    Assert.That(EncodeProblem(type, Int(high + 1)), Is.EqualTo($"value {high + 1} does not fit {type}"));
  }

  /// <summary>Test design 9: the byte counts of a bigint at its boundaries.</summary>
  [TestCase("0", 0)]
  [TestCase("1", 1)]
  [TestCase("-1", 1)]
  [TestCase("127", 1)]
  [TestCase("128", 2)]
  [TestCase("-128", 1)]
  [TestCase("-129", 2)]
  [TestCase("18446744073709551616", 9)]
  public void Bigint_IsMinimal(string value, int count) {
    var image = ImageCodec.Encode(ImageType.BigInt, Int(BigInteger.Parse(value)));
    Assert.That((BitConverter.ToUInt32(image), image.Length), Is.EqualTo(((uint)count, 4 + count)));
  }

  /// <summary>Test design 10: a redundant sign byte or a zero written with a byte is not canonical.</summary>
  [TestCase("020000007f00")]
  [TestCase("0100000000")]
  [TestCase("02000000ffff")]
  [TestCase("0200000080ff")]
  [TestCase("030000000080ff")]
  public void ANonMinimalBigint_IsRefused(string hex) => Assert.That(DecodeProblem("bigint", hex), Is.EqualTo("image: bigint is not minimal"));

  /// <summary>Test design 11: empty and single-element counted values.</summary>
  [Test]
  public void EmptyAndSingle_CountedValues() {
    Assert.That(Encode("bytes", new ImageValue.Bytes([])), Is.EqualTo("00000000"));
    Assert.That(Encode("utf8", new ImageValue.Text("")), Is.EqualTo("00000000"));
    Assert.That(Encode("utf16", new ImageValue.Text("")), Is.EqualTo("00000000"));
    Assert.That(Encode("seq<bigint>", new ImageValue.Sequence([])), Is.EqualTo("00000000"));
    Assert.That(Encode("seq<bigint>", new ImageValue.Sequence([Int(0)])), Is.EqualTo("0100000000000000"));
    Assert.That(Decode("seq<bigint>", "0100000000000000"), Is.EqualTo(new ImageValue.Sequence([Int(0)])));
  }

  /// <summary>Test design 12: a count beyond the bytes is a truncation; the count ffffffff is malformed, never a null sequence.</summary>
  [Test]
  public void SequenceCounts_AreChecked() {
    Assert.That(DecodeProblem("seq<u8>", "feffffff0102"), Is.EqualTo("image is truncated at byte 6"));
    Assert.That(DecodeProblem("seq<u8>", "ffffffff"), Is.EqualTo("image: count ffffffff at 0 is malformed"));
    Assert.That(DecodeProblem("bytes", "03000000aabb"), Is.EqualTo("image is truncated at byte 6"));
    Assert.That(DecodeProblem("utf16", "02000000e900e9"), Is.EqualTo("image is truncated at byte 7"));
    Assert.That(DecodeProblem("seq<u16>", "020000000100"), Is.EqualTo("image is truncated at byte 6"));
  }

  [TestCase("u32", "0102")]
  [TestCase("f64", "00000000")]
  [TestCase("bool", "")]
  [TestCase("utf8", "010000")]
  [TestCase("opt<u8>", "01")]
  [TestCase("union<null|u8>", "")]
  [TestCase("record{a:u8,b:u8}", "01")]
  public void TruncatedImages_AreRefused(string type, string hex)
    => Assert.That(DecodeProblem(type, hex), Is.EqualTo($"image is truncated at byte {hex.Length / 2}"));

  /// <summary>Test design 13: the last alternative decodes, one beyond is refused.</summary>
  [Test]
  public void UnionTags_AreChecked() {
    Assert.That(Decode("union<null|i64|f64>", "02000000000000f83f"), Is.EqualTo(new ImageValue.Union(2, ImageValue.FloatBits.Of(1.5))));
    Assert.That(DecodeProblem("union<null|i64|f64>", "03"), Is.EqualTo("image: tag 3 exceeds the 3 alternatives of union<null|i64|f64>"));
    Assert.That(Decode("union<null|i64|f64>", "00"), Is.EqualTo(new ImageValue.Union(0, null)));
  }

  /// <summary>Test design 14: UTF-16 strings are unit sequences; a lone surrogate is kept, a pair is two units.</summary>
  [Test]
  public void Utf16_KeepsLoneSurrogates() {
    Assert.That(Encode("utf16", new ImageValue.Text("\ud83d")), Is.EqualTo("010000003dd8"));
    Assert.That(Decode("utf16", "010000003dd8"), Is.EqualTo(new ImageValue.Text("\ud83d")));
    Assert.That(Encode("utf16", new ImageValue.Text("\U0001F600")), Is.EqualTo("020000003dd800de"));
    Assert.That(Encode("char16", Int(0xd83d)), Is.EqualTo("3dd8"));
  }

  /// <summary>Test design 15: ill-formed UTF-8 is refused both ways.</summary>
  [Test]
  public void IllFormedUtf8_IsRefused() {
    Assert.That(DecodeProblem("utf8", "01000000ff"), Is.EqualTo("image: the utf8 bytes at 4 are not well-formed UTF-8"));
    Assert.That(DecodeProblem("utf8", "02000000c0af"), Is.EqualTo("image: the utf8 bytes at 4 are not well-formed UTF-8"));
    Assert.That(EncodeProblem("utf8", new ImageValue.Text("\ud83d")), Is.EqualTo("value \"\\ud83d\" does not fit utf8"));
  }

  [TestCase("bool", "02", "image: byte 02 is not a valid bool")]
  [TestCase("opt<u8>", "0207", "image: byte 02 is not a valid presence byte of opt<u8>")]
  public void InvalidMarkerBytes_AreRefused(string type, string hex, string problem) => Assert.That(DecodeProblem(type, hex), Is.EqualTo(problem));

  /// <summary>Test design 7: NaN payloads, signed zeros, subnormals and infinities keep their bits.</summary>
  [TestCase("f32", 0x7fc00001UL)]
  [TestCase("f32", 0xffbfffffUL)]
  [TestCase("f32", 0x80000000UL)]
  [TestCase("f32", 0x00000001UL)]
  [TestCase("f32", 0x7f800000UL)]
  [TestCase("f64", 0x7ff8000000000001UL)]
  [TestCase("f64", 0xfff4000000000000UL)]
  [TestCase("f64", 0x8000000000000000UL)]
  [TestCase("f64", 0x000fffffffffffffUL)]
  [TestCase("f64", 0xfff0000000000000UL)]
  public void FloatBits_ArePreserved(string type, ulong bits) {
    var value = new ImageValue.FloatBits(bits);
    Assert.That(Decode(type, Encode(type, value)), Is.EqualTo(value));
  }

  [Test]
  public void AnF32WithMoreThan32Bits_DoesNotFit()
    => Assert.That(EncodeProblem("f32", new ImageValue.FloatBits(0x1_0000_0000UL)), Is.EqualTo("value 0x100000000 does not fit f32"));

  [Test]
  public void TheBytesAfterTheImage_AreNotRead() {
    Assert.That(ImageCodec.Decode(ImageType.U8, new byte[] { 7, 8 }, out var consumed), Is.EqualTo(Int(7)));
    Assert.That(consumed, Is.EqualTo(1));
  }

  [Test]
  public void AValueOfAnotherShape_DoesNotFit() {
    Assert.That(EncodeProblem("u8", new ImageValue.Boolean(true)), Is.EqualTo("value true does not fit u8"));
    Assert.That(EncodeProblem("bool", Int(1)), Is.EqualTo("value 1 does not fit bool"));
    Assert.That(EncodeProblem("seq<u8>", new ImageValue.Tuple([Int(1), Int(2)])), Is.EqualTo("value (1, 2) does not fit seq<u8>"));
    Assert.That(EncodeProblem("u8", new ImageValue.Trap(new(TrapKind.Overflow))), Is.EqualTo("value !overflow does not fit u8"));
    Assert.That(EncodeProblem("seq<u8>", new ImageValue.Sequence([Int(256)])), Is.EqualTo("value 256 does not fit u8"));
  }

  [Test]
  public void RecordsAndTuples_NeedOneValuePerMember() {
    Assert.That(EncodeProblem("record{x:u8,y:u32}", new ImageValue.Record([Int(1)])), Is.EqualTo("value {1} does not fit record{x:u8,y:u32}"));
    Assert.That(EncodeProblem("tuple<u8,u8>", new ImageValue.Tuple([Int(1), Int(2), Int(3)])), Is.EqualTo("value (1, 2, 3) does not fit tuple<u8,u8>"));
  }

  [Test]
  public void UnionValues_MatchTheirAlternative() {
    Assert.That(EncodeProblem("union<null|u8>", new ImageValue.Union(2, Int(1))), Is.EqualTo("value #2 1 does not fit union<null|u8>"));
    Assert.That(EncodeProblem("union<null|u8>", new ImageValue.Union(-1, Int(1))), Is.EqualTo("value #-1 1 does not fit union<null|u8>"));
    Assert.That(EncodeProblem("union<null|u8>", new ImageValue.Union(0, Int(1))), Is.EqualTo("value #0 1 does not fit union<null|u8>"));
    Assert.That(EncodeProblem("union<null|u8>", new ImageValue.Union(1, null)), Is.EqualTo("value #1 null does not fit union<null|u8>"));
  }

  [Test]
  public void TheNullType_HasNoImageOfItsOwn() {
    Assert.That(Assert.Throws<ImageException>(() => ImageCodec.Encode(ImageType.Null, Int(0)))!.Message, Is.EqualTo("value 0 does not fit null"));
    Assert.Throws<ArgumentException>(() => ImageCodec.Decode(ImageType.Null, [], out _));
  }

  [Test]
  public void Null_IsRefused() {
    Assert.Throws<ArgumentNullException>(() => ImageCodec.Encode(null!, Int(0)));
    Assert.Throws<ArgumentNullException>(() => ImageCodec.Encode(ImageType.U8, null!));
    Assert.Throws<ArgumentNullException>(() => ImageCodec.Decode(null!, [], out _));
  }

  [Test]
  public void TheDiagnostic_IsPLY0064WithExitCode2() {
    var diagnostic = Assert.Throws<ImageException>(() => ImageCodec.Encode(ImageType.U8, Int(256)))!.ToDiagnostic();
    Assert.That((diagnostic.Code, diagnostic.Message, (int?)ImageDiagnostics.Invalid.ExitCode), Is.EqualTo(("PLY0064", "value 256 does not fit u8", (int?)2)));
  }

}
