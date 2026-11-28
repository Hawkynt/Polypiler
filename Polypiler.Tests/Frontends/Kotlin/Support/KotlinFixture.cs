using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Polypiler.Core.Frontends.Kotlin;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Kotlin.Testing;

/// <summary>
/// The Kotlin fixtures of <c>tests/fixtures/kotlin</c>: their sources, the calls they are compared on,
/// and the recording of what the pinned <c>kotlinc</c> does with every call, which the unit tests hold
/// the interpreter against and the acceptance tests hold <c>kotlinc</c> and the other backends against.
/// </summary>
internal static class KotlinFixture {

  /// <summary>The fixtures, by the name of their file without the extension.</summary>
  public static readonly string[] Names = ["Scalars", "Control", "Arrays", "Kernels", "Functions"];

  /// <summary>The folder of the fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "kotlin");

  /// <summary>The path of a fixture's source.</summary>
  public static string PathOf(string fixture) => Path.Combine(Directory, fixture + ".kt");

  /// <summary>The text of a fixture's source.</summary>
  public static string SourceOf(string fixture) => File.ReadAllText(PathOf(fixture));

  /// <summary>The path of a fixture's recording.</summary>
  public static string RecordingOf(string fixture) => Path.Combine(Directory, fixture + ".recorded.txt");

  /// <summary>A fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Import(string fixture) {
    var result = KotlinFrontend.Import(fixture + ".kt", SourceOf(fixture));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>The Kotlin name of every function of an imported fixture: its IR name without the parameter types an overload's carries.</summary>
  public static IReadOnlyDictionary<string, string> NamesOf(IrModule module) {
    var names = module.Functions.Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
    return module.Functions.ToDictionary(function => function.Name,
      function => function.Name.IndexOf('_', StringComparison.Ordinal) is var cut and > 0 && names.Contains(function.Name[..cut]) ? function.Name[..cut] : function.Name,
      StringComparer.Ordinal);
  }

  /// <summary>The arguments of the functions whose loops or recursion would run too long on the general values, by function.</summary>
  private static readonly Dictionary<string, object[][]> _arguments = new(StringComparer.Ordinal) {
    ["sumTo"] = [[-1], [0], [1], [10], [1000]],
    ["countUntil"] = [[0], [1], [10], [100]],
    ["countDown"] = [[0], [1], [10], [20], [-5]],
    ["stepped"] = [[0, 10, 3], [1, 1, 1], [5, 1, 1], [0, 100, 7], [-10, 10, 5], [2147483640, 2147483647, 2]],
    ["nearMax"] = [[int.MaxValue], [int.MaxValue - 1], [int.MaxValue - 5]],
    ["collatz"] = [[1L], [2L], [3L], [27L], [97L], [-1L], [0L]],
    ["firstPair"] = [[10, 12], [5, 100], [20, 0], [0, 0], [30, 391]],
    ["repeated"] = [[0], [1], [10], [100], [-3]],
    ["factorial"] = [[0], [1], [5], [20], [25], [-3]],
    ["fibonacci"] = [[0], [1], [2], [10], [20]],
    ["ackermann"] = [[0, 0], [1, 2], [2, 3], [3, 3]],
    ["square"] = [[0], [3], [7], [8], [-1]],
    ["table"] = [[0], [1], [5], [10]],
    ["words"] = [[0], [3], [18], [19], [-1]],
    ["blur"] = [[new[] { 10, 20, 30, 40, 50, 60, 70, 80 }, 8], [new[] { 1, 2, 3 }, 3], [new[] { 9, 9, 9, 9 }, 9], [Array.Empty<int>(), 1]],
    ["isqrt"] = [[0L], [1L], [2L], [15L], [16L], [1000000000000L], [4611686018427387903L], [-4L]],
    ["xorshift"] = [[1, 0], [1, 1], [2463534242, 10], [-1, 100], [0, 5]],
    ["power"] = [[2L, 0], [2L, 10], [3L, 40], [-7L, 63], [5L, 64], [0L, 3]],
    ["deinterlace"] = [[new[] { 1, 2, 3, 4 }, new[] { 5, 6 }, new int[6]], [new[] { 255 }, new[] { 0 }, new int[3]], [Array.Empty<int>(), new[] { 1 }, new int[2]]]
  };

  /// <summary>The general values of a type the calls are made of: zero, the small values, both bounds and those next to them.</summary>
  private static object[] Values(IrType type) => type.Kind switch {
    IrTypeKind.Bool => [false, true],
    IrTypeKind.I8 => new sbyte[] { 0, 1, -1, 5, 127, -128 }.Cast<object>().ToArray(),
    IrTypeKind.U8 => new byte[] { 0, 1, 128, 255 }.Cast<object>().ToArray(),
    IrTypeKind.I16 => new short[] { 0, 1, -1, 300, 32767, -32768 }.Cast<object>().ToArray(),
    IrTypeKind.U16 => new ushort[] { 0, 1, 65535 }.Cast<object>().ToArray(),
    IrTypeKind.I32 => new[] { 0, 1, -1, 2, 7, -7, 31, 32, 33, 65521, int.MaxValue, int.MinValue }.Cast<object>().ToArray(),
    IrTypeKind.U32 => new[] { 0u, 1u, 2u, 7u, 0x80000000u, uint.MaxValue }.Cast<object>().ToArray(),
    IrTypeKind.I64 => new[] { 0L, 1L, -1L, 3L, -7L, 1000000007L, long.MaxValue, long.MinValue }.Cast<object>().ToArray(),
    IrTypeKind.U64 => new[] { 0UL, 1UL, 12345UL, 1UL << 63, ulong.MaxValue }.Cast<object>().ToArray(),
    IrTypeKind.F64 => new[] { 0.0, -0.0, 1.5, -2.5, 0.1, 1e10, 3e9, -3e9, 1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity }.Cast<object>().ToArray(),
    IrTypeKind.F32 => new[] { 0f, -0f, 1.5f, -2.5f, 0.1f, 3e9f, float.NaN, float.PositiveInfinity }.Cast<object>().ToArray(),
    IrTypeKind.Array when type.Element == IrType.I8 => [Array.Empty<sbyte>(), new sbyte[] { 97, 98, 99 }, new sbyte[] { -1, 0, 127, -128, 42, 7, 13, 99 }],
    IrTypeKind.Array when type.Element == IrType.I32 => [Array.Empty<int>(), new[] { 5 }, new[] { 3, 1, 4, 1, 5, 9, 2, 6 }, new[] { int.MaxValue, 1, int.MinValue }],
    IrTypeKind.Array when type.Element == IrType.I64 => [Array.Empty<long>(), new[] { 1L, 2L, 3L, 4L }, new[] { long.MaxValue, 1L }],
    IrTypeKind.Array when type.Element == IrType.F64 => [Array.Empty<double>(), new[] { 1.0, -2.5, 0.1, 1e300 }],
    IrTypeKind.Array when type.Element == IrType.Bool => [Array.Empty<bool>(), new[] { true, false, true, true }],
    _ => throw new ArgumentException($"no values of {type}", nameof(type))
  };

  /// <summary>
  /// The calls of a fixture: every public function on the product of the general values of its
  /// parameters, at most 160 tuples spread over it evenly, or on its own arguments where the general
  /// ones would loop too long.
  /// </summary>
  public static IReadOnlyList<PrototypeCall> Cases(IrModule module) {
    var calls = new List<PrototypeCall>();
    foreach (var function in module.Functions.Where(function => function.IsPublic)) {
      var name = NamesOf(module)[function.Name];
      if (_arguments.TryGetValue(name, out var fixedArguments) && (function.Parameters.Count == 0 || fixedArguments[0].Length == function.Parameters.Count)) {
        calls.AddRange(fixedArguments.Select(arguments => new PrototypeCall(function.Name, [.. arguments.Select((argument, index) => Typed(argument, function.Parameters[index].Type))])));
        continue;
      }

      var tuples = function.Parameters.Aggregate((IEnumerable<object[]>)[[]], (product, parameter) => product.SelectMany(tuple => Values(parameter.Type).Select(value => (object[])[.. tuple, value]))).ToList();
      var stride = Math.Max(1, (tuples.Count + 159) / 160);
      calls.AddRange(tuples.Where((_, index) => index % stride == 0).Select(tuple => new PrototypeCall(function.Name, tuple)));
    }

    return calls;
  }

  /// <summary>An argument as the .NET value of the IR type of its parameter.</summary>
  private static object Typed(object argument, IrType type) => type.Kind switch {
    IrTypeKind.I32 => System.Convert.ToInt32(argument is uint word ? unchecked((int)word) : argument, CultureInfo.InvariantCulture),
    IrTypeKind.I64 => System.Convert.ToInt64(argument, CultureInfo.InvariantCulture),
    _ => argument
  };

  /// <summary>One line of a recording: the call and its outcome.</summary>
  public static string Line(PrototypeCall call, string outcome) => $"{call} => {outcome}";

  /// <summary>The lines of a fixture's recording.</summary>
  public static string[] RecordedLines(string fixture) => File.ReadAllLines(RecordingOf(fixture));

  /// <summary>Writes a fixture's recording.</summary>
  public static void Record(string fixture, IEnumerable<string> lines) => File.WriteAllLines(RecordingOf(fixture), lines);

  /// <summary>What the interpreter does for a call of an imported fixture, as a line of a recording.</summary>
  public static string Interpreted(IrModule module, PrototypeCall call) => Line(call, PrototypeFixture.InterpretedOutcome(module, call));

}
