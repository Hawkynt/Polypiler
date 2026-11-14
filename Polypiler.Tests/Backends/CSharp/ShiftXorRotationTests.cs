using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// RyuJIT's rotation of a shift-xor (#1310, docs/reference/known-runtime-defects.md): .NET compiles
/// <c>(x &lt;&lt; n) ^ (x &gt;&gt; (32 - n))</c> to a rotation, also at <c>n = 0</c>, where C# computes
/// <c>x ^ x = 0</c>. The C# backend writes every shift-xor the JIT would take for a rotation in a form
/// it does not, and leaves every other expression as it was.
/// </summary>
[TestFixture]
public sealed class ShiftXorRotationTests {

  /// <summary>
  /// The shapes of the differential test: per width the exclusive or of the issue in every form the JIT
  /// recognizes - either direction, either order, the counts masked, the complement written as a sum,
  /// held in a local or beside a copy of the count, a signed value shifted logically - and the
  /// operators, widths and constant counts the JIT computes correctly.
  /// </summary>
  private const string Shapes = """
    public static uint Xor32(uint x, int n) => (x << n) ^ (x >> (32 - n));
    public static uint XorRight32(uint x, int n) => (x >> n) ^ (x << (32 - n));
    public static uint XorSwapped32(uint x, int n) => (x >> (32 - n)) ^ (x << n);
    public static uint XorMasked32(uint x, int n) => (x << (n & 31)) ^ (x >> ((32 - n) & 31));
    public static uint XorSum32(uint x, int n) => (x << n) ^ (x >> (-n + 32));
    public static uint XorLocal32(uint x, int n) { int r = 32 - n; return (x << n) ^ (x >> r); }
    public static uint XorCopy32(uint x, int n) { int k = n; return (x << k) ^ (x >> (32 - n)); }
    public static int XorSigned32(int x, int n) => (x << n) ^ (x >>> (32 - n));
    public static int XorCast32(int x, int n) => (x << n) ^ (int)((uint)x >> (32 - n));
    public static uint Add32(uint x, int n) => (x << n) + (x >> (32 - n));
    public static uint Or32(uint x, int n) => (x << n) | (x >> (32 - n));
    public static uint XorConstant32(uint x) => (x << 8) ^ (x >> 24);
    public static uint XorZero32(uint x) => (x << 0) ^ (x >> 32);
    public static ulong Xor64(ulong x, int n) => (x << n) ^ (x >> (64 - n));
    public static ulong XorRight64(ulong x, int n) => (x >> n) ^ (x << (64 - n));
    public static ulong XorSwapped64(ulong x, int n) => (x >> (64 - n)) ^ (x << n);
    public static ulong XorMasked64(ulong x, int n) => (x << (n & 63)) ^ (x >> ((64 - n) & 63));
    public static ulong XorSum64(ulong x, int n) => (x << n) ^ (x >> (-n + 64));
    public static ulong XorLocal64(ulong x, int n) { int r = 64 - n; return (x << n) ^ (x >> r); }
    public static ulong XorCopy64(ulong x, int n) { int k = n; return (x << k) ^ (x >> (64 - n)); }
    public static long XorSigned64(long x, int n) => (x << n) ^ (x >>> (64 - n));
    public static long XorCast64(long x, int n) => (x << n) ^ (long)((ulong)x >> (64 - n));
    public static ulong Add64(ulong x, int n) => (x << n) + (x >> (64 - n));
    public static ulong Or64(ulong x, int n) => (x << n) | (x >> (64 - n));
    public static ulong XorConstant64(ulong x) => (x << 8) ^ (x >> 56);
    public static ulong XorZero64(ulong x) => (x << 0) ^ (x >> 64);
    public static ushort Xor16(ushort x, int n) => (ushort)((x << n) ^ (x >> (16 - n)));
    public static byte Xor8(byte x, int n) => (byte)((x << n) ^ (x >> (8 - n)));
    """;

  /// <summary>The direct forms, which the JIT rotates in every tier; the others it rotates once it optimizes.</summary>
  private static readonly string[] _everyTier = ["Xor32", "XorRight32", "XorSwapped32", "XorSum32", "XorSigned32", "XorCast32", "Xor64", "XorRight64", "XorSwapped64", "XorSum64", "XorSigned64", "XorCast64"];

  private static readonly bool[] _overflowChecking = [false, true];

  private static IrModule Import(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  private static string Emit(IrModule module, CSharpProfile? profile = null, CSharpCapabilities? table = null)
    => CSharpEmitter.Emit(module, profile ?? CSharpProfile.Default, null, table).Single().Content;

  /// <summary>The statement of the function <c>F</c> of a method, the generated C# for a profile.</summary>
  private static string Body(string method, CSharpProfile? profile = null, CSharpCapabilities? table = null)
    => string.Join(" ", EmittedCSharp.Body(Emit(Import(method), profile, table)));

  /// <summary>The table without the row of the defect: what the backend wrote before #1310.</summary>
  private static CSharpCapabilities Unguarded { get; } = new(CSharpCapabilities.Default.Rows.Where(row => row.Defect is null));

  /// <summary>The values a parameter is tried with: the counts 0 to the width plus one around the edges of <c>int</c>, a few patterns of the value.</summary>
  private static object[] Operands(Type type, int width) => type == typeof(int) && width > 0
    ? [.. Enumerable.Range(-1, width + 3).Cast<object>(), 2 * width, int.MinValue, int.MaxValue]
    : type == typeof(uint) ? [0x12345678u, uint.MaxValue, 0x80000001u]
    : type == typeof(ulong) ? [0x123456789ABCDEF1ul, ulong.MaxValue, 0x8000000000000001ul]
    : type == typeof(int) ? [0x12345678, -1, int.MinValue + 1]
    : type == typeof(long) ? [0x123456789ABCDEF1L, -1L, long.MinValue + 1]
    : type == typeof(ushort) ? [(ushort)0x1234, ushort.MaxValue]
    : [(byte)0x5A, byte.MaxValue];

  /// <summary>Every call of the shapes: each value with each count.</summary>
  private static List<PrototypeCall> Calls(IrModule module) {
    var calls = new List<PrototypeCall>();
    foreach (var function in module.Functions) {
      var width = int.Parse(new string(function.Name.Where(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);
      var values = Operands(Clr(function.Parameters[0].Type), 0);
      var arguments = function.Parameters.Count == 1
        ? values.Select(value => new[] { value })
        : values.SelectMany(value => Operands(typeof(int), width).Select(count => new[] { value, count }));
      calls.AddRange(arguments.Select(tuple => new PrototypeCall(function.Name, tuple)));
    }

    return calls;
  }

  private static Type Clr(IrType type) => type.Kind switch {
    IrTypeKind.U8 => typeof(byte),
    IrTypeKind.U16 => typeof(ushort),
    IrTypeKind.I32 => typeof(int),
    IrTypeKind.U32 => typeof(uint),
    IrTypeKind.I64 => typeof(long),
    _ => typeof(ulong)
  };

  /// <summary>The calls whose compiled outcome differs from the interpreter's, as a failure lists them.</summary>
  private static List<string> Mismatches(IrModule module, string text, bool fullyOptimized) {
    var mismatches = new List<string>();
    foreach (var checking in _overflowChecking) {
      var build = GeneratedCSharp.Compile(text, checking, fullyOptimized);
      foreach (var call in Calls(module))
        if (build.Outcome(call) is var actual && PrototypeFixture.InterpretedOutcome(module, call) is var expected && actual != expected)
          mismatches.Add($"CheckOverflow {checking}: {call}: {actual}, not {expected}");
    }

    return mismatches;
  }

  // ---------------------------------------------------------------- the differential test

  /// <summary>
  /// Test design 1 to 3: every shape, compiled in Release and run in the first tier and fully
  /// optimized, computes what the interpreter computes for the counts 0 to the width plus one and
  /// around the edges of <c>int</c>; the shape of the issue gives 0 at count 0.
  /// </summary>
  [TestCase(false), TestCase(true), Category("Slow")]
  public void EveryShape_ComputesWhatTheInterpreterComputes(bool fullyOptimized) {
    var module = Import(Shapes);
    var text = Emit(module);
    Assert.That(Mismatches(module, text, fullyOptimized), Is.Empty);
    Assert.That(GeneratedCSharp.Compile(text, false, fullyOptimized).Outcome(new PrototypeCall("Xor32", [0x12345678u, 0])), Is.EqualTo("0"));
  }

  /// <summary>
  /// The defect itself: without the row the same shapes, fully optimized, differ from the interpreter -
  /// every direct form at count 0 - and only at a count that is 0 modulo the width. When a runtime no
  /// longer does this, the row and docs/reference/known-runtime-defects.md are due for a revision.
  /// </summary>
  [Test, Category("Slow")]
  public void WithoutTheRow_TheJitRotatesAtCountZero() {
    var module = Import(Shapes);
    var mismatches = Mismatches(module, Emit(module, table: Unguarded), true);
    foreach (var shape in _everyTier)
      Assert.That(mismatches, Has.Some.Contains($": {shape}(").And.Contains(", 0)"), shape);

    Assert.That(mismatches, Has.All.Contains(": Xor"));
    foreach (var line in mismatches) {
      var call = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..line.IndexOf(')', StringComparison.Ordinal)].Split('(', ',');
      var width = int.Parse(new string(call[0].Where(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);
      Assert.That(int.Parse(call[2], CultureInfo.InvariantCulture) % width, Is.Zero, line);
    }
  }

  // ---------------------------------------------------------------- the forms written

  /// <summary>Test design 1 and 3: a count <c>w - y</c> of a shift-xor is written <c>-y</c>, which C# reduces modulo the width to the same count.</summary>
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));", "return (x << n) ^ (x >> -n);")]
  [TestCase("public static uint F(uint x, int n) => (x >> n) ^ (x << (32 - n));", "return (x >> n) ^ (x << -n);")]
  [TestCase("public static uint F(uint x, int n) => (x >> (32 - n)) ^ (x << n);", "return (x >> -n) ^ (x << n);")]
  [TestCase("public static uint F(uint x, int n) => (x << (n & 31)) ^ (x >> ((32 - n) & 31));", "return (x << (n & 31)) ^ (x >> (-n & 31));")]
  [TestCase("public static uint F(uint x, int n) => (x << (n & 31)) ^ (x >> (31 & (32 - n)));", "return (x << (n & 31)) ^ (x >> (31 & -n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> ((32 - n) & 31 & 255));", "return (x << n) ^ (x >> ((-n & 31) & 255));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (-n + 32));", "return (x << n) ^ (x >> -n);")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (32 + -n));", "return (x << n) ^ (x >> -n);")]
  [TestCase("public static uint F(uint x, int n, int k) => (x << k) ^ (x >> (32 - n));", "return (x << k) ^ (x >> -n);")]
  [TestCase("public static uint F(uint x, uint y, int n) => (x << n) ^ (y >> (32 - n));", "return (x << n) ^ (y >> -n);")]
  [TestCase("public static uint F(uint x, int n) => (x << (32 - n)) ^ (x >> (32 - n));", "return (x << -n) ^ (x >> -n);")]
  [TestCase("public static ulong F(ulong x, int n) => (x << n) ^ (x >> (64 - n));", "return (x << n) ^ (x >> -n);")]
  [TestCase("public static int F(int x, int n) => (x << n) ^ (x >>> (32 - n));", "return (x << n) ^ (x >>> -n);")]
  [TestCase("public static int F(int x, int n) => (x << n) ^ (int)((uint)x >> (32 - n));", "return (x << n) ^ (int)((uint)x >> -n);")]
  [TestCase("public static long F(long x, int n) => (x << n) ^ (long)((ulong)x >> (64 - n));", "return (x << n) ^ (long)((ulong)x >> -n);")]
  public void ComplementCount_IsWrittenNegated(string method, string expected) => Assert.That(Body(method), Is.EqualTo(expected));

  /// <summary>A count held in a local the function sets to <c>w - y</c> is written <c>r ^ w</c>, which keeps its low bits.</summary>
  [TestCase("public static uint F(uint x, int n) { int r = 32 - n; return (x << n) ^ (x >> r); }", "var r = 32 - n; return (x << n) ^ (x >> (r ^ 32));")]
  [TestCase("public static uint F(uint x, int n) { int r = 32 - n; return (x >> r) ^ (x << n); }", "var r = 32 - n; return (x >> (r ^ 32)) ^ (x << n);")]
  [TestCase("public static ulong F(ulong x, int n) { int r; r = (64 - n) & 63; return (x << n) ^ (x >> r); }", "int r; r = (64 - n) & 63; return (x << n) ^ (x >> (r ^ 64));")]
  [TestCase("public static uint F(uint x, int n) { int r = 0; r = 32 - n; return (x << n) ^ (x >> (r & 31)); }", "int r = 0; r = 32 - n; return (x << n) ^ (x >> ((r ^ 32) & 31));")]
  public void ComplementLocal_IsWrittenWithTheWidthFlipped(string method, string expected) => Assert.That(Body(method), Is.EqualTo(expected));

  /// <summary>
  /// Test design 4 and the second scenario of the issue: what the JIT does not take for a rotation,
  /// or computes correctly, is written as before.
  /// </summary>
  [TestCase("public static uint F(uint x) => (x << 8) | (x >> 24);", "return (x << 8) | (x >> 24);")]
  [TestCase("public static uint F(uint x, int n) => (x << n) | (x >> (32 - n));", "return (x << n) | (x >> (32 - n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) + (x >> (32 - n));", "return (x << n) + (x >> (32 - n));")]
  [TestCase("public static uint F(uint x) => (x << 8) ^ (x >> 24);", "return (x << 8) ^ (x >> 24);")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x << (32 - n));", "return (x << n) ^ (x << (32 - n));")]
  [TestCase("public static int F(int x, int n) => (x << n) ^ (x >> (32 - n));", "return (x << n) ^ (x >> (32 - n));")]
  [TestCase("public static uint F(uint x, int n) => x ^ (x >> (32 - n));", "return x ^ (x >> (32 - n));")]
  [TestCase("public static uint F(uint x, int n) => (x >> (32 - n)) ^ x;", "return (x >> (32 - n)) ^ x;")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (31 - n));", "return (x << n) ^ (x >> (31 - n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (n - 32));", "return (x << n) ^ (x >> (n - 32));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (32 + n));", "return (x << n) ^ (x >> (32 + n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (-n + 31));", "return (x << n) ^ (x >> (-n + 31));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> (~n + 32));", "return (x << n) ^ (x >> (~n + 32));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> ((32 - n) & 15));", "return (x << n) ^ (x >> ((32 - n) & 15));")]
  [TestCase("public static uint F(uint x, int n, int m) => (x << n) ^ (x >> ((32 - n) & m));", "return (x << n) ^ (x >> ((32 - n) & m));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> ((32 - n) | 31));", "return (x << n) ^ (x >> ((32 - n) | 31));")]
  [TestCase("public static ulong F(ulong x, int n) => (x << n) ^ (x >> (32 - n));", "return (x << n) ^ (x >> (32 - n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (x >> checked(32 - n));", "return (x << n) ^ (x >> checked(32 - n));")]
  [TestCase("public static uint F(uint x, int n) => (x << n) ^ (uint)((ulong)x >> (32 - n));", "return (x << n) ^ (uint)((ulong)x >> (32 - n));")]
  [TestCase("public static ushort F(ushort x, int n) => (ushort)((x << n) ^ (x >> (16 - n)));", "return (ushort)((x << n) ^ (x >> (16 - n)));")]
  [TestCase("public static uint F(uint x, int n) { int r = n + 1; return (x << n) ^ (x >> r); }", "var r = n + 1; return (x << n) ^ (x >> r);")]
  [TestCase("public static uint F(uint x, int n) { int r = 32 - n; return (x << n) | (x >> r); }", "var r = 32 - n; return (x << n) | (x >> r);")]
  [TestCase("public static ulong F(ulong x, int n) { int r = 32 - n; return (x << n) ^ (x >> r); }", "var r = 32 - n; return (x << n) ^ (x >> r);")]
  public void OtherExpression_IsWrittenAsBefore(string method, string expected) => Assert.That(Body(method), Is.EqualTo(expected));

  /// <summary>Test design 4: a count <c>w - c</c> with a constant <c>c</c> is a constant the C# compiler reduces modulo the width, which the JIT rotates correctly.</summary>
  [TestCase(IrBinaryOp.Subtract, false, "32 - 5")]
  [TestCase(IrBinaryOp.Add, false, "32 + -5")]
  [TestCase(IrBinaryOp.Add, true, "-5 + 32")]
  public void ConstantComplement_IsWrittenAsBefore(IrBinaryOp op, bool negationFirst, string count) {
    // The frontend folds these; the IR of another frontend or of a pass need not.
    var x = new IrLocal("x", IrType.U32);
    var value = new IrLocalRef(x);
    var width = new IrConst(IrType.I32, 32);
    IrExpr five = op == IrBinaryOp.Subtract ? new IrConst(IrType.I32, 5) : new IrUnary(IrUnaryOp.Negate, new IrConst(IrType.I32, 5));
    var expression = new IrBinary(IrBinaryOp.Xor,
      new IrBinary(IrBinaryOp.ShiftLeft, value, new IrConst(IrType.I32, 5)),
      new IrBinary(IrBinaryOp.ShiftRight, value, negationFirst ? new IrBinary(op, five, width) : new IrBinary(op, width, five)));
    var module = new IrModule("Snippet", [new IrFunction("F", true, IrType.U32, [x], [], new IrBlock([new IrReturn(expression)]))], []);
    Assert.That(string.Join(" ", EmittedCSharp.Body(Emit(module))), Is.EqualTo($"return (x << 5) ^ (x >> ({count}));"));
  }

  /// <summary>Test design 4: a conversion that traps is a check the JIT does not look through.</summary>
  [Test]
  public void TrappingConversion_IsWrittenAsBefore() {
    var x = new IrLocal("x", IrType.I32);
    var n = new IrLocal("n", IrType.I32);
    var shifted = new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(IrType.U32, new IrLocalRef(x), IrConversion.Wrap),
      new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.I32, 32), new IrLocalRef(n)));
    var expression = new IrBinary(IrBinaryOp.Xor, new IrBinary(IrBinaryOp.ShiftLeft, new IrLocalRef(x), new IrLocalRef(n)), new IrConvert(IrType.I32, shifted, IrConversion.Trap));
    var module = new IrModule("Snippet", [new IrFunction("F", true, IrType.I32, [x, n], [], new IrBlock([new IrReturn(expression)]))], []);
    Assert.That(string.Join(" ", EmittedCSharp.Body(Emit(module))), Does.Contain("32 - n"));
  }

  /// <summary>The form is written in every profile, in the operators its language version has.</summary>
  [Test]
  public void EveryProfile_WritesTheForm() {
    foreach (var profile in CSharpProfile.All) {
      Assert.That(CSharpCapabilities.Default.Suffers(CSharpRuntimeDefect.ShiftXorRotation, profile), Is.True, profile.Name);
      Assert.That(Body("public static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));", profile), Does.Contain("(x >> -n)"), profile.Name);
    }

    Assert.That(Body("public static int F(int x, int n) => (x << n) ^ (x >>> (32 - n));", CSharpProfile.Net48), Is.EqualTo("return (x << n) ^ (int)((uint)x >> -n);"));
  }

  /// <summary>Test design 4: a table without the row writes the expression as it stands.</summary>
  [Test]
  public void TableWithoutTheRow_WritesTheExpressionAsItStands() {
    Assert.That(Unguarded.Suffers(CSharpRuntimeDefect.ShiftXorRotation, CSharpProfile.Net100), Is.False);
    Assert.That(Body("public static uint F(uint x, int n) => (x << n) ^ (x >> (32 - n));", table: Unguarded), Is.EqualTo("return (x << n) ^ (x >> (32 - n));"));
    Assert.That(Body("public static uint F(uint x, int n) { int r = 32 - n; return (x << n) ^ (x >> r); }", table: Unguarded), Is.EqualTo("var r = 32 - n; return (x << n) ^ (x >> r);"));
  }

  /// <summary>The row names the defect and begins at the oldest profile; a row that is not about a defect answers no question about one.</summary>
  [Test]
  public void Row_IsFoundByItsDefect() {
    var row = CSharpCapabilities.Default.Rows.Single(candidate => candidate.Defect == CSharpRuntimeDefect.ShiftXorRotation);
    Assert.That((row.Since, row.Feature, row.Language, row.Operation), Is.EqualTo((CSharpProfile.Net20, (CSharpFeature?)null, (Version?)null, (IrIntrinsicOp?)null)));
    Assert.That(row.WithTotal(new CSharpForm(null), _ => true).Defect, Is.EqualTo(CSharpRuntimeDefect.ShiftXorRotation));
    Assert.That(new CSharpCapabilities([CSharpCapabilities.Default.Of(CSharpFeature.Discard)!]).Suffers(CSharpRuntimeDefect.ShiftXorRotation, CSharpProfile.Net100), Is.False);
  }

}
