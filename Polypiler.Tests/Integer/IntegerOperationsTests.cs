using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The integer and bit operations of the IR: their signatures, the rules the verifier applies to
/// them and their meaning, which the interpreter takes from <see cref="IntegerOperations"/> and which
/// has to be that of the bit-by-bit reference of the tests on every boundary and on random operands.
/// </summary>
[TestFixture]
public sealed class IntegerOperationsTests {

  private static readonly IrModule _module = IntegerCases.Module(IntegerCases.All);

  private static IEnumerable<TestCaseData> Cases()
    => IntegerCases.All.Select(@case => new TestCaseData(@case.Function));

  private static string Interpreted(string function, params object[] arguments)
    => PrototypeFixture.InterpretedOutcome(_module, new PrototypeCall(function, arguments));

  /// <summary>Test design 13 for the IR: the operations and the widths of each are those the issue lists, no more and no fewer.</summary>
  [Test]
  public void Signatures_AreThoseOfTheIssue() {
    var expected = IntegerCases.All.GroupBy(@case => @case.Op).ToDictionary(group => group.Key, group => group.Select(@case => (@case.Result, string.Join(",", @case.Types.Select(type => type.ToString())))).ToList());
    Assert.That(IntegerOperations.Operations, Is.EquivalentTo(expected.Keys));
    foreach (var (op, signatures) in expected)
      Assert.That(IntegerOperations.Signatures(op).Select(signature => (signature.Result, string.Join(",", signature.Arguments.Select(type => type.ToString())))), Is.EquivalentTo(signatures), op.ToString());

    Assert.That(() => IntegerOperations.Signatures(IrIntrinsicOp.Sqrt), Throws.ArgumentException.With.Message.Contains("Sqrt"));
  }

  /// <summary>Every case is well-typed IR.</summary>
  [Test]
  public void EveryCase_IsWellTyped() => Assert.That(IrVerifier.Verify(_module), Is.Empty);

  /// <summary>
  /// Test design 11: per operation and signature, one argument too few, a first argument of a type
  /// the operation does not take, and a node of the wrong type are each refused by the verifier.
  /// </summary>
  [TestCaseSource(nameof(Cases))]
  public void WrongOperands_AreRefusedByTheVerifier(string function) {
    var @case = IntegerCases.All.Single(candidate => candidate.Function == function);
    IrFunction Built(IrType result, IrType[] types) => IntegerCases.Function(@case with { Result = result, Types = types });
    var wrongFirst = @case.Types[0].IsArray ? IrType.Array(IrType.I32) : IrType.Bool;
    IrFunction[] broken = [
      Built(@case.Result, @case.Types[..^1]),
      Built(@case.Result, [wrongFirst, .. @case.Types[1..]]),
      Built(@case.Result == IrType.F64 ? IrType.Bool : IrType.F64, @case.Types)
    ];
    foreach (var wrong in broken)
      Assert.That(IrVerifier.Verify(new IrModule("Broken", [wrong])).Select(diagnostic => diagnostic.Code), Has.Member(IrVerifier.Code), wrong.ToString());
  }

  /// <summary>A shared operation of floating point is still the floating-point one, and an integer operation is not one of floating point.</summary>
  [Test]
  public void FloatingPoint_KeepsItsOwnRules() {
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Abs, IrType.F64, [IrType.F64]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Min, IrType.F32, [IrType.F32, IrType.F32]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Sign, IrType.I32, [IrType.F32]), Is.True);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Abs, IrType.Bool, [IrType.Bool]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Clamp, IrType.F64, [IrType.F64, IrType.F64, IrType.F64]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.PopCount, IrType.I32, [IrType.F64]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Min, IrType.I32, [IrType.I32, IrType.U32]), Is.False);
    Assert.That(IrOperators.Accepts(IrIntrinsicOp.Abs, IrType.I32, []), Is.False);
  }

  /// <summary>
  /// The bits of a float are no integer operation: the interpreter still makes an <c>f32</c> of an
  /// <c>i32</c> and an <c>f64</c> of an <c>i64</c>, each of its own type.
  /// </summary>
  [Test]
  public void FloatFromBits_IsStillAFloatOfItsWidth() {
    var word = new IrLocal("a", IrType.I32);
    var wide = new IrLocal("b", IrType.I64);
    var module = new IrModule("Bits", [
      new IrFunction("Single", true, IrType.F32, [word], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F32, [new IrLocalRef(word)]))])),
      new IrFunction("Double", true, IrType.F64, [wide], [], new IrBlock([new IrReturn(new IrIntrinsic(IrIntrinsicOp.BitsFloat, IrType.F64, [new IrLocalRef(wide)]))]))
    ]);
    Assert.That(IrInterpreter.Call(module, "Single", [0x3F800000]), Is.TypeOf<float>().And.EqualTo(1.0f));
    Assert.That(IrInterpreter.Call(module, "Double", [0x3FF0000000000000L]), Is.TypeOf<double>().And.EqualTo(1.0));
  }

  /// <summary>Published and hand-computed values: one or two per family, through the interpreter.</summary>
  [TestCase("RotateLeft_u16", (ushort)0x8001, 17, "3")]
  [TestCase("RotateLeft_i8", (sbyte)-128, -1, "64")]
  [TestCase("RotateRight_u32", 1u, 1, "2147483648")]
  [TestCase("LeadingZeroCount_u8", (byte)0, "8")]
  [TestCase("LeadingZeroCount_i16", (short)0, "16")]
  [TestCase("TrailingZeroCount_u32", 0u, "32")]
  [TestCase("TrailingZeroCount_i64", 0L, "64")]
  [TestCase("PopCount_i8", (sbyte)-1, "8")]
  [TestCase("Log2_i32", -1, "31")]
  [TestCase("Log2_u64", 0UL, "0")]
  [TestCase("Parity_u16", (ushort)7, "1")]
  [TestCase("ByteSwap_u32", 0x12345678u, "2018915346")]
  [TestCase("ByteSwap_i8", (sbyte)-5, "-5")]
  [TestCase("BitReverse_u8", (byte)1, "128")]
  [TestCase("IsPow2_i32", int.MinValue, "false")]
  [TestCase("IsPow2_u64", 0x8000000000000000UL, "true")]
  [TestCase("IsPow2_u8", (byte)0, "false")]
  [TestCase("RoundUpPow2_u8", (byte)0, "0")]
  [TestCase("RoundUpPow2_u8", (byte)129, "0")]
  [TestCase("RoundUpPow2_u32", 5u, "8")]
  [TestCase("BigMul_i32", int.MinValue, int.MinValue, "4611686018427387904")]
  [TestCase("MulHigh_u64", ulong.MaxValue, ulong.MaxValue, "18446744073709551614")]
  [TestCase("MulHigh_i8", (sbyte)-128, (sbyte)-128, "64")]
  [TestCase("DivFloor_i32", -7, 2, "-4")]
  [TestCase("ModFloor_i32", -7, 2, "1")]
  [TestCase("ModFloor_i32", 7, -2, "-1")]
  [TestCase("DivEuclid_i32", -7, -2, "4")]
  [TestCase("ModEuclid_i32", -7, -2, "1")]
  [TestCase("DivFloor_i16", (short)-32768, (short)-1, "trap: integer overflow")]
  [TestCase("ModEuclid_u8", (byte)7, (byte)0, "trap: divide by zero")]
  [TestCase("Clamp_i32", 5, 9, 3, "trap: invalid operation")]
  [TestCase("Clamp_u8", (byte)200, (byte)10, (byte)100, "100")]
  [TestCase("Abs_i64", long.MinValue, "trap: integer overflow")]
  [TestCase("Abs_u16", (ushort)65535, "65535")]
  [TestCase("AbsWrap_i8", (sbyte)-128, "-128")]
  [TestCase("Sign_u32", 7u, "1")]
  [TestCase("AddSaturate_i8", (sbyte)100, (sbyte)100, "127")]
  [TestCase("SubSaturate_u32", 1u, 2u, "0")]
  [TestCase("MulSaturate_u64", ulong.MaxValue, 2UL, "18446744073709551615")]
  [TestCase("AddCarry_u8", (byte)255, (byte)1, true, "1")]
  [TestCase("CarryOut_u64", ulong.MaxValue, 0UL, true, "true")]
  [TestCase("SubBorrow_u16", (ushort)0, (ushort)0, true, "65535")]
  [TestCase("BorrowOut_u32", 5u, 5u, true, "true")]
  [TestCase("AddOverflows_i32", int.MaxValue, 1, "true")]
  [TestCase("MulOverflows_u8", (byte)16, (byte)16, "true")]
  [TestCase("ExtractBits_u32", 0xABCD1234u, 8, 8, "18")]
  [TestCase("ExtractBits_i8", (sbyte)-1, 0, 0, "0")]
  [TestCase("ExtractBits_u16", (ushort)1, 9, 8, "trap: invalid operation")]
  [TestCase("InsertBits_u8", (byte)0, (byte)0xFF, 2, 3, "28")]
  [TestCase("TestBit_i64", -1L, 63, "true")]
  [TestCase("SetBit_i8", (sbyte)0, 7, "-128")]
  [TestCase("ClearBit_u8", (byte)255, 8, "trap: invalid operation")]
  [TestCase("ToggleBit_u16", (ushort)1, 0, "0")]
  [TestCase("LowMask_u32", 32, "4294967295")]
  [TestCase("LowMask_i16", 0, "0")]
  [TestCase("LowMask_u8", 9, "trap: invalid operation")]
  [TestCase("IsolateLowestSet_u8", (byte)12, "4")]
  [TestCase("ClearLowestSet_u8", (byte)12, "8")]
  [TestCase("MaskToLowestSet_u8", (byte)0, "255")]
  [TestCase("SignExtend_i32", 0x80, 8, "-128")]
  [TestCase("SignExtend_u8", (byte)1, 0, "trap: invalid operation")]
  [TestCase("ZeroExtend_i16", (short)-1, 4, "15")]
  public void KnownValue_IsComputed(string function, params object[] call) {
    var expected = (string)call[^1];
    var arguments = call[..^1];
    Assert.That(Interpreted(function, arguments), Is.EqualTo(expected));
    var @case = IntegerCases.All.Single(candidate => candidate.Function == function);
    Assert.That(IntegerCases.Expected(@case, new PrototypeCall(function, arguments)), Is.EqualTo(expected), "the reference disagrees");
  }

  /// <summary>Test design 7: loads and stores at 0, at the last index that fits, at the first that does not and at -1, in both byte orders.</summary>
  [TestCase("LoadLittle_u32", 0, "1732584193")]
  [TestCase("LoadBig_u32", 0, "19088743")]
  [TestCase("LoadLittle_i16", 9, "32640")]
  [TestCase("LoadBig_i16", 9, "-32641")]
  [TestCase("LoadBig_u64", 3, "7460683158699016319")]
  [TestCase("LoadLittle_i64", 4, "trap: index out of range")]
  [TestCase("LoadBig_u16", -1, "trap: index out of range")]
  public void Load_ReadsTheBytes(string function, int index, string expected) {
    byte[] bytes = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0x80, 0x7F];
    Assert.That(Interpreted(function, bytes, index), Is.EqualTo(expected == expected.TrimStart('t') ? expected + " | [1,35,69,103,137,171,205,239,254,128,127]" : expected));
  }

  /// <summary>Test design 7 for stores: the bytes in place in both orders, and a store that does not fit traps before it writes one byte.</summary>
  [Test]
  public void Store_WritesTheBytes_OrNothing() {
    var bytes = new byte[6];
    Assert.That(IrInterpreter.Call(_module, "StoreLittle_u32", [bytes, 2, 0x12345678u]), Is.Null);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0, 0, 0x78, 0x56, 0x34, 0x12 }));
    Assert.That(IrInterpreter.Call(_module, "StoreBig_i16", [bytes, 0, (short)-2]), Is.Null);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0xFF, 0xFE, 0x78, 0x56, 0x34, 0x12 }));
    var before = (byte[])bytes.Clone();
    Assert.That(() => IrInterpreter.Call(_module, "StoreBig_u64", [bytes, 0, ulong.MaxValue]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(() => IrInterpreter.Call(_module, "StoreLittle_u16", [bytes, 5, (ushort)1]), Throws.TypeOf<IrTrapException>());
    Assert.That(bytes, Is.EqualTo(before));
  }

  /// <summary>
  /// Test design 2, 5, 6 and 12 for the interpreter: every operation on every width, on every
  /// combination of the boundaries of its arguments and on 1000 random tuples (seed 2026), has the
  /// outcome of the bit-by-bit reference.
  /// </summary>
  [Test, Category("Slow")]
  public void Interpreter_IsTheBitByBitReference() {
    var random = new Random(IntegerCases.Seed);
    var calls = 0;
    var mismatches = new List<string>();
    foreach (var @case in IntegerCases.All)
      foreach (var call in IntegerCases.Calls(@case, random)) {
        ++calls;
        var expected = IntegerCases.Expected(@case, call);
        if (PrototypeFixture.InterpretedOutcome(_module, call) is var outcome && outcome != expected)
          mismatches.Add($"{call}: {outcome}, the reference {expected}");
      }

    TestContext.Out.WriteLine($"{IntegerCases.All.Count} operations and widths, {calls} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls} calls differ");
  }

}
