using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/records</c>. A file there declares structs next to its
/// static class, so the class is not named after the file; the C# compiler's build of it in this
/// assembly is the oracle, as for every other fixture.
/// </summary>
internal static class RecordFixtures {

  /// <summary>The C# fixtures by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Geometry", "Geometry"), ("Money", "Ledger"), ("U128", "Wide128")];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "records", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>A C# fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string file) {
    var result = CSharpFrontend.Import(file + ".cs", SourceOf(file + ".cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>
  /// The calls every implementation is compared on: the values of the issue, the boundaries of the
  /// arrays and of equality on floating-point fields, and values drawn with the fixed seed 2026.
  /// </summary>
  /// <returns>Each call with the file and the class of its fixture, and the type the method returns.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    yield return Call("Geometry", "Geometry", "Copy");
    yield return Call("Geometry", "Geometry", "ArrayCopy");
    yield return Call("Geometry", "Geometry", "DotOf", 1.0, 2.0, 3.0, 4.0);
    foreach (var n in new[] { -1, 0, 1, 2, 7 })
      yield return Call("Geometry", "Geometry", "Sum", n);

    foreach (var index in new[] { -1, 0, 1, 2 })
      yield return Call("Geometry", "Geometry", "At", index);

    yield return Call("Money", "Ledger", "Same", 150L, 150L);
    yield return Call("Money", "Ledger", "Same", 150L, 151L);
    yield return Call("Money", "Ledger", "Differs", 150L, 978);
    yield return Call("Money", "Ledger", "Differs", 150L, 840);
    yield return Call("Money", "Ledger", "EqualsCall", 7L, 7L);
    yield return Call("Money", "Ledger", "EqualsCall", 7L, -7L);
    yield return Call("Money", "Ledger", "NaNEqual");
    yield return Call("Money", "Ledger", "PairEqual", double.NaN, 1.0, double.NaN, 1.0);
    yield return Call("Money", "Ledger", "PairEqual", 0.0, 1.0, -0.0, 1.0);
    yield return Call("Money", "Ledger", "PairEqual", 1.0, 1.0, Math.BitIncrement(1.0), 1.0);
    yield return Call("Money", "Ledger", "Deposit", 100L, 50L);
    yield return Call("Money", "Ledger", "Deposit", long.MaxValue, 1L);

    yield return Call("U128", "Wide128", "AddLo", ulong.MaxValue, 0UL, 1UL, 0UL);
    yield return Call("U128", "Wide128", "AddHi", ulong.MaxValue, 0UL, 1UL, 0UL);
    yield return Call("U128", "Wide128", "AddHi", ulong.MaxValue, ulong.MaxValue, 1UL, 0UL);

    var random = new Random(2026);
    for (var draw = 0; draw < 8; ++draw) {
      var halves = Enumerable.Range(0, 4).Select(_ => ((ulong)random.NextInt64() << 1) | (uint)random.Next(2)).Cast<object>().ToArray();
      yield return Call("U128", "Wide128", "AddLo", halves);
      yield return Call("U128", "Wide128", "AddHi", halves);
      var numbers = Enumerable.Range(0, 4).Select(_ => Math.Round(random.NextDouble() * 200 - 100, 3)).Cast<object>().ToArray();
      yield return Call("Geometry", "Geometry", "DotOf", numbers);
      yield return Call("Money", "Ledger", "PairEqual", numbers[0], numbers[1], numbers[draw % 2 == 0 ? 0 : 2], numbers[1]);
      yield return Call("Money", "Ledger", "Same", random.NextInt64(), random.NextInt64(2) == 0 ? 5L : random.NextInt64());
    }
  }

  /// <summary>A method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(RecordFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + className, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

}
