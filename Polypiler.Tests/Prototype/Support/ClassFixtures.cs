using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/classes</c>. A C# file there declares classes next to
/// its static class, in the namespace <c>Polypiler.Fixtures.Prototype.Classes</c>; the C# compiler's
/// build of it in this assembly is the oracle, as for every other fixture.
/// </summary>
internal static class ClassFixtures {

  /// <summary>The namespace of the C# fixtures.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Classes";

  /// <summary>The C# fixtures by file name, each with the name of its static class.</summary>
  public static readonly (string File, string Class)[] CSharp = [("Counter", "CounterDemo"), ("Range", "RangeDemo"), ("null", "NullDemo"), ("Adler32", "Adler32Demo")];

  /// <summary>The bytes of the text "Wikipedia", whose Adler-32 is the published 0x11E60398.</summary>
  public static byte[] Wikipedia => [87, 105, 107, 105, 112, 101, 100, 105, 97];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "classes", file);

  /// <summary>The text of a fixture file of the folder, its extension included.</summary>
  public static string SourceOf(string file) => File.ReadAllText(PathOf(file));

  /// <summary>A C# fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string file) {
    var result = CSharpFrontend.Import(file + ".cs", SourceOf(file + ".cs"));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A type of the C# fixtures as the C# compiler built it into this assembly.</summary>
  public static Type Type(string name) => typeof(ClassFixtures).Assembly.GetType($"{Namespace}.{name}", true)!;

  /// <summary>A static method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => Type(className).GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(className, method);

  /// <summary>
  /// The calls every implementation is compared on: the values of the issue, the boundaries of the
  /// counters and the ranges, and values drawn with the fixed seed 2026. The calls on <c>null</c>
  /// are compared by their own scenario, since node reports them as a <c>TypeError</c>, and
  /// <c>MadeByBoth</c>, which reads a static field that parallel tests of .NET change, by its own test.
  /// </summary>
  /// <returns>Each call with the file and the class of its fixture.</returns>
  public static IEnumerable<(string File, string Class, PrototypeCall Call)> Calls() {
    (string, string, PrototypeCall) Call(string file, string className, string method, params object[] arguments)
      => (file, className, new PrototypeCall(method, arguments) { Returns = Compiled(className, method).ReturnType });

    yield return Call("Counter", "CounterDemo", "Run", 10, 3);
    yield return Call("Counter", "CounterDemo", "CountAfter", 10, 3);
    yield return Call("Counter", "CounterDemo", "Run", int.MaxValue, 1);
    yield return Call("Counter", "CounterDemo", "Aliased");
    yield return Call("Counter", "CounterDemo", "SameObject");
    yield return Call("Counter", "CounterDemo", "TwoObjects");
    foreach (var start in new[] { -1, 0, 1, 2 })
      yield return Call("Counter", "CounterDemo", "Even", start);

    yield return Call("Range", "RangeDemo", "ValueEqual", 2, 9);
    yield return Call("Range", "RangeDemo", "Differs", 2, 9);
    yield return Call("Range", "RangeDemo", "EqualsCall", 2, 9);
    yield return Call("Range", "RangeDemo", "SameReference", 2, 9);
    yield return Call("Range", "RangeDemo", "Length", 2, 9);
    yield return Call("Range", "RangeDemo", "Length", int.MinValue, int.MaxValue);
    yield return Call("Range", "RangeDemo", "NullEqual", 3);
    yield return Call("null", "NullDemo", "NotNull", 4);
    yield return Call("Adler32", "Adler32Demo", "OfWikipedia");
    yield return Call("Adler32", "Adler32Demo", "Of", Wikipedia);
    yield return Call("Adler32", "Adler32Demo", "Of", System.Array.Empty<byte>());
    foreach (var split in new[] { 0, 1, 8, 9 })
      yield return Call("Adler32", "Adler32Demo", "InTwoParts", Wikipedia, split);

    var random = new Random(2026);
    for (var draw = 0; draw < 6; ++draw) {
      var (a, b) = (random.Next(-1000, 1000), random.Next(-1000, 1000));
      yield return Call("Counter", "CounterDemo", "Run", a, b);
      yield return Call("Counter", "CounterDemo", "CountAfter", b, a);
      yield return Call("Range", "RangeDemo", "Length", a, b);
      yield return Call("null", "NullDemo", "NotNull", a);
      var bytes = new byte[random.Next(0, 6000)];
      random.NextBytes(bytes);
      yield return Call("Adler32", "Adler32Demo", "Of", bytes);
      yield return Call("Adler32", "Adler32Demo", "InTwoParts", bytes, random.Next(0, bytes.Length + 1));
    }
  }

}
