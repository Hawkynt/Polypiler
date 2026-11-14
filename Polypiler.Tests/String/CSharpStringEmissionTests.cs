using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.String;

/// <summary>
/// The string operations written by the C# backend for every profile: compiled against the
/// reference assemblies of the profile at its language version, then run on this runtime with
/// random strings against the interpreter.
/// </summary>
[TestFixture]
internal sealed class CSharpStringEmissionTests {

  private static string Emitted(CSharpProfile profile) => CSharpEmitter.Emit(StringModules.Operations, profile)[0].Content;

  /// <summary>Test design 3 and 14: every operation in every profile is written, compiles, and computes what the interpreter computes.</summary>
  [TestCaseSource(typeof(CSharpProfile), nameof(CSharpProfile.All))]
  public void EveryOperation_IsWrittenInEveryProfile_AndRunsLikeTheInterpreter(CSharpProfile profile) {
    Assert.That(CSharpEmitter.Check(StringModules.Operations, profile), Is.Empty);
    var text = Emitted(profile);
    Assert.That(GeneratedCSharp.ComplaintsFor(text, profile.Name, profile.LanguageVersionName), Is.Empty, text);
    var build = GeneratedCSharp.Compile(text, true);
    var mismatches = new List<string>();
    foreach (var (name, _) in StringModules.Functions)
      foreach (var arguments in StringModules.Calls(name)) {
        var expected = Outcome(() => IrInterpreter.Call(StringModules.Operations, name, arguments));
        var actual = Outcome(() => Invoke(build.Method(name), arguments));
        if (expected != actual)
          mismatches.Add($"{name}({string.Join(", ", arguments.Select(PrototypeFixture.Show))}): {actual}, the interpreter {expected}");
      }

    Assert.That(mismatches, Is.Empty, text);
  }

  private static object? Invoke(MethodInfo method, object[] arguments) {
    try {
      return method.Invoke(null, arguments);
    } catch (TargetInvocationException exception) {
      throw exception.InnerException!;
    }
  }

  /// <summary>The value a call returned, or that it trapped: an exception here, an <see cref="IrTrapException"/> in the interpreter.</summary>
  internal static string Outcome(Func<object?> call) {
    try {
      return Shown(call());
    } catch (Exception exception) when (exception is IrTrapException or IndexOutOfRangeException or ArgumentException or OverflowException) {
      return "trap";
    }
  }

  internal static string Shown(object? value) => value switch {
    string text => "\"" + string.Concat(text.Select(unit => $"{(int)unit:X4} ")) + "\"",
    string[] parts => "[" + string.Join(",", parts.Select(Shown)) + "]",
    _ => PrototypeFixture.Show(value)
  };

  [TestCase("net2.0", "(ushort)a[i]")]
  [TestCase("net2.0", "return a.IndexOf(b, i, global::System.StringComparison.Ordinal);")]
  [TestCase("net2.0", "return string.CompareOrdinal(a, b) < 0;")]
  [TestCase("net2.0", "return global::System.Math.Sign(string.CompareOrdinal(a, b));")]
  [TestCase("net2.0", "return StartsWithUnit_(a, (char)c);")]
  [TestCase("net2.0", "return (a.IndexOf((char)c) >= 0);")]
  [TestCase("net2.0", "return a.Split(new char[] { (char)c });")]
  [TestCase("net2.0", "return TrimDotNet(a, true, true);")]
  [TestCase("net4.6", "TrimNet(string a) => a.Trim();")]
  [TestCase("net10.0", "a.StartsWith((char)c)")]
  [TestCase("net10.0", "a.Contains((char)c)")]
  [TestCase("net10.0", "a.Split((char)c)")]
  [TestCase("net10.0", "Split_(a, char.ToString((char)c), i)")]
  [TestCase("net10.0", "TrimEcma_(a, false, true)")]
  [TestCase("net10.0", "private const string Greeting = \"h\\u00E9llo \\\"\\\\\\u0001\\uD800\";")]
  [TestCase("net10.0", "a.Replace(b, \"$&$$\")")]
  [TestCase("net10.0", "string.Concat(a, b, a.Substring(0, 0), \"!\")")]
  public void Operation_HasTheFormOfTheProfile(string profile, string form)
    => Assert.That(Emitted(CSharpProfile.Parse(profile)), Does.Contain(form));

  /// <summary>Test design 13: the first profile with a native member and the one before it.</summary>
  [TestCase("String.StartsWith(Char)", "netcoreapp3.1", "netstandard2.0")]
  [TestCase("String.EndsWith(Char)", "netstandard2.1", "netstandard2.0")]
  [TestCase("String.Contains(Char)", "netstandard2.1", "netstandard2.0")]
  [TestCase("String.Split(Char, StringSplitOptions)", "netstandard2.1", "net4.8")]
  [TestCase("String.Trim()", "net4.6", "net3.5")]
  public void NativeRow_StartsAtItsFirstProfile(string name, string first, string before) {
    var row = CSharpCapabilities.Default.Rows.Single(candidate => candidate.Name == name);
    Assert.That((row.IsIn(CSharpProfile.Parse(first)), row.IsIn(CSharpProfile.Parse(before)), row.Replacement is not null), Is.EqualTo((true, false, true)));
  }

  [Test]
  public void Rows_CoverEveryStringOperation() {
    var rows = CSharpCapabilities.Default.Rows.Where(row => row.Operation is { } op && StringOperations.IsString(op)).Select(row => row.Operation!.Value);
    Assert.That(rows.Distinct(), Is.EquivalentTo(StringOperations.Operations));
  }

  [Test]
  public void Row_OfANodeHonoursItsCondition() {
    var trim = new IrIntrinsic(IrIntrinsicOp.StrTrim, IrType.String, [new IrStrConst("a"), StringOperations.Set(WhitespaceSet.Ecma)]);
    Assert.That(CSharpCapabilities.Default.Of(trim)!.Name, Is.EqualTo("trim of ECMA-262"));
    var table = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Name != "trim of ECMA-262"));
    Assert.That(table.Of(trim), Is.Null);
    Assert.That(CSharpCapabilities.Default.Rows.Single(row => row.Name == "String.Trim()").WithTotal(new CSharpForm("x"), _ => true).IsFor(trim), Is.False);
    Assert.That(CSharpCapabilities.Default.Rows.Single(row => row.Name == "String.Trim()").WithRewrite(node => node).IsFor(trim), Is.False);
  }

  [Test]
  public void Module_WithoutAStringRow_IsUnsupported() {
    var table = new CSharpCapabilities(CSharpCapabilities.Default.Rows.Where(row => row.Operation != IrIntrinsicOp.StrNextCodePoint));
    Assert.That(CSharpEmitter.Check(StringModules.Operations, CSharpProfile.Default, table).Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { CSharpEmitter.Unsupported }));
  }

}
