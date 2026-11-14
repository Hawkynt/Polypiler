using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/optional</c>. The C# compiler's build of
/// <c>Find.cs</c> in this assembly is the oracle of the C# fixture, <c>node</c> that of the
/// JavaScript ones.
/// </summary>
internal static class OptionalFixtures {

  /// <summary>The namespace of the C# fixture.</summary>
  public const string Namespace = "Polypiler.Fixtures.Prototype.Optional";

  /// <summary>The static class of the C# fixture.</summary>
  public const string Class = "OptionalDemo";

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "optional", file);

  /// <summary>The C# fixture imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported() {
    var result = CSharpFrontend.Import("Find.cs", File.ReadAllText(PathOf("Find.cs")));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A static method of the C# fixture as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string method)
    => typeof(OptionalFixtures).Assembly.GetType($"{Namespace}.{Class}", true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(Class, method);

  /// <summary>
  /// The calls every implementation is compared on: the values of the issue, every lifted operator
  /// with none and a value on each side, the boundaries of <c>??</c> - 0, NaN, -0.0 - and the
  /// counted calls that prove an operand evaluated once.
  /// </summary>
  public static IEnumerable<PrototypeCall> Calls() {
    PrototypeCall Call(string method, params object?[] arguments) => new(method, arguments!) { Returns = Compiled(method).ReturnType };

    int[] Values() => [4, 5, 6];
    yield return Call("FindOr", Values(), 6);
    yield return Call("FindOr", Values(), 7);
    yield return Call("FindOr", Values(), 4);
    yield return Call("FindOr", Array.Empty<int>(), 0);
    foreach (var a in new int?[] { null, 0, 5, int.MaxValue }) {
      yield return Call("Next", a);
      yield return Call("Less", a);
      yield return Call("IsNull", a);
      yield return Call("Or5", a);
      yield return Call("Negated", a);
      yield return Call("Complement", a);
      yield return Call("Widened", a);
      yield return Call("Shifted", a, 3);
      yield return Call("HasValue", a);
      yield return Call("OrDefault", a);
      yield return Call("AtMost", a, 0);
      yield return Call("AssignedValue", a);
      foreach (var b in new int?[] { null, 0, 5 }) {
        yield return Call("Lifted", a, b);
        yield return Call("Equal", a, b);
        yield return Call("NotEqual", a, b);
        yield return Call("Greater", a, b);
      }
    }

    foreach (var b in new bool?[] { null, true, false })
      yield return Call("Inverted", b);

    foreach (var mask in new uint?[] { null, 0x1234u })
      yield return Call("Masked", mask);

    foreach (var d in new double?[] { null, 3.0, -0.0, double.NaN })
      yield return Call("Halved", d);

    foreach (var l in new long?[] { null, 7L, 1L << 40 })
      yield return Call("Cast", l);

    yield return Call("Assigned");
    yield return Call("Length", (int[]?)null);
    yield return Call("Length", new int[3]);
    yield return Call("Element", (int[]?)null, 0);
    yield return Call("Element", new[] { 8, 9 }, 1);
    yield return Call("IsMissing", (int[]?)null);
    yield return Call("IsMissing", Array.Empty<int>());
    yield return Call("IsThere", (int[]?)null);
    yield return Call("IsThere", new int[1]);
    yield return Call("ValueOfNone");
    yield return Call("Unwrapped", 3);
    yield return Call("Unwrapped", (int?)null);
    yield return Call("NaNEqualsItself", double.NaN);
    yield return Call("NaNEqualsItself", 1.5);
    yield return Call("Chosen", true);
    yield return Call("Chosen", false);
    foreach (var present in new[] { true, false }) {
      yield return Call("CountedLength", new int[1], present);
      yield return Call("CountedCoalesce", new int[1], present);
      yield return Call("CountedLifted", new int[1], present);
      yield return Call("CountedLess", new int[1], present);
      yield return Call("CountedEqual", new int[1], present);
    }
  }

}
