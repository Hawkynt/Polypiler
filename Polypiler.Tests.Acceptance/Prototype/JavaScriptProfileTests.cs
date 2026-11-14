using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Acceptance.Prototype;

/// <summary>
/// The scenarios of <c>tests/features/prototype/javascript-profiles.feature</c> and the test design
/// of the JavaScript profiles, with the real <c>polypiler</c> executable and the generated file run
/// by <c>node</c>. The harness is the one of <see cref="CSharpToJavaScriptTests"/>, run once per
/// profile: the expected outcome of a call is what the fixture does after the C# compiler built it
/// into this assembly or, for a method that is no fixture, what <c>node</c> does with the file of
/// the default profile. Every generated file is also parsed at the level it claims.
/// <para>
/// Every test here starts processes, so the fixture is in the <c>EndToEnd</c> tier. The tests share
/// nothing but the cache of what the compiled fixtures do, so they run side by side.
/// </para>
/// </summary>
[TestFixture, Category("Acceptance"), Category("EndToEnd"), FixtureLifeCycle(LifeCycle.InstancePerTestCase), Parallelizable(ParallelScope.All)]
public sealed class JavaScriptProfileTests {

  /// <summary>What the compiled fixture does for every call of a fixture, computed once for all profiles.</summary>
  private static readonly ConcurrentDictionary<string, Lazy<(IReadOnlyList<PrototypeCall> Calls, string[] Compiled)>> _expected = new();

  private string _directory = "";

  [SetUp]
  public void CreateWorkingDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), "polypiler-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void DeleteWorkingDirectory() => Directory.Delete(this._directory, true);

  /// <summary>Every fixture in every profile.</summary>
  private static IEnumerable<TestCaseData> FixturesAndProfiles()
    => from fixture in PrototypeFixture.Names
       from profile in ProfileCase.All
       select new TestCaseData(fixture, profile).SetName($"{{m}}({fixture}, {profile})");

  /// <summary>The profiles that lack something the default profile has, one per set of replacements.</summary>
  private static IEnumerable<ProfileCase> Replacing() => ProfileCase.All.Where(profile => profile.Module == "cjs" && profile.Level != "es2022");

  /// <summary>One profile per language level, with and without typed arrays at ECMAScript 5: what the arithmetic depends on.</summary>
  private static IEnumerable<ProfileCase> Levels() => [.. Replacing(), ProfileCase.Default];

  /// <summary>The usage error of the scenario "An ES module is not available at ES5", word for word, and the other command lines that name no profile (test design 8).</summary>
  [TestCase(new[] { "--profile", "es5", "--module", "esm" }, "the profile 'es5' has no ES modules")]
  [TestCase(new[] { "--profile", "es3" }, "unknown JavaScript profile 'es3'")]
  [TestCase(new[] { "--module", "amd" }, "unknown module format 'amd'")]
  [TestCase(new[] { "--module", "iife" }, "needs the name of its global object")]
  [TestCase(new[] { "--module", "iife", "--global", "not-a-name" }, "'not-a-name' is not a name a JavaScript global can have")]
  public void InvalidProfile_IsAUsageError(string[] options, string message) {
    File.Copy(PrototypeFixture.PathOf("Adler32"), Path.Combine(this._directory, "Adler32.cs"));
    string[] arguments = ["translate", "Adler32.cs", "--from", "csharp", "--to", "javascript", .. options, "--output", "out"];
    var (exitCode, output) = Toolchain.RunPolypiler(this._directory, arguments);
    Assert.That(exitCode, Is.EqualTo(2));
    Assert.That(output, Does.Contain("PLY0003").And.Contain(message));
    Assert.That(Directory.Exists(Path.Combine(this._directory, "out")), Is.False);
  }

  /// <summary>
  /// Test design 1, 2, 10 and 11: every call of every fixture — the published vectors, each operator
  /// and conversion at its boundaries and on a thousand random operand tuples (seed 2026), every
  /// trap — has in <c>node</c>, for every profile and module format, the outcome of the compiled
  /// fixture; and the file is at the level of its profile.
  /// <para>
  /// One comparison is left out. Without a <c>DataView</c> a NaN has no bits a program could read,
  /// so the payload this runtime happens to give one cannot be reproduced: in the profile without
  /// typed arrays the one fixture function that reads the bits of its operand without saying so in
  /// its name is compared on every number but NaN, as the functions that do say so are everywhere.
  /// </para>
  /// </summary>
  [TestCaseSource(nameof(FixturesAndProfiles))]
  public void Node_AgreesWithTheCompiledFixture(string fixture, ProfileCase profile) {
    var (calls, compiled) = Expected(fixture);
    var watch = Stopwatch.StartNew();
    var text = this.TranslateFixture(fixture, profile, true);
    var translated = watch.ElapsedMilliseconds;
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);

    var node = this.Node(profile, PrototypeFixture.ClassOf(fixture), calls);
    var mismatches = Enumerable.Range(0, calls.Count)
      .Where(index => node[index] != compiled[index])
      .Where(index => profile.Mode != "plain" || calls[index].Method != "NextUp" || calls[index].Arguments[0] is not float.NaN)
      .Select(index => $"{calls[index]}: C# {compiled[index]}, node {node[index]}")
      .ToList();
    TestContext.Out.WriteLine($"{fixture} as {profile}: translated in {translated} ms, {calls.Count} calls, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {calls.Count} calls differ");
  }

  /// <summary>
  /// Test design 4 and 5: every 64-bit operator of the fixture on every combination of the values
  /// at which a half carries into the other — 0, 1, 2^32-1, 2^32, 2^63-1, 2^63, 2^64-1 — and shifts
  /// and rotations by 0, 1, 31, 32, 33 and 63, in halves and as <c>BigInt</c>s.
  /// </summary>
  [TestCaseSource(nameof(Levels))]
  public void Halves_AgreeAtTheirBoundaries(ProfileCase profile) {
    ulong[] values = [0, 1, 0xFFFFFFFF, 0x100000000, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0xFFFFFFFFFFFFFFFF];
    int[] counts = [0, 1, 31, 32, 33, 63];
    var calls = new List<PrototypeCall>();
    foreach (var fixture in new[] { "wide/OperatorsWide", "wide/Library", "wide/Conversions" }) {
      var methods = PrototypeFixture.CompiledType(fixture).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.GetParameters().All(parameter => parameter.ParameterType == typeof(long) || parameter.ParameterType == typeof(ulong) || parameter.ParameterType == typeof(int)))
        .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType != typeof(int)))
        .OrderBy(method => method.Name, StringComparer.Ordinal);
      foreach (var method in methods) {
        IEnumerable<object[]> combinations = [[]];
        foreach (var parameter in method.GetParameters()) {
          var operands = parameter.ParameterType == typeof(int) ? counts.Cast<object>()
            : parameter.ParameterType == typeof(long) ? values.Select(value => (object)unchecked((long)value))
            : values.Cast<object>();
          combinations = combinations.SelectMany(_ => operands, (prefix, value) => prefix.Append(value).ToArray()).ToList();
        }

        calls.AddRange(combinations.Select(arguments => new PrototypeCall(fixture + ":" + method.Name, arguments)));
      }
    }

    Assert.That(calls, Has.Count.GreaterThan(1000));
    foreach (var group in calls.GroupBy(call => call.Method[..call.Method.IndexOf(':')])) {
      var fixture = group.Key;
      var named = PrototypeFixture.Typed(fixture, group.Select(call => call with { Method = call.Method[(fixture.Length + 1)..] }));
      this.TranslateFixture(fixture, profile, true);
      var node = this.Node(profile, PrototypeFixture.ClassOf(fixture), named);
      var mismatches = Enumerable.Range(0, named.Count)
        .Select(index => (Call: named[index], Compiled: PrototypeFixture.CompiledOutcome(fixture, named[index]), Node: node[index]))
        .Where(run => run.Compiled != run.Node)
        .Select(run => $"{run.Call}: C# {run.Compiled}, node {run.Node}")
        .ToList();
      Assert.That(mismatches.Take(20), Is.Empty, $"{fixture}: {mismatches.Count} of {named.Count} calls differ");
    }
  }

  /// <summary>
  /// Test design 4: divisions that take every path of the long division — a divisor of one to four
  /// digits of 16 bits, a quotient digit that is first estimated too high, and the rare estimate
  /// that is still one too high after the correction and has to be added back.
  /// </summary>
  [TestCaseSource(nameof(Replacing))]
  public void HalvesDivision_TakesEveryPath(ProfileCase profile) {
    (ulong Dividend, ulong Divisor)[] pairs = [
      (0x00008000FFFE0000, 0x000000008000FFFF), (0x8000000000000000, 0x0000800000000001), (0x7FFF800000000000, 0x0000800000000001),
      (0xFFFFFFFFFFFFFFFF, 0x0000000000000001), (0xFFFFFFFFFFFFFFFF, 0x000000000000FFFF), (0xFFFFFFFFFFFFFFFF, 0x0000000000010000),
      (0xFFFFFFFFFFFFFFFF, 0x00000000FFFFFFFF), (0xFFFFFFFFFFFFFFFF, 0x0000000100000000), (0xFFFFFFFFFFFFFFFF, 0x0000FFFFFFFFFFFF),
      (0xFFFFFFFFFFFFFFFF, 0x0001000000000000), (0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF), (0xFFFFFFFFFFFFFFFE, 0xFFFFFFFFFFFFFFFF),
      (0x0000000000000000, 0x0000000000000007), (0x0000000000000006, 0x0000000000000007), (0x80000000FFFF0000, 0x00000000FFFFFFFF),
      (0x00000000FFFFFFFF, 0x0000000100000000), (0x8000000000000000, 0x00000000FFFF0001), (0x7FFFFFFF00000000, 0x000000008000FFFF),
      (0x0000FFFE00000000, 0x00000000FFFF8000), (0x800000000000FFFE, 0x80000000FFFF0000), (0xC7DEDE795531554C, 0x0000C9FA87DCD619),
      (0xF977FFAD6AE4537A, 0x000085B86837C5AC)
    ];
    var calls = pairs.SelectMany(pair => new[] {
      new PrototypeCall("DivideULong", [pair.Dividend, pair.Divisor]),
      new PrototypeCall("RemainderULong", [pair.Dividend, pair.Divisor]),
      new PrototypeCall("DivideLong", [unchecked((long)pair.Dividend), unchecked((long)pair.Divisor)]),
      new PrototypeCall("RemainderLong", [unchecked((long)pair.Dividend), unchecked((long)pair.Divisor)])
    });
    var typed = PrototypeFixture.Typed("wide/OperatorsWide", calls);
    this.TranslateFixture("wide/OperatorsWide", profile, true);
    var node = this.Node(profile, "OperatorsWide", typed);
    Assert.That(node, Is.EqualTo(typed.Select(call => PrototypeFixture.CompiledOutcome("wide/OperatorsWide", call))));
    Assert.That(node[0], Is.EqualTo("65535"));
    Assert.That(node[1], Is.EqualTo("2147483647"));
  }

  /// <summary>
  /// Test design 6: rounding to binary32 without <c>Math.fround</c> — both zeros, the smallest
  /// subnormal and the halves around it, the largest subnormal and the smallest normal number,
  /// 2^24 and its neighbours, ties that round to even, the largest finite number, the first double
  /// that rounds to infinity, and NaN.
  /// </summary>
  [TestCaseSource(nameof(Replacing))]
  public void SoftwareRounding_HasTheBitsOfMathFround(ProfileCase profile) {
    double[] values = [
      0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon,
      float.Epsilon, float.Epsilon / 2, Math.BitIncrement(float.Epsilon / 2.0), Math.BitDecrement(float.Epsilon / 2.0), float.Epsilon * 1.5, float.Epsilon * 2.5,
      1.1754942106924411e-38, 1.1754943508222875e-38, 1.1754942807573643e-38, -1.1754943508222875e-38, 1e-40, -1e-40, 1e-46, 1e-300,
      16777215, 16777216, 16777217, 16777218, 16777219, -16777217, 0.1, -0.1, 1.0000000596046448, 1.0000001788139343,
      float.MaxValue, 3.4028235677973362e+38, 3.4028235677973366e+38, -3.4028235677973366e+38, 3.5e38, double.MaxValue, 1e30, 123456789.123
    ];
    var conversions = values.Select(value => new PrototypeCall("DoubleToFloat", [value])).ToList();
    var floats = values.Select(value => (float)value).Distinct().ToList();
    var arithmetic = (from method in new[] { "MultiplyAdd", "AddFloat", "SubtractFloat", "MultiplyFloat", "DivideFloat", "RemainderFloat" }
                      from a in floats
                      from b in floats
                      select new PrototypeCall(method, [a, b])).ToList();
    this.TranslateFixture("wide/Conversions", profile, true);
    Assert.That(this.Node(profile, "Conversions", conversions),
      Is.EqualTo(conversions.Select(call => PrototypeFixture.CompiledOutcome("wide/Conversions", call))));
    this.TranslateFixture("wide/OperatorsWide", profile, true);
    Assert.That(this.Node(profile, "OperatorsWide", arithmetic),
      Is.EqualTo(arithmetic.Select(call => PrototypeFixture.CompiledOutcome("wide/OperatorsWide", call))));
  }

  /// <summary>
  /// Test design 7: a plain array in the place of a typed array wraps a stored value around as the
  /// typed array would — a step up at the largest value of every element type and a step down at
  /// its smallest, prefix and postfix. The expected outcome is that of the default profile, whose
  /// arrays are typed, which the interpreter confirms.
  /// </summary>
  [TestCase("sbyte", new object[] { new sbyte[] { 127, -128, 0 } })]
  [TestCase("byte", new object[] { new byte[] { 255, 0, 7 } })]
  [TestCase("short", new object[] { new short[] { 32767, -32768, 0 } })]
  [TestCase("ushort", new object[] { new ushort[] { 65535, 0, 7 } })]
  [TestCase("char", new object[] { new[] { '\uFFFF', '\0', 'a' } })]
  [TestCase("int", new object[] { new[] { int.MaxValue, int.MinValue, 0 } })]
  [TestCase("uint", new object[] { new[] { uint.MaxValue, 0u, 7u } })]
  [TestCase("long", new object[] { new[] { long.MaxValue, long.MinValue, -1L } })]
  [TestCase("ulong", new object[] { new[] { ulong.MaxValue, 0UL, 0xFFFFFFFFUL } })]
  [TestCase("float", new object[] { new[] { 16777216f, -16777216f, 0.5f } })]
  [TestCase("double", new object[] { new[] { 9007199254740992.0, -9007199254740992.0, 0.5 } })]
  public void PlainArray_WrapsLikeTheTypedArray(string element, object[] arguments) {
    var source = $"public static {element} F({element}[] a) {{ {element} before = a[0]++; ++a[0]; --a[1]; a[1]--; a[2]++; a[2]--; --a[2]; return before; }}";
    var call = new PrototypeCall("F", arguments) { Returns = arguments[0].GetType().GetElementType() };
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(source));
    var expected = PrototypeFixture.InterpretedOutcome(module, call);
    ProfileCase[] profiles = [ProfileCase.Default, .. Replacing()];
    foreach (var profile in profiles) {
      Assert.That(this.Snippet(source, profile, [call], true).Single(), Is.EqualTo(expected), profile.ToString());
      Directory.Delete(Path.Combine(this._directory, "out"), true);
    }
  }

  /// <summary>
  /// Test design 3: for every entry of the capability table that has a replacement, a function that
  /// uses it computes the same with the native form and with the replacement. The expected outcome
  /// is that of the default profile, in which every entry is native. The module format changes
  /// nothing about a replacement, so each level is run in one; the fixtures run in all of them.
  /// </summary>
  [TestCase("let, const", "static readonly int K = 3; public static int F(int a) { int b = a + K; { int c = b * 2; b = c; } return b; }", new object[] { 5 })]
  [TestCase("Math.imul", "public static uint F(uint a, int b) => a * 0x9E3779B1u + (uint)(b * b);", new object[] { 0xFFFFFFFFu, -46341 })]
  [TestCase("Math.clz32", "public static int F(uint a, uint b) => System.Numerics.BitOperations.LeadingZeroCount(a) + System.Numerics.BitOperations.Log2(b) + System.Numerics.BitOperations.TrailingZeroCount(a);", new object[] { 0x00010000u, 0u })]
  [TestCase("Math.fround", "public static float F(float a, float b) => a / b + System.MathF.Sqrt(a) + (float)1e-46;", new object[] { 1f, 3f })]
  [TestCase("Math.trunc", "public static double F(double a, double b) => System.Math.Truncate(a) + (int)b + checked((long)a);", new object[] { -2.5, 1e10 })]
  [TestCase("Math.sign", "public static int F(int a, sbyte b) => System.Math.Sign(a) + System.Math.Sign(b) * 2;", new object[] { -7, (sbyte)0 })]
  [TestCase("typed arrays", "public static double F(sbyte[] a, byte[] b, short[] c, ushort[] d, int[] e, uint[] f, float[] g, double[] h) { a[0] = (sbyte)(b[0] + c[0]); d[0] = (ushort)(e[0] - f[0]); g[0] = (float)h[0]; h[0] = g[0] * 3; return new double[2].Length + (new float[1])[0] + (new short[3])[2]; }",
    new object[] { new sbyte[] { 1 }, new byte[] { 200 }, new short[] { -1 }, new ushort[] { 2 }, new[] { 3 }, new[] { 4u }, new[] { 0.5f }, new[] { 0.1 } })]
  [TestCase("DataView", "public static long F(double a, float b, long c, int d) => System.BitConverter.DoubleToInt64Bits(a) ^ System.BitConverter.SingleToInt32Bits(b) ^ System.BitConverter.DoubleToInt64Bits(System.BitConverter.Int64BitsToDouble(c)) ^ System.BitConverter.SingleToInt32Bits(System.BitConverter.Int32BitsToSingle(d));",
    new object[] { -1.5e-310, 1e-40f, 0x7FEFFFFFFFFFFFFF, unchecked((int)0x80000001) })]
  [TestCase(".set, .subarray", "public static void F(int[] a, long[] b) { System.Array.Copy(a, 0, a, 1, 3); System.Array.Copy(a, 2, a, 0, 2); System.Array.Copy(b, 1, b, 0, 2); System.Array.Copy(b, 0, b, 1, 2); }",
    new object[] { new[] { 1, 2, 3, 4, 5 }, new[] { 6L, 7L, 8L } })]
  [TestCase(".fill", "public static void F(int[] a, long[] b, float[] c) { System.Array.Fill(a, 7); System.Array.Fill(b, -2); System.Array.Clear(c); }",
    new object[] { new[] { 1, 2 }, new[] { 3L, 4L }, new[] { 5f } })]
  [TestCase("BigInt", "static readonly ulong[] T = { 1, 0xFFFFFFFFFFFFFFFF }; public static long F(long a, ulong b, int c) { ulong[] t = { b, T[1] }; long[] n = new long[2]; n[1] = a; return (long)(t[0] * T[1]) + n[c] - (a >> c) + (long)(b >> c) + c; }",
    new object[] { long.MinValue, 0xFFFFFFFF00000001UL, 1 })]
  public void Replacement_ComputesWhatTheNativeFormDoes(string capability, string source, object[] arguments) {
    var call = new PrototypeCall("F", arguments);
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(source));
    var typed = call with { Returns = module.Functions.Single(function => function.Name == "F").ReturnType.IsLong ? typeof(long) : typeof(int) };
    var expected = this.Snippet(source, ProfileCase.Default, [typed], true).Single();
    Assert.That(expected, Is.EqualTo(PrototypeFixture.InterpretedOutcome(module, call)), capability);
    foreach (var profile in Levels()) {
      Directory.Delete(Path.Combine(this._directory, "out"), true);
      Assert.That(this.Snippet(source, profile, [typed], true).Single(), Is.EqualTo(expected), $"{capability} as {profile}");
    }
  }

  /// <summary>
  /// The operations of ECMA-262 the JavaScript frontend imports — <c>ToInt32</c>, <c>ToUint32</c>
  /// and <c>Math.round</c> — compute the same at every level: an untyped JavaScript function
  /// translated back to JavaScript in each profile, compared with the original run by node.
  /// </summary>
  [TestCaseSource(nameof(Levels))]
  public void EcmaScriptOperations_ComputeTheSameAtEveryLevel(ProfileCase profile) {
    const string source = "export function F(x) { return ((x | 0) + (x >>> 0)) * 4 + Math.round(x); }\n";
    File.WriteAllText(Path.Combine(this._directory, "f.mjs"), source);
    double[] values = [0, -0.0, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 2147483647.5, 2147483648, -2147483649, 4294967296.7, 1e20, -1e20, 9007199254740993, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
    var calls = values.Select(value => new PrototypeCall("F", [value]) { Returns = typeof(double) }).ToList();
    var original = Toolchain.NodeOutcomes(this._directory, "f.mjs", calls, true);
    string[] arguments = ["translate", "f.mjs", "--from", "javascript", "--to", "javascript", .. profile.Arguments, "--output", "out"];
    var (exitCode, output) = Toolchain.Translate(this._directory, arguments);
    Assert.That(exitCode, Is.Zero, output);
    var text = File.ReadAllText(Path.Combine(this._directory, "out", "f" + profile.Parsed.Extension));
    Assert.That(GeneratedJavaScript.Problems(text, profile.Parsed), Is.Empty);
    Assert.That(text, Does.Contain("| 0").And.Contain(">>> 0").And.Contain("Math.round("));
    Assert.That(this.Node(profile, "f", calls), Is.EqualTo(original));
  }

  /// <summary>
  /// Test design 10: a trap is the same <c>RangeError</c> in every profile — division by zero, the
  /// one quotient that does not exist, a checked operation and a checked conversion that overflow,
  /// an index and a range outside an array, and the sign of a NaN. These are the traps by name,
  /// at every level; the fixtures, whose calls trap as well, run in every module format.
  /// </summary>
  [TestCaseSource(nameof(Levels))]
  public void Traps_AreTheSameRangeError(ProfileCase profile) {
    (string Fixture, PrototypeCall Call, string Trap)[] cases = [
      ("Operators", new("DivideInt", [1, 0]), "trap: divide by zero"),
      ("Operators", new("RemainderInt", [int.MinValue, -1]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("DivideLong", [1L, 0L]), "trap: divide by zero"),
      ("wide/OperatorsWide", new("RemainderULong", [1UL, 0UL]), "trap: divide by zero"),
      ("wide/OperatorsWide", new("DivideLong", [long.MinValue, -1L]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("RemainderLong", [long.MinValue, -1L]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("AddCheckedInt", [int.MaxValue, 1]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("SubtractCheckedUInt", [0u, 1u]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("MultiplyCheckedLong", [long.MinValue, -1L]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("MultiplyCheckedULong", [ulong.MaxValue, 2UL]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("NegateCheckedLong", [long.MinValue]), "trap: integer overflow"),
      ("wide/OperatorsWide", new("IncrementCheckedByte", [(byte)254]), "trap: integer overflow"),
      ("wide/Conversions", new("LongToIntChecked", [2147483648L]), "trap: integer overflow"),
      ("wide/Conversions", new("IntToULongChecked", [-1]), "trap: integer overflow"),
      ("wide/Conversions", new("ULongToLongChecked", [ulong.MaxValue]), "trap: integer overflow"),
      ("wide/Conversions", new("DoubleToLongChecked", [9223372036854775808.0]), "trap: integer overflow"),
      ("wide/Conversions", new("FloatToULongChecked", [float.PositiveInfinity]), "trap: integer overflow"),
      ("wide/Library", new("AbsLong", [long.MinValue]), "trap: integer overflow"),
      ("wide/Library", new("AbsSByte", [sbyte.MinValue]), "trap: integer overflow"),
      ("wide/Library", new("SignSingle", [float.NaN]), "trap: invalid operation"),
      ("wide/Tables", new("Move", [new long[] { 1, 2, 3, 4 }, 0, 2, 3]), "trap: index out of range"),
      ("wide/StatementsWide", new("StepLongElement", [new long[] { 1 }, 1]), "trap: index out of range"),
      ("wide/StatementsWide", new("StepCheckedULongElement", [new ulong[] { 0 }, 0]), "trap: integer overflow")
    ];
    foreach (var group in cases.GroupBy(test => test.Fixture)) {
      var calls = PrototypeFixture.Typed(group.Key, group.Select(test => test.Call));
      this.TranslateFixture(group.Key, profile, true);
      Assert.That(this.Node(profile, PrototypeFixture.ClassOf(group.Key), calls), Is.EqualTo(group.Select(test => test.Trap)), group.Key);
      Assert.That(calls.Select(call => PrototypeFixture.CompiledOutcome(group.Key, call)), Is.EqualTo(group.Select(test => test.Trap)), group.Key);
    }
  }

  /// <summary>The calls of a fixture with what the compiled fixture does for each, computed once.</summary>
  private static (IReadOnlyList<PrototypeCall> Calls, string[] Compiled) Expected(string fixture)
    => _expected.GetOrAdd(fixture, name => new(() => {
      var calls = PrototypeFixture.Typed(name, PrototypeCases.For(name));
      return (calls, calls.Select(call => PrototypeFixture.CompiledOutcome(name, call)).ToArray());
    })).Value;

  /// <summary>
  /// Translates a fixture for a profile with the real executable, or in process where the test is
  /// about the generated file, and returns the text of the generated file.
  /// </summary>
  private string TranslateFixture(string fixture, ProfileCase profile, bool inProcess = false) {
    var output = Path.Combine(this._directory, "out");
    string[] arguments = ["translate", PrototypeFixture.PathOf(fixture), "--from", "csharp", "--to", "javascript", .. profile.Arguments, "--output", output];
    var (exitCode, text) = Toolchain.Polypiler(inProcess, this._directory, arguments);
    Assert.That(exitCode, Is.Zero, text);
    return File.ReadAllText(Path.Combine(output, PrototypeFixture.ClassOf(fixture) + profile.Parsed.Extension));
  }

  /// <summary>Translates one method for a profile, in process or with the executable, checks the level of the file and runs the calls through node.</summary>
  private IReadOnlyList<string> Snippet(string method, ProfileCase profile, IReadOnlyList<PrototypeCall> calls, bool inProcess = false) {
    File.WriteAllText(Path.Combine(this._directory, "Snippet.cs"), PrototypeFixture.Wrap(method));
    string[] arguments = ["translate", "Snippet.cs", "--from", "csharp", "--to", "javascript", .. profile.Arguments, "--output", "out"];
    var (exitCode, text) = Toolchain.Polypiler(inProcess, this._directory, arguments);
    Assert.That(exitCode, Is.Zero, text);
    Assert.That(GeneratedJavaScript.Problems(File.ReadAllText(Path.Combine(this._directory, "out", "Snippet" + profile.Parsed.Extension)), profile.Parsed), Is.Empty, profile.ToString());
    return this.Node(profile, "Snippet", calls);
  }

  private IReadOnlyList<string> Node(ProfileCase profile, string module, IReadOnlyList<PrototypeCall> calls)
    => Toolchain.NodeOutcomes(Path.Combine(this._directory, "out"), module + profile.Parsed.Extension, calls, true, profile.Mode, profile.Global);

}

/// <summary>A profile as a test case: what the command line says, and how <c>node</c> has to call the file.</summary>
/// <param name="Level">The value of <c>--profile</c>.</param>
/// <param name="Module">The value of <c>--module</c>.</param>
/// <param name="TypedArrays">Whether <c>--typed-arrays</c> is given.</param>
/// <param name="Global">The value of <c>--global</c>, for a script.</param>
public sealed record ProfileCase(string Level, string Module, bool TypedArrays, string? Global) {

  /// <summary>The profile of a command line that names none.</summary>
  public static ProfileCase Default { get; } = new("es2022", "esm", false, null);

  /// <summary>Every valid combination of level, module format and, at ECMAScript 5, typed arrays.</summary>
  public static IReadOnlyList<ProfileCase> All { get; } = [
    .. from level in JavaScriptProfile.LevelNames
       from module in JavaScriptProfile.ModuleNames
       where level != "es5" || module != "esm"
       from typed in level == "es5" ? new[] { false, true } : [false]
       select new ProfileCase(level, module, typed, module == "iife" ? "Generated" : null)
  ];

  /// <summary>
  /// The profile of the options of a command line: <c>--profile</c>, <c>--module</c>,
  /// <c>--global</c> and <c>--typed-arrays</c>; without <c>--module</c> a CommonJS module at ECMAScript
  /// 5, which has no ES modules, and an ES module above.
  /// </summary>
  public static ProfileCase Of(string[] options) {
    string? Value(string option) => Array.IndexOf(options, option) is >= 0 and var index ? options[index + 1] : null;
    var level = Value("--profile") ?? Default.Level;
    return new ProfileCase(level, Value("--module") ?? (level == "es5" ? "cjs" : "esm"), options.Contains("--typed-arrays"), Value("--global"));
  }

  /// <summary>The profile as the backend reads it.</summary>
  public JavaScriptProfile Parsed => JavaScriptProfile.Parse(this.Level, this.Module, this.Global, this.TypedArrays);

  /// <summary>The options of the command line.</summary>
  public string[] Arguments => [
    "--profile", this.Level, "--module", this.Module,
    .. this.Global is null ? Array.Empty<string>() : ["--global", this.Global],
    .. this.TypedArrays ? ["--typed-arrays"] : Array.Empty<string>()
  ];

  /// <summary>How the driver passes values: 64-bit integers as <c>BigInt</c>s or halves, arrays typed or plain.</summary>
  public string Mode => this.Parsed.Level >= JavaScriptLevel.Es2020 ? "native" : this.Parsed.TypedArrays ? "halves" : "plain";

  /// <summary>The profile as a test name shows it.</summary>
  public override string ToString() => this.Parsed.ToString();

}
