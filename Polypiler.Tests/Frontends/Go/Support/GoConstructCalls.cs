using System.Collections.Generic;
using System.IO;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Go.Support;

/// <summary>
/// The calls of <c>tests/fixtures/go/frontend/constructs.go</c> and where Go's own outcomes of them
/// are recorded: the Go layer runs the file under go and checks the record, the unit tests compare the
/// interpreter's outcomes of the imported module with it.
/// </summary>
internal static class GoConstructCalls {

  /// <summary>The recorded outcomes, one per call, as Go printed them.</summary>
  public static string Recorded { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "go", "frontend", "constructs.outcomes.json");

  /// <summary>The calls, each with arguments of the types of the Go function.</summary>
  public static IReadOnlyList<PrototypeCall> All { get; } = [
    new("NarrowSigned", [(sbyte)100, (sbyte)-3]), new("NarrowSigned", [sbyte.MinValue, (sbyte)-1]),
    new("NarrowUnsigned", [(ushort)65535, (ushort)7]), new("NarrowUnsigned", [(ushort)12, (ushort)0]),
    new("Wide", [-7L, 3UL]), new("Wide", [long.MinValue, ulong.MaxValue]),
    new("ShiftCounts", [(short)-3, (sbyte)4, 2u]), new("ShiftCounts", [(short)1, (sbyte)17, 40u]), new("ShiftCounts", [(short)1, (sbyte)-1, 0u]),
    new("ShiftLong", [0x8000000000000001UL, (byte)3]), new("ShiftLong", [1UL, (byte)200]),
    new("Conversions", [1.75f, (short)-300]), new("Conversions", [float.MaxValue, (short)5]),
    new("Truncations", [3.9]), new("Truncations", [100.7]),
    new("Floats", [2.25, -1.5]), new("Floats", [-9.0, 4.5]),
    new("Transcendental", [0.5]), new("Transcendental", [-3.0]),
    new("FloatBits", [1.5, 2.5f]),
    new("Specials", [1L]), new("Specials", [-1L]),
    new("Bits", [0xF0F0u, 0x10UL, (ushort)0x100, (byte)0x81]), new("Bits", [0u, 0UL, (ushort)0, (byte)0]),
    new("Comparisons", [(byte)1, (byte)2, "a", "b", true, false]), new("Comparisons", [(byte)3, (byte)2, "x", "x", false, false]),
    new("Structs", [(short)5, (ushort)7]),
    new("Globals", [3]),
    new("Slices", [4]),
    new("Indexes", [new byte[] { 1, 2, 3, 4 }, 1u, (sbyte)2, 3UL, 0L]), new("Indexes", [new byte[] { 1, 2, 3 }, 5u, (sbyte)0, 0UL, 0L]),
    new("Indexes", [new byte[] { 1, 2, 3 }, 0u, (sbyte)-1, 0UL, 0L]), new("Indexes", [new byte[] { 1, 2, 3 }, 0u, (sbyte)0, 0x100000000UL, 0L]),
    new("Loops", [10L]), new("Loops", [0L]),
    new("Switches", [1, 0]), new("Switches", [2, 1]), new("Switches", [11, 2]), new("Switches", [-5, 0]), new("Switches", [0, 2]),
    new("Strings", [1L]), new("Strings", [0L]),
    new("Minimum", [3, -4, 9]),
    new("Unsigned", [5u]),
    new("Blank", [6]),
    new("Constants", []), new("Escapes", []), new("Unary", [1.5, 7L, true]), new("Unary", [1.5, 7L, false])
  ];

}
