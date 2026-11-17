using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Integer;

/// <summary>
/// Byte-ordered loads and stores through a pointer to bytes (#113): <see cref="IrIntrinsicOp.PointerLoadLittle"/>,
/// <see cref="IrIntrinsicOp.PointerLoadBig"/>, <see cref="IrIntrinsicOp.PointerStoreLittle"/> and
/// <see cref="IrIntrinsicOp.PointerStoreBig"/> for every width that is a whole number of bytes, the
/// <see cref="IrField.ByteOrder"/> of record fields, and the profile's byte order for plain loads. Every case runs
/// in the interpreter and, where the lowering expands it, again after <see cref="IrLayoutLowering"/>.
/// </summary>
[TestFixture]
public sealed class OrderedAccessTests {

  private static readonly IrType _bytes = IrType.Array(IrType.U8);
  private static readonly IrType _bytePointer = IrType.Pointer(IrType.U8);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrIntrinsic Op(IrIntrinsicOp op, IrType type, params IrExpr[] arguments) => new(op, type, arguments);

  private static IrExpr At(IrExpr pointer, long offset) => offset == 0 ? pointer : Op(IrIntrinsicOp.PointerAdd, _bytePointer, pointer, Int(offset));

  private static IReadOnlyList<string> Verified(IrModule module) => [.. IrVerifier.Verify(module).Select(diagnostic => diagnostic.Message)];

  /// <summary><c>F(bytes)</c>: pins the bytes into <c>p</c>, runs the statements made of <c>p</c>, and returns the value made of it.</summary>
  private static IrModule Pinned(IrType returns, Func<IrExpr, IrExpr?> result, Func<IrExpr, IrStmt[]>? before = null, IrTypeDeclaration[]? types = null) {
    var array = new IrLocal("bytes", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var p = new IrLocalRef(pointer);
    IrStmt[] body = [
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, _bytePointer, new IrLocalRef(array))),
      .. before?.Invoke(p) ?? [],
      new IrReturn(result(p))
    ];
    return new IrModule("M", [new IrFunction("F", true, returns, [array], [pointer], new IrBlock(body))]) { Types = types ?? [] };
  }

  /// <summary>The value the module returns for the bytes, interpreted as it is and after lowering, which must agree.</summary>
  private static object? Run(IrModule module, byte[] bytes, LayoutProfile? profile = null) {
    Assert.That(Verified(module), Is.Empty);
    var lowered = IrLayoutLowering.Lower(module, profile ?? LayoutProfile.Lp64SysV);
    Assert.That(Verified(lowered), Is.Empty);
    var copy = (byte[])bytes.Clone();
    var direct = profile is null ? IrInterpreter.Call(module, "F", [bytes]) : IrInterpreter.Call(module, "F", [bytes], profile);
    var again = IrInterpreter.Call(lowered, "F", [copy]);
    Assert.That((again, copy), Is.EqualTo((direct, bytes)), "the lowered module agrees");
    return direct;
  }

  private static IrTrap Trapped(IrModule module, byte[] bytes) {
    Assert.That(Verified(module), Is.Empty);
    var lowered = IrLayoutLowering.Lower(module, LayoutProfile.Lp64SysV);
    var copy = (byte[])bytes.Clone();
    var trap = Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", [bytes]))!.Trap;
    Assert.That(Assert.Throws<IrTrapException>(() => IrInterpreter.Call(lowered, "F", [copy]))!.Trap, Is.EqualTo(trap));
    Assert.That(copy, Is.EqualTo(bytes), "the lowered module writes as little before the trap");
    return trap;
  }

  private static long Number(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

  private static IrType Type(string name) => name switch {
    "u8" => IrType.U8,
    "i8" => IrType.I8,
    "u16" => IrType.U16,
    "i16" => IrType.I16,
    "u32" => IrType.U32,
    "i32" => IrType.I32,
    "u64" => IrType.U64,
    "i64" => IrType.I64,
    "f32" => IrType.F32,
    _ => IrType.F64
  };

  /// <summary>Scenario "The same bytes read in both orders", test design 1.</summary>
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "u32", 0x12345678L)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "u32", 0x78563412L)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "u16", 0x5678L)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "u16", 0x7856L)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "i16", 22136L)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "u8", 0x78L)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "i8", 0x78L)]
  public void Load_ReadsTheStatedOrder(IrIntrinsicOp op, string type, long expected)
    => Assert.That(Number(Run(Pinned(Type(type), p => Op(op, Type(type), p)), [0x78, 0x56, 0x34, 0x12])), Is.EqualTo(expected));

  /// <summary>Scenario "Stores write the stated order at an odd address", test design 7.</summary>
  [TestCase(IrIntrinsicOp.PointerStoreBig, 1, new byte[] { 0x00, 0xAB, 0xCD, 0x00, 0x00 })]
  [TestCase(IrIntrinsicOp.PointerStoreLittle, 3, new byte[] { 0x00, 0x00, 0x00, 0xCD, 0xAB })]
  public void Store_WritesTheStatedOrder(IrIntrinsicOp op, int offset, byte[] expected) {
    var bytes = new byte[5];
    var module = Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(op, IrType.Void, At(p, offset), new IrConst(IrType.U16, 0xABCD)))]);
    Run(module, bytes);
    Assert.That(bytes, Is.EqualTo(expected));
  }

  /// <summary>Scenario "Odd byte counts work": three bytes as an unsigned big integer, in both orders.</summary>
  [TestCase(IrIntrinsicOp.PointerLoadLittle, 0x030201)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, 0x010203)]
  public void Wide_ReadsAnyNumberOfBytes(IrIntrinsicOp op, int expected)
    => Assert.That(Run(Pinned(IrType.BigInt, p => Op(op, IrType.BigInt, p, Int(3))), [1, 2, 3]), Is.EqualTo(new BigInteger(expected)));

  /// <summary>Scenario "A partial access fails as a whole", test design 6: on the end, one beyond it, and a store that writes nothing.</summary>
  [Test]
  public void PartialAccess_TrapsWhole() {
    Assert.That(Trapped(Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.U32, p)), [1, 2, 3]), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Trapped(Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.U32, At(p, 3))), [1, 2, 3, 4, 5, 6]), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Number(Run(Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.U32, At(p, 2))), [1, 2, 3, 4, 5, 6])), Is.EqualTo(0x03040506));
    var storing = Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreLittle, IrType.Void, At(p, 1), new IrConst(IrType.U32, 0x01020304)))]);
    Assert.That(Trapped(storing, [9, 9, 9, 9]), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Trapped(Pinned(IrType.BigInt, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.BigInt, p, Int(-1))), [1]), Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Trapped(Pinned(IrType.BigInt, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.BigInt, At(p, -1), Int(1))), [1]), Is.EqualTo(IrTrap.IndexOutOfRange));
  }

  /// <summary>A null pointer traps as every load through one does.</summary>
  [Test]
  public void NullPointer_Traps() {
    var module = new IrModule("M", [new IrFunction("F", true, IrType.U16, [], [], new IrBlock([new IrReturn(Op(IrIntrinsicOp.PointerLoadLittle, IrType.U16, new IrNull(_bytePointer)))]))]);
    Assert.That(Verified(module), Is.Empty);
    Assert.That(Assert.Throws<IrTrapException>(() => IrInterpreter.Call(module, "F", []))!.Trap, Is.EqualTo(IrTrap.NullReference));
  }

  /// <summary>Scenario "Cipher's packers agree": StoreBE of 0x12345678 is OpCodes.Unpack32BE, LoadLE of it OpCodes.Pack32LE.</summary>
  [Test]
  public void CipherPackers_Agree() {
    var bytes = new byte[4];
    var module = Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.U32, p),
      p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreBig, IrType.Void, p, new IrConst(IrType.U32, 0x12345678)))]);
    Assert.That(Number(Run(module, bytes)), Is.EqualTo(0x78563412));
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x12, 0x34, 0x56, 0x78 }));
  }

  /// <summary>Test design 2 and 5: random bytes of 1, 2, 3, 4, 8, 16 and 512 bytes in both orders against the positional sum.</summary>
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(8)]
  [TestCase(16)]
  [TestCase(512)]
  public void Wide_AgreesWithThePositionalSum(int width) {
    var random = new Random(164);
    var little = Pinned(IrType.BigInt, p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.BigInt, p, Int(width)));
    var big = Pinned(IrType.BigInt, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.BigInt, p, Int(width)));
    var storeLittle = Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreLittle, IrType.Void, p, Int(width), new IrBigConst(BigInteger.One << (8 * width - 1))))]);
    for (var vector = 0; vector < 25; ++vector) {
      var bytes = new byte[width];
      random.NextBytes(bytes);
      BigInteger lowFirst = 0, highFirst = 0;
      for (var index = width - 1; index >= 0; --index)
        lowFirst = lowFirst * 256 + bytes[index];
      foreach (var value in bytes)
        highFirst = highFirst * 256 + value;

      Assert.That((IrInterpreter.Call(little, "F", [bytes]), IrInterpreter.Call(big, "F", [bytes])), Is.EqualTo(((object)lowFirst, (object)highFirst)));
    }

    var written = new byte[width];
    IrInterpreter.Call(storeLittle, "F", [written]);
    Assert.That(written[^1], Is.EqualTo(0x80));
  }

  /// <summary>Test design 3: signed integers reinterpret the unsigned bytes, floating-point numbers keep their bits exactly.</summary>
  [TestCase("i64", new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
  [TestCase("i32", new byte[] { 0x00, 0x00, 0x00, 0x80 })]
  [TestCase("f32", new byte[] { 0x01, 0x00, 0xC0, 0x7F })]
  [TestCase("f64", new byte[] { 0x18, 0x2D, 0x44, 0x54, 0xFB, 0x21, 0x09, 0x40 })]
  [TestCase("u64", new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x88 })]
  public void SignedAndFloat_KeepTheirBits(string name, byte[] littleEndian) {
    var type = Type(name);
    var loaded = Run(Pinned(type, p => Op(IrIntrinsicOp.PointerLoadLittle, type, p)), littleEndian);
    object expected = name switch {
      "i64" => BitConverter.ToInt64(littleEndian),
      "i32" => (long)BitConverter.ToInt32(littleEndian),
      "f32" => BitConverter.ToSingle(littleEndian),
      "f64" => BitConverter.ToDouble(littleEndian),
      _ => BitConverter.ToUInt64(littleEndian)
    };
    Assert.That(loaded, Is.EqualTo(expected));
    if (loaded is float single)
      Assert.That(BitConverter.SingleToInt32Bits(single), Is.EqualTo(BitConverter.ToInt32(littleEndian)), "the NaN payload stays");

    // Stored big-endian, the bytes come out reversed.
    var local = new IrLocal("v", type);
    var array = new IrLocal("bytes", _bytes);
    var pointer = new IrLocal("p", _bytePointer);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.I32, [array], [pointer, local], new IrBlock([
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, _bytePointer, new IrLocalRef(array))),
      new IrDeclare(local, Op(IrIntrinsicOp.PointerLoadLittle, type, new IrLocalRef(pointer))),
      new IrExprStmt(Op(IrIntrinsicOp.PointerStoreBig, IrType.Void, new IrLocalRef(pointer), new IrLocalRef(local))),
      new IrReturn(Int(0))]))]);
    var buffer = (byte[])littleEndian.Clone();
    Run(module, buffer);
    Assert.That(buffer, Is.EqualTo(littleEndian.Reverse().ToArray()));
  }

  /// <summary>Test design 10: a plain load through a cast pointer reads the bytes in the profile's order, little without one.</summary>
  [TestCase(null, 0x12345678L)]
  [TestCase(IrByteOrder.Little, 0x12345678L)]
  [TestCase(IrByteOrder.Big, 0x78563412L)]
  public void PlainLoad_UsesTheProfilesOrder(IrByteOrder? order, long expected) {
    var module = Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoad, IrType.U32, Op(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.U32), p)));
    Assert.That(Verified(module), Is.Empty);
    byte[] bytes = [0x78, 0x56, 0x34, 0x12];
    var result = order is { } stated ? IrInterpreter.Call(module, "F", [bytes], LayoutProfile.Lp64SysV with { ByteOrder = stated }) : IrInterpreter.Call(module, "F", [bytes]);
    Assert.That(Number(result), Is.EqualTo(expected));
  }

  /// <summary>A plain store through a cast pointer writes in the profile's order, and a 32-bit number read as bytes shows it.</summary>
  [Test]
  public void PlainStore_UsesTheProfilesOrder() {
    var module = Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoad, IrType.U32, Op(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.U32), p)),
      p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, Op(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.U16), At(p, 1)), new IrConst(IrType.U16, 0xABCD)))]);
    var bytes = new byte[4];
    var big = LayoutProfile.Lp64SysV with { ByteOrder = IrByteOrder.Big };
    Assert.That(Number(IrInterpreter.Call(module, "F", [bytes], big)), Is.EqualTo(0x00ABCD00));
    Assert.That(bytes, Is.EqualTo(new byte[] { 0, 0xAB, 0xCD, 0 }));

    // The bytes of 32-bit numbers, read as bytes, lie highest first in big-endian memory.
    var words = new IrLocal("words", IrType.Array(IrType.U32));
    var pointer = new IrLocal("p", IrType.Pointer(IrType.U32));
    var reading = new IrModule("M", [new IrFunction("F", true, IrType.U8, [words], [pointer], new IrBlock([
      new IrDeclare(pointer, Op(IrIntrinsicOp.Pin, IrType.Pointer(IrType.U32), new IrLocalRef(words))),
      new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, Op(IrIntrinsicOp.PointerCast, _bytePointer, new IrLocalRef(pointer)), new IrConst(IrType.U8, 0x7F))),
      new IrReturn(Op(IrIntrinsicOp.PointerLoad, IrType.U8, Op(IrIntrinsicOp.PointerAdd, _bytePointer, Op(IrIntrinsicOp.PointerCast, _bytePointer, new IrLocalRef(pointer)), Int(3))))]))]);
    var memory = new uint[] { 0x11223344 };
    Assert.That(Number(IrInterpreter.Call(reading, "F", [memory], big)), Is.EqualTo(0x44));
    Assert.That(memory[0], Is.EqualTo(0x7F223344u));
    memory[0] = 0x11223344;
    Assert.That(Number(IrInterpreter.Call(reading, "F", [memory])), Is.EqualTo(0x11));
    Assert.That(memory[0], Is.EqualTo(0x1122337Fu));
  }

  /// <summary>Test design 4: a field of a stated order read at its address equals the ordered load at that offset.</summary>
  [Test]
  public void OrderedField_ReadsAsTheOrderedLoad() {
    var magic = new IrField("magic", IrType.U32, false) { ByteOrder = IrByteOrder.Big };
    var size = new IrField("size", IrType.U16, false) { ByteOrder = IrByteOrder.Little };
    var header = new IrStructType("Hdr", [magic, size]) { Layout = new IrStructLayout(IrLayoutKind.Sequential, 1, 0, []) };
    byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x00];
    var viaField = Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.U32, new IrFieldAddress(p, header, magic)), types: [header]);
    var viaOffset = Pinned(IrType.U32, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.U32, p));
    Assert.That(Run(viaField, bytes), Is.EqualTo(Run(viaOffset, bytes)));
    Assert.That(Number(Run(viaField, bytes)), Is.EqualTo(0x89504E47));
    Assert.That(Number(Run(Pinned(IrType.U16, p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.U16, new IrFieldAddress(p, header, size)), types: [header]), bytes)), Is.EqualTo(13));
  }

  /// <summary>The rule of ordered fields: the wrong order, the wrong type and a cast of the address are refused.</summary>
  [Test]
  public void OrderedField_IsAccessedInItsOrder() {
    var magic = new IrField("magic", IrType.U32, false) { ByteOrder = IrByteOrder.Big };
    var plain = new IrField("plain", IrType.U32, false);
    var header = new IrStructType("Hdr", [magic, plain]) { Layout = new IrStructLayout(IrLayoutKind.Sequential, 1, 0, []) };
    IReadOnlyList<string> Problems(Func<IrExpr, IrExpr> read, IrType type) => Verified(Pinned(type, read, types: [header]));
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.U32, new IrFieldAddress(p, header, magic)), IrType.U32),
      Has.Some.Contains("'magic', a u32 stored Big-endian, is accessed as a u32 by PointerLoadLittle [ir.verifier.records.field-byte-order]"));
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoadBig, IrType.I32, new IrFieldAddress(p, header, magic)), IrType.I32), Has.Some.Contains("accessed as a i32"));
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoad, IrType.U32, Op(IrIntrinsicOp.PointerCast, IrType.Pointer(IrType.U32), new IrFieldAddress(p, header, magic))), IrType.U32),
      Has.Some.Contains("the address of 'magic', stored Big-endian, is cast to u32*"));
    Assert.That(Verified(Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreLittle, IrType.Void, new IrFieldAddress(p, header, magic), new IrConst(IrType.U32, 1)))], [header])),
      Has.Some.Contains("by PointerStoreLittle"));
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.U32, Op(IrIntrinsicOp.PointerCast, _bytePointer, new IrFieldAddress(p, header, plain))), IrType.U32), Is.Empty);
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoad, IrType.U32, new IrFieldAddress(p, header, plain)), IrType.U32), Is.Empty);
    Assert.That(Problems(p => Op(IrIntrinsicOp.PointerLoad, IrType.U8, new IrFieldAddress(p, header, magic)), IrType.U8), Is.Empty);
    var rule = new ByteOrderedFieldRule();
    Assert.That((rule.Id, rule.Family, rule.Description), Is.EqualTo(("ir.verifier.records.field-byte-order", "records", "a field of a stated byte order is read and written in that order and as its type")));
  }

  /// <summary>Test design 8 and 9 and the boundaries of width: what the typing rules take and refuse.</summary>
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "u32", "u8*", true)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "f64", "u8*", true)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "u8", "u8*", true)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "bigint", "u8* i32", true)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "bigint", "u8* i64", false)]
  [TestCase(IrIntrinsicOp.PointerLoadBig, "u32", "u8* i32", false)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "u32", "u32*", false)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "bool", "u8*", false)]
  [TestCase(IrIntrinsicOp.PointerLoadLittle, "u32", "", false)]
  [TestCase(IrIntrinsicOp.PointerStoreLittle, "void", "u8* u16", true)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "void", "u8* i32 bigint", true)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "void", "u8* bigint", false)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "u16", "u8* u16", false)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "void", "u8* bool", false)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "void", "u8* i32 i32", false)]
  [TestCase(IrIntrinsicOp.PointerStoreBig, "void", "u16* u16", false)]
  public void Operation_TakesItsTypes(IrIntrinsicOp op, string result, string arguments, bool accepted) {
    IrType Spelled(string name) => name switch {
      "u8*" => _bytePointer,
      "u32*" => IrType.Pointer(IrType.U32),
      "u16*" => IrType.Pointer(IrType.U16),
      "bigint" => IrType.BigInt,
      "bool" => IrType.Bool,
      "void" => IrType.Void,
      _ => Type(name)
    };
    Assert.That(IrOperators.Accepts(op, Spelled(result), [.. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Spelled)]), Is.EqualTo(accepted));
  }

  /// <summary>Scenario "A width that is not a whole number of bytes is rejected": a truth value, an enum and a pointer to other numbers are refused by the verifier.</summary>
  [Test]
  public void Verifier_RefusesWhatIsNoWholeNumberOfBytes() {
    var colour = new IrEnumType("Colour", IrType.U16, [new IrEnumMember("Red", 0)], false);
    Assert.That(Verified(Pinned(IrType.Bool, p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.Bool, p))), Has.Some.Contains("PointerLoadLittle with the type bool"));
    Assert.That(Verified(Pinned(IrType.Enum(colour), p => Op(IrIntrinsicOp.PointerLoadLittle, IrType.Enum(colour), p), types: [colour])), Has.Some.Contains("PointerLoadLittle"));
    Assert.That(IrOrderedAccess.IsOrderedType(IrType.Enum(colour)), Is.False);
    Assert.That((IrOrderedAccess.Load(IrByteOrder.Big), IrOrderedAccess.Store(IrByteOrder.Little)), Is.EqualTo((IrIntrinsicOp.PointerLoadBig, IrIntrinsicOp.PointerStoreLittle)));
    Assert.That((IrOrderedAccess.Load(IrByteOrder.Little), IrOrderedAccess.Store(IrByteOrder.Big)), Is.EqualTo((IrIntrinsicOp.PointerLoadLittle, IrIntrinsicOp.PointerStoreBig)));
  }

  /// <summary>The lowering leaves an access whose address or value it cannot repeat, and every wide access, as it is.</summary>
  [Test]
  public void Lowering_LeavesWhatItCannotRepeat() {
    var x = new IrLocal("x", IrType.U8);
    var addressed = new IrModule("M", [new IrFunction("F", true, IrType.U8, [], [x], new IrBlock([
      new IrDeclare(x, new IrConst(IrType.U8, 5)),
      new IrReturn(Op(IrIntrinsicOp.PointerLoadBig, IrType.U8, Op(IrIntrinsicOp.AddressOf, _bytePointer, new IrLocalRef(x))))]))]);
    Assert.That(Verified(addressed), Is.Empty);
    Assert.That(IrLayoutLowering.Lower(addressed, LayoutProfile.Lp64SysV), Is.SameAs(addressed));
    Assert.That(Number(IrInterpreter.Call(addressed, "F", [])), Is.EqualTo(5));

    var wide = Pinned(IrType.BigInt, p => Op(IrIntrinsicOp.PointerLoadBig, IrType.BigInt, p, Int(2)));
    Assert.That(IrLayoutLowering.Lower(wide, LayoutProfile.Lp64SysV), Is.SameAs(wide));

    var called = new IrModule("M", [
      new IrFunction("G", true, IrType.U16, [], [], new IrBlock([new IrReturn(new IrConst(IrType.U16, 7))])),
      Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreBig, IrType.Void, p, new IrCall("G", IrType.U16, [])))]).Functions[0]]);
    Assert.That(IrLayoutLowering.Lower(called, LayoutProfile.Lp64SysV), Is.SameAs(called));
    var notOrdered = Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStore, IrType.Void, p, new IrConst(IrType.U8, 1)))]);
    Assert.That(IrLayoutLowering.Lower(notOrdered, LayoutProfile.Lp64SysV), Is.SameAs(notOrdered));
    var wideStore = Pinned(IrType.I32, _ => Int(0), p => [new IrExprStmt(Op(IrIntrinsicOp.PointerStoreBig, IrType.Void, p, Int(2), new IrBigConst(5)))]);
    Assert.That(IrLayoutLowering.Lower(wideStore, LayoutProfile.Lp64SysV), Is.SameAs(wideStore));
  }

  /// <summary>The library of wide loads and stores: no bytes, a negative number and the bounds.</summary>
  [Test]
  public void WideLibrary_Boundaries() {
    Assert.That(IntegerOperations.LoadWide([1, 2], 2, 0, true), Is.EqualTo(BigInteger.Zero));
    var bytes = new byte[3];
    IntegerOperations.StoreWide(bytes, 0, 3, -2, false);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFE }));
    IntegerOperations.StoreWide(bytes, 1, 2, 0x10203, true);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0xFF, 0x03, 0x02 }));
    Assert.That(Assert.Throws<IrTrapException>(() => IntegerOperations.LoadWide(bytes, 1, 3, true))!.Trap, Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Assert.Throws<IrTrapException>(() => IntegerOperations.StoreWide(bytes, -1, 1, 0, true))!.Trap, Is.EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(Assert.Throws<IrTrapException>(() => IntegerOperations.LoadWide(bytes, 0, -1, true))!.Trap, Is.EqualTo(IrTrap.IndexOutOfRange));
  }

}
