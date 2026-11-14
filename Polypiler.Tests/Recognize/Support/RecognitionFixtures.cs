using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize;
using Polypiler.Core.Recognize;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/recognize</c>: per rule one C# and one JavaScript file
/// with every pattern the rule lists and the near misses it must leave alone, each function preceded
/// by a line <c>// expect: O0100 O0100</c> (or <c>// expect: -</c>) naming the rule applications
/// expected in it; and the spelled-out SipHash and xxHash. Per function the calls of the
/// equivalence test: every combination of the values of narrow parameters where there are few
/// enough of them, otherwise every combination of the edges and 10 000 random tuples from the seed 2026.
/// </summary>
internal static partial class RecognitionFixtures {

  /// <summary>The seed of the random tuples.</summary>
  public const int Seed = 2026;

  /// <summary>The number of random tuples per function.</summary>
  public const int RandomTuples = 10_000;

  /// <summary>The most calls a function is tried with exhaustively.</summary>
  private const int Exhaustive = 1 << 23;

  /// <summary>The directory of the fixtures.</summary>
  public static string Directory { get; } = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "recognize");

  /// <summary>The C# fixtures, by the name of their class and file.</summary>
  public static string[] CSharp => [.. System.IO.Directory.GetFiles(Directory, "Recognize*.cs").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

  /// <summary>The JavaScript fixtures, by the name of their file without the extension.</summary>
  public static string[] Scripts => [.. System.IO.Directory.GetFiles(Directory, "*.js").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

  /// <summary>Every fixture with its file name.</summary>
  public static string[] Files => [.. CSharp.Select(name => name + ".cs"), .. Scripts.Select(name => name + ".js")];

  /// <summary>The text of a fixture file.</summary>
  public static string SourceOf(string file) => File.ReadAllText(Path.Combine(Directory, file));

  /// <summary>A fixture file imported into IR.</summary>
  public static IrModule Imported(string file) => file.EndsWith(".js", StringComparison.Ordinal)
    ? JavaScriptFixture.Import(file, SourceOf(file))
    : PrototypeFixture.Import(file, SourceOf(file));

  /// <summary>The options every fixture test optimizes with: verification after each application, and a target that prefers the remainder from the quotient.</summary>
  public static OptimizerOptions Options { get; } = new() { Verify = true, Target = new RuleTarget(true) };

  /// <summary>The rule applications expected in each function of a fixture file, sorted.</summary>
  public static IReadOnlyDictionary<string, string[]> Expectations(string file) {
    var expected = new Dictionary<string, string[]>();
    string[]? pending = null;
    foreach (var line in SourceOf(file).Split('\n')) {
      if (ExpectLine().Match(line) is { Success: true } expect) {
        pending = [.. expect.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(id => id != "-").Order(StringComparer.Ordinal)];
        continue;
      }

      if (pending is not null && FunctionLine().Match(line) is { Success: true } function) {
        expected.Add(function.Groups[1].Value + function.Groups[2].Value, pending);
        pending = null;
      }
    }

    return expected;
  }

  [GeneratedRegex(@"^\s*// expect:(.*)$")]
  private static partial Regex ExpectLine();

  [GeneratedRegex(@"^\s*(?:public static [^(=]*?\b(\w+)\(|export function (\w+)\()")]
  private static partial Regex FunctionLine();

  /// <summary>
  /// What the interpreter does for a call: the outcome as <see cref="PrototypeFixture.InterpretedOutcome"/>
  /// writes it, and after a trap the arrays as the trap left them, so that a partial write counts.
  /// </summary>
  public static string Outcome(IrModule module, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return PrototypeFixture.Outcome(IrInterpreter.Call(module, call.Method, arguments), arguments);
    } catch (IrTrapException exception) {
      return PrototypeFixture.Trap(exception.Trap) + " | " + string.Join(" | ", arguments.Where(argument => argument is Array).Select(PrototypeFixture.Show));
    }
  }

  /// <summary>The calls of a function: exhaustive where it is small, else the edges and the random tuples.</summary>
  public static IEnumerable<PrototypeCall> Calls(IrFunction function) {
    var parameters = function.Parameters.Select(parameter => parameter.Type).ToArray();
    var indices = Indices(function);
    var narrow = parameters.Any(type => type.IsInteger && type.Bits <= 16);
    var all = parameters.Select((type, index) => Values(parameters, indices, index, narrow)).ToArray();
    if (all.All(values => values is not null) && all.Aggregate(1L, (product, values) => product * values!.Length) <= Exhaustive)
      return Product(function.Name, all!);

    var edges = parameters.Select((type, index) => Edges(parameters, indices, index)).ToArray();
    var random = new Random(Seed);
    return Product(function.Name, edges).Concat(Enumerable.Range(0, RandomTuples)
      .Select(_ => new PrototypeCall(function.Name, [.. parameters.Select((type, index) => RandomOf(parameters, indices, index, random))])));
  }

  /// <summary>Whether a function is tried exhaustively.</summary>
  public static bool IsExhaustive(IrFunction function) {
    var parameters = function.Parameters.Select(parameter => parameter.Type).ToArray();
    var indices = Indices(function);
    var narrow = parameters.Any(type => type.IsInteger && type.Bits <= 16);
    var all = parameters.Select((type, index) => Values(parameters, indices, index, narrow)).ToArray();
    return all.All(values => values is not null) && all.Aggregate(1L, (product, values) => product * values!.Length) <= Exhaustive;
  }

  private static IEnumerable<PrototypeCall> Product(string method, object[][] values) {
    IEnumerable<object[]> tuples = [[]];
    foreach (var choices in values)
      tuples = tuples.SelectMany(tuple => choices.Select(value => (object[])[.. tuple, value]));

    return tuples.Select(tuple => new PrototypeCall(method, tuple));
  }

  /// <summary>The bytes the array parameters are tried with.</summary>
  private static readonly byte[][] _arrays = [
    [], [0x12], [0x78, 0x56, 0x34, 0x12], [0xFF, 0x80, 0x7F, 0x00, 0x01, 0xFE, 0xAA, 0x55], BitsFixtures.Counting(11)
  ];

  /// <summary>Which parameters of a function are array indices: an <c>i32</c> named <c>i</c> after an array.</summary>
  private static bool[] Indices(IrFunction function) => [.. function.Parameters.Select((parameter, index)
    => parameter.Type == IrType.I32 && parameter.Name == "i" && function.Parameters.Take(index).Any(earlier => earlier.Type.IsArray))];

  /// <summary>Every value of a narrow parameter, the values of an exhaustive run for a count or an index, or <see langword="null"/> for a wide value.</summary>
  private static object[]? Values(IrType[] parameters, bool[] indices, int index, bool narrow) {
    var type = parameters[index];
    if (type.IsArray)
      return Arrays(type);

    if (indices[index])
      return [.. Enumerable.Range(-1, 14).Cast<object>(), int.MaxValue, int.MinValue];

    if (type.IsInteger && type.Bits <= 16)
      return [.. Enumerable.Range(0, 1 << type.Bits).Select(value => IntegerCases.FromPattern(type, (ulong)value))];

    if (type == IrType.I32 && narrow)
      return [.. Enumerable.Range(-1, 67).Cast<object>(), int.MaxValue, int.MinValue, 100];

    return null;
  }

  private static object[] Arrays(IrType type) => type.Element!.Kind switch {
    IrTypeKind.U8 => [.. _arrays],
    _ => [Array.CreateInstance(IntegerCases.HostType(type.Element), 0), Typed(type.Element, [0x12345678, 0x80000001, 0xFFFFFFFF, 0])]
  };

  private static Array Typed(IrType element, ulong[] patterns) {
    var array = Array.CreateInstance(IntegerCases.HostType(element), patterns.Length);
    for (var index = 0; index < patterns.Length; ++index)
      array.SetValue(IntegerCases.FromPattern(element, patterns[index]), index);

    return array;
  }

  private static object[] Edges(IrType[] parameters, bool[] indices, int index) {
    var type = parameters[index];
    if (type.IsArray)
      return Arrays(type);

    if (indices[index])
      return [-1, 0, 1, 3, 4, 7, 8, 9, 10, int.MaxValue];

    if (type == IrType.F64)
      return [0.0, -0.0, 1.5, -2.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300];

    return type == IrType.I32 && parameters.Any(other => other.IsInteger && other != IrType.I32)
      ? [.. IntegerCases.Edges(type), 4, 5, 7, 8, 13, 15, 16, 17, 31, 32, 33, 63, 64, 65, -1, -31, -32, -33]
      : IntegerCases.Edges(type);
  }

  private static object RandomOf(IrType[] parameters, bool[] indices, int index, Random random) {
    var type = parameters[index];
    if (type.IsArray) {
      var length = random.Next(0, 13);
      return Typed(type.Element!, [.. Enumerable.Range(0, length).Select(_ => (ulong)random.NextInt64(long.MinValue, long.MaxValue))]);
    }

    if (indices[index])
      return random.Next(-2, 15);

    if (type == IrType.F64)
      return (random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-10, 70));

    var pattern = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
    return IntegerCases.FromPattern(type, random.Next(4) == 0 ? pattern % 130 - (type.IsSigned ? 65UL : 0UL) : pattern);
  }

}
