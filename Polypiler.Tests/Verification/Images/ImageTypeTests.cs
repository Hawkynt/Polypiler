using System;
using NUnit.Framework;
using Polypiler.Core.Verification.Images;

namespace Polypiler.Tests.Verification.Images;

/// <summary><see cref="ImageType"/>: the notation, its canonical spelling and its refusals (test design 3, 17).</summary>
[TestFixture]
public sealed class ImageTypeTests {

  private static string Problem(string text) => Assert.Throws<ImageException>(() => ImageType.Parse(text))!.Message;

  [TestCase("u8", ImageKind.Integer, 1, false)]
  [TestCase("u16", ImageKind.Integer, 2, false)]
  [TestCase("u32", ImageKind.Integer, 4, false)]
  [TestCase("u64", ImageKind.Integer, 8, false)]
  [TestCase("i8", ImageKind.Integer, 1, true)]
  [TestCase("i16", ImageKind.Integer, 2, true)]
  [TestCase("i32", ImageKind.Integer, 4, true)]
  [TestCase("i64", ImageKind.Integer, 8, true)]
  [TestCase("bool", ImageKind.Boolean, 1, false)]
  [TestCase("f32", ImageKind.Float, 4, false)]
  [TestCase("f64", ImageKind.Float, 8, false)]
  [TestCase("char16", ImageKind.Char16, 2, false)]
  [TestCase("bigint", ImageKind.BigInt, 0, false)]
  [TestCase("bytes", ImageKind.Bytes, 0, false)]
  [TestCase("utf8", ImageKind.Utf8, 0, false)]
  [TestCase("utf16", ImageKind.Utf16, 0, false)]
  public void EveryName_ParsesToItsKind(string text, ImageKind kind, int width, bool signed) {
    var type = ImageType.Parse(text);
    Assert.That((type.Kind, type.Width, type.Signed, type.Element, type.Fields.Count, type.ToString()), Is.EqualTo((kind, width, signed, (ImageType?)null, 0, text)));
  }

  [TestCase("seq<u8>", ImageKind.Sequence)]
  [TestCase("opt<i32>", ImageKind.Optional)]
  [TestCase("enum<i16>", ImageKind.Enum)]
  public void ElementTypes_CarryTheirElement(string text, ImageKind kind) {
    var type = ImageType.Parse(text);
    Assert.That((type.Kind, type.Element!.Kind, type.ToString()), Is.EqualTo((kind, ImageKind.Integer, text)));
  }

  [Test]
  public void AnEnum_HasTheWidthAndSignOfItsUnderlyingType() {
    var type = ImageType.Parse("enum<i16>");
    Assert.That((type.Width, type.Signed, type.Element), Is.EqualTo((2, true, ImageType.I16)));
  }

  [Test]
  public void Composites_ListTheirMembers() {
    var record = ImageType.Parse("record{x:u8,y:u32}");
    Assert.That(record.Fields, Is.EqualTo(new[] { new ImageField("x", ImageType.U8), new ImageField("y", ImageType.U32) }));
    var union = ImageType.Parse("union<null|i64|f64>");
    Assert.That((union.Kind, union.Fields[0].Type, union.Fields[0].Type.Kind, union.Fields[2].Type), Is.EqualTo((ImageKind.Union, ImageType.Null, ImageKind.Null, ImageType.F64)));
    var tuple = ImageType.Parse("tuple<bool,i16,utf8>");
    Assert.That((tuple.Kind, tuple.Fields.Count, tuple.Fields[2].Type), Is.EqualTo((ImageKind.Tuple, 3, ImageType.Utf8)));
  }

  /// <summary>Test design 6: nested types, with spaces anywhere between the parts, have one canonical spelling.</summary>
  [TestCase(" seq < opt< record{ a : bigint , b :utf8 } > > ", "seq<opt<record{a:bigint,b:utf8}>>")]
  [TestCase("union< null | tuple<u8, u8> | seq<union<i8|u8>> >", "union<null|tuple<u8,u8>|seq<union<i8|u8>>>")]
  [TestCase("record{ _x1 : enum<u64> }", "record{_x1:enum<u64>}")]
  public void Spelling_IsCanonical(string text, string canonical) {
    var type = ImageType.Parse(text);
    Assert.That(type.ToString(), Is.EqualTo(canonical));
    Assert.That(ImageType.Parse(canonical), Is.EqualTo(type));
  }

  [Test]
  public void Equality_IsBySpelling() {
    var type = ImageType.Parse("seq<u8>");
    Assert.That(type, Is.EqualTo(ImageType.Seq(ImageType.U8)));
    Assert.That(type.GetHashCode(), Is.EqualTo(ImageType.Seq(ImageType.U8).GetHashCode()));
    Assert.That(type.Equals((object)ImageType.Seq(ImageType.U8)), Is.True);
    Assert.That(type.Equals(ImageType.Seq(ImageType.I8)), Is.False);
    Assert.That(type.Equals(null), Is.False);
    Assert.That(type.Equals("seq<u8>"), Is.False);
  }

  /// <summary>Test design 17: unknown types, unbalanced brackets, duplicate fields and empty unions name the text.</summary>
  [TestCase("int", "unknown image type 'int'")]
  [TestCase("seq<int>", "unknown image type 'int'")]
  [TestCase("null", "unknown image type 'null'")]
  [TestCase("void", "unknown image type 'void'")]
  [TestCase("", "'' is not an image type: an image type expected at position 0")]
  [TestCase("seq<u8", "'seq<u8' is not an image type: '>' expected at position 6")]
  [TestCase("seq<u8>>", "'seq<u8>>' is not an image type: unexpected '>' at position 7")]
  [TestCase("tuple<u8,u8", "'tuple<u8,u8' is not an image type: '>' expected at position 11")]
  [TestCase("seq u8", "'seq u8' is not an image type: '<' expected at position 4")]
  [TestCase("record{a:u8,a:u32}", "'record{a:u8,a:u32}' has the field 'a' twice")]
  [TestCase("record{}", "'record{}' is not an image type: a field name is missing at position 7")]
  [TestCase("record{a u8}", "'record{a u8}' is not an image type: ':' expected at position 9")]
  [TestCase("union<>", "'union<>' is not an image type: an image type expected at position 6")]
  [TestCase("union<u8>", "'union<u8>' needs two or more alternatives")]
  [TestCase("union<u8|u8>", "'union<u8|u8>' lists an alternative twice")]
  [TestCase("tuple<u8>", "'tuple<u8>' needs two or more element types")]
  [TestCase("enum<f32>", "the underlying type of an enum must be an integer type, not 'f32'")]
  [TestCase("seq<null>", "unknown image type 'null'")]
  public void Malformed_IsRefused(string text, string problem) => Assert.That(Problem(text), Is.EqualTo(problem));

  [Test]
  public void Factories_RefuseNullAsAValueType() {
    Assert.That(Assert.Throws<ImageException>(() => ImageType.Seq(ImageType.Null))!.Message, Is.EqualTo("'null' is only an alternative of a union"));
    Assert.That(Assert.Throws<ImageException>(() => ImageType.Record(new ImageField("a", ImageType.Null)))!.Message, Is.EqualTo("'null' is only an alternative of a union"));
    Assert.That(ImageType.Union(ImageType.Null, ImageType.U8).ToString(), Is.EqualTo("union<null|u8>"));
    Assert.That(Assert.Throws<ImageException>(() => ImageType.Record([]))!.Message, Is.EqualTo("'record{}' has no field"));
  }

  [Test]
  public void Factories_RefuseNull() {
    Assert.Throws<ArgumentNullException>(() => ImageType.Parse(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Seq(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Enum(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Record(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Tuple(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Union(null!));
    Assert.Throws<ArgumentNullException>(() => ImageType.Union(ImageType.U8, null!));
  }

}
