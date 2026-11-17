using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Interpreter;

/// <summary>
/// The host-independent values of a run (#119): bit patterns of every scalar type, their host forms and bytes, and the
/// built-in schedules and the flat memory model the options hand to evaluators.
/// </summary>
[TestFixture]
public sealed class IrValueTests {

  private static readonly IrEnumType _color = new("Color", IrType.U8, [new IrEnumMember("Red", 0)]);

  /// <summary>Each integer type keeps the pattern of its width, a negative number in two's complement, and gives back its number and host value.</summary>
  [TestCase("I8", -1, 255, (sbyte)-1)]
  [TestCase("U8", 255, 255, (byte)255)]
  [TestCase("I16", -2, 65534, (short)-2)]
  [TestCase("U16", 65535, 65535, (ushort)65535)]
  [TestCase("I32", int.MinValue, 2147483648L, int.MinValue)]
  [TestCase("U32", 4294967295L, 4294967295L, uint.MaxValue)]
  [TestCase("I64", -1L, -1L, -1L)]
  public void AnInteger_KeepsItsPattern(string kind, long number, long bits, object host) {
    var type = Type(kind);
    var value = IrValue.Integer(type, number);
    var pattern = bits < 0 ? new BigInteger(ulong.MaxValue) : new BigInteger(bits);
    Assert.Multiple(() => {
      Assert.That(value.Bits, Is.EqualTo(pattern));
      Assert.That(value.Number, Is.EqualTo(new BigInteger(number)));
      Assert.That(value.ToHost(), Is.EqualTo(host));
      Assert.That(IrValue.FromHost(type, host), Is.EqualTo(value));
      Assert.That(value.ToString(), Is.EqualTo(number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      Assert.That(value.IsScalar, Is.True);
      Assert.That(value.Reference, Is.Null);
    });
  }

  private static IrType Type(string kind) => kind switch {
    "I8" => IrType.I8,
    "U8" => IrType.U8,
    "I16" => IrType.I16,
    "U16" => IrType.U16,
    "I32" => IrType.I32,
    "U32" => IrType.U32,
    "I64" => IrType.I64,
    _ => IrType.U64
  };

  /// <summary>The largest unsigned 64-bit value and a char cross as their patterns.</summary>
  [Test]
  public void TheWidestAndTheCharacter_CrossAsPatterns() {
    Assert.That(IrValue.FromHost(IrType.U64, ulong.MaxValue).ToHost(), Is.EqualTo(ulong.MaxValue));
    Assert.That(IrValue.FromHost(IrType.U16, 'A'), Is.EqualTo(IrValue.Integer(IrType.U16, 65)));
    Assert.That(IrValue.FromHost(IrType.I32, (byte)7), Is.EqualTo(IrValue.Integer(IrType.I32, 7)));
  }

  /// <summary>Floating-point numbers keep their encoding exactly, the payload of a NaN and the sign of a zero too.</summary>
  [Test]
  public void AFloatingPointNumber_KeepsItsEncoding() {
    var nan = BitConverter.UInt32BitsToSingle(0x7FC00001);
    Assert.Multiple(() => {
      Assert.That(IrValue.F32(nan).Bits, Is.EqualTo(new BigInteger(0x7FC00001)));
      Assert.That(IrValue.F64(-0.0).Bits, Is.EqualTo(new BigInteger(0x8000000000000000UL)));
      Assert.That(IrValue.F64(-0.0), Is.Not.EqualTo(IrValue.F64(0.0)));
      Assert.That(IrValue.F64(0.1).ToHost(), Is.EqualTo(0.1));
      Assert.That(IrValue.F32(1.5f).ToHost(), Is.EqualTo(1.5f));
      Assert.That(IrValue.FromHost(IrType.F32, 1.5f), Is.EqualTo(IrValue.F32(1.5f)));
      Assert.That(IrValue.FromHost(IrType.F64, 1.5), Is.EqualTo(IrValue.F64(1.5)));
      Assert.That(IrValue.F64(0.1).ToString(), Is.EqualTo("0.1"));
      Assert.That(IrValue.F32(1.5f).ToString(), Is.EqualTo("1.5"));
    });
  }

  /// <summary>Truth values, big integers and enums.</summary>
  [Test]
  public void TheOtherScalars_CrossAsTheirPatterns() {
    var big = -(BigInteger.One << 100);
    var color = IrType.Enum(_color);
    Assert.Multiple(() => {
      Assert.That(IrValue.Bool(true).Bits, Is.EqualTo(BigInteger.One));
      Assert.That(IrValue.Bool(false).ToHost(), Is.EqualTo(false));
      Assert.That(IrValue.FromHost(IrType.Bool, true), Is.EqualTo(IrValue.Bool(true)));
      Assert.That((IrValue.Bool(true).ToString(), IrValue.Bool(false).ToString()), Is.EqualTo(("true", "false")));
      Assert.That(IrValue.Integer(IrType.BigInt, big).Bits, Is.EqualTo(big));
      Assert.That(IrValue.FromHost(IrType.BigInt, big).ToHost(), Is.EqualTo(big));
      Assert.That(IrValue.Of(IrType.BigInt, big).Number, Is.EqualTo(big));
      Assert.That(IrValue.Integer(color, 300).Bits, Is.EqualTo(new BigInteger(44)));
      Assert.That(IrValue.Integer(color, 3).ToHost(), Is.EqualTo((byte)3));
      Assert.That(IrValue.IsScalarType(color), Is.True);
    });
  }

  /// <summary>A pattern must fit its type; a type that is no scalar crosses as a reference only.</summary>
  [Test]
  public void APattern_MustFitItsType() {
    Assert.Multiple(() => {
      Assert.That(IrValue.Of(IrType.U8, 255).Number, Is.EqualTo(new BigInteger(255)));
      Assert.That(IrValue.Of(IrType.I8, 128).Number, Is.EqualTo(new BigInteger(-128)));
      Assert.Throws<ArgumentOutOfRangeException>(() => IrValue.Of(IrType.U8, 256));
      Assert.Throws<ArgumentOutOfRangeException>(() => IrValue.Of(IrType.U8, -1));
      Assert.Throws<ArgumentOutOfRangeException>(() => IrValue.Of(IrType.Bool, 2));
      Assert.Throws<ArgumentException>(() => IrValue.Of(IrType.String, 0));
      Assert.Throws<ArgumentException>(() => IrValue.Integer(IrType.F64, 0));
      Assert.Throws<ArgumentNullException>(() => IrValue.Integer(null!, 0));
      Assert.Throws<ArgumentException>(() => IrValue.Referring(IrType.I32, 1));
      Assert.Throws<ArgumentNullException>(() => IrValue.Referring(null!, 1));
      Assert.Throws<ArgumentNullException>(() => IrValue.FromHost(null!, 1));
      Assert.Throws<ArgumentNullException>(() => IrValue.IsScalarType(null!));
      Assert.Throws<ArgumentException>(() => IrValue.FromHost(IrType.I32, "1"));
      Assert.Throws<ArgumentException>(() => IrValue.FromHost(IrType.I32, null));
      Assert.Throws<ArgumentException>(() => IrValue.FromHost(IrType.Bool, 1));
      Assert.Throws<ArgumentException>(() => IrValue.FromHost(IrType.F64, 1));
    });
  }

  /// <summary>A reference crosses as it is: its pattern is zero, its text names its type.</summary>
  [Test]
  public void AReference_CrossesAsItIs() {
    var array = new[] { 1 };
    var value = IrValue.FromHost(IrType.Array(IrType.I32), array);
    Assert.Multiple(() => {
      Assert.That(value.Reference, Is.SameAs(array));
      Assert.That(value.ToHost(), Is.SameAs(array));
      Assert.That(value.Bits, Is.EqualTo(BigInteger.Zero));
      Assert.That(value.IsScalar, Is.False);
      Assert.That(value.ToString(), Is.EqualTo("<i32[]>"));
      Assert.That(IrValue.Referring(IrType.String, null).ToString(), Is.EqualTo("null str"));
    });
  }

  /// <summary>The bytes of a value in either order, and back.</summary>
  [Test]
  public void TheBytes_FollowTheByteOrder() {
    var value = IrValue.Integer(IrType.U32, 0x11223344);
    Assert.Multiple(() => {
      Assert.That(value.ToBytes(ByteOrder.Little), Is.EqualTo(new byte[] { 0x44, 0x33, 0x22, 0x11 }));
      Assert.That(value.ToBytes(ByteOrder.Big), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
      Assert.That(IrValue.FromBytes(IrType.U32, [0x11, 0x22, 0x33, 0x44], ByteOrder.Big), Is.EqualTo(value));
      Assert.That(IrValue.FromBytes(IrType.U32, [0x44, 0x33, 0x22, 0x11], ByteOrder.Little), Is.EqualTo(value));
      Assert.That(IrValue.Integer(IrType.U16, 0).ToBytes(ByteOrder.Big), Is.EqualTo(new byte[] { 0, 0 }));
      Assert.That(IrValue.Bool(true).ToBytes(ByteOrder.Little), Is.EqualTo(new byte[] { 1 }));
      Assert.That(IrValue.FromBytes(IrType.Bool, [1], ByteOrder.Big), Is.EqualTo(IrValue.Bool(true)));
      Assert.That(IrValue.FromBytes(IrType.F64, IrValue.F64(-2.5).ToBytes(ByteOrder.Big), ByteOrder.Big), Is.EqualTo(IrValue.F64(-2.5)));
      Assert.Throws<InvalidOperationException>(() => IrValue.Integer(IrType.BigInt, 1).ToBytes(ByteOrder.Little));
      Assert.Throws<InvalidOperationException>(() => IrValue.Referring(IrType.String, "").ToBytes(ByteOrder.Little));
      Assert.Throws<ArgumentException>(() => IrValue.FromBytes(IrType.U32, [1, 2], ByteOrder.Little));
      Assert.Throws<ArgumentException>(() => IrValue.FromBytes(IrType.BigInt, [1], ByteOrder.Little));
      Assert.Throws<ArgumentNullException>(() => IrValue.FromBytes(null!, [1], ByteOrder.Little));
      Assert.Throws<ArgumentOutOfRangeException>(() => IrValue.FromBytes(IrType.Bool, [2], ByteOrder.Little));
    });
  }

  /// <summary>The ascending and descending schedules, at 0, 1 and several iterations.</summary>
  [Test]
  public void TheFixedSchedules_OrderAndChunk() {
    Assert.Multiple(() => {
      Assert.That(Schedules.Ascending.Order(4), Is.EqualTo(new long[] { 0, 1, 2, 3 }));
      Assert.That(Schedules.Ascending.Chunks(4), Is.EqualTo(new[] { (0L, 4L) }));
      Assert.That(Schedules.Ascending.Chunks(0), Is.Empty);
      Assert.That(Schedules.Descending.Order(3), Is.EqualTo(new long[] { 2, 1, 0 }));
      Assert.That(Schedules.Descending.Chunks(3), Is.EqualTo(new[] { (2L, 1L), (1L, 1L), (0L, 1L) }));
      Assert.That(Schedules.Descending.Order(0), Is.Empty);
      Assert.Throws<ArgumentOutOfRangeException>(() => Schedules.Ascending.Order(-1));
      Assert.Throws<ArgumentOutOfRangeException>(() => Schedules.Descending.Chunks(-1));
    });
  }

  /// <summary>A seeded schedule is a permutation and a cover, the same for the same seed on every run, another for another seed.</summary>
  [TestCase(0UL, 0)]
  [TestCase(1UL, 1)]
  [TestCase(42UL, 17)]
  [TestCase(ulong.MaxValue, 100)]
  public void ASeededSchedule_IsADeterministicPermutation(ulong seed, long count) {
    var schedule = Schedules.Seeded(seed);
    var order = schedule.Order(count);
    var chunks = schedule.Chunks(count);
    Assert.Multiple(() => {
      Assert.That(order.Order(), Is.EqualTo(Enumerable.Range(0, (int)count).Select(index => (long)index)));
      Assert.That(Schedules.Seeded(seed).Order(count), Is.EqualTo(order));
      Assert.That(Schedules.Seeded(seed).Chunks(count), Is.EqualTo(chunks));
      Assert.That(chunks.SelectMany(chunk => Enumerable.Range((int)chunk.Start, (int)chunk.Length)).Order(), Is.EqualTo(Enumerable.Range(0, (int)count)));
      Assert.That(chunks.All(chunk => chunk.Length > 0), Is.True);
    });
  }

  /// <summary>The seeded schedule draws a different permutation from another seed, and refuses a negative count.</summary>
  [Test]
  public void TwoSeeds_GiveTwoSchedules() {
    Assert.That(Schedules.Seeded(1).Order(20), Is.Not.EqualTo(Schedules.Seeded(2).Order(20)));
    Assert.That(Schedules.Seeded(1).Order(20), Is.Not.EqualTo(Schedules.Ascending.Order(20)));
    Assert.Throws<ArgumentOutOfRangeException>(() => Schedules.Seeded(1).Order(-1));
    Assert.Throws<ArgumentOutOfRangeException>(() => Schedules.Seeded(1).Chunks(-1));
  }

  /// <summary>The flat model maps an allocation and an offset to an address and back, and leaves the first 4 GiB unmapped.</summary>
  [Test]
  public void TheFlatModel_MapsBothWays() {
    var flat = FlatMemoryModel.Instance;
    var last = (long.MaxValue / FlatMemoryModel.Stride) - 1;
    Assert.Multiple(() => {
      Assert.That(flat.Address(0, 0), Is.EqualTo(FlatMemoryModel.Stride));
      Assert.That(flat.Locate(flat.Address(3, 17)), Is.EqualTo((3L, 17L)));
      Assert.That(flat.Locate(flat.Address(last, FlatMemoryModel.Stride - 1)), Is.EqualTo((last, FlatMemoryModel.Stride - 1)));
      Assert.That(flat.Locate(0), Is.Null);
      Assert.That(flat.Locate(FlatMemoryModel.Stride - 1), Is.Null);
      Assert.Throws<ArgumentOutOfRangeException>(() => flat.Address(-1, 0));
      Assert.Throws<ArgumentOutOfRangeException>(() => flat.Address(last + 1, 0));
      Assert.Throws<ArgumentOutOfRangeException>(() => flat.Address(0, -1));
      Assert.Throws<ArgumentOutOfRangeException>(() => flat.Address(0, FlatMemoryModel.Stride));
    });
  }

}
