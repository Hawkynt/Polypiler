using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Globals;

/// <summary>
/// The static fields the C# frontend imports as globals of the module, the construct table of
/// #1316 row by row, and the fixtures of <c>tests/fixtures/prototype/state</c>, whose sequences of
/// calls the interpreter computes as .NET does.
/// </summary>
[TestFixture]
public sealed class CSharpGlobalImportTests {

  private static IrModule Import(string methods) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result.Module!;
  }

  private static IReadOnlyList<object?> Calls(string methods, params string[] functions) {
    var instance = IrInterpreter.Instantiate(Import(methods));
    return [.. functions.Select(function => instance.Call(function))];
  }

  /// <summary>Test design 1: every fixture computes in the interpreter, call after call in one instance, what .NET computes of the fixture.</summary>
  [TestCaseSource(typeof(StateFixtures), nameof(StateFixtures.All))]
  public void Fixture_RunsInTheInterpreterLikeDotNet((string File, string Class, string Script) fixture) {
    var calls = StateFixtures.Sequence(fixture.File);
    var expected = StateFixtures.Expected(fixture.File);
    Assert.That(StateFixtures.Interpreted(StateFixtures.ImportedCSharp(fixture.File), calls), Is.EqualTo(expected));
    Assert.That(StateFixtures.Compiled(StateFixtures.SourceOf(fixture.File + ".cs"), calls), Is.EqualTo(expected));
  }

  /// <summary>The globals of the fixtures, in source order, each with what initialises it.</summary>
  [Test]
  public void TheFixtures_HaveTheirFieldsAsGlobals() {
    var xorshift = StateFixtures.ImportedCSharp("Xorshift32");
    Assert.That(xorshift.Globals.Select(global => (global.Name, global.Type)), Is.EqualTo(new[] { ("_state", IrType.U32), ("_calls", IrType.I32) }));
    Assert.That(xorshift.Globals[0].Initializer, Is.EqualTo(new IrConst(IrType.U32, 2463534242)));
    Assert.That(xorshift.Globals[1].Initializer, Is.Null);
    var lazy = StateFixtures.ImportedCSharp("Lazy");
    Assert.That(lazy.Globals.Select(global => global.Type), Is.EqualTo(new[] { IrType.Array(IrType.U32), IrType.Bool }));
    Assert.That(lazy.Constants, Is.Empty);
  }

  /// <summary>Test design 3: every class of initializer, each with the value C# gives the field.</summary>
  [TestCase("static int x; public static int F() => x;", 0)]
  [TestCase("static readonly int x; public static int F() => x;", 0)]
  [TestCase("static uint x = 7; public static uint F() => x;", 7u)]
  [TestCase("static long x = 6 * 7; public static long F() => x;", 42L)]
  [TestCase("const int K = 5; static int x = K + 1; public static int F() => x;", 6)]
  [TestCase("static int a = 2; static int b = a * 3; public static int F() => b;", 6)]
  [TestCase("static readonly int x = G(4); private static int G(int a) => a + a; public static int F() => x;", 8)]
  [TestCase("static readonly int[] t = { G(), 2 }; private static int G() => 1; public static int F() => t[0] + t[1];", 3)]
  [TestCase("static readonly byte[] t = new byte[4]; public static int F() => t.Length;", 4)]
  [TestCase("static int n = 3; static readonly double[] t = new double[n]; public static int F() => t.Length;", 3)]
  [TestCase("static bool b = !false; public static bool F() => b;", true)]
  [TestCase("static uint x = unchecked(0u - 1u); public static uint F() => x;", uint.MaxValue)]
  public void EveryKindOfInitializer_GivesTheValueOfCSharp(string methods, object expected)
    => Assert.That(Calls(methods, "F").Single(), Is.EqualTo(expected));

  /// <summary>Construct table: reads, assignments, steps and compound assignments, also of <c>Class.field</c>, change the field for the next call.</summary>
  [TestCase("static int x; public static int F() { x += 5; return x; }", 5, 10)]
  [TestCase("static int x; public static int F() { Snippet.x -= 2; return Snippet.x; }", -2, -4)]
  [TestCase("static int x; public static int F() => ++x;", 1, 2)]
  [TestCase("static int x; public static int F() => x++;", 0, 1)]
  [TestCase("static int x; public static int F() { x--; return x; }", -1, -2)]
  [TestCase("static int x; public static int F() { x = x * 2 + 1; return x; }", 1, 3)]
  [TestCase("static byte x = 254; public static byte F() => ++x;", (byte)255, (byte)0)]
  [TestCase("static ulong x = ulong.MaxValue - 1; public static ulong F() => ++x;", ulong.MaxValue, 0UL)]
  [TestCase("static int x = int.MaxValue - 1; public static int F() { x += 1; return x; }", int.MaxValue, int.MinValue)]
  [TestCase("static double x = 0.5; public static double F() { x *= 2; return x; }", 1.0, 2.0)]
  [TestCase("static readonly uint[] t = { 1, 2 }; public static uint F() { t[0] += 3; return t[0]; }", 4u, 7u)]
  [TestCase("static readonly uint[] t = { 1, 2 }; public static uint F() => t[1]++;", 2u, 3u)]
  [TestCase("static readonly uint[] t = { 1, 2 }; public static uint F() { System.Array.Fill(t, t[0] + 1u); return t[1]; }", 2u, 3u)]
  [TestCase("static readonly uint[] t = { 1, 2 }; public static uint F() { System.Array.Clear(t); t[0]++; return t[0]; }", 1u, 1u)]
  [TestCase("static readonly int[] t = { 1, 2 }; public static int F() { System.Array.Copy(new[] { t[1] + 1 }, 0, t, 1, 1); return t[1]; }", 3, 4)]
  [TestCase("static int g = 1; static readonly int[] t = new int[3]; public static int F() { t[g] += 5; return t[1]; }", 5, 10)]
  public void TheStateOfAField_LastsFromCallToCall(string methods, object first, object second)
    => Assert.That(Calls(methods, "F", "F"), Is.EqualTo(new[] { first, second }));

  /// <summary>Test design 5: a local of the name of a field hides it, as in C#; the field is still read as <c>Class.field</c>.</summary>
  [Test]
  public void ALocalOfTheNameOfAField_HidesIt() {
    var module = Import("static int x = 5; public static int F() { int x = 1; Snippet.x += x; return x + Snippet.x; }");
    var instance = IrInterpreter.Instantiate(module);
    Assert.That((instance.Call("F"), instance.Call("F")), Is.EqualTo((7, 8)));
    Assert.That(module.Functions[0].Locals.Single().Name, Is.EqualTo("x"));
  }

  /// <summary>A table only read stays a constant; one whose elements are written is a global, also when a negated element is read.</summary>
  [Test]
  public void OnlyAWrittenTable_IsAGlobal() {
    var read = Import("static readonly int[] t = { 1, -2 }; public static int F() => -t[1] + t.Length;");
    Assert.That((read.Constants.Count, read.Globals.Count), Is.EqualTo((1, 0)));
    var written = Import("static readonly int[] t = { 1, -2 }; public static int F() => -t[1]--;");
    Assert.That((written.Constants.Count, written.Globals.Single().Name), Is.EqualTo((0, "t")));
    var internalField = Import("internal static int counter; public static int F() => ++counter;");
    Assert.That(internalField.Globals.Single().Name, Is.EqualTo("counter"));
  }

  /// <summary>Test design 7: what the construct table refuses, each named in the diagnostic.</summary>
  [TestCase("public static int Counter; public static int F() => ++Counter;", "a public mutable static field Counter")]
  [TestCase("public static readonly int[] T = new int[2]; public static int F() => ++T[0];", "a public mutable static field T")]
  [TestCase("static int x; static Snippet() { x = 1; } public static int F() => x;", "a static constructor")]
  [TestCase("static int X { get; set; } public static int F() => X;", "a static property")]
  [TestCase("[System.ThreadStatic] static int x; public static int F() => x;", "the attribute [System.ThreadStatic] on a field")]
  [TestCase("static volatile int x; public static int F() => x;", "volatile")]
  [TestCase("static int x; public static int F() => System.Threading.Interlocked.Increment(ref x);", "Interlocked.Increment")]
  [TestCase("static int x; public static int F() { lock (new object()) { return x; } }", "LockStatement")]
  [TestCase("static int x = G(); static int y; private static int G() => y; public static int F() => x;", "an initializer that calls G, which reads or writes a static field")]
  [TestCase("static int x = G(); static int y; private static int G() => H(); private static int H() { y = 1; return 2; } public static int F() => x;",
    "an initializer that calls G, which reads or writes a static field")]
  [TestCase("static int x = y; static int y = 1; public static int F() => x;", "an initializer that reads the field y, which is not initialised before it")]
  [TestCase("static int x = x + 1; public static int F() => x;", "an initializer that reads the field x, which is not initialised before it")]
  [TestCase("static int[] t = new int[2]; public static void F() { t = new int[3]; }", "an assignment to the array field t")]
  [TestCase("static int[] t; public static int F() => t.Length;", "an array field without an initializer, t")]
  [TestCase("static readonly int[] t = { 1, 2 }; public static int F() => System.Array.IndexOf(t, 2);", "Array.IndexOf")]
  public void Construct_IsRefusedByName(string methods, string what) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(methods));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)), Has.Some.EqualTo((CSharpFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

}
