using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The conversions between numbers and text written by the C# backend (#1315): every operation in
/// every profile compiles against the reference assemblies of the profile and computes what the
/// interpreter computes; the helpers that stand in for .NET Core 3.0's rules before netcoreapp3.1
/// write what .NET 10 writes for the reference vectors and for the xorshift numbers of the issue.
/// </summary>
[TestFixture]
public sealed class CSharpNumberTextEmissionTests {

  private static string Emitted(CSharpProfile profile) => CSharpEmitter.Emit(NumberTextModules.Operations, profile)[0].Content;

  /// <summary>Test design 1, 13 and 14: every operation in every profile is written, compiles and runs like the interpreter.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void EveryOperation_IsWrittenInEveryProfile_AndRunsLikeTheInterpreter(CSharpProfile profile) {
    Assert.That(CSharpEmitter.Check(NumberTextModules.Operations, profile), Is.Empty);
    var text = Emitted(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    var mismatches = NumberTextModules.Calls()
      .Select(call => (call, Expected: PrototypeFixture.InterpretedOutcome(NumberTextModules.Operations, call), Actual: build.Outcome(call)))
      .Where(result => result.Expected != result.Actual)
      .Select(result => $"{result.call}: {result.Actual}, the interpreter {result.Expected}")
      .ToList();
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} mismatches\n{text}");
  }

  [TestCase("net2.0", "NumberText(x, 0, 0)")]
  [TestCase("net2.0", "NumberText(x, 1, 0)")]
  [TestCase("net2.0", "NumberText(x, 4, 2)")]
  [TestCase("net2.0", "ParseText(x, 0, 0)")]
  [TestCase("net2.0", "(float)ParseText(x, 1, 0)")]
  [TestCase("net2.0", "return x.ToString(\"x8\");")]
  [TestCase("net2.0", "return x.ToString(\"X\");")]
  [TestCase("net2.0", "return FormatRadix((long)(byte)x, false, 2, 0, false);")]
  [TestCase("net2.0", "return FormatRadix((long)x, true, 10, 5, false);")]
  [TestCase("net2.0", "return int.Parse(x, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture);")]
  [TestCase("net2.0", "return (x ? \"True\" : \"False\");")]
  [TestCase("netstandard2.1", "NumberText(x, 0, 0)")]
  [TestCase("netcoreapp3.1", "FormatNet(double x) => x.ToString(global::System.Globalization.CultureInfo.InvariantCulture);")]
  [TestCase("netcoreapp3.1", "x.ToString(\"F15\", global::System.Globalization.CultureInfo.InvariantCulture);")]
  [TestCase("net10.0", "=> double.Parse(x, global::System.Globalization.NumberStyles.Float | global::System.Globalization.NumberStyles.AllowThousands, global::System.Globalization.CultureInfo.InvariantCulture);")]
  [TestCase("net10.0", "=> NumberText(x, 2, 0);")]
  [TestCase("net10.0", "=> NumberText(x, 3, 100);")]
  [TestCase("net10.0", "=> ParseText(x, 4, 7);")]
  public void Operation_HasTheFormOfTheProfile(string profile, string form)
    => Assert.That(Emitted(CSharpProfile.Parse(profile)), Does.Contain(form));

  /// <summary>Test design 13: the first profile with the rule of .NET Core 3.0 and the one before it.</summary>
  [TestCase("Double.ToString(IFormatProvider)")]
  [TestCase("Single.ToString(IFormatProvider)")]
  [TestCase("Double.ToString(\"F2\", IFormatProvider)")]
  [TestCase("Double.Parse(String, NumberStyles, IFormatProvider)")]
  [TestCase("Single.Parse(String, NumberStyles, IFormatProvider)")]
  public void NativeRow_StartsAtNetCoreApp31(string name) {
    var row = CSharpCapabilities.Default.Rows.Single(candidate => candidate.Name == name);
    Assert.That((row.IsIn(CSharpProfile.Parse("netcoreapp3.1")), row.IsIn(CSharpProfile.Parse("netstandard2.1")), row.Helper is not null), Is.EqualTo((true, false, true)));
  }

  [Test]
  public void Rows_CoverEveryConversion() {
    var rows = CSharpCapabilities.Default.Rows.Where(row => row.Operation is { } op && NumberTextOperations.IsNumberText(op)).Select(row => row.Operation!.Value);
    Assert.That(rows.Distinct(), Is.EquivalentTo(NumberTextOperations.Operations));
  }

  [Test]
  public void Module_WithoutARow_IsUnsupported() {
    var table = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Operation != IrIntrinsicOp.ParseFloatEcma));
    Assert.That(CSharpEmitter.Check(NumberTextModules.Operations, CSharpProfile.Default, table).Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { CSharpEmitter.Unsupported }));
  }

  [Test]
  public void Header_NamesTheCaveatsOfTheImport() {
    var module = NumberTextModules.Operations with { Caveats = ["cs-invariant-culture"] };
    Assert.That(CSharpEmitter.Emit(module, CSharpProfile.Default)[0].Content, Does.Contain("// Caveats of the import: cs-invariant-culture."));
    Assert.That(Emitted(CSharpProfile.Default), Does.Not.Contain("Caveats"));
  }

  /// <summary>
  /// Test design 2 and 3: the helpers of the profiles before netcoreapp3.1 write the committed strings
  /// of .NET 10 and node for every reference vector, and what this runtime writes for 200 000 doubles
  /// and 100 000 floats of xorshift64 from the seed 0x2026.
  /// </summary>
  [Test]
  [Category("Slow")]
  public void Helpers_WriteTheReferenceVectors_AndWhatDotNetWrites() {
    var build = GeneratedCSharp.Compile(Emitted(CSharpProfile.Net20), true);
    var net = Function<double>(build.Method("FormatNet"));
    var single = Function<float>(build.Method("FormatNetSingle"));
    var ecma = Function<double>(build.Method("FormatEcma"));
    var mismatches = new List<string>();
    foreach (var (kind, bits, dotNet, expected) in NumberTextModules.Vectors()) {
      if (kind == "f32") {
        if (single(BitConverter.UInt32BitsToSingle((uint)bits)) is var text && text != dotNet)
          mismatches.Add($"f32 {bits:x8}: {text}");
        continue;
      }

      var value = BitConverter.UInt64BitsToDouble(bits);
      if (net(value) != dotNet || ecma(value) != expected)
        mismatches.Add($"f64 {bits:x16}: {net(value)} {ecma(value)}");
    }

    var state = 0x2026UL;
    ulong Next() {
      state ^= state << 13;
      state ^= state >> 7;
      state ^= state << 17;
      return state;
    }

    for (var count = 0; count < 200_000;) {
      var value = BitConverter.UInt64BitsToDouble(Next());
      if (!double.IsFinite(value))
        continue;

      ++count;
      if (net(value) != value.ToString(CultureInfo.InvariantCulture))
        mismatches.Add($"f64 {BitConverter.DoubleToInt64Bits(value):x16}: {net(value)}");
    }

    for (var count = 0; count < 100_000;) {
      var value = BitConverter.UInt32BitsToSingle((uint)Next());
      if (!float.IsFinite(value))
        continue;

      ++count;
      if (single(value) != value.ToString(CultureInfo.InvariantCulture))
        mismatches.Add($"f32 {BitConverter.SingleToInt32Bits(value):x8}: {single(value)}");
    }

    Assert.That(mismatches.Take(10), Is.Empty);
  }

  private static Func<T, string> Function<T>(MethodInfo method) => method.CreateDelegate<Func<T, string>>();

}
