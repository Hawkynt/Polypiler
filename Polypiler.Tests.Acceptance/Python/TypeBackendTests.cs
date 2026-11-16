using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Prototype.Testing;
using Polypiler.Tests.Acceptance.Infra;

namespace Polypiler.Tests.Acceptance.Python;

/// <summary>
/// The scenarios of #448 and #446 with the values they state, run under the pinned CPython: a class
/// with an accessor, the Xorshift32 state of Marsaglia's paper, value and reference equality, an enum
/// and its switch, an optional zero, traps raised and caught, an endian load, a pointer walk and an
/// overlapping copy. The generated text is checked by the unit tests of the backend.
/// </summary>
[TestFixture, Layer("pyperl")]
[Category("EndToEnd")]
public sealed class TypeBackendTests {

  private const string Source = """
    using System;
    using System.Buffers.Binary;

    public sealed class Plane {
      public int Width; public int Height; public int[] Pixels;
      public Plane(int width, int height, int[] pixels) { Width = width; Height = height; Pixels = pixels; }
      public int Get(int x, int y) => Pixels[y * Width + x];
    }

    public sealed class Xorshift32 {
      public uint State;
      public Xorshift32(uint seed) { State = seed; }
      public uint Next() { var x = State; x ^= x << 13; x ^= x >> 17; x ^= x << 5; State = x; return x; }
    }

    public sealed class Box { public int X; public int Y; public Box(int x, int y) { X = x; Y = y; } }

    public static unsafe class Scenarios {
      public readonly record struct Point(int X, int Y);
      public enum Method { Yadif = 0, Bwdif = 1, GreedyHm = 2 }

      public static int PlaneAt(int x, int y) => new Plane(3, 2, new[] { 10, 20, 30, 40, 50, 60 }).Get(x, y);
      public static long Xorshift(int calls) { var r = new Xorshift32(1); uint last = 0; for (var i = 0; i < calls; i++) last = r.Next(); return last + (long)r.State * 0; }
      public static bool ValueEqual(int v) => new Point(v, 2) == new Point(1, 2);
      public static bool ReferenceEqual(int v) => ReferenceEquals(new Box(v, 2), new Box(1, 2));
      public static int Shader(int m) { switch ((Method)m) { case Method.Yadif: return 0; case Method.Bwdif: return 1; default: return 2; } }
      public static string MethodName(int m) => ((Method)m).ToString();
      public static int Present(int? x) => x is null ? -1 : x.Value;
      public static int Optional(int which) => Present(which < 0 ? null : which);
      public static int Guarded(int x, int y) {
        try {
          if (x < 0) throw new ArgumentException("negative");
          return x / y;
        } catch (DivideByZeroException) {
          return -1;
        }
      }
      public static uint Little(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
      public static uint Big(byte[] b) => BinaryPrimitives.ReadUInt32BigEndian(b);
      public static uint Walk(byte[] data, int start, int n) {
        uint total = 0;
        fixed (byte* p = data) {
          var q = p + start;
          for (var i = 0; i < n; i++) total += q[i];
        }
        return total;
      }
      public static int[] Overlap(int[] xs) { Array.Copy(xs, 0, xs, 1, 4); return xs; }
    }
    """;

  private static readonly Dictionary<string, PythonTranslation> _translated = [];

  private static PythonTranslation Translated() {
    lock (_translated) {
      if (!_translated.TryGetValue("", out var translation))
        _translated[""] = translation = PythonFixtures.Translate(Source, "Scenarios.cs");

      Assert.That(translation.Source, Is.Not.Null, string.Join("; ", translation.Problems));
      return translation;
    }
  }

  private static async Task<IReadOnlyList<string>> Run(params PrototypeCall[] calls) {
    var translation = Translated();
    return await PythonDriver.OutcomesAsync(translation.Source!, [.. calls.Select(call => call with { Method = translation.Names![call.Method] })]);
  }

  [Test, RequiresToolchain("python")]
  public async Task PlaneAccessor_ReadsTheRowMajorPixel()
    => Assert.That(await Run(new PrototypeCall("PlaneAt", [2, 1]), new PrototypeCall("PlaneAt", [1, 1])), Is.EqualTo(new[] { "60", "50" }));

  [Test, RequiresToolchain("python")]
  public async Task Xorshift32_GivesThePublishedSequence()
    => Assert.That(await Run(new PrototypeCall("Xorshift", [1]), new PrototypeCall("Xorshift", [2]), new PrototypeCall("Xorshift", [3])),
      Is.EqualTo(new[] { "270369", "67634689", "2647435461" }));

  [Test, RequiresToolchain("python")]
  public async Task ValueRecords_CompareFieldWise_ReferenceRecordsByIdentity()
    => Assert.That(await Run(new PrototypeCall("ValueEqual", [1]), new PrototypeCall("ValueEqual", [3]), new PrototypeCall("ReferenceEqual", [1])),
      Is.EqualTo(new[] { "true", "false", "false" }));

  [Test, RequiresToolchain("python")]
  public async Task Enum_SwitchesAndNamesItsMembers()
    => Assert.That(await Run(new PrototypeCall("Shader", [2]), new PrototypeCall("Shader", [1]), new PrototypeCall("MethodName", [2]), new PrototypeCall("MethodName", [7])),
      Is.EqualTo(new[] { "2", "1", "GreedyHm", "7" }));

  [Test, RequiresToolchain("python")]
  public async Task OptionalZero_IsAPresentValue()
    => Assert.That(await Run(new PrototypeCall("Optional", [-1]), new PrototypeCall("Optional", [0])), Is.EqualTo(new[] { "-1", "0" }));

  [Test, RequiresToolchain("python")]
  public async Task Traps_AreRaisedAndCaught()
    => Assert.That(await Run(new PrototypeCall("Guarded", [6, 3]), new PrototypeCall("Guarded", [1, 0]), new PrototypeCall("Guarded", [-1, 1])),
      Is.EqualTo(new[] { "2", "-1", "trap: index out of range" }));

  [Test, RequiresToolchain("python")]
  public async Task EndianLoads_ReadTheirByteOrder_AndTrapPastTheEnd()
    => Assert.That(await Run(new PrototypeCall("Little", [new byte[] { 0x78, 0x56, 0x34, 0x12 }]), new PrototypeCall("Big", [new byte[] { 0x78, 0x56, 0x34, 0x12 }]),
      new PrototypeCall("Little", [new byte[] { 0x78, 0x56 }])),
      Is.EqualTo(new[] { "305419896 | [120,86,52,18]", "2018915346 | [120,86,52,18]", "trap: index out of range" }));

  [Test, RequiresToolchain("python")]
  public async Task PointerWalk_IsARegionAndAnOffset()
    => Assert.That(await Run(new PrototypeCall("Walk", [new byte[] { 1, 2, 3, 4 }, 1, 3]), new PrototypeCall("Walk", [new byte[] { 1, 2, 3, 4 }, 2, 3])),
      Is.EqualTo(new[] { "9 | [1,2,3,4]", "trap: index out of range" }));

  [Test, RequiresToolchain("python")]
  public async Task OverlappingCopy_KeepsMemmoveSemanticsAndTheLength()
    => Assert.That(await Run(new PrototypeCall("Overlap", [new[] { 1, 2, 3, 4, 5 }])), Is.EqualTo(new[] { "[1,1,2,3,4] | [1,1,2,3,4]" }));

}
