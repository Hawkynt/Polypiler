using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The integer and bit operations in the C# backend: one row of the capability table per operation
/// and width, each beginning where the reference assemblies say its API begins, and generated code
/// of every profile that computes, on every boundary and on random operands, what the bit-by-bit
/// reference computes, with the project's overflow checking off and on.
/// </summary>
[TestFixture]
public sealed class IntegerCSharpTests {

  private static readonly bool[] _overflowChecking = [false, true];

  /// <summary>
  /// The first profile of every operation and width, from the .NET API reference and written down
  /// here rather than read from the table: "never" for a helper in every profile, net2.0 for what C#
  /// writes with its operators.
  /// </summary>
  private static string First(IntegerCase @case) {
    var kind = @case.Types[0].IsArray ? (@case.Types.Length == 3 ? @case.Types[2] : @case.Result) : @case.Op == IrIntrinsicOp.LowMask ? @case.Result : @case.Types[0];
    var width = kind.Bits;
    var narrow = width < 32;
    var unsigned = !kind.IsSigned;
    return @case.Op switch {
      IrIntrinsicOp.RotateLeft or IrIntrinsicOp.RotateRight => narrow ? "net8.0" : "netcoreapp3.1",
      IrIntrinsicOp.PopCount or IrIntrinsicOp.LeadingZeroCount or IrIntrinsicOp.TrailingZeroCount or IrIntrinsicOp.Log2 or IrIntrinsicOp.Parity => "netcoreapp3.1",
      IrIntrinsicOp.ByteSwap or IrIntrinsicOp.LoadLittle or IrIntrinsicOp.LoadBig => "netstandard2.1",
      IrIntrinsicOp.IsPow2 or IrIntrinsicOp.RoundUpPow2 => "net8.0",
      IrIntrinsicOp.BigMul => unsigned ? "net10.0" : "net2.0",
      IrIntrinsicOp.MulHigh => width == 64 ? "net8.0" : "net2.0",
      IrIntrinsicOp.Min or IrIntrinsicOp.Max or IrIntrinsicOp.Abs or IrIntrinsicOp.Sign or IrIntrinsicOp.AddCarry or IrIntrinsicOp.SubBorrow => "net2.0",
      IrIntrinsicOp.AbsWrap => unsigned ? "net2.0" : "never",
      IrIntrinsicOp.CarryOut or IrIntrinsicOp.BorrowOut => width == 64 ? "never" : "net2.0",
      IrIntrinsicOp.SubOverflows => unsigned ? "net2.0" : "never",
      IrIntrinsicOp.AddOverflows or IrIntrinsicOp.MulOverflows => unsigned && width < 64 ? "net2.0" : "never",
      _ => "never"
    };
  }

  private static IEnumerable<TestCaseData> Cases() => IntegerCases.All.Select(@case => new TestCaseData(@case.Function));

  private static CSharpCapability RowOf(IntegerCase @case) {
    var module = IntegerCases.Module([@case]);
    var node = (IrIntrinsic)(module.Functions[0].Body.Statements[0] switch {
      IrReturn result => result.Value!,
      var statement => ((IrExprStmt)statement).Expression
    });
    return CSharpCapabilities.Default.Of(@case.Op, CSharpCapabilities.OperandOf(node))!;
  }

  private static CSharpProfile? Before(CSharpProfile profile) => CSharpProfile.All.TakeWhile(candidate => candidate != profile).LastOrDefault();

  /// <summary>The generated file without the header that names the profile.</summary>
  private static string Code(string text) => text[text.IndexOf("\n\n", StringComparison.Ordinal)..];

  /// <summary>Test design 13: every operation and width has a form in every profile, so no profile reports PLY1052 or PLY1053.</summary>
  [Test]
  public void EveryOperation_HasAFormInEveryProfile() {
    var module = IntegerCases.Module(IntegerCases.All);
    foreach (var profile in CSharpProfile.All)
      Assert.That(CSharpEmitter.Check(module, profile), Is.Empty, profile.Name);
  }

  /// <summary>One row per operation and width, named after both, beginning at the profile the .NET API reference names.</summary>
  [TestCaseSource(nameof(Cases))]
  public void Row_BeginsWhereTheApiDoes(string function) {
    var @case = IntegerCases.All.Single(candidate => candidate.Function == function);
    var row = RowOf(@case);
    Assert.That(row, Is.Not.Null);
    Assert.That(row.Since?.Name ?? "never", Is.EqualTo(First(@case)), row.Name);
    Assert.That(row.Native is not null || row.Replacement is not null, Is.True);
    Assert.That(row.Since is null || row.Native is not null, Is.True, "a row with a first profile has a native form");
    Assert.That(row.Since == CSharpProfile.Net20 || row.Replacement is not null, Is.True, "a row without a form in every profile has a replacement");
    if (row.Replacement?.Template is { } template)
      Assert.That(template.Contains("{h}"), Is.EqualTo(row.Helper is not null), "a replacement calls the helper of its row");
  }

  /// <summary>The integer rows are named apart from each other and from every other row of the table.</summary>
  [Test]
  public void Rows_AreNamedApart() {
    var rows = CSharpCapabilities.Default.Rows;
    Assert.That(rows.Select(row => row.Name), Is.Unique);
    var named = IntegerCases.All.Select(RowOf).Where(row => row.Name.Contains(" of ")).ToList();
    Assert.That(named.Select(row => row.Name), Is.Unique);
    Assert.That(named, Has.Count.GreaterThan(300));
  }

  /// <summary>
  /// Test design 4: every operation that is a library call somewhere, at the first profile that has
  /// it and the one before: the call stands in the first and the helper in the other, both compile
  /// for their own framework, and the first does not compile for the framework before - which is
  /// what makes it the first.
  /// </summary>
  [Test, Category("Slow")]
  public void NativeForm_BeginsAtItsFirstProfile_AndTheHelperStandsBefore() {
    var checkedRows = 0;
    foreach (var @case in IntegerCases.All) {
      var row = RowOf(@case);
      if (row.Since is not { } since || Before(since) is not { } before)
        continue;

      var module = IntegerCases.Module([@case], "Snippet");
      var native = CSharpEmitter.Emit(module, since).Single().Content;
      var replaced = CSharpEmitter.Emit(module, before).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(native, since.Name, since.LanguageVersionName), Is.Empty, native);
      Assert.That(GeneratedCSharp.ComplaintsFor(replaced, before.Name, before.LanguageVersionName), Is.Empty, replaced);
      if (row.Native!.Method is { } method) {
        Assert.That(native, Does.Contain(method + "("), native);
        Assert.That(replaced, Does.Not.Contain(method + "("), replaced);
        Assert.That(GeneratedCSharp.ComplaintsFor(native, before.Name, since.LanguageVersionName), Is.Not.Empty, $"{before.Name} has '{row.Name}' too:\n{native}");
      }

      if (row.Helper is { } helper)
        Assert.That(replaced, Does.Contain($"private static {helper.Returns} {helper.Name}("), replaced);

      ++checkedRows;
    }

    Assert.That(checkedRows, Is.EqualTo(IntegerCases.All.Count(@case => First(@case) is not ("never" or "net2.0"))));
  }

  /// <summary>
  /// Test design 2, 4, 5, 6, 9 and 12 for C#: the module of every operation and width, in every
  /// profile, compiles against that profile's reference assemblies at its language version and, built
  /// for the running runtime without and with overflow checking, has the outcome of the bit-by-bit
  /// reference for every boundary combination and 1000 random tuples per signature (seed 2026).
  /// </summary>
  [Test, Category("Slow")]
  public void EveryProfile_ComputesTheReference() {
    var module = IntegerCases.Module(IntegerCases.All);
    var random = new Random(IntegerCases.Seed);
    var calls = IntegerCases.All.SelectMany(@case => IntegerCases.Calls(@case, random).Select(call => (Case: @case, Call: call))).ToList();
    var expected = calls.Select(entry => IntegerCases.Expected(entry.Case, entry.Call)).ToList();
    var executed = new Dictionary<string, string>();
    var mismatches = new List<string>();
    foreach (var profile in CSharpProfile.All) {
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, profile.Name);
      if (!executed.TryAdd(Code(text), profile.Name))
        continue;

      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        for (var index = 0; index < calls.Count; ++index)
          if (build.Outcome(calls[index].Call) is var outcome && outcome != expected[index])
            mismatches.Add($"{profile.Name}, CheckOverflow {checking}: {calls[index].Call} is {outcome}, the reference {expected[index]}");
      }
    }

    TestContext.Out.WriteLine($"{IntegerCases.All.Count} operations and widths, {calls.Count} calls x {executed.Count} distinct texts ({string.Join(", ", executed.Values)}) x 2 builds, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

  /// <summary>The byte swap of the issue: a library call in .NET 10, a helper and no BinaryPrimitives in .NET Framework 2.0.</summary>
  [Test]
  public void ByteSwap_IsALibraryCallWhereTheLibraryHasIt() {
    var swap = IntegerCases.Module([IntegerCases.All.Single(@case => @case.Function == "ByteSwap_u32")], "Snippet");
    var modern = CSharpEmitter.Emit(swap, CSharpProfile.Net100).Single().Content;
    var old = CSharpEmitter.Emit(swap, CSharpProfile.Net20).Single().Content;
    Assert.That(modern, Does.Contain("global::System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(a)").And.Not.Contain("private static"));
    Assert.That(old, Does.Contain("private static ulong ByteSwapBits(ulong value, int bits)").And.Not.Contain("BinaryPrimitives"));
    foreach (var text in new[] { modern, old })
      Assert.That(GeneratedCSharp.Compile(text, true).Outcome(new PrototypeCall("ByteSwap_u32", [0x12345678u])), Is.EqualTo("2018915346"));
  }

  /// <summary>A template stands where overflow is not checked even inside checked arithmetic, and its checked operands stay checked.</summary>
  [Test]
  public void Template_InCheckedArithmetic_WrapsItsCastsAndKeepsTheTrap() {
    var a = new IrLocal("a", IrType.I32);
    var b = new IrLocal("b", IrType.I32);
    var popCount = new IrIntrinsic(IrIntrinsicOp.PopCount, IrType.I32, [new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(a), new IrLocalRef(b))]);
    var sum = new IrBinary(IrBinaryOp.AddChecked, popCount, new IrLocalRef(b));
    var module = new IrModule("Snippet", [new IrFunction("F", true, IrType.I32, [a, b], [], new IrBlock([new IrReturn(sum)]))]);
    foreach (var profile in new[] { CSharpProfile.Net20, CSharpProfile.Net100 }) {
      var text = CSharpEmitter.Emit(module, profile).Single().Content;
      foreach (var checking in _overflowChecking) {
        var build = GeneratedCSharp.Compile(text, checking);
        Assert.That(build.Outcome(new PrototypeCall("F", [-1, 0])), Is.EqualTo("32"), text);
        Assert.That(build.Outcome(new PrototypeCall("F", [int.MaxValue, 1])), Is.EqualTo("trap: integer overflow"), text);
        Assert.That(build.Outcome(new PrototypeCall("F", [-2, int.MaxValue])), Is.EqualTo("trap: integer overflow"), text);
      }
    }
  }

}
