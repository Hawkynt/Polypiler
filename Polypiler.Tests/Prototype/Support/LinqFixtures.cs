using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of LINQ and of JavaScript's array callbacks in <c>tests/fixtures/prototype/sequences</c>
/// (#1329). The C# fixtures are built into this assembly in the namespace
/// <c>Polypiler.Fixtures.Prototype.Sequences</c>, and that build is their oracle; node is the oracle of
/// the JavaScript ones.
/// </summary>
internal static class LinqFixtures {

  /// <summary>The C# fixtures, by the name of their class.</summary>
  public static readonly string[] CSharpFixtures = ["Pipelines", "ByteMedian"];

  /// <summary>The JavaScript fixtures, by their file name.</summary>
  public static readonly string[] ScriptFixtures = ["pipes.mjs", "runLength.mjs"];

  /// <summary>The C# fixture of a class imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string className) {
    var result = CSharpFrontend.Import(className + ".cs", SequenceFixtures.SourceOf(className + ".cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A JavaScript fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule ImportedScript(string file) {
    var result = JavaScriptFrontend.Import(file, SequenceFixtures.SourceOf(file));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(LinqFixtures).Assembly.GetType($"{SequenceFixtures.Namespace}.{className}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>The calls of a C# fixture every implementation is compared on, each with the type it returns.</summary>
  public static IEnumerable<PrototypeCall> Calls(string className) {
    PrototypeCall Call(string method, params object[] arguments) => new(method, arguments) { Returns = Compiled(className, method).ReturnType };
    if (className == "ByteMedian") {
      yield return Call("Median", new byte[] { 7 });
      yield return Call("Median", new byte[] { 9, 1, 200, 4, 4 });
      yield return Call("Median", new byte[] { 255, 0, 3, 3, 100, 101 });
      yield return Call("Median", Array.Empty<byte>());
      yield return Call("Histogram", new byte[] { 3, 1, 3, 2, 3 });
      yield break;
    }

    foreach (var method in new[] { "Deferred", "Trace", "Recount", "Late", "KeyOrder", "Infinite", "ZipTrace", "Flattened", "Pairs", "Indexed", "Nested", "Escaped", "IndexOfNaN", "ContainsNaN", "FromList", "Words", "LazyTrap" })
      yield return Call(method);

    foreach (var a in new[] { new[] { 2147483647, 1 }, new[] { 2147483647, -1, 1 }, [], new[] { -5, 3 }, Long() })
      yield return Call("SumOf", a);

    yield return Call("SumOfLongs", new[] { long.MaxValue, 1L });
    yield return Call("SumOfLongs", new[] { long.MaxValue, -1L, 1L });
    yield return Call("SumOfSingles", new[] { 16777216f, 1f, 1f });
    yield return Call("SumOfSingles", Array.Empty<float>());
    yield return Call("SumOfDoubles", new[] { -0.0 });
    yield return Call("SumOfDoubles", new[] { 0.1, 0.2, 0.3 });
    yield return Call("Stable", new[] { 2, 1, 2, 1 }, new[] { 10, 20, 30, 40 });
    yield return Call("SortedDoubles", new[] { 3, double.NaN, 1, -0.0, 0.0 });
    yield return Call("Descending", new[] { 12, 21, 11, 22, 3 });
    foreach (var a in new[] { new[] { 1, 2, 3, 4 }, [], new[] { 5 }, new[] { 2, 4 } }) {
      yield return Call("FirstOf", a);
      yield return Call("FirstEven", a);
      yield return Call("Terminals", a);
    }

    yield return Call("Extremes", new[] { 2.0, double.NaN, 1.0 });
    yield return Call("Extremes", new[] { double.NaN, 3.0 });
    yield return Call("Extremes", new[] { 0.0, -0.0 });
    foreach (var (skip, take) in new[] { (2, 3), (0, 0), (0, 1), (0, 10), (0, 11), (-1, -1), (9, 5), (10, 1), (11, 1) })
      yield return Call("Window", skip, take);

    foreach (var (start, count) in new[] { (0, -1), (int.MaxValue, 2), (int.MaxValue, 1), (0, 0), (-5, 3) })
      yield return Call("RangeOf", start, count);

    foreach (var n in new[] { -1, 0, 1, 2, 5, 6 })
      yield return Call("TakeCalls", n);

    yield return Call("Zipped", new[] { 1, 2, 3 }, new[] { 4, 5 });
    yield return Call("Zipped", new[] { 1 }, new[] { 4, 5 });
    yield return Call("Query", new[] { 3, 1, 4, 1, 5 });
    yield return Call("Searches", new[] { 1, 2, 3, 4 });
    yield return Call("Searches", new[] { 5 });
  }

  /// <summary>64 numbers with the largest int32 first, 1 at 8 and -1 at 9: the vectorized sum of .NET 8 and later sees them too.</summary>
  private static int[] Long() {
    var a = new int[64];
    a[0] = int.MaxValue;
    a[8] = 1;
    a[9] = -1;
    return a;
  }

}
