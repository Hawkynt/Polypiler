using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Records;

/// <summary>
/// The byte layout of records (#110): <see cref="RecordLayout"/> for the natural, packed and explicit rules
/// under the <see cref="LayoutProfile"/>s, with every padding range reported. The expected offsets and sizes are
/// what <c>gcc</c> gives for the same C structs with <c>-m64</c> (lp64-sysv) and <c>-m32</c> (ilp32-sysv),
/// with and without <c>#pragma pack</c>, and what the 16-bit DOS compilers give with word alignment (dos16).
/// </summary>
[TestFixture]
public sealed class RecordLayoutTests {

  private static IrField Field(string name, IrType type) => new(name, type, false);

  private static IrStructLayout Packed(int pack) => new(IrLayoutKind.Sequential, pack, 0, []);

  private static IrStructLayout Explicit(int size, params int[] offsets) => new(IrLayoutKind.Explicit, 0, size, offsets);

  /// <summary><c>H { a: u8, b: u32, c: u16 }</c> under a rule.</summary>
  private static IrStructType H(IrStructLayout? rule) => new("H", [Field("a", IrType.U8), Field("b", IrType.U32), Field("c", IrType.U16)]) { Layout = rule };

  private static string Padding(RecordLayout layout) => layout.Padding.Count == 0 ? "none" : string.Join(", ", layout.Padding);

  private static LayoutProfile Profile(string name) => LayoutProfile.Find(name)!;

  /// <summary>Scenario "Layout follows the rule and the profile".</summary>
  [TestCase(0, "lp64-sysv", "0, 4, 8", 12, 4, "1+3, 10+2")]
  [TestCase(1, "lp64-sysv", "0, 1, 5", 7, 1, "none")]
  [TestCase(2, "lp64-sysv", "0, 2, 6", 8, 2, "1+1")]
  [TestCase(0, "dos16", "0, 2, 6", 8, 2, "1+1")]
  [TestCase(0, "ilp32-sysv", "0, 4, 8", 12, 4, "1+3, 10+2")]
  public void Layout_FollowsTheRuleAndTheProfile(int pack, string profile, string offsets, long size, int alignment, string padding) {
    var layout = RecordLayout.Compute(H(pack == 0 ? null : Packed(pack)), Profile(profile));
    Assert.That((string.Join(", ", layout.Offsets), layout.Size, layout.Alignment, Padding(layout)), Is.EqualTo((offsets, size, alignment, padding)));
    Assert.That(layout.Profile.Name, Is.EqualTo(profile));
  }

  /// <summary>Scenario "A double is aligned to 8 on lp64 and to 4 on ilp32".</summary>
  [TestCase("lp64-sysv", 8, 16)]
  [TestCase("ilp32-sysv", 4, 12)]
  [TestCase("dos16", 2, 10)]
  public void Double_IsAlignedByTheProfile(string profile, long offset, long size) {
    var value = Field("value", IrType.F64);
    var record = new IrStructType("D", [Field("tag", IrType.U8), value]);
    var layout = RecordLayout.Compute(record, Profile(profile));
    Assert.That((layout.OffsetOf(value), layout.Size), Is.EqualTo((offset, size)));
    Assert.That(RecordLayout.OffsetOf(record, value, Profile(profile)), Is.EqualTo(offset));
  }

  /// <summary>Test design 1: a packed pixel is three bytes in a row.</summary>
  [Test]
  public void Pixel_IsThreeBytes() {
    var pixel = new IrStructType("Pixel", [Field("r", IrType.U8), Field("g", IrType.U8), Field("b", IrType.U8)]) { Layout = Packed(1) };
    var layout = RecordLayout.Compute(pixel, LayoutProfile.Lp64SysV);
    Assert.That((layout.Size, layout.Offsets), Is.EqualTo((3L, new long[] { 0, 1, 2 })));
  }

  /// <summary>Test design 3: a record within a record is aligned as its own alignment says, under natural and packed rules.</summary>
  [TestCase("lp64-sysv", 0, 4, 16, 4)]
  [TestCase("dos16", 0, 2, 10, 2)]
  [TestCase("lp64-sysv", 1, 1, 13, 1)]
  public void NestedRecord_IsAlignedAsItself(string profile, int pack, long offset, long size, int alignment) {
    var inner = Field("h", IrType.Struct(H(null)));
    var outer = new IrStructType("R", [Field("x", IrType.U8), inner]) { Layout = pack == 0 ? null : Packed(pack) };
    var layout = RecordLayout.Compute(outer, Profile(profile));
    Assert.That((layout.OffsetOf(inner), layout.Size, layout.Alignment), Is.EqualTo((offset, size, alignment)));
  }

  /// <summary>Test design 4 (layout side): a truth value, a signed integer, a float and an enum are laid out by their sizes.</summary>
  [Test]
  public void Scalars_AreLaidOutBySize() {
    var colour = new IrEnumType("Colour", IrType.U16, [new IrEnumMember("Red", 0)], false);
    var record = new IrStructType("S", [Field("flag", IrType.Bool), Field("colour", IrType.Enum(colour)), Field("ratio", IrType.F32), Field("count", IrType.I64)]);
    var layout = RecordLayout.Compute(record, LayoutProfile.Lp64SysV);
    Assert.That((layout.Offsets, layout.Size), Is.EqualTo((new long[] { 0, 2, 4, 8 }, 16L)));
    Assert.That(Padding(layout), Is.EqualTo("1+1"));
  }

  /// <summary>Test design 6: the fields and the padding ranges tile the record exactly, for every rule and profile.</summary>
  [Test]
  public void FieldsAndPadding_TileTheRecord() {
    var records = new List<IrStructType> { H(null), H(Packed(1)), H(Packed(2)), H(Packed(4)), H(Explicit(12, 8, 0, 4)) };
    records.Add(new IrStructType("N", [Field("x", IrType.U8), Field("h", IrType.Struct(H(null))), Field("y", IrType.F64)]));
    foreach (var profile in LayoutProfile.Standard)
      foreach (var record in records) {
        var layout = RecordLayout.Compute(record, profile);
        var covered = new int[layout.Size];
        for (var index = 0; index < record.Fields.Count; ++index)
          for (var at = layout.Offsets[index]; at < layout.Offsets[index] + RecordLayout.SizeOf(record.Fields[index].Type, profile); ++at)
            ++covered[at];

        foreach (var gap in layout.Padding)
          for (var at = gap.Offset; at < gap.Offset + gap.Length; ++at)
            ++covered[at];

        Assert.That(covered, Is.All.EqualTo(1), $"{record.Name} on {profile}");
      }
  }

  /// <summary>Test design 7: an empty record, a single field, and a thousand fields.</summary>
  [Test]
  public void Boundaries_EmptySingleAndMany() {
    var empty = RecordLayout.Compute(new IrStructType("E", []), LayoutProfile.Lp64SysV);
    Assert.That((empty.Size, empty.Alignment, empty.Padding.Count), Is.EqualTo((0L, 1, 0)));

    var single = RecordLayout.Compute(new IrStructType("One", [Field("v", IrType.U32)]), LayoutProfile.Lp64SysV);
    Assert.That((single.Size, single.Alignment), Is.EqualTo((4L, 4)));

    // 500 pairs of a byte and a 32-bit number: each pair 8 bytes on lp64, 6 on dos16.
    var many = new IrStructType("Many", [.. Enumerable.Range(0, 1000).Select(index => Field($"f{index}", index % 2 == 0 ? IrType.U8 : IrType.U32))]);
    Assert.That(RecordLayout.SizeOf(IrType.Struct(many), LayoutProfile.Lp64SysV), Is.EqualTo(4000));
    Assert.That(RecordLayout.SizeOf(IrType.Struct(many), LayoutProfile.Dos16), Is.EqualTo(3000));
    Assert.That(RecordLayout.Compute(many, LayoutProfile.Lp64SysV).Padding, Has.Count.EqualTo(500));
  }

  /// <summary>Test design 8: packing by every power of two, and by 3, which is a problem.</summary>
  [TestCase(1, "0, 1, 5", 7)]
  [TestCase(2, "0, 2, 6", 8)]
  [TestCase(4, "0, 4, 8", 12)]
  [TestCase(8, "0, 4, 8", 12)]
  [TestCase(16, "0, 4, 8", 12)]
  public void Packing_CapsTheAlignment(int pack, string offsets, long size) {
    var layout = RecordLayout.Compute(H(Packed(pack)), LayoutProfile.Lp64SysV);
    Assert.That((string.Join(", ", layout.Offsets), layout.Size), Is.EqualTo((offsets, size)));
  }

  /// <summary>Test design 8 and 11: a packing that is no power of two is named, and computing throws with it.</summary>
  [TestCase(3)]
  [TestCase(-2)]
  public void Packing_ThatIsNoPowerOfTwo_IsAProblem(int pack) {
    Assert.That(RecordLayout.Problems(H(Packed(pack)), LayoutProfile.Lp64SysV), Is.EqualTo(new[] { $"a packing of {pack}, which is no power of two" }));
    var thrown = Assert.Throws<ArgumentException>(() => RecordLayout.Compute(H(Packed(pack)), LayoutProfile.Lp64SysV))!;
    Assert.That(thrown.Message, Does.StartWith($"layout of 'H': a packing of {pack}"));
  }

  /// <summary>Test design 9: an explicit size equal to the end of the fields, larger, and smaller.</summary>
  [TestCase(6, 6, "none")]
  [TestCase(8, 8, "6+2")]
  [TestCase(0, 6, "none")]
  public void ExplicitSize_EqualOrLarger_IsTaken(int stated, long size, string padding) {
    var layout = RecordLayout.Compute(new IrStructType("X", [Field("a", IrType.U32), Field("b", IrType.U16)]) { Layout = Explicit(stated, 0, 4) }, LayoutProfile.Lp64SysV);
    Assert.That((layout.Size, layout.Alignment, Padding(layout)), Is.EqualTo((size, 1, padding)));
  }

  /// <summary>Test design 9: an explicit size smaller than the end of the fields is a problem.</summary>
  [Test]
  public void ExplicitSize_Smaller_IsAProblem()
    => Assert.That(RecordLayout.Problems(new IrStructType("X", [Field("a", IrType.U32), Field("b", IrType.U16)]) { Layout = Explicit(5, 0, 4) }, LayoutProfile.Lp64SysV),
      Is.EqualTo(new[] { "the size 5 is smaller than the end 6 of the fields of 'X'" }));

  /// <summary>Scenario "Overlapping explicit fields are rejected", test design 11.</summary>
  [Test]
  public void ExplicitOverlap_IsAProblem()
    => Assert.That(RecordLayout.Problems(new IrStructType("X", [Field("a", IrType.U32), Field("b", IrType.U16)]) { Layout = Explicit(0, 0, 2) }, LayoutProfile.Lp64SysV),
      Is.EqualTo(new[] { "the fields 'a' and 'b' overlap" }));

  /// <summary>An explicit layout in any order of offsets, with gaps, packed by 4, and an empty record within, which takes no bytes.</summary>
  [Test]
  public void Explicit_PlacesFieldsWhereStated() {
    var empty = new IrStructType("E", []);
    var record = new IrStructType("X", [Field("a", IrType.U16), Field("e", IrType.Struct(empty)), Field("b", IrType.U8)]) { Layout = new IrStructLayout(IrLayoutKind.Explicit, 4, 0, [4, 0, 1]) };
    var layout = RecordLayout.Compute(record, LayoutProfile.Dos16);
    Assert.That((layout.Size, layout.Alignment, Padding(layout)), Is.EqualTo((6L, 4, "0+1, 2+2")));
  }

  /// <summary>An explicit layout with the wrong number of offsets, or a negative one, is a problem.</summary>
  [Test]
  public void ExplicitOffsets_Malformed_AreProblems() {
    var fields = new[] { Field("a", IrType.U32), Field("b", IrType.U16) };
    Assert.That(RecordLayout.Problems(new IrStructType("X", fields) { Layout = Explicit(0, 0) }, LayoutProfile.Lp64SysV), Is.EqualTo(new[] { "1 offsets for 2 fields" }));
    Assert.That(RecordLayout.Problems(new IrStructType("X", fields) { Layout = Explicit(0, -4, 0) }, LayoutProfile.Lp64SysV),
      Is.EqualTo(new[] { "the field 'a' has the negative offset -4" }));
  }

  /// <summary>A stated size of a sequential layout enlarges the record; one short of the fields is a problem.</summary>
  [Test]
  public void SequentialSize_EnlargesOrIsAProblem() {
    var larger = RecordLayout.Compute(H(new IrStructLayout(IrLayoutKind.Sequential, 0, 16, [])), LayoutProfile.Lp64SysV);
    Assert.That((larger.Size, Padding(larger)), Is.EqualTo((16L, "1+3, 10+6")));
    Assert.That(RecordLayout.Problems(H(new IrStructLayout(IrLayoutKind.Sequential, 0, 9, [])), LayoutProfile.Lp64SysV),
      Is.EqualTo(new[] { "the size 9 is smaller than the end 10 of the fields of 'H'" }));
  }

  /// <summary>Scenario "A record cannot contain itself by value", test design 12: itself and through another record.</summary>
  [Test]
  public void Containment_OfItself_IsAProblem() {
    var self = new IrStructType("N");
    self.Define([Field("next", IrType.Struct(self))]);
    Assert.That(RecordLayout.Problems(self, LayoutProfile.Lp64SysV), Is.EqualTo(new[] { "'N' contains itself" }));

    var a = new IrStructType("A");
    var b = new IrStructType("B", [Field("a", IrType.Struct(a))]);
    a.Define([Field("b", IrType.Struct(b))]);
    Assert.That(RecordLayout.Problems(a, LayoutProfile.Lp64SysV), Is.EqualTo(new[] { "'A' contains itself" }));
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Struct(self)), Is.False);
  }

  /// <summary>A field without bytes and a record without fields yet are problems; the same record twice within another is none.</summary>
  [Test]
  public void FieldsWithoutBytes_AreProblems() {
    var text = new IrStructType("T", [Field("name", IrType.String), Field("data", IrType.Array(IrType.U8))]);
    Assert.That(RecordLayout.Problems(text, LayoutProfile.Lp64SysV), Is.EqualTo(new[] {
      "the field 'name' has the type str, which has no byte layout", "the field 'data' has the type u8[], which has no byte layout" }));
    Assert.That(RecordLayout.Problems(new IrStructType("U"), LayoutProfile.Lp64SysV), Is.EqualTo(new[] { "'U' has no fields yet" }));

    var twice = new IrStructType("Twice", [Field("first", IrType.Struct(H(null))), Field("second", IrType.Struct(H(null)))]);
    Assert.That(RecordLayout.Compute(twice, LayoutProfile.Lp64SysV).Size, Is.EqualTo(24));
  }

  /// <summary>Sizes and alignments of the types that are not records, a pointer by the profile; a type without bytes throws.</summary>
  [Test]
  public void SizeAndAlignment_OfOtherTypes() {
    Assert.That((RecordLayout.SizeOf(IrType.Bool, LayoutProfile.Lp64SysV), RecordLayout.AlignOf(IrType.Bool, LayoutProfile.Dos16)), Is.EqualTo((1L, 1)));
    Assert.That((RecordLayout.SizeOf(IrType.U64, LayoutProfile.Dos16), RecordLayout.AlignOf(IrType.U64, LayoutProfile.Dos16)), Is.EqualTo((8L, 2)));
    Assert.That((RecordLayout.SizeOf(IrType.Pointer(IrType.U8), LayoutProfile.Ilp32SysV), RecordLayout.AlignOf(IrType.Pointer(IrType.U8), LayoutProfile.Lp64SysV)), Is.EqualTo((4L, 8)));
    Assert.That(RecordLayout.AlignOf(IrType.Struct(H(null)), LayoutProfile.Lp64SysV), Is.EqualTo(4));
    Assert.That(() => RecordLayout.SizeOf(IrType.String, LayoutProfile.Lp64SysV), Throws.ArgumentException.With.Message.StartsWith("the type str has no byte layout"));
    Assert.That((RecordLayout.HasBytes(IrType.F32), RecordLayout.HasBytes(IrType.Struct(H(null))), RecordLayout.HasBytes(IrType.Array(IrType.U8))), Is.EqualTo((true, true, false)));
  }

  /// <summary>Every query of the answer function, and an offset of a field that is not the record's.</summary>
  [Test]
  public void Answer_ToEveryQuery() {
    var record = H(null);
    Assert.That(RecordLayout.Answer(IrLayoutQuery.Size, IrType.Struct(record), null, LayoutProfile.Lp64SysV), Is.EqualTo(12));
    Assert.That(RecordLayout.Answer(IrLayoutQuery.Alignment, IrType.Struct(record), null, LayoutProfile.Lp64SysV), Is.EqualTo(4));
    Assert.That(RecordLayout.Answer(IrLayoutQuery.Offset, IrType.Struct(record), record.Fields[2], LayoutProfile.Lp64SysV), Is.EqualTo(8));
    Assert.That(() => RecordLayout.Compute(record, LayoutProfile.Lp64SysV).OffsetOf(Field("a", IrType.U8)), Throws.ArgumentException.With.Message.StartsWith("'a' is no field of H"));
    var first = RecordLayout.Compute(record, LayoutProfile.Lp64SysV);
    Assert.That(RecordLayout.Compute(record, LayoutProfile.Ilp32SysV with { }), Is.Not.SameAs(first));
    Assert.That(RecordLayout.Compute(record, LayoutProfile.Lp64SysV), Is.SameAs(first));
  }

  /// <summary>Which queries no profile changes: packed(1) and explicit records, the size of any number, the alignment of a byte.</summary>
  [Test]
  public void ProfileIndependence_OfQueries() {
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Offset, IrType.Struct(H(Packed(1)))), Is.True);
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Struct(H(Explicit(0, 0, 1, 5)))), Is.True);
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Offset, IrType.Struct(H(null))), Is.False);
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Offset, IrType.Struct(H(Packed(2)))), Is.False);
    var holding = new IrStructType("Outer", [Field("h", IrType.Struct(H(null)))]) { Layout = Packed(1) };
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Struct(holding)), Is.False);
    var holdingPacked = new IrStructType("Outer", [Field("h", IrType.Struct(H(Packed(1)))), Field("n", IrType.U32)]) { Layout = Packed(1) };
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Struct(holdingPacked)), Is.True);
    Assert.That(RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Struct(new IrStructType("U"))), Is.False);
    Assert.That((RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.F64), RecordLayout.IsProfileIndependent(IrLayoutQuery.Alignment, IrType.F64)), Is.EqualTo((true, false)));
    Assert.That((RecordLayout.IsProfileIndependent(IrLayoutQuery.Alignment, IrType.Bool), RecordLayout.IsProfileIndependent(IrLayoutQuery.Size, IrType.Pointer(IrType.U8))), Is.EqualTo((true, false)));
  }

  /// <summary>Test design 5: <c>layoutObservable</c> restricts optimizations, not the layout.</summary>
  [Test]
  public void LayoutObservable_DoesNotChangeTheLayout() {
    var observable = new IrStructType("H", H(null).Fields.Select(field => field with { }).ToList()) { LayoutObservable = true };
    var plain = H(null);
    Assert.That((observable.LayoutObservable, plain.LayoutObservable), Is.EqualTo((true, false)));
    var (left, right) = (RecordLayout.Compute(observable, LayoutProfile.Lp64SysV), RecordLayout.Compute(plain, LayoutProfile.Lp64SysV));
    Assert.That((left.Offsets, left.Size, left.Alignment, Padding(left)), Is.EqualTo((right.Offsets, right.Size, right.Alignment, Padding(right))));
  }

  /// <summary>The standard profiles, a profile of one's own, and what a profile refuses.</summary>
  [Test]
  public void Profiles_StandardAndOwn() {
    Assert.That(LayoutProfile.Standard.Select(profile => profile.ToString()), Is.EqualTo(new[] { "lp64-sysv", "ilp32-sysv", "dos16" }));
    Assert.That(LayoutProfile.Find("vax"), Is.Null);
    var big = LayoutProfile.Lp64SysV with { Name = "lp64-big", ByteOrder = IrByteOrder.Big };
    Assert.That((big.PointerSize, big.MaxAlignment, big.ByteOrder, big.AlignmentOf(16)), Is.EqualTo((8, 8, IrByteOrder.Big, 8)));
    Assert.That(new LayoutProfile("m", 2, 16, IrByteOrder.Little).MaxAlignment, Is.EqualTo(16));
    Assert.That(() => new LayoutProfile(" ", 8, 8, IrByteOrder.Little), Throws.ArgumentException);
    Assert.That(() => new LayoutProfile("p", 3, 8, IrByteOrder.Little), Throws.ArgumentException);
    Assert.That(() => new LayoutProfile("p", 8, 6, IrByteOrder.Little), Throws.ArgumentException);
    Assert.That(() => new LayoutProfile("p", 8, 0, IrByteOrder.Little), Throws.ArgumentException);
    Assert.That(() => new LayoutProfile("p", 8, 32, IrByteOrder.Little), Throws.ArgumentException);
    Assert.That(() => RecordLayout.Compute(H(null), null!), Throws.ArgumentNullException);
    Assert.That(() => RecordLayout.Problems(null!, LayoutProfile.Dos16), Throws.ArgumentNullException);
    Assert.That(() => RecordLayout.SizeOf(null!, LayoutProfile.Dos16), Throws.ArgumentNullException);
  }

}
