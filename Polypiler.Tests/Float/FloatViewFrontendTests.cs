using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Float;

/// <summary>
/// Floating-point numbers in byte buffers from both frontends: the fixtures <c>float/FloatView.cs</c>
/// and <c>js/FloatView.js</c> imported and run by the interpreter, with the outcomes of the C#
/// fixture as the C# compiler built it into this assembly; the members of both libraries mapped to
/// the endian operations on <c>f32</c> and <c>f64</c>, and the forms outside the subset refused by name.
/// </summary>
[TestFixture]
public sealed class FloatViewFrontendTests {

  private static readonly string _csharp = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "float", "FloatView.cs");
  private static readonly string _script = Path.Combine(PrototypeFixture.RepositoryRoot, "tests", "fixtures", "prototype", "js", "FloatView.js");

  private static IrModule CSharpModule() => PrototypeFixture.Import("FloatView.cs", File.ReadAllText(_csharp));

  private static IrModule ScriptModule() {
    var result = JavaScriptFrontend.Import("FloatView.js", File.ReadAllText(_script));
    Assert.That(result.Diagnostics, Is.Empty);
    return result.Module!;
  }

  private static string Compiled(PrototypeCall call) => PrototypeFixture.CompiledOutcome("float/FloatView", call);

  /// <summary>The operations of the IR below a statement, with the type of each node.</summary>
  private static IEnumerable<string> Operations(IrStmt statement) => statement switch {
    IrBlock block => block.Statements.SelectMany(Operations),
    IrDeclare { Initializer: { } value } => Operations(value),
    IrAssign assign => Operations(assign.Value),
    IrReturn { Value: { } value } => Operations(value),
    IrExprStmt expression => Operations(expression.Expression),
    IrIf conditional => Operations(conditional.Then).Concat(conditional.Else is null ? [] : Operations(conditional.Else)).Concat(Operations(conditional.Condition)),
    IrWhile loop => Operations(loop.Body).Concat(Operations(loop.Condition)),
    IrFor loop => Operations(loop.Body).Concat(Operations(loop.Step)).Concat(Operations(loop.Condition)),
    _ => []
  };

  private static IEnumerable<string> Operations(IrExpr expression) => expression switch {
    IrIntrinsic intrinsic => intrinsic.Arguments.SelectMany(Operations).Prepend($"{intrinsic.Op} {(intrinsic.Type == IrType.Void ? intrinsic.Arguments[2].Type : intrinsic.Type)}"),
    IrBinary binary => Operations(binary.Left).Concat(Operations(binary.Right)),
    IrConvert conversion => Operations(conversion.Operand).Prepend($"Convert {conversion.Operand.Type} {conversion.Type}"),
    _ => []
  };

  /// <summary>Test design 3: each method of the fixtures is the operation and format its member names.</summary>
  [TestCase("GetF32LE", "LoadLittle f32")]
  [TestCase("GetF32BE", "LoadBig f32")]
  [TestCase("GetF64LE", "LoadLittle f64")]
  [TestCase("GetF64BE", "LoadBig f64")]
  [TestCase("ToSingle", "LoadLittle f32")]
  [TestCase("ToDouble", "LoadLittle f64")]
  [TestCase("PutF32LE", "StoreLittle f32")]
  [TestCase("PutF32BE", "StoreBig f32")]
  [TestCase("PutF64LE", "StoreLittle f64")]
  [TestCase("PutF64BE", "StoreBig f64")]
  public void Method_IsTheOperationOfItsMember(string method, string operation) {
    Assert.That(Operations(CSharpModule().Functions.Single(function => function.Name == method).Body), Has.Member(operation));
    if (!method.StartsWith("To", StringComparison.Ordinal))
      Assert.That(Operations(ScriptModule().Functions.Single(function => function.Name == FloatViewCases.Script(method)).Body), Has.Member(operation));
  }

  /// <summary>
  /// <c>setFloat32</c> rounds the number to binary32 first (ECMA-262 NumericToRawBytes), and the result
  /// of <c>getFloat32</c> is a binary32 number, which a double holds exactly.
  /// </summary>
  [Test]
  public void Float32View_RoundsWhatItStores_AndReadsABinary32Number() {
    var module = ScriptModule();
    Assert.That(Operations(module.Functions.Single(function => function.Name == "putF32LE").Body), Has.Member("Convert f64 f32"));
    Assert.That(Operations(module.Functions.Single(function => function.Name == "putF64LE").Body), Has.No.Member("Convert f64 f32"));
    Assert.That(module.Functions.Single(function => function.Name == "getF32BE").ReturnType, Is.EqualTo(IrType.F32));
    Assert.That(module.Functions.Single(function => function.Name == "getF64BE").ReturnType, Is.EqualTo(IrType.F64));
  }

  /// <summary>
  /// Test design 1, 2, 4, 5 and 6 for the frontends, against the first oracle: every method of both
  /// fixtures, imported and interpreted, has the outcome of the C# fixture as .NET runs it - on every
  /// class of value at every boundary of the index, the rounding boundaries of binary32, random bit
  /// patterns (seed 2026) and the WAV files.
  /// </summary>
  [Test]
  public void Fixtures_ComputeWhatDotNetComputes() {
    var csharp = CSharpModule();
    var script = ScriptModule();
    var random = new Random(IntegerCases.Seed);
    var mismatches = new List<string>();
    var calls = 0;
    foreach (var method in FloatViewCases.FixtureMethods)
      foreach (var call in FloatViewCases.FixtureCalls(method, random)) {
        ++calls;
        var expected = Compiled(call);
        if (PrototypeFixture.InterpretedOutcome(csharp, call) is var fromCSharp && !FloatViewCases.FixtureMatches(method, call, expected, fromCSharp))
          mismatches.Add($"C# {call}: {fromCSharp}, .NET {expected}");

        if (method.StartsWith("To", StringComparison.Ordinal))
          continue;

        var named = call with { Method = FloatViewCases.Script(method) };
        if (PrototypeFixture.InterpretedOutcome(script, named) is var fromScript && !FloatViewCases.FixtureMatches(method, call, expected, fromScript))
          mismatches.Add($"JavaScript {named}: {fromScript}, .NET {expected}");
      }

    TestContext.Out.WriteLine($"{calls} calls of {FloatViewCases.FixtureMethods.Length} methods, {mismatches.Count} mismatches");
    Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} outcomes differ");
  }

  /// <summary>Test design 1: the WAV reader of both fixtures sums the eight samples to the double .NET sums them to.</summary>
  [Test]
  public void WavReader_SumsTheSamples() {
    var call = new PrototypeCall("SumSamples", [FloatViewCases.Wav(FloatViewCases.Samples)]);
    var sum = FloatViewCases.Sum(FloatViewCases.Samples);
    var expected = PrototypeFixture.Outcome(sum, call.Arguments);
    Assert.That(Compiled(call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(CSharpModule(), call), Is.EqualTo(expected));
    Assert.That(PrototypeFixture.InterpretedOutcome(ScriptModule(), call with { Method = "sumSamples" }), Is.EqualTo(expected));
  }

  /// <summary>The values of the scenarios, in the interpreter.</summary>
  [Test]
  public void ScenarioValues_AreComputed() {
    var script = ScriptModule();
    string Run(string function, params object[] arguments) => PrototypeFixture.InterpretedOutcome(script, new PrototypeCall(function, arguments));
    Assert.That(Run("getF32LE", new byte[] { 0x00, 0x00, 0x80, 0x3F }, 0), Is.EqualTo("1 | [0,0,128,63]"));
    Assert.That(Run("getF64BE", new byte[] { 0x40, 0x09, 0x21, 0xFB, 0x54, 0x44, 0x2D, 0x18 }, 0), Does.StartWith("f:400921fb54442d18 |"));
    Assert.That(BitConverter.Int64BitsToDouble(0x400921fb54442d18), Is.EqualTo(3.141592653589793));
    Assert.That(Run("putF32LE", new byte[4], 0, 0.1), Is.EqualTo("[205,204,204,61] | [205,204,204,61]"));
    Assert.That(Run("putF32LE", new byte[4], 0, 1e40), Is.EqualTo("[0,0,128,127] | [0,0,128,127]"));
    Assert.That(Run("putF32LE", new byte[4], 0, 3.4028235677973366e38), Is.EqualTo("[0,0,128,127] | [0,0,128,127]"));
    Assert.That(Run("putF32LE", new byte[4], 0, 1.0509738482436128e-45), Is.EqualTo("[1,0,0,0] | [1,0,0,0]"));
    Assert.That(Run("putF32LE", new byte[4], 0, 7.006492321624085e-46), Is.EqualTo("[0,0,0,0] | [0,0,0,0]"));
    Assert.That(Run("putF64BE", new byte[8], 0, -0.0), Is.EqualTo("[128,0,0,0,0,0,0,0] | [128,0,0,0,0,0,0,0]"));
    Assert.That(Run("getF32LE", new byte[FloatViewCases.Length], 9), Is.EqualTo("trap: index out of range"));
  }

  /// <summary>Test design 8: the binary32 number read is added to a double exactly - once rounded, as a double sum.</summary>
  [Test]
  public void Float32Read_AddedToADouble_IsExact() {
    var result = JavaScriptFrontend.Import("snippet.mjs", "/** @param {Uint8Array} b */ export function f(b) { const v = new DataView(b.buffer); return v.getFloat32(0, true) + 0.1; }");
    Assert.That(result.Diagnostics, Is.Empty);
    var bytes = new byte[] { 0xCD, 0xCC, 0xCC, 0x3D };
    var outcome = PrototypeFixture.InterpretedOutcome(result.Module!, new PrototypeCall("f", [bytes]));
    Assert.That(outcome, Is.EqualTo(PrototypeFixture.Outcome((double)0.1f + 0.1, [bytes])));
    Assert.That((double)0.1f + 0.1, Is.Not.EqualTo((double)(0.1f + 0.1f)), "a sum in binary32 would differ");
  }

  /// <summary>Test design 9 for C#: a span for BitConverter, a stored value that can trap, a slice of another size and Half are refused by name.</summary>
  [TestCase("public static float F(byte[] a) => System.BitConverter.ToSingle(a.AsSpan());", "BitConverter.ToSingle")]
  [TestCase("public static double F(byte[] a) => System.BitConverter.ToDouble(a.AsSpan(1));", "BitConverter.ToDouble")]
  [TestCase("public static byte[] F(byte[] a, float x, int d) { System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(a, x / (float)(4 / d)); return a; }", "a value that can trap or has an effect")]
  [TestCase("public static double F(byte[] a) => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(a.AsSpan(0, 4));", "as anything but an array or its AsSpan with an index and the size of the value")]
  [TestCase("public static float F(byte[] a) => (float)System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(a);", "Half")]
  public void UnsupportedForm_IsRefusedByName(string method, string named) {
    var result = CSharpFrontend.Import("Snippet.cs", "using System; " + PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.Member(CSharpFrontend.Unsupported));
    Assert.That(string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)), Does.Contain(named));
  }

}
