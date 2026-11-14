using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/spans</c> (#1325): their C# builds in this assembly are the
/// oracle, <c>node</c> is the oracle of the JavaScript ones. A view's traps are told apart from the bounds
/// trap of an array here: .NET throws an <see cref="ArgumentOutOfRangeException"/> for a slice outside its
/// source and an <see cref="ArgumentException"/> naming the destination for a copy into a shorter span,
/// both the trap <see cref="IrTrap.ArgumentOutOfRange"/>; <see cref="PrototypeFixture"/> keeps counting the
/// first as the bounds trap, as the endian accesses of arrays always have.
/// </summary>
internal static class SpanFixtures {

  /// <summary>The bytes 0, 1, …, 15 of the scenarios.</summary>
  public static byte[] D => [.. Enumerable.Range(0, 16).Select(value => (byte)value)];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "spans", file);

  /// <summary>A C# fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string className) {
    var result = CSharpFrontend.Import(className + ".cs", File.ReadAllText(PathOf(className + ".cs")));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(SpanFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + className, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>What a static method does for a call, with the traps of views told apart.</summary>
  public static string Outcome(MethodInfo method, PrototypeCall call) {
    var arguments = call.FreshArguments();
    try {
      return PrototypeFixture.Outcome(method.Invoke(null, arguments), arguments);
    } catch (TargetInvocationException exception) when (TrapOf(exception.InnerException) is { } trap) {
      return PrototypeFixture.Trap(trap);
    }
  }

  /// <summary>What the C# build of a fixture does for a call.</summary>
  public static string DotNet(string className, PrototypeCall call) => Outcome(Compiled(className, call.Method), call);

  /// <summary>
  /// The trap of a .NET exception thrown through a view; <see langword="null"/> for any other exception.
  /// <see cref="ArgumentOutOfRangeException"/> comes before <see cref="ArgumentException"/>, which it is one of.
  /// </summary>
  public static IrTrap? TrapOf(Exception? exception) => exception switch {
    ArgumentOutOfRangeException => IrTrap.ArgumentOutOfRange,
    ArgumentException { ParamName: "destination" } => IrTrap.ArgumentOutOfRange,
    IndexOutOfRangeException or ArgumentException => IrTrap.IndexOutOfRange,
    OverflowException => IrTrap.IntegerOverflow,
    _ => null
  };

  /// <summary>
  /// The calls of <c>Spans.cs</c> every implementation is compared on: the values of the scenarios, the
  /// boundaries of the test design, one call per element type and per origin of a view, and offsets and
  /// lengths drawn with the fixed seed 2026. A negative <c>stackalloc</c> length is not among them: .NET ends
  /// the process on it.
  /// </summary>
  public static IEnumerable<PrototypeCall> Calls() {
    PrototypeCall Call(string method, params object[] arguments) => new(method, arguments) { Returns = Compiled("Spans", method).ReturnType };

    foreach (var (start, length) in new[] { (4, 4), (12, 4), (16, 0), (13, 4), (-1, 2), (0, 17), (0, 16), (17, 0) })
      yield return Call("Sum", D, start, length);

    yield return Call("Word", D);
    foreach (var n in new[] { 8, 7, 3, 0, 16, 17 })
      yield return Call("CastXor", D, n);

    yield return Call("Tail", D);
    foreach (var index in new[] { 15, 16, -1, 0 })
      yield return Call("At", D, index);

    yield return Call("Overlap");
    yield return Call("OverlapBack");
    foreach (var n in new[] { 3, 4, 5 }) {
      yield return Call("CopyInto", n);
      yield return Call("TryInto", n);
    }

    yield return Call("NaNEqual");
    yield return Call("Same", new[] { double.NaN, 0.0 }, new[] { double.NaN, -0.0 });
    yield return Call("Same", new[] { 1.0, 2.0 }, new[] { 1.0, 2.5 });
    yield return Call("Same", new[] { 1.0 }, new[] { 1.0, 2.0 });
    yield return Call("Same", Array.Empty<double>(), Array.Empty<double>());
    yield return Call("ReadFrom", D);
    yield return Call("WriteAt", D, 3, 0x11223344u);
    yield return Call("WriteAt", D, 10, 0xA1B2C3D4u);
    yield return Call("WriteAt", D, 11, 1u);
    yield return Call("WriteAt", D, 17, 1u);
    foreach (var n in new[] { 0, 1, 4 })
      yield return Call("Zeroed", n);

    yield return Call("Fixed");
    yield return Call("Origins", D);
    yield return Call("Filled", 3);
    yield return Call("Filled", -9);
    yield return Call("Doubled", new uint[] { 1, 2, 3 });
    yield return Call("Doubled", new uint[] { 1 });
    yield return Call("Bytes", (byte)255);
    yield return Call("Shorts", (short)-7);
    yield return Call("Ints", int.MaxValue);
    yield return Call("Words", uint.MaxValue);
    yield return Call("Longs", long.MinValue);
    yield return Call("Ulongs", ulong.MaxValue);
    yield return Call("Singles", 1.5f);
    yield return Call("Singles", float.NaN);
    yield return Call("Doubles", double.NaN);
    yield return Call("Doubles", -0.0);
    yield return Call("ShortBytes", new short[] { 0x102, -2, 7 });
    yield return Call("ShortBytes", Array.Empty<short>());

    var random = new Random(2026);
    for (var draw = 0; draw < 12; ++draw) {
      var start = random.Next(-2, 19);
      yield return Call("Sum", D, start, random.Next(-2, 19 - Math.Max(start, 0)));
      yield return Call("CastXor", D, random.Next(0, 17));
    }
  }

  /// <summary>The bytes of "Wikipedia" at the offset 5 of 20 bytes, whose Adler-32 is <c>0x11E60398</c>.</summary>
  public static byte[] Wikipedia {
    get {
      var buffer = new byte[20];
      "Wikipedia"u8.ToArray().CopyTo(buffer, 5);
      return buffer;
    }
  }

}
