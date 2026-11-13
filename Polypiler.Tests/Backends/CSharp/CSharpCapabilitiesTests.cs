using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// The capability table of the C# backend, as data and as what the emitter makes of it. The table
/// says where each API and each feature of the language begins; whether it says so correctly is asked
/// of the C# compiler, which builds a use of the row against the reference assemblies of the first
/// profile that is said to have it and of the one before. What stands in for a row elsewhere is run
/// against the row itself: a helper has to return what the library call returns on the running
/// runtime, bit for bit.
/// </summary>
[TestFixture]
public sealed class CSharpCapabilitiesTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>The whole table: every row by its name and the first profile or language version that has it.</summary>
  private static readonly (string Name, string First)[] _table = [
    ("implicitly typed local variable (var)", "C# 3"),
    ("expression-bodied member", "C# 6"),
    ("local function", "C# 7"),
    ("discard", "C# 7"),
    ("binary literal", "C# 7"),
    ("digit separator", "C# 7"),
    ("default literal", "C# 7.1"),
    ("static local function", "C# 8"),
    ("native-sized integer (nint)", "C# 9"),
    ("file-scoped namespace", "C# 10"),
    ("unsigned right shift (>>>)", "C# 11"),
    ("Array.Copy(Array, int, Array, int, int)", "net2.0"),
    ("Array.Fill<T>(T[], T)", "netstandard2.1"),
    ("Array.Clear(Array)", "net8.0"),
    ("Array.Empty<T>()", "net4.6"),
    ("Math.Abs", "net2.0"),
    ("Math.Sign", "net2.0"),
    ("Math.Min(float, float) as the minimum of IEEE 754", "netcoreapp3.1"),
    ("Math.Min(double, double) as the minimum of IEEE 754", "netcoreapp3.1"),
    ("Math.Min", "net2.0"),
    ("Math.Max(float, float) as the maximum of IEEE 754", "netcoreapp3.1"),
    ("Math.Max(double, double) as the maximum of IEEE 754", "netcoreapp3.1"),
    ("Math.Max", "net2.0"),
    ("Math.Clamp", "netstandard2.1"),
    ("Math.Sqrt(double)", "net2.0"),
    ("Math.Floor(double)", "net2.0"),
    ("Math.Ceiling(double)", "net2.0"),
    ("Math.Truncate(double)", "net2.0"),
    ("Math.Round(double)", "net2.0"),
    ("Math.Round(double, MidpointRounding)", "net2.0"),
    ("Math.round of ECMA-262 on float", "never"),
    ("Math.round of ECMA-262", "never"),
    ("ToInt32 of ECMA-262", "never"),
    ("ToUint32 of ECMA-262", "never"),
    ("MathF.Sqrt(float)", "netstandard2.1"),
    ("MathF.Floor(float)", "netstandard2.1"),
    ("MathF.Ceiling(float)", "netstandard2.1"),
    ("MathF.Truncate(float)", "netstandard2.1"),
    ("MathF.Round(float)", "netstandard2.1"),
    ("MathF.Round(float, MidpointRounding)", "netstandard2.1"),
    ("BitOperations.RotateLeft(uint, int)", "netcoreapp3.1"),
    ("BitOperations.RotateLeft(ulong, int)", "netcoreapp3.1"),
    ("BitOperations.RotateRight(uint, int)", "netcoreapp3.1"),
    ("BitOperations.RotateRight(ulong, int)", "netcoreapp3.1"),
    ("BitOperations.PopCount(uint)", "netcoreapp3.1"),
    ("BitOperations.PopCount(ulong)", "netcoreapp3.1"),
    ("BitOperations.LeadingZeroCount(uint)", "netcoreapp3.1"),
    ("BitOperations.LeadingZeroCount(ulong)", "netcoreapp3.1"),
    ("BitOperations.TrailingZeroCount(uint)", "netcoreapp3.1"),
    ("BitOperations.TrailingZeroCount(ulong)", "netcoreapp3.1"),
    ("BitOperations.Log2(uint)", "netcoreapp3.1"),
    ("BitOperations.Log2(ulong)", "netcoreapp3.1"),
    ("Math.BigMul(uint, uint)", "net10.0"),
    ("Math.BigMul(ulong, ulong, out ulong)", "net8.0"),
    ("BitConverter.DoubleToInt64Bits(double)", "net2.0"),
    ("BitConverter.SingleToInt32Bits(float)", "netstandard2.1"),
    ("BitConverter.Int64BitsToDouble(long)", "net2.0"),
    ("BitConverter.Int32BitsToSingle(int)", "netstandard2.1"),
    ("saturating cast to sbyte", "never"),
    ("saturating cast to byte", "never"),
    ("saturating cast to short", "never"),
    ("saturating cast to ushort", "never"),
    ("saturating cast to int", "net10.0"),
    ("saturating cast to uint", "net10.0"),
    ("saturating cast to long", "net10.0"),
    ("saturating cast to ulong", "net10.0")
  ];

  /// <summary>
  /// The rows whose API an earlier profile has too, with another result: the smaller and the larger
  /// of two floating-point numbers ordered the zeros only from .NET Core 3.0 on. The compiler cannot
  /// tell; the first profile of these rows is taken from the .NET API reference.
  /// </summary>
  private static readonly string[] _sameNameOtherResult = [
    "Math.Min(float, float) as the minimum of IEEE 754", "Math.Min(double, double) as the minimum of IEEE 754",
    "Math.Max(float, float) as the maximum of IEEE 754", "Math.Max(double, double) as the maximum of IEEE 754"
  ];

  /// <summary>
  /// One function per row the emitter writes, as C# the frontend imports into the row's operation.
  /// A row may have more than one. The test fails when a row of the table is missing here. C# has no
  /// conversion that saturates to a type narrower than <c>int</c> - its cast saturates to
  /// <c>int</c> and cuts that down - so those rows are used by IR built here: <c>saturate</c> and
  /// the type of the operand.
  /// </summary>
  private static readonly (string Row, string Method)[] _uses = [
    ("Array.Copy(Array, int, Array, int, int)", "public static int[] F(int[] a, int[] b, int n) { System.Array.Copy(a, 0, b, 1, n); return b; }"),
    ("Array.Fill<T>(T[], T)", "public static int[] F(int[] a, int n) { System.Array.Fill(a, n); return a; }"),
    ("Array.Fill<T>(T[], T)", "public static byte[] F(byte[] a) { System.Array.Fill(a, (byte)7); return a; }"),
    ("Array.Clear(Array)", "public static int[] F(int[] a) { System.Array.Clear(a); return a; }"),
    ("Math.Abs", "public static int F(int a) => System.Math.Abs(a);"),
    ("Math.Abs", "public static float F(float a) => System.Math.Abs(a);"),
    ("Math.Sign", "public static int F(double a) => System.Math.Sign(a);"),
    ("Math.Min(float, float) as the minimum of IEEE 754", "public static float F(float a, float b) => System.Math.Min(a, b);"),
    ("Math.Min(double, double) as the minimum of IEEE 754", "public static double F(double a, double b) => System.Math.Min(a, b);"),
    ("Math.Min", "public static uint F(uint a, uint b) => System.Math.Min(a, b);"),
    ("Math.Max(float, float) as the maximum of IEEE 754", "public static float F(float a, float b) => System.Math.Max(a, b);"),
    ("Math.Max(double, double) as the maximum of IEEE 754", "public static double F(double a, double b) => System.Math.Max(a, b);"),
    ("Math.Max", "public static long F(long a, long b) => System.Math.Max(a, b);"),
    ("Math.Sqrt(double)", "public static double F(double a) => System.Math.Sqrt(a);"),
    ("Math.Floor(double)", "public static double F(double a) => System.Math.Floor(a);"),
    ("Math.Ceiling(double)", "public static double F(double a) => System.Math.Ceiling(a);"),
    ("Math.Truncate(double)", "public static double F(double a) => System.Math.Truncate(a);"),
    ("Math.Round(double)", "public static double F(double a) => System.Math.Round(a);"),
    ("Math.Round(double, MidpointRounding)", "public static double F(double a) => System.Math.Round(a, System.MidpointRounding.AwayFromZero);"),
    ("MathF.Sqrt(float)", "public static float F(float a) => System.MathF.Sqrt(a);"),
    ("MathF.Floor(float)", "public static float F(float a) => System.MathF.Floor(a);"),
    ("MathF.Ceiling(float)", "public static float F(float a) => System.MathF.Ceiling(a);"),
    ("MathF.Truncate(float)", "public static float F(float a) => System.MathF.Truncate(a);"),
    ("MathF.Round(float)", "public static float F(float a) => System.MathF.Round(a);"),
    ("MathF.Round(float, MidpointRounding)", "public static float F(float a) => System.MathF.Round(a, System.MidpointRounding.AwayFromZero);"),
    ("BitOperations.RotateLeft(uint, int)", "public static uint F(uint a, int n) => System.Numerics.BitOperations.RotateLeft(a, n);"),
    ("BitOperations.RotateLeft(ulong, int)", "public static ulong F(ulong a, int n) => System.Numerics.BitOperations.RotateLeft(a, n);"),
    ("BitOperations.RotateRight(uint, int)", "public static uint F(uint a, int n) => System.Numerics.BitOperations.RotateRight(a, n);"),
    ("BitOperations.RotateRight(ulong, int)", "public static ulong F(ulong a, int n) => System.Numerics.BitOperations.RotateRight(a, n);"),
    ("BitOperations.PopCount(uint)", "public static int F(uint a) => System.Numerics.BitOperations.PopCount(a);"),
    ("BitOperations.PopCount(ulong)", "public static int F(ulong a) => System.Numerics.BitOperations.PopCount(a);"),
    ("BitOperations.LeadingZeroCount(uint)", "public static int F(uint a) => System.Numerics.BitOperations.LeadingZeroCount(a);"),
    ("BitOperations.LeadingZeroCount(ulong)", "public static int F(ulong a) => System.Numerics.BitOperations.LeadingZeroCount(a);"),
    ("BitOperations.TrailingZeroCount(uint)", "public static int F(uint a) => System.Numerics.BitOperations.TrailingZeroCount(a);"),
    ("BitOperations.TrailingZeroCount(ulong)", "public static int F(ulong a) => System.Numerics.BitOperations.TrailingZeroCount(a);"),
    ("BitOperations.Log2(uint)", "public static int F(uint a) => System.Numerics.BitOperations.Log2(a);"),
    ("BitOperations.Log2(ulong)", "public static int F(ulong a) => System.Numerics.BitOperations.Log2(a);"),
    ("Math.BigMul(uint, uint)", "public static ulong F(uint a, uint b) => System.Math.BigMul(a, b);"),
    ("BitConverter.DoubleToInt64Bits(double)", "public static long F(double a) => System.BitConverter.DoubleToInt64Bits(a);"),
    ("BitConverter.SingleToInt32Bits(float)", "public static int F(float a) => System.BitConverter.SingleToInt32Bits(a);"),
    ("BitConverter.Int64BitsToDouble(long)", "public static double F(long a) => System.BitConverter.Int64BitsToDouble(a);"),
    ("BitConverter.Int32BitsToSingle(int)", "public static float F(int a) => System.BitConverter.Int32BitsToSingle(a);"),
    ("Math.round of ECMA-262 on float", "ecma RoundHalfUp f32"),
    ("Math.round of ECMA-262", "ecma RoundHalfUp f64"),
    ("ToInt32 of ECMA-262", "ecma ToInt32 f64"),
    ("ToUint32 of ECMA-262", "ecma ToUint32 f64"),
    ("saturating cast to sbyte", "saturate f64"),
    ("saturating cast to sbyte", "saturate f32"),
    ("saturating cast to byte", "saturate f64"),
    ("saturating cast to byte", "saturate f32"),
    ("saturating cast to short", "saturate f64"),
    ("saturating cast to short", "saturate f32"),
    ("saturating cast to ushort", "saturate f64"),
    ("saturating cast to ushort", "saturate f32"),
    ("saturating cast to int", "public static int F(double a) => (int)a;"),
    ("saturating cast to int", "public static int F(float a) => (int)a;"),
    ("saturating cast to uint", "public static uint F(double a) => (uint)a;"),
    ("saturating cast to uint", "public static uint F(float a) => (uint)a;"),
    ("saturating cast to long", "public static long F(double a) => (long)a;"),
    ("saturating cast to long", "public static long F(float a) => (long)a;"),
    ("saturating cast to ulong", "public static ulong F(double a) => (ulong)a;"),
    ("saturating cast to ulong", "public static ulong F(float a) => (ulong)a;")
  ];

  /// <summary>
  /// Test design 6 and 7, beyond the edges every type has anyway: an integer of 8, 16, 32 and 64 bits
  /// with all bits, the top bit and the lowest bit set; the rotation counts around both widths, the
  /// negative ones and the two ends of <c>int</c>; the subnormals, the halves that round either way
  /// and the neighbours of 2^23 and 2^24, where a <c>float</c> runs out of fraction bits.
  /// </summary>
  private static readonly Dictionary<Type, object[]> _further = new() {
    [typeof(uint)] = [0u, 1u, 0xFFu, 0x80u, 0xFFFFu, 0x8000u, 0x10000u, 0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu, 0x12345678u],
    [typeof(ulong)] = [0UL, 1UL, 0xFFUL, 0x80UL, 0xFFFFUL, 0x8000UL, 0xFFFFFFFFUL, 0x80000000UL, 0x100000000UL, ulong.MaxValue, 0x8000000000000000UL, 0x7FFFFFFFFFFFFFFFUL, 0x123456789ABCDEF0UL],
    [typeof(int)] = [0, 1, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, -1, -31, -32, -33, int.MinValue, int.MaxValue],
    [typeof(float)] = [
      0f, -0f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, float.Epsilon, -float.Epsilon, 1e-40f, -1e-40f, 1.17549421e-38f,
      0.5f, -0.5f, 1.5f, -1.5f, 2.5f, -2.5f, 0.49999997f, 8388607.5f, 8388608f, 8388609f, 16777215f, 16777216f, 16777218f, -16777215f, float.MaxValue
    ],
    [typeof(double)] = [
      double.Epsilon, -double.Epsilon, 2.2250738585072009e-308, 0.49999999999999994, 4503599627370495.5, 4503599627370497.0, -4503599627370495.5
    ]
  };

  private static readonly Dictionary<IrTypeKind, Type> _clr = new() {
    [IrTypeKind.I8] = typeof(sbyte), [IrTypeKind.U8] = typeof(byte), [IrTypeKind.I16] = typeof(short), [IrTypeKind.U16] = typeof(ushort),
    [IrTypeKind.I32] = typeof(int), [IrTypeKind.U32] = typeof(uint), [IrTypeKind.I64] = typeof(long), [IrTypeKind.U64] = typeof(ulong),
    [IrTypeKind.F32] = typeof(float), [IrTypeKind.F64] = typeof(double)
  };

  private static string First(CSharpCapability row)
    => row.Language is { } language ? "C# " + CSharpProfile.NameOf(language) : row.Since?.Name ?? "never";

  private static IEnumerable<TestCaseData> Rows()
    => CSharpCapabilities.Default.Rows.Select(row => new TestCaseData(row).SetArgDisplayNames(row.Name));

  private static IEnumerable<TestCaseData> Uses()
    => _uses.Select(use => new TestCaseData(use.Row, use.Method).SetArgDisplayNames(use.Row, use.Method.Replace("public static ", "")).SetCategory("Slow"));

  /// <summary>The module with the one function <c>F</c> that uses a row.</summary>
  private static IrModule Using(CSharpCapability row, string method) {
    if (method.StartsWith("ecma ", StringComparison.Ordinal)) {
      // No C# source has these operations; the JavaScript frontend makes them, so the IR is written here.
      var x = new IrLocal("a", method.EndsWith("f32", StringComparison.Ordinal) ? IrType.F32 : IrType.F64);
      var op = row.Operation!.Value;
      var type = op == IrIntrinsicOp.ToInt32 ? IrType.I32 : op == IrIntrinsicOp.ToUint32 ? IrType.U32 : x.Type;
      return new IrModule("Snippet", [new IrFunction("F", true, type, [x], [], new IrBlock([new IrReturn(new IrIntrinsic(op, type, [new IrLocalRef(x)]))]))]);
    }

    if (!method.StartsWith("saturate ", StringComparison.Ordinal))
      return PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));

    var a = new IrLocal("a", method.EndsWith("f32", StringComparison.Ordinal) ? IrType.F32 : IrType.F64);
    var target = new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16 }.Single(type => type.Kind == row.Saturates);
    return new IrModule("Snippet", [new IrFunction("F", true, target, [a], [], new IrBlock([new IrReturn(new IrConvert(target, new IrLocalRef(a), IrConversion.Saturate))]))]);
  }

  /// <summary>The profile just before a profile, or <see langword="null"/> for the oldest.</summary>
  private static CSharpProfile? Before(CSharpProfile profile) => CSharpProfile.All.TakeWhile(candidate => candidate != profile).LastOrDefault();

  /// <summary>The values a parameter is tried with: an array of three lengths, or the edges of its type and those of this file.</summary>
  private static object[] Operands(IrType type) {
    if (type.Element is { } element) {
      var clr = _clr[element.Kind];
      return [Array.CreateInstance(clr, 0), Filled(clr, 1), Filled(clr, 4)];
    }

    var further = _further.GetValueOrDefault(_clr[type.Kind], []);
    return [.. PrototypeCases.Edges(_clr[type.Kind]).Concat(further).Distinct()];
  }

  private static Array Filled(Type element, int length) {
    var array = Array.CreateInstance(element, length);
    for (var index = 0; index < length; ++index)
      array.SetValue(Convert.ChangeType(index + 3, element), index);

    return array;
  }

  /// <summary>Every combination of the operands of the parameters of <c>F</c>.</summary>
  private static List<PrototypeCall> Calls(IrModule module) {
    IEnumerable<object[]> tuples = [[]];
    foreach (var parameter in module.Functions.Single().Parameters)
      tuples = tuples.SelectMany(tuple => Operands(parameter.Type).Select(operand => (object[])[.. tuple, operand])).ToList();

    return [.. tuples.Select(tuple => new PrototypeCall("F", tuple))];
  }

  // ---------------------------------------------------------------- the table as data

  /// <summary>The table names every API and every feature of the language the issue lists, each with the first profile or version that has it.</summary>
  [Test]
  public void Table_IsWhatTheBackendKnows() {
    var rows = CSharpCapabilities.Default.Rows;
    Assert.That(rows.Select(row => (row.Name, First(row))), Is.EqualTo(_table));
    Assert.That(rows.Select(row => row.Name), Is.Unique);
    Assert.That(rows.Select(row => row.ToString()), Is.EqualTo(rows.Select(row => row.Name)));
    Assert.That(rows.Where(row => row.Feature is not null).Select(row => row.Feature), Is.EquivalentTo(Enum.GetValues<CSharpFeature>()));
    Assert.That(rows.Where(row => row.Language is not null), Has.All.Matches<CSharpCapability>(row => row.Since is null && row.Feature is not null));
  }

  /// <summary>
  /// Test design 8: a row begins exactly at its first profile or language version - it is in that
  /// one and in every later one, and in none before. Raising the language version of the oldest
  /// profile reaches a feature of the language and no API.
  /// </summary>
  [TestCaseSource(nameof(Rows))]
  public void Row_BeginsAtItsFirstProfile(CSharpCapability row) {
    var raised = CSharpProfile.LanguageVersions.Where(version => version >= CSharpProfile.Net20.LanguageVersion)
      .Select(version => CSharpProfile.Parse("net2.0", CSharpProfile.NameOf(version))).ToList();
    if (row.Language is { } language) {
      Assert.That(raised.Where(row.IsIn).Select(profile => profile.LanguageVersion), Is.EqualTo(CSharpProfile.LanguageVersions.Where(version => version >= language)));
      Assert.That(CSharpProfile.All.Where(row.IsIn), Is.EqualTo(CSharpProfile.All.Where(profile => profile.LanguageVersion >= language)));
    } else {
      var from = row.Since is null ? CSharpProfile.All.Count : CSharpProfile.All.ToList().IndexOf(row.Since);
      Assert.That(CSharpProfile.All.Where(row.IsIn), Is.EqualTo(CSharpProfile.All.Skip(from)));
      Assert.That(raised.Where(row.IsIn), row.Since == CSharpProfile.Net20 ? Is.EqualTo(raised) : Is.Empty);
    }
  }

  /// <summary>A row is found by what it is about; the row for one type of operand comes before the row for every type.</summary>
  [Test]
  public void Rows_AreFoundByWhatTheyAreAbout() {
    var table = CSharpCapabilities.Default;
    Assert.That(table.Of(CSharpFeature.Discard)!.Name, Is.EqualTo("discard"));
    Assert.That(table.Of(IrIntrinsicOp.Min, IrTypeKind.F32)!.Name, Is.EqualTo("Math.Min(float, float) as the minimum of IEEE 754"));
    Assert.That(table.Of(IrIntrinsicOp.Min, IrTypeKind.I16)!.Name, Is.EqualTo("Math.Min"));
    Assert.That(table.Of(IrIntrinsicOp.Sqrt, IrTypeKind.F32)!.Name, Is.EqualTo("MathF.Sqrt(float)"));
    Assert.That(table.Of(IrIntrinsicOp.Sqrt, IrTypeKind.I32), Is.Null);
    Assert.That(table.Of(IrIntrinsicOp.ArrayOf, IrTypeKind.I32), Is.Null);
    Assert.That(table.OfSaturation(IrTypeKind.U16)!.Name, Is.EqualTo("saturating cast to ushort"));
    Assert.That(table.OfSaturation(IrTypeKind.F32), Is.Null);
    Assert.That(table.Has(CSharpFeature.FileScopedNamespace, CSharpProfile.Net100), Is.True);
    Assert.That(table.Has(CSharpFeature.FileScopedNamespace, CSharpProfile.Net48), Is.False);

    var empty = new CSharpCapabilities([]);
    Assert.That(empty.Rows, Is.Empty);
    Assert.That((empty.Of(CSharpFeature.Discard), empty.Of(IrIntrinsicOp.Min, IrTypeKind.F32), empty.OfSaturation(IrTypeKind.I32)), Is.EqualTo(((CSharpCapability?)null, (CSharpCapability?)null, (CSharpCapability?)null)));
    Assert.That(empty.Has(CSharpFeature.FileScopedNamespace, CSharpProfile.Net100), Is.False);
  }

  /// <summary>
  /// The language versions of the table are those of the compiler: a use of each feature is accepted
  /// at the version the table names and refused at the one before.
  /// </summary>
  [TestCase(CSharpFeature.ImplicitlyTypedLocal, "static class C { static int F(int x) { var y = x; return y; } }")]
  [TestCase(CSharpFeature.ExpressionBody, "static class C { static int F(int x) => x; }")]
  [TestCase(CSharpFeature.LocalFunction, "static class C { static int F(int x) { int G(int y) { return y; } return G(x); } }")]
  [TestCase(CSharpFeature.Discard, "static class C { static void F(int x) { _ = x; } }")]
  [TestCase(CSharpFeature.BinaryLiteral, "static class C { static int F() { return 0b101; } }")]
  [TestCase(CSharpFeature.DigitSeparator, "static class C { static int F() { return 1_000; } }")]
  [TestCase(CSharpFeature.DefaultLiteral, "static class C { static int F() { int x = default; return x; } }")]
  [TestCase(CSharpFeature.StaticLocalFunction, "static class C { static int F(int x) { static int G(int y) { return y; } return G(x); } }")]
  [TestCase(CSharpFeature.NativeInteger, "static class C { static long F(int x) { nint y = x; return y; } }")]
  [TestCase(CSharpFeature.FileScopedNamespace, "namespace N; static class C { }")]
  [TestCase(CSharpFeature.UnsignedRightShift, "static class C { static int F(int x) { return x >>> 1; } }")]
  public void LanguageRow_IsTheCompilersVersion(CSharpFeature feature, string source) {
    var version = CSharpCapabilities.Default.Of(feature)!.Language!;
    var before = CSharpProfile.LanguageVersions.TakeWhile(candidate => candidate < version).Last();
    Assert.That(GeneratedCSharp.ComplaintsFor(source, "net10.0", CSharpProfile.NameOf(version)), Is.Empty);
    Assert.That(GeneratedCSharp.ComplaintsFor(source, "net10.0", CSharpProfile.NameOf(before)), Is.Not.Empty);
  }

  /// <summary>
  /// The first profiles of the APIs no operation of the IR uses yet are those of the reference
  /// assemblies: a use compiles in the profile the table names and not in the one before.
  /// </summary>
  [TestCase("Array.Empty<T>()", "static class C { static int[] F() { return System.Array.Empty<int>(); } }")]
  [TestCase("Math.Clamp", "static class C { static int F(int x) { return System.Math.Clamp(x, 0, 9); } }")]
  [TestCase("Math.BigMul(ulong, ulong, out ulong)", "static class C { static ulong F(ulong x, out ulong low) { return System.Math.BigMul(x, x, out low); } }")]
  public void ApiRow_IsTheFrameworksFirstProfile(string name, string source) {
    var row = CSharpCapabilities.Default.Rows.Single(candidate => candidate.Name == name);
    Assert.That((row.Operation, row.Feature, row.Saturates, row.Native), Is.EqualTo(((IrIntrinsicOp?)null, (CSharpFeature?)null, (IrTypeKind?)null, (CSharpForm?)null)));
    Assert.That(GeneratedCSharp.ComplaintsFor(source, row.Since!.Name, "2"), Is.Empty);
    Assert.That(GeneratedCSharp.ComplaintsFor(source, Before(row.Since)!.Name, "2"), Is.Not.Empty);
  }

  // ---------------------------------------------------------------- the table as the emitter uses it

  /// <summary>Every row an operation or a conversion is written by has a function here that uses it.</summary>
  [Test]
  public void EveryWrittenRow_HasAUse() {
    var written = CSharpCapabilities.Default.Rows.Where(row => row.Operation is not null || row.Saturates is not null).Select(row => row.Name);
    Assert.That(_uses.Select(use => use.Row).Distinct(), Is.EquivalentTo(written));
  }

  /// <summary>
  /// Test design 3, 6, 7 and 8: one function that uses a row, in every profile. From the row's first
  /// profile on the file calls the API, before it the replacement - the helper is declared exactly
  /// where it is called; every file compiles against its profile's reference assemblies, and the
  /// file of the first profile does not compile against those of the profile before; and whatever a
  /// profile wrote returns, for every operand, the bits the library call of the newest profile and
  /// the interpreter return, without and with overflow checking.
  /// </summary>
  [TestCaseSource(nameof(Uses))]
  public void Row_IsTheApiWhereTheProfileHasIt_AndItsReplacementElsewhere(string name, string method) {
    var row = CSharpCapabilities.Default.Rows.Single(candidate => candidate.Name == name);
    var module = Using(row, method);
    var calls = Calls(module);
    var expected = calls.Select(call => PrototypeFixture.InterpretedOutcome(module, call)).ToList();
    var executed = new Dictionary<string, string>();
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      var native = row.IsIn(profile);
      var declarations = text.Split('\n').Where(line => line.StartsWith("  private static ", StringComparison.Ordinal)).ToList();
      // What the function calls, not what a helper behind it calls in turn.
      var function = declarations.Count == 0 ? text : text[..text.IndexOf(declarations[0], StringComparison.Ordinal)];
      if (row.Native is { } form)
        Assert.That(function.Contains(form.Method + "("), Is.EqualTo(native), $"{profile}:\n{text}");

      Assert.That(declarations, Is.EqualTo(native || row.Helper is null ? [] : new[] { "  " + row.Helper.Lines(row.Helper.Name)[0] }), $"{profile}:\n{text}");
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, $"{profile}:\n{text}");
      if (profile == row.Since && Before(profile) is { } before && row.Native is not null && !_sameNameOtherResult.Contains(name))
        Assert.That(GeneratedCSharp.ComplaintsFor(text, before.Name, profile.LanguageVersionName), Is.Not.Empty, $"{before.Name} has '{name}' too:\n{text}");

      if (!executed.TryAdd(text[text.IndexOf("\n\n", StringComparison.Ordinal)..], profile.Name))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        var mismatches = calls.Where((call, index) => build.Outcome(call) != expected[index]).Select(call => call.ToString()).ToList();
        Assert.That(mismatches.Take(10), Is.Empty, $"{profile}, CheckOverflow {checking}: {mismatches.Count} of {calls.Count} calls differ from the interpreter\n{text}");
      }
    }

    // A row that begins after the oldest profile is written in two ways at least, and both were run.
    Assert.That(executed, Has.Count.GreaterThan(row.Since == CSharpProfile.Net20 ? 0 : 1));
    Assert.That(calls, Has.Count.GreaterThan(2));
  }

  /// <summary>
  /// Every helper of the table is C# 2 for .NET Framework 2.0, whatever profile calls it, and all of
  /// them together are one class: no two that can stand in one class have one signature.
  /// </summary>
  [Test]
  public void EveryHelper_IsCSharp2ForNetFramework20() {
    var helpers = CSharpCapabilities.Default.Rows.Select(row => row.Helper).OfType<CSharpHelper>().Distinct().ToList();
    var text = $"public static class Helpers {{\n\n{string.Join("\n\n", helpers.Select(helper => "  " + string.Join("\n  ", helper.Lines(helper.Name))))}\n\n}}\n";
    Assert.That(helpers, Has.Count.EqualTo(30));
    Assert.That(GeneratedCSharp.ComplaintsFor(text, "net2.0", "2"), Is.Empty, text);
    foreach (var checking in _overflowChecking)
      Assert.That(() => GeneratedCSharp.Compile(text, checking), Throws.Nothing);
  }

  /// <summary>
  /// Test design 6 for the one helper no operation of the IR calls yet: the product of two 64-bit
  /// numbers in four partial products has the two halves <c>Math.BigMul</c> has, at 0, 1, all ones,
  /// the top bit and the carries between the halves, and on a thousand random pairs (seed 2026).
  /// </summary>
  [Test]
  public void FullProductOfTwo64BitNumbers_IsWhatTheLibraryComputes() {
    var helper = CSharpCapabilities.Default.Rows.Single(row => row.Name == "Math.BigMul(ulong, ulong, out ulong)").Helper!;
    var text = $"public static class Product {{\n\n  {string.Join("\n  ", helper.Lines(helper.Name))}\n\n}}\n";
    ulong[] edges = [0, 1, 2, 0xFFFFFFFF, 0x100000000, 0x100000001, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0xFFFFFFFF00000000, ulong.MaxValue];
    var random = new Random(2026);
    var pairs = edges.SelectMany(left => edges.Select(right => (left, right)))
      .Concat(Enumerable.Range(0, 1000).Select(_ => ((ulong)random.NextInt64(long.MinValue, long.MaxValue), (ulong)random.NextInt64(long.MinValue, long.MaxValue))))
      .ToList();
    foreach (var checking in _overflowChecking) {
      var product = GeneratedCSharp.Compile(text, checking).Method("BigMul");
      foreach (var (left, right) in pairs) {
        object?[] arguments = [left, right, null];
        var high = product.Invoke(null, arguments);
        Assert.That((high, arguments[2]), Is.EqualTo((Math.BigMul(left, right, out var low), low)), $"{left} * {right}, CheckOverflow {checking}");
      }
    }

    Assert.That(pairs, Has.Count.EqualTo(1100));
  }

}
