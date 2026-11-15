using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;

namespace Polypiler.Prototype.Testing;

/// <summary>
/// The fixtures of <c>tests/fixtures/prototype/pointers</c> (#1331): their C# builds in this assembly, compiled with
/// <c>AllowUnsafeBlocks</c>, are the oracle. Out-of-bounds access is undefined behaviour in C#, so the calls .NET is
/// compared on stay in bounds; the traps of the IR are checked by the calls of <see cref="Traps"/>, against the
/// interpreter and JavaScript only.
/// </summary>
internal static class PointerFixtures {

  /// <summary>The bytes 0, 1, …, 15 of the scenarios.</summary>
  public static byte[] D => [.. Enumerable.Range(0, 16).Select(value => (byte)value)];

  /// <summary>The path of a fixture file of the folder, its extension included.</summary>
  public static string PathOf(string file) => Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "pointers", file);

  /// <summary>A C# fixture of the folder imported into IR; fails with its diagnostics when it is refused.</summary>
  public static IrModule Imported(string className) {
    var result = CSharpFrontend.Import(className + ".cs", File.ReadAllText(PathOf(className + ".cs")));
    return result.Module ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
  }

  /// <summary>A method of a fixture class as the C# compiler built it into this assembly.</summary>
  public static MethodInfo Compiled(string className, string method)
    => typeof(PointerFixtures).Assembly.GetType("Polypiler.Fixtures.Prototype." + className, true)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
      ?? throw new MissingMethodException(className, method);

  /// <summary>What the C# build of a fixture does for a call.</summary>
  public static string DotNet(string className, PrototypeCall call) => PrototypeFixture.InvokedOutcome(Compiled(className, call.Method), call);

  /// <summary>
  /// The calls of <c>Pointers.cs</c> every implementation is compared on: the values of the scenarios, the
  /// boundaries in bounds, one call per element type and per origin of a pointer, and offsets drawn with the
  /// fixed seed 2026.
  /// </summary>
  public static IEnumerable<PrototypeCall> Calls() {
    PrototypeCall Call(string method, params object[] arguments) => new(method, arguments) { Returns = Compiled("Pointers", method).ReturnType };

    yield return Call("Checksum", D);
    yield return Call("Checksum", Array.Empty<byte>());
    yield return Call("Checksum", new byte[] { 255 });
    foreach (var at in new[] { 0, 1, 12 })
      yield return Call("Word", D, at);

    yield return Call("Distance", new int[10], 2, 7);
    yield return Call("Distance", new int[10], 7, 2);
    yield return Call("Distance", new int[10], 0, 10);
    foreach (var n in new[] { 3, 0, -4 })
      yield return Call("Stack", n);

    yield return Call("Local");
    yield return Call("EmptyIsNull");
    yield return Call("Bits");
    yield return Call("At", new[] { 4, 5, 6 }, 2);
    yield return Call("At", new[] { 4, 5, 6 }, 0);
    yield return Call("Origins", D);
    foreach (var at in new[] { 0, 7, 14 }) {
      yield return Call("Shorts", D, at);
      yield return Call("UShorts", new byte[] { 0xFF, 0xFE, 0x80, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, at);
    }

    foreach (var at in new[] { 0, 3, 12 }) {
      yield return Call("Ints", D, at);
      yield return Call("Floats", D, at);
    }

    var high = Enumerable.Repeat((byte)0xF0, 16).ToArray();
    foreach (var at in new[] { 0, 5, 8 }) {
      yield return Call("Longs", D, at);
      yield return Call("Longs", high, at);
      yield return Call("ULongs", high, at);
      yield return Call("Doubles", D, at);
    }

    yield return Call("Store", D, 3, 0x0102030405060708L);
    yield return Call("Store", D, 8, -1L);
    yield return Call("Wide", new ulong[] { 1, 2, ulong.MaxValue });
    yield return Call("Wide", Array.Empty<ulong>());
    foreach (var i in new[] { -1, 0, 3, 4 })
      yield return Call("Inside", new[] { 1, 2, 3 }, i);

    yield return Call("Through", new[] { 9, 8, 7 });
    yield return Call("Passed", new[] { 1, 2, 3, 4 });
    yield return Call("Passed", Array.Empty<int>());
    yield return Call("Apart", new[] { 1 }, new[] { 1 });

    var random = new Random(2026);
    for (var draw = 0; draw < 8; ++draw) {
      var bytes = new byte[16];
      random.NextBytes(bytes);
      yield return Call("Word", bytes, random.Next(0, 13));
      yield return Call("Longs", bytes, random.Next(0, 9));
      yield return Call("Doubles", bytes, random.Next(0, 9));
      yield return Call("Checksum", bytes);
    }
  }

  /// <summary>
  /// The calls whose outcome is a trap of the IR, with the trap: reads past either end of the region, a read that
  /// straddles the end, the null pointer of an empty array, and an ordering across two regions. .NET is no oracle
  /// for them.
  /// </summary>
  public static IEnumerable<(PrototypeCall Call, IrTrap Trap)> Traps() {
    yield return (new("At", [new[] { 4, 5, 6 }, 3]), IrTrap.IndexOutOfRange);
    yield return (new("At", [new[] { 4, 5, 6 }, -1]), IrTrap.IndexOutOfRange);
    yield return (new("Word", [D, 13]), IrTrap.IndexOutOfRange);
    yield return (new("Word", [D, -1]), IrTrap.IndexOutOfRange);
    yield return (new("Before", [new[] { 1 }, new[] { 2 }]), IrTrap.InvalidOperation);
    yield return (new("At", [Array.Empty<int>(), 0]), IrTrap.NullReference);
  }

}
