using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Float;

/// <summary>
/// The endian loads and stores of <c>f32</c> and <c>f64</c> in the IR: the signatures the verifier
/// accepts and refuses, and the interpreter, which has to read and write exactly the bytes .NET's
/// <c>BinaryPrimitives</c> reads and writes - on the boundaries of the index, for every class of value
/// and for 1000 random bit patterns per operation (seed 2026).
/// </summary>
[TestFixture]
public sealed class FloatViewOperationsTests {

  private static readonly IrModule _module = FloatViewCases.Module();

  private static readonly IrType _bytes = IrType.Array(IrType.U8);

  private static IEnumerable<TestCaseData> Cases() => FloatViewCases.All.Select(@case => new TestCaseData(@case.Function));

  private static string Interpreted(string function, params object[] arguments)
    => PrototypeFixture.InterpretedOutcome(_module, new PrototypeCall(function, arguments));

  /// <summary>Every operation takes both formats, in the signatures the issue states.</summary>
  [Test]
  public void Signatures_TakeBothFormats() {
    foreach (var type in FloatViewCases.Formats) {
      foreach (var op in new[] { IrIntrinsicOp.LoadLittle, IrIntrinsicOp.LoadBig })
        Assert.That(IntegerOperations.Signatures(op), Has.Some.Matches<(IrType Result, IReadOnlyList<IrType> Arguments)>(
          signature => signature.Result == type && signature.Arguments.SequenceEqual([_bytes, IrType.I32])), $"{op} of {type}");

      foreach (var op in new[] { IrIntrinsicOp.StoreLittle, IrIntrinsicOp.StoreBig })
        Assert.That(IntegerOperations.Signatures(op), Has.Some.Matches<(IrType Result, IReadOnlyList<IrType> Arguments)>(
          signature => signature.Result == IrType.Void && signature.Arguments.SequenceEqual([_bytes, IrType.I32, type])), $"{op} of {type}");
    }
  }

  /// <summary>The module of every case is well-typed.</summary>
  [Test]
  public void EveryCase_IsWellTyped() => Assert.That(IrVerifier.Verify(_module), Is.Empty);

  /// <summary>
  /// Test design 10: what the verifier refuses about a float access. The forms the issue lists - an
  /// <c>i32</c> result, an <c>i64</c> value - are the integer accesses of the same width, which are
  /// well-typed; refused are a result or a value that is no number of more than one byte, an array
  /// that is no <c>u8[]</c>, an index that is no <c>i32</c> and a store whose node has a type.
  /// </summary>
  [TestCase(IrIntrinsicOp.LoadLittle, "bool", "u8[]", "i32", null)]
  [TestCase(IrIntrinsicOp.LoadBig, "f32", "f32[]", "i32", null)]
  [TestCase(IrIntrinsicOp.LoadLittle, "f64", "u8[]", "i64", null)]
  [TestCase(IrIntrinsicOp.LoadBig, "f64", "u8[]", "f64", null)]
  [TestCase(IrIntrinsicOp.StoreLittle, "void", "u8[]", "i32", "bool")]
  [TestCase(IrIntrinsicOp.StoreBig, "void", "u16[]", "i32", "f64")]
  [TestCase(IrIntrinsicOp.StoreLittle, "f32", "u8[]", "i32", "f32")]
  [TestCase(IrIntrinsicOp.StoreBig, "void", "f64[]", "i32", "f64")]
  public void IllTypedAccess_IsRefused(IrIntrinsicOp op, string result, string array, string index, string? value) {
    IrType Parse(string name) => name switch {
      "bool" => IrType.Bool,
      "void" => IrType.Void,
      "i32" => IrType.I32,
      "i64" => IrType.I64,
      "f32" => IrType.F32,
      "f64" => IrType.F64,
      "u8[]" => _bytes,
      "u16[]" => IrType.Array(IrType.U16),
      "f32[]" => IrType.Array(IrType.F32),
      _ => IrType.Array(IrType.F64)
    };

    IrType[] types = value is null ? [Parse(array), Parse(index)] : [Parse(array), Parse(index), Parse(value)];
    var function = IntegerCases.Function(new IntegerCase("F", op, Parse(result), types));
    Assert.That(IrVerifier.Verify(new IrModule("Broken", [function])).Select(diagnostic => diagnostic.Code), Has.Member(IrVerifier.Code));
  }

  /// <summary>The integer access of the width of a format stays well-typed: the forms test design 10 names are those.</summary>
  [Test]
  public void IntegerAccessOfTheWidth_IsWellTyped() {
    var load = IntegerCases.Function(new IntegerCase("L", IrIntrinsicOp.LoadLittle, IrType.I32, [_bytes, IrType.I32]));
    var store = IntegerCases.Function(new IntegerCase("S", IrIntrinsicOp.StoreBig, IrType.Void, [_bytes, IrType.I32, IrType.I64]));
    Assert.That(IrVerifier.Verify(new IrModule("Fine", [load, store])), Is.Empty);
  }

  /// <summary>Test design 2 and 6: one value per operation, at the index 0 and at the last that fits; outside the buffer the trap.</summary>
  [TestCase("LoadLittle_f32", new byte[] { 0x00, 0x00, 0x80, 0x3F }, 0, "1 | [0,0,128,63]")]
  [TestCase("LoadBig_f32", new byte[] { 0x00, 0xC0, 0x49, 0x0F, 0xDB }, 1, "f:c00921fb60000000 | [0,192,73,15,219]")]
  [TestCase("LoadBig_f64", new byte[] { 0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18 }, 0, "f:400921fb54442d18 | [64,9,33,251,84,68,45,24]")]
  [TestCase("LoadLittle_f64", new byte[] { 0x18, 0x2D, 0x44, 0x54, 0xFB, 0x21, 0x09, 0x40 }, 0, "f:400921fb54442d18 | [24,45,68,84,251,33,9,64]")]
  [TestCase("LoadLittle_f32", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 9, "trap: index out of range")]
  [TestCase("LoadBig_f64", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 5, "trap: index out of range")]
  [TestCase("LoadLittle_f64", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, -1, "trap: index out of range")]
  public void Load_ReadsTheEncoding(string function, byte[] bytes, int index, string expected)
    => Assert.That(Interpreted(function, bytes, index), Is.EqualTo(expected));

  /// <summary>Test design 2, 4 and 6 for stores: the bytes of the encoding in either order, the sign of a zero kept, and a store that does not fit writes nothing.</summary>
  [Test]
  public void Store_WritesTheEncoding_OrNothing() {
    var bytes = new byte[FloatViewCases.Length];
    Assert.That(IrInterpreter.Call(_module, "StoreLittle_f32", [bytes, 0, 0.1f]), Is.Null);
    Assert.That(bytes[..4], Is.EqualTo(new byte[] { 0xCD, 0xCC, 0xCC, 0x3D }));
    Assert.That(IrInterpreter.Call(_module, "StoreBig_f64", [bytes, 4, -0.0]), Is.Null);
    Assert.That(bytes[4..], Is.EqualTo(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }));
    Assert.That(IrInterpreter.Call(_module, "StoreBig_f32", [bytes, 8, float.Epsilon]), Is.Null);
    Assert.That(bytes[8..], Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
    var before = (byte[])bytes.Clone();
    Assert.That(() => IrInterpreter.Call(_module, "StoreLittle_f32", [bytes, 9, 1.0f]), Throws.TypeOf<IrTrapException>().With.Property("Trap").EqualTo(IrTrap.IndexOutOfRange));
    Assert.That(() => IrInterpreter.Call(_module, "StoreLittle_f64", [bytes, -1, 1.0]), Throws.TypeOf<IrTrapException>());
    Assert.That(() => IrInterpreter.Call(_module, "StoreBig_f64", [bytes, 5, 1.0]), Throws.TypeOf<IrTrapException>());
    Assert.That(bytes, Is.EqualTo(before));
  }

  /// <summary>
  /// A binary32 number is never converted through binary64: the bits of a signalling NaN come back
  /// as they were read, where a widening would have set the quiet bit, and a subnormal keeps its value.
  /// </summary>
  [Test]
  public void Binary32_IsNotConvertedThroughBinary64() {
    var read = IrInterpreter.Call(_module, "LoadLittle_f32", [new byte[] { 0x00, 0x00, 0xA0, 0x7F }, 0]);
    Assert.That(read, Is.TypeOf<float>());
    Assert.That(BitConverter.SingleToInt32Bits((float)read!), Is.EqualTo(0x7FA00000));
    var bytes = new byte[4];
    IrInterpreter.Call(_module, "StoreBig_f32", [bytes, 0, FloatViewCases.Single(0x7FA00000)]);
    Assert.That(bytes, Is.EqualTo(new byte[] { 0x7F, 0xA0, 0x00, 0x00 }));
    Assert.That(IrInterpreter.Call(_module, "LoadBig_f64", [new byte[] { 0x7F, 0xF0, 0, 0, 0, 0, 0, 1 }, 0]), Is.TypeOf<double>().And.NaN);
  }

  /// <summary>
  /// Test design 2, 4, 5 and 6 for the interpreter, and the differential measurement against .NET:
  /// every operation on every index of the boundaries, every class of value and 1000 random bit
  /// patterns (seed 2026) has the outcome of <c>BinaryPrimitives</c>; a stored NaN as a NaN.
  /// </summary>
  [Test]
  public void Interpreter_IsBinaryPrimitives() {
    var random = new Random(IntegerCases.Seed);
    var calls = 0;
    var bitwise = 0;
    var mismatches = new List<string>();
    foreach (var @case in FloatViewCases.All)
      foreach (var call in FloatViewCases.Calls(@case, random)) {
        ++calls;
        var expected = FloatViewCases.Expected(@case, call);
        var outcome = PrototypeFixture.InterpretedOutcome(_module, call);
        if (outcome == expected)
          ++bitwise;
        else if (!FloatViewCases.Matches(@case, call, expected, outcome))
          mismatches.Add($"{call}: {outcome}, BinaryPrimitives {expected}");
      }

    TestContext.Out.WriteLine($"{FloatViewCases.All.Count} operations, {calls} calls, {bitwise} identical, {calls - bitwise - mismatches.Count} NaN by class, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls} calls differ");
  }

  /// <summary>The comparison accepts a NaN for a NaN in a store and nothing else (test design 12).</summary>
  [Test]
  public void StoredNaN_IsComparedByClass() {
    var @case = FloatViewCases.Of("StoreLittle_f64");
    var call = new PrototypeCall(@case.Function, [new byte[10], 1, double.NaN]);
    var dotnet = FloatViewCases.Expected(@case, call);
    var v8 = "void | [0,0,0,0,0,0,0,248,127,0]";
    Assert.That(dotnet, Is.EqualTo("void | [0,0,0,0,0,0,0,248,255,0]"), "the sign bit of .NET's NaN");
    Assert.That(FloatViewCases.Matches(@case, call, dotnet, v8), Is.True);
    Assert.That(FloatViewCases.Matches(@case, call, dotnet, "void | [0,0,0,0,0,0,0,240,127,0]"), Is.False, "an infinity is no NaN");
    Assert.That(FloatViewCases.Matches(@case, call, dotnet, "void | [1,0,0,0,0,0,0,248,127,0]"), Is.False, "a byte outside the number");
    Assert.That(FloatViewCases.Matches(@case, call, dotnet, "void | [0,0,0,0,0,0,0,248,127]"), Is.False, "a byte missing");
    Assert.That(FloatViewCases.Matches(@case, call, dotnet, "trap: index out of range"), Is.False);
    var single = FloatViewCases.Of("StoreBig_f32");
    var nan = new PrototypeCall(single.Function, [new byte[4], 0, float.NaN]);
    Assert.That(FloatViewCases.Matches(single, nan, FloatViewCases.Expected(single, nan), "void | [127,192,0,0]"), Is.True);
    var number = new PrototypeCall(single.Function, [new byte[4], 0, 1.0f]);
    Assert.That(FloatViewCases.Matches(single, number, FloatViewCases.Expected(single, number), "void | [63,128,0,1]"), Is.False, "a number is compared bit for bit");
    var outside = new PrototypeCall(single.Function, [new byte[4], 1, float.NaN]);
    Assert.That(FloatViewCases.Matches(single, outside, FloatViewCases.Expected(single, outside), "void | [0,0,0,0]"), Is.False);
    Assert.That(FloatViewCases.Matches(FloatViewCases.Of("LoadBig_f32"), new PrototypeCall("LoadBig_f32", [new byte[4], 0]), "NaN | [0,0,0,0]", "1 | [0,0,0,0]"), Is.False);
  }

}
