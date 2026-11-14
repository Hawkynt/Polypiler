using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Float;

/// <summary>
/// The endian loads and stores of floating-point numbers in the C# backend: one row of the capability
/// table per operation and format, three forms of a load - <c>BinaryPrimitives.Read{Single,Double}…</c>
/// from net8.0, the bit cast of <c>BinaryPrimitives.ReadInt…</c> from netstandard2.1, the helpers
/// before - and a store that is the helper of the integer of its width with the bits of the number
/// in every profile. Generated code of every profile computes what .NET's <c>BinaryPrimitives</c> computes.
/// </summary>
[TestFixture]
public sealed class FloatViewCSharpTests {

  private const string Binary = "global::System.Buffers.Binary.BinaryPrimitives.";
  private const string Converter = "global::System.BitConverter.";

  private static readonly bool[] _overflowChecking = [false, true];

  private static IEnumerable<TestCaseData> Cases() => FloatViewCases.All.Select(@case => new TestCaseData(@case.Function));

  private static CSharpCapability RowOf(IntegerCase @case)
    => CSharpCapabilities.Default.Of(@case.Op, FloatViewCases.FormatOf(@case).Kind)!;

  private static string Emit(string function, CSharpProfile profile, CSharpCapabilities? table = null)
    => CSharpEmitter.Emit(IntegerCases.Module([FloatViewCases.Of(function)], "Snippet"), profile, capabilities: table).Single().Content;

  /// <summary>The body of the one method of a generated class, its expression or its statements, without the helpers.</summary>
  private static string Body(string text, string function) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($" {function}("));
    return string.Join("\n", lines.Skip(start).TakeWhile((line, index) => index == 0 || line.Length > 0)).Trim();
  }

  /// <summary>Test design 3 and 7: one row per operation and format, named after both; a load is native from net8.0 on, a store never.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_BeginsWhereTheApiDoes(string function) {
    var @case = FloatViewCases.Of(function);
    var row = RowOf(@case);
    var format = FloatViewCases.FormatOf(@case) == IrType.F32 ? "float" : "double";
    Assert.That(row.Name, Is.EqualTo($"{@case.Op} of {format}"));
    Assert.That(row.Rewrite, Is.Not.Null);
    Assert.That(row.Replacement, Is.Null);
    Assert.That(row.Since?.Name ?? "never", Is.EqualTo(FloatViewCases.Stores(@case) ? "never" : "net8.0"));
    Assert.That(row.IsRewrittenIn(CSharpProfile.NetCoreApp31), Is.True);
    Assert.That(row.IsRewrittenIn(CSharpProfile.Net80), Is.EqualTo(FloatViewCases.Stores(@case)));
  }

  /// <summary>The rows of the formats are named apart from every other row of the table.</summary>
  [Test]
  public void Rows_AreNamedApart() => Assert.That(CSharpCapabilities.Default.Rows.Select(row => row.Name), Is.Unique);

  /// <summary>Test design 11: every operation and format can be written in every profile - no PLY1052, no PLY1053.</summary>
  [Test]
  public void EveryOperation_HasAFormInEveryProfile() {
    var module = FloatViewCases.Module();
    foreach (var profile in CSharpProfile.All)
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
  }

  /// <summary>Test design 7: the three forms of a load, at the first profile of each and the one before.</summary>
  [TestCase("LoadLittle_f32", "net8.0", Binary + "ReadSingleLittleEndian(new global::System.ReadOnlySpan<byte>(a, b, 4))")]
  [TestCase("LoadLittle_f32", "netcoreapp3.1", Converter + "Int32BitsToSingle(" + Binary + "ReadInt32LittleEndian(new global::System.ReadOnlySpan<byte>(a, b, 4)))")]
  [TestCase("LoadBig_f32", "netstandard2.1", Converter + "Int32BitsToSingle(" + Binary + "ReadInt32BigEndian(new global::System.ReadOnlySpan<byte>(a, b, 4)))")]
  [TestCase("LoadBig_f32", "netstandard2.0", "Int32BitsToSingle((int)Load(a, b, 4, false))")]
  [TestCase("LoadBig_f64", "net10.0", Binary + "ReadDoubleBigEndian(new global::System.ReadOnlySpan<byte>(a, b, 8))")]
  [TestCase("LoadLittle_f64", "netstandard2.1", Converter + "Int64BitsToDouble(" + Binary + "ReadInt64LittleEndian(new global::System.ReadOnlySpan<byte>(a, b, 8)))")]
  [TestCase("LoadLittle_f64", "net2.0", Converter + "Int64BitsToDouble((long)Load(a, b, 8, true))")]
  [TestCase("StoreLittle_f32", "net10.0", "Store(a, b, (ulong)" + Converter + "SingleToInt32Bits(c), 4, true);")]
  [TestCase("StoreLittle_f32", "netstandard2.1", "Store(a, b, (ulong)" + Converter + "SingleToInt32Bits(c), 4, true);")]
  [TestCase("StoreBig_f32", "netstandard2.0", "Store(a, b, (ulong)SingleToInt32Bits(c), 4, false);")]
  [TestCase("StoreBig_f64", "net2.0", "Store(a, b, (ulong)" + Converter + "DoubleToInt64Bits(c), 8, false);")]
  [TestCase("StoreLittle_f64", "net10.0", "Store(a, b, (ulong)" + Converter + "DoubleToInt64Bits(c), 8, true);")]
  public void Form_IsTheOneOfTheProfile(string function, string profile, string form) {
    var text = Emit(function, CSharpProfile.Parse(profile));
    Assert.That(Body(text, function), Does.Contain(form), text);
    if (!form.Contains("Load(") && !form.Contains("Store("))
      Assert.That(text, Does.Not.Contain("private static"), "no helper where the base library has the form");
  }

  /// <summary>Nothing generated reads the byte order of the host.</summary>
  [Test]
  public void NoProfile_AsksTheHostForItsByteOrder() {
    foreach (var profile in CSharpProfile.All)
      Assert.That(CSharpEmitter.Emit(FloatViewCases.Module(), profile).Single().Content, Does.Not.Contain("IsLittleEndian"), profile.Name);
  }

  /// <summary>
  /// A row written as other operations needs a form of those in the profile: a table whose bit cast
  /// of an <c>int</c> has no replacement cannot write the load of a <c>float</c> before net8.0 - and
  /// says so by the row it lacks.
  /// </summary>
  [Test]
  public void RewrittenRow_NeedsTheRowsItIsWrittenWith() {
    var rows = CSharpCapabilities.Default.Rows.Select(row => row.Operation == IrIntrinsicOp.BitsFloat && row.Operands.SequenceEqual([IrTypeKind.I32])
      ? CSharpCapability.OfOperation(row.Name, row.Since, IrIntrinsicOp.BitsFloat, row.Operands, row.Native)
      : row);
    var table = new CSharpCapabilities(rows);
    var module = IntegerCases.Module([FloatViewCases.Of("LoadLittle_f32")], "Snippet");
    Assert.That(CSharpEmitter.Check(module, CSharpProfile.Net80, table), Is.Empty);
    Assert.That(CSharpEmitter.Check(module, CSharpProfile.NetStandard21, table), Is.Empty);
    var problems = CSharpEmitter.Check(module, CSharpProfile.NetStandard20, table);
    Assert.That(problems.Select(problem => problem.Code), Is.EqualTo(new[] { CSharpEmitter.Inexpressible }));
    Assert.That(problems[0].Message, Does.Contain("BitConverter.Int32BitsToSingle(int)").And.Contain("netstandard2.0"));
    Assert.That(() => Emit("LoadLittle_f32", CSharpProfile.NetStandard20, table), Throws.ArgumentException);
  }

  /// <summary>A rewrite and a form for arguments that cannot trap travel together: each extension keeps the other.</summary>
  [Test]
  public void Extensions_KeepEachOther() {
    var row = RowOf(FloatViewCases.Of("LoadBig_f64"));
    var total = new CSharpForm(null, Template: "{0}");
    Func<IReadOnlyList<IrExpr>, bool> never = _ => false;
    var both = row.WithTotal(total, never);
    Assert.That(both.Rewrite, Is.SameAs(row.Rewrite));
    Assert.That(both.Total, Is.SameAs(total));
    var again = both.WithRewrite(row.Rewrite!);
    Assert.That(again.Total, Is.SameAs(total));
    Assert.That(again.IsTotal, Is.SameAs(never));
    Assert.That(again.Since, Is.EqualTo(row.Since));
    Assert.That(again.Native, Is.EqualTo(row.Native));
    Assert.That(CSharpCapability.OfApi("Something()", CSharpProfile.Net20).IsRewrittenIn(CSharpProfile.Net20), Is.False);
  }

  /// <summary>
  /// A load written as the integer one and a bit cast stands where overflow is not checked: the cast
  /// of the helper's <c>ulong</c> to an <c>int</c> keeps the bits of a negative number in a project
  /// that checks overflow.
  /// </summary>
  [Test]
  public void RewrittenLoad_StandsWhereOverflowIsNotChecked() {
    var text = Emit("LoadLittle_f32", CSharpProfile.Net20);
    Assert.That(text, Does.Contain("unchecked"));
    var build = GeneratedCSharp.Compile(text, true);
    Assert.That(build.Outcome(new PrototypeCall("LoadLittle_f32", [new byte[] { 0, 0, 0xC0, 0xBF }, 0])), Is.EqualTo("f:bff8000000000000 | [0,0,192,191]"));
  }

  /// <summary>
  /// Test design 7: the native form at its first profile compiles there and not in the profile
  /// before, whose composed form compiles in it; the composed form of netstandard2.1 does not compile
  /// for netstandard2.0, whose helpers do.
  /// </summary>
  [Test, Category("Slow")]
  public void EachForm_BeginsAtItsFirstProfile() {
    foreach (var @case in FloatViewCases.All.Where(@case => !FloatViewCases.Stores(@case))) {
      var module = IntegerCases.Module([@case], "Snippet");
      foreach (var (first, before) in new[] { (CSharpProfile.Net80, CSharpProfile.NetCoreApp31), (CSharpProfile.NetStandard21, CSharpProfile.NetStandard20) }) {
        var native = CSharpEmitter.Emit(module, first).Single().Content;
        var replaced = CSharpEmitter.Emit(module, before).Single().Content;
        Assert.That(GeneratedCSharp.ComplaintsFor(native, first.Name, first.LanguageVersionName), Is.Empty, native);
        Assert.That(GeneratedCSharp.ComplaintsFor(replaced, before.Name, before.LanguageVersionName), Is.Empty, replaced);
        Assert.That(GeneratedCSharp.ComplaintsFor(native, before.Name, first.LanguageVersionName), Is.Not.Empty, $"{before.Name} has the form of {first.Name}:\n{native}");
      }
    }
  }

  /// <summary>
  /// Test design 1, 2, 4, 5, 6 and the differential measurement for C#: the module of every operation
  /// and format, in every profile, compiles against that profile's reference assemblies and, built for
  /// the running runtime without and with overflow checking, has the outcome of <c>BinaryPrimitives</c>
  /// on every boundary, every class of value and 1000 random bit patterns per operation (seed 2026).
  /// </summary>
  [Test, Category("Slow")]
  public void EveryProfile_ComputesBinaryPrimitives() {
    var module = FloatViewCases.Module();
    var random = new Random(IntegerCases.Seed);
    var calls = FloatViewCases.All.SelectMany(@case => FloatViewCases.Calls(@case, random).Select(call => (Case: @case, Call: call))).ToList();
    var expected = calls.Select(entry => FloatViewCases.Expected(entry.Case, entry.Call)).ToList();
    var executed = new Dictionary<string, string>();
    var mismatches = new List<string>();
    var compared = 0;
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, profile.Name);
      var code = text[text.IndexOf("\n\n", StringComparison.Ordinal)..];
      if (!executed.TryAdd(code, profile.Name))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        for (var index = 0; index < calls.Count; ++index, ++compared)
          if (build.Outcome(calls[index].Call) is var outcome && !FloatViewCases.Matches(calls[index].Case, calls[index].Call, expected[index], outcome))
            mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index].Call} is {outcome}, BinaryPrimitives {expected[index]}");
      }
    }

    TestContext.Out.WriteLine($"{calls.Count} calls x {executed.Count} distinct texts ({string.Join(", ", executed.Values)}) x 2 builds = {compared} outcomes, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

}
