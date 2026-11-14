using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The endian loads and stores of floating-point numbers: the eight operations of the issue (load and
/// store, little and big byte order, <c>f32</c> and <c>f64</c>), one function each, and their calls -
/// the boundaries of the index on a 12-byte buffer, one value of every class, and random bit patterns
/// from one seed. The oracle is .NET's <see cref="BinaryPrimitives"/>, which shares no code with the
/// interpreter or the backends. Two outcomes match when they are equal, a stored NaN aside, whose
/// bytes only have to be a NaN of the format (ADR 0005): .NET, V8 and the bit casts of the helpers
/// encode a NaN each their own way.
/// </summary>
internal static class FloatViewCases {

  /// <summary>The 12-byte buffer of the scenarios.</summary>
  public const int Length = 12;

  private static readonly IrType _bytes = IrType.Array(IrType.U8);

  /// <summary>The two formats.</summary>
  public static readonly IrType[] Formats = [IrType.F32, IrType.F64];

  /// <summary>Every operation on every format.</summary>
  public static IReadOnlyList<IntegerCase> All { get; } = [
    .. from op in new[] { IrIntrinsicOp.LoadLittle, IrIntrinsicOp.LoadBig }
       from type in Formats
       select new IntegerCase($"{op}_{type}", op, type, [_bytes, IrType.I32]),
    .. from op in new[] { IrIntrinsicOp.StoreLittle, IrIntrinsicOp.StoreBig }
       from type in Formats
       select new IntegerCase($"{op}_{type}", op, IrType.Void, [_bytes, IrType.I32, type])
  ];

  /// <summary>One module with one public function per case.</summary>
  public static IrModule Module(string name = "FloatViews") => IntegerCases.Module(All, name);

  /// <summary>The case of a function.</summary>
  public static IntegerCase Of(string function) => All.Single(@case => @case.Function == function);

  /// <summary>The format a case reads or writes.</summary>
  public static IrType FormatOf(IntegerCase @case) => @case.Result == IrType.Void ? @case.Types[2] : @case.Result;

  /// <summary>Whether a case stores.</summary>
  public static bool Stores(IntegerCase @case) => @case.Result == IrType.Void;

  /// <summary>Whether a case has the lowest byte first.</summary>
  public static bool Little(IntegerCase @case) => @case.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.StoreLittle;

  /// <summary>
  /// One value of every class of a format: normal numbers, the smallest and largest subnormal and the
  /// largest finite number, both zeros, both infinities, a quiet NaN and, for binary32, a signalling one.
  /// </summary>
  public static object[] Classes(IrType format) => format == IrType.F32
    ? [1.0f, -1.5f, 0.1f, float.Epsilon, Single(0x007FFFFF), float.MaxValue, 0.0f, -0.0f,
       float.PositiveInfinity, float.NegativeInfinity, float.NaN, Single(0x7FA00000)]
    : [1.0, -1.5, 0.1, Math.PI, double.Epsilon, BitConverter.Int64BitsToDouble(0x000FFFFFFFFFFFFF), double.MaxValue, 0.0, -0.0,
       double.PositiveInfinity, double.NegativeInfinity, double.NaN];

  /// <summary>
  /// The binary32 number with the given bits, made at run time: the JIT folds
  /// <c>BitConverter.Int32BitsToSingle</c> of a constant through binary64, which sets the quiet bit of
  /// a signalling NaN.
  /// </summary>
  public static float Single(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits));

  /// <summary>The eight samples of the WAV file of the fixtures: numbers of every sign and size, a subnormal and a negative zero.</summary>
  public static readonly float[] Samples = [0.5f, -0.25f, 0.1f, -1.0f, 1e-3f, float.Epsilon, -0.0f, 0.75f];

  /// <summary>
  /// A WAV file of IEEE float samples, mono, 44100 Hz, 32 bits: the canonical 44-byte header - "RIFF",
  /// the format chunk with the format tag 3 at 20 and the bits per sample at 34, the size of the data
  /// chunk at 40 - and the samples, little-endian, from 44 on.
  /// </summary>
  public static byte[] Wav(float[] samples) {
    var bytes = new byte[44 + 4 * samples.Length];
    void Text(int at, string text) => System.Text.Encoding.ASCII.GetBytes(text).CopyTo(bytes, at);
    Text(0, "RIFF");
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
    Text(8, "WAVE");
    Text(12, "fmt ");
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 3);
    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 44100 * 4);
    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 4);
    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 32);
    Text(36, "data");
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 4 * samples.Length);
    for (var index = 0; index < samples.Length; ++index)
      BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(44 + 4 * index), samples[index]);

    return bytes;
  }

  /// <summary>The sum of samples as a double, in their order: what the WAV readers of the fixtures return.</summary>
  public static double Sum(float[] samples) {
    var sum = 0.0;
    foreach (var sample in samples)
      sum += sample;

    return sum;
  }

  /// <summary>
  /// The doubles the scenarios store as binary32: 0.1; the largest finite binary32 number; that plus
  /// half an ulp, which rounds to the even infinity; 1e40; half the smallest subnormal, which rounds to
  /// +0; three quarters of it, which rounds to the smallest subnormal; both zeros, both infinities, a NaN.
  /// </summary>
  public static readonly double[] Rounded = [
    0.1, 3.4028234663852886e38, 3.4028235677973366e38, 1e40, 7.006492321624085e-46, 1.0509738482436128e-45,
    0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN
  ];

  /// <summary>The public methods of the C# fixture <c>float/FloatView.cs</c>; the JavaScript fixture has those but the two of <c>BitConverter</c>, spelled from a small letter.</summary>
  public static readonly string[] FixtureMethods = [
    "GetF32LE", "GetF32BE", "GetF64LE", "GetF64BE", "ToSingle", "ToDouble", "PutF32LE", "PutF32BE", "PutF64LE", "PutF64BE", "SumSamples"
  ];

  /// <summary>The name of a method of the C# fixture in the JavaScript one.</summary>
  public static string Script(string method) => char.ToLowerInvariant(method[0]) + method[1..];

  /// <summary>
  /// The calls of a method of the fixtures: for a read, every class of value at every index of the
  /// boundaries and random bytes; for a write, the doubles of <see cref="Rounded"/> and random ones at
  /// those indexes; for the WAV reader, the file of <see cref="Samples"/>, an empty one, one of another
  /// format, one shorter than its data chunk says and one shorter than its header.
  /// </summary>
  public static IReadOnlyList<PrototypeCall> FixtureCalls(string method, Random generator, int random = 100) {
    var calls = new List<PrototypeCall>();
    if (method == "SumSamples") {
      var other = Wav(Samples);
      other[20] = 1;
      var truncated = Wav(Samples)[..60];
      return [
        new(method, [Wav(Samples)]), new(method, [Wav([])]), new(method, [other]), new(method, [truncated]), new(method, [new byte[43]]),
        new(method, [Wav([.. Enumerable.Range(0, 64).Select(_ => (float)(generator.NextDouble() * 2 - 1))])])
      ];
    }

    var single = method.Contains("F32", StringComparison.Ordinal) || method == "ToSingle";
    var size = single ? 4 : 8;
    var little = method.EndsWith("LE", StringComparison.Ordinal) || method.StartsWith("To", StringComparison.Ordinal);
    var returns = single ? typeof(float) : typeof(double);
    if (method.StartsWith("Put", StringComparison.Ordinal)) {
      foreach (var index in Indexes(size))
        foreach (var value in Rounded)
          calls.Add(new PrototypeCall(method, [new byte[Length], index, value]));

      for (var count = 0; count < random; ++count)
        calls.Add(new PrototypeCall(method, [new byte[Length], generator.Next(-1, Length), BitConverter.Int64BitsToDouble(generator.NextInt64())]));

      return calls;
    }

    foreach (var value in Classes(single ? IrType.F32 : IrType.F64))
      foreach (var index in Indexes(size))
        calls.Add(new PrototypeCall(method, [Holding(value, Math.Clamp(index, 0, Length - size), little), index]) { Returns = returns });

    for (var count = 0; count < random; ++count) {
      var bytes = new byte[Length];
      generator.NextBytes(bytes);
      calls.Add(new PrototypeCall(method, [bytes, generator.Next(-1, Length)]) { Returns = returns });
    }

    return calls;
  }

  /// <summary>Whether an outcome of a method of the fixtures is the expected one, a stored NaN compared by class.</summary>
  public static bool FixtureMatches(string method, PrototypeCall call, string expected, string actual)
    => actual == expected
      || (method.StartsWith("Put", StringComparison.OrdinalIgnoreCase) && call.Arguments[2] is double number && double.IsNaN(number)
        && SameStoreOfNaN(expected, actual, (int)call.Arguments[1], method.Contains("F32", StringComparison.Ordinal) ? 4 : 8, method.EndsWith("LE", StringComparison.Ordinal)));

  /// <summary>The indexes of the boundaries: -1, 0, 1, the last that fits, the first that does not, the extremes.</summary>
  public static int[] Indexes(int size) => [-1, 0, 1, Length - size, Length - size + 1, int.MinValue, int.MaxValue];

  /// <summary>The 12 bytes with the encoding of a value at an index, in a byte order, and a pattern around it.</summary>
  public static byte[] Holding(object value, int index, bool little) {
    var bytes = Enumerable.Range(0, Length).Select(offset => (byte)(0xA5 ^ offset)).ToArray();
    Write(bytes.AsSpan(index), value, little);
    return bytes;
  }

  /// <summary>
  /// The calls of a case: every index of the boundaries, with every class of value in the buffer for
  /// a load and as the value of a store, then <paramref name="random"/> calls with random bits.
  /// </summary>
  public static IReadOnlyList<PrototypeCall> Calls(IntegerCase @case, Random generator, int random = 1000) {
    var format = FormatOf(@case);
    var size = format.Size;
    var returns = Stores(@case) ? null : format == IrType.F32 ? typeof(float) : typeof(double);
    var calls = new List<PrototypeCall>();
    foreach (var value in Classes(format))
      foreach (var index in Indexes(size))
        calls.Add(Stores(@case)
          ? new PrototypeCall(@case.Function, [Holding(value, 0, true), index, value])
          : new PrototypeCall(@case.Function, [Holding(value, Math.Clamp(index, 0, Length - size), Little(@case)), index]) { Returns = returns });

    for (var count = 0; count < random; ++count) {
      var bytes = new byte[Length];
      generator.NextBytes(bytes);
      var index = generator.Next(-2, Length + 1);
      if (Stores(@case)) {
        var pattern = new byte[8];
        generator.NextBytes(pattern);
        var value = format == IrType.F32 ? (object)BitConverter.ToSingle(pattern) : BitConverter.ToDouble(pattern);
        calls.Add(new PrototypeCall(@case.Function, [bytes, index, value]));
      } else {
        calls.Add(new PrototypeCall(@case.Function, [bytes, index]) { Returns = returns });
      }
    }

    return calls;
  }

  /// <summary>The outcome <see cref="BinaryPrimitives"/> gives for a call of a case: the value read, or nothing and the bytes written, or the bounds trap.</summary>
  public static string Expected(IntegerCase @case, PrototypeCall call) {
    var arguments = call.FreshArguments();
    var bytes = (byte[])arguments[0];
    var index = (int)arguments[1];
    var size = FormatOf(@case).Size;
    if (index < 0 || index > bytes.Length - size)
      return PrototypeFixture.Trap(IrTrap.IndexOutOfRange);

    var span = bytes.AsSpan(index, size);
    if (Stores(@case)) {
      Write(span, arguments[2], Little(@case));
      return PrototypeFixture.Outcome(null, arguments);
    }

    var read = FormatOf(@case) == IrType.F32
      ? (object)(Little(@case) ? BinaryPrimitives.ReadSingleLittleEndian(span) : BinaryPrimitives.ReadSingleBigEndian(span))
      : Little(@case) ? BinaryPrimitives.ReadDoubleLittleEndian(span) : BinaryPrimitives.ReadDoubleBigEndian(span);
    return PrototypeFixture.Outcome(read, arguments);
  }

  /// <summary>
  /// Whether an outcome is the expected one: equal, or - for a store of a NaN - equal in every byte
  /// but those of the number, which hold a NaN of the format in the byte order of the case.
  /// </summary>
  public static bool Matches(IntegerCase @case, PrototypeCall call, string expected, string actual) {
    if (actual == expected)
      return true;

    if (!Stores(@case) || !IsNaN(call.Arguments[2]) || expected.StartsWith("trap:", StringComparison.Ordinal))
      return false;

    return SameStoreOfNaN(expected, actual, (int)call.Arguments[1], FormatOf(@case).Size, Little(@case));
  }

  /// <summary>
  /// Whether two outcomes of a store of a NaN at an index - <c>void | [bytes]</c>, or the bytes as
  /// the first array argument - hold the same bytes outside the number and a NaN of the size in it.
  /// </summary>
  public static bool SameStoreOfNaN(string expected, string actual, int index, int size, bool little) {
    var wanted = Bytes(expected);
    var written = Bytes(actual);
    if (wanted is null || written is null || written.Length != wanted.Length || index < 0 || index + size > written.Length)
      return false;

    var number = written.AsSpan(index, size);
    var decoded = size == 4
      ? float.IsNaN(little ? BinaryPrimitives.ReadSingleLittleEndian(number) : BinaryPrimitives.ReadSingleBigEndian(number))
      : double.IsNaN(little ? BinaryPrimitives.ReadDoubleLittleEndian(number) : BinaryPrimitives.ReadDoubleBigEndian(number));
    return decoded && Enumerable.Range(0, written.Length).All(offset => (offset >= index && offset < index + size) || written[offset] == wanted[offset]);
  }

  private static bool IsNaN(object value) => value is float single ? float.IsNaN(single) : double.IsNaN((double)value);

  /// <summary>The bytes of the first array of an outcome, <c>void | [1,2,…]</c> or <c>[1,2,…] | [1,2,…]</c>, or <see langword="null"/> for an outcome without one.</summary>
  private static byte[]? Bytes(string outcome) {
    var start = outcome.IndexOf('[', StringComparison.Ordinal);
    var end = start < 0 ? -1 : outcome.IndexOf(']', start);
    return end < 0 ? null : [.. outcome[(start + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(text => byte.Parse(text, CultureInfo.InvariantCulture))];
  }

  /// <summary>Writes the encoding of a <see cref="float"/> or a <see cref="double"/> with <see cref="BinaryPrimitives"/>.</summary>
  private static void Write(Span<byte> span, object value, bool little) {
    if (value is float single) {
      if (little)
        BinaryPrimitives.WriteSingleLittleEndian(span, single);
      else
        BinaryPrimitives.WriteSingleBigEndian(span, single);
    } else if (little) {
      BinaryPrimitives.WriteDoubleLittleEndian(span, (double)value);
    } else {
      BinaryPrimitives.WriteDoubleBigEndian(span, (double)value);
    }
  }

}
