using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Polypiler.Core.Backends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Backends.CSharp;

/// <summary>
/// The C# the emitter writes for the widened IR: every scalar type, the trapping operators and
/// conversions, the further statements, the constants of a module and the library operations. The
/// spelling is pinned where it is the point - a real <c>for</c>, a <c>checked(...)</c>, a cast to
/// <c>float</c> - and what the text means is decided by the C# compiler, which builds it with the
/// project's overflow checking off and on, and by the interpreter the result is compared with.
/// </summary>
[TestFixture]
public sealed class CSharpEmitterWideTests {

  private static readonly bool[] _overflowChecking = [false, true];

  private static readonly IrType[] _integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];

  private static IrModule Import(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  private static string Emit(IrModule module) => CSharpEmitter.Emit(module).Single().Content;

  /// <summary>The lines of the function <c>F</c>, without indentation and without the <c>unchecked</c> block around them.</summary>
  private static string[] Body(IrModule module) => EmittedCSharp.Body(Emit(module));

  private static string[] Body(string methods) => Body(Import(methods));

  private static List<GeneratedCSharp> Builds(IrModule module) {
    var text = Emit(module);
    return _overflowChecking.Select(checking => GeneratedCSharp.Compile(text, checking)).ToList();
  }

  /// <summary>
  /// Every call in the generated C#, built without and with overflow checking, and in the
  /// interpreter: the outcomes by call, which are the same three times or the test fails here.
  /// </summary>
  private static Dictionary<string, string> Outcomes(IrModule module, IEnumerable<PrototypeCall> calls) {
    var builds = Builds(module);
    var outcomes = new Dictionary<string, string>();
    foreach (var call in calls) {
      var interpreted = PrototypeFixture.InterpretedOutcome(module, call);
      Assert.That(builds.Select(build => build.Outcome(call)), Has.All.EqualTo(interpreted), call.ToString());
      outcomes[call.ToString()] = interpreted;
    }

    return outcomes;
  }

  private static IrFunction Function(string name, IrType returns, IrLocal[] parameters, params IrStmt[] body)
    => new(name, true, returns, parameters, [.. Declared(new IrBlock(body)).Distinct()], new IrBlock(body));

  /// <summary>Every local a statement declares, however deep.</summary>
  private static IEnumerable<IrLocal> Declared(IrStmt statement) => statement switch {
    IrDeclare declare => [declare.Local],
    IrBlock block => block.Statements.SelectMany(Declared),
    IrIf conditional => Declared(conditional.Then).Concat(conditional.Else is null ? [] : Declared(conditional.Else)),
    IrWhile loop => Declared(loop.Body),
    IrDoWhile loop => Declared(loop.Body),
    IrFor loop => Declared(loop.Body).Concat(Declared(loop.Step)),
    IrSwitch choice => choice.Sections.SelectMany(section => Declared(section.Body)),
    _ => []
  };

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static IrConst Real(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  private static IrConst Truth(bool value) => new(IrType.Bool, value ? 1 : 0);

  private static IrLocalRef Ref(IrLocal local) => new(local);

  private static IrBinary Op(IrBinaryOp op, IrExpr left, IrExpr right) => new(op, left, right);

  private static IrBlock Block(params IrStmt[] statements) => new(statements);

  private static IrAssign Add(IrLocal local, long amount) => new(local, Op(IrBinaryOp.Add, Ref(local), Int(amount)));

  private static PrototypeCall Call(string method, params object[] arguments) => new(method, arguments);

  // ---------------------------------------------------------------- the spelling

  /// <summary>Test design 2: the operators on the 64-bit and the floating-point types, each constant with the suffix of its type.</summary>
  [TestCase("long", "a + 1", "a + 1L")]
  [TestCase("long", "a - b", "a - b")]
  [TestCase("long", "a * b", "a * b")]
  [TestCase("long", "a / b", "a / b")]
  [TestCase("long", "a % 3", "a % 3L")]
  [TestCase("long", "a / -1", "a / global::System.Math.Max(-1L, -1L)")]
  [TestCase("long", "a << n", "a << n")]
  [TestCase("long", "a >> n", "a >> n")]
  [TestCase("long", "a >>> n", "a >>> n")]
  [TestCase("long", "-a", "-a")]
  [TestCase("long", "~a", "~a")]
  [TestCase("long", "a ^ long.MinValue", "a ^ -9223372036854775808L")]
  [TestCase("ulong", "a + 1", "a + 1UL")]
  [TestCase("ulong", "a / 3", "a / 3UL")]
  [TestCase("ulong", "a % ulong.MaxValue", "a % 18446744073709551615UL")]
  [TestCase("ulong", "a >> n", "a >> n")]
  [TestCase("ulong", "a >>> n", "a >> n")]
  [TestCase("ulong", "a & 0xFFFFFFFF00000000", "a & 18446744069414584320UL")]
  [TestCase("double", "a + b", "a + b")]
  [TestCase("double", "a * b + a", "(a * b) + a")]
  [TestCase("double", "a / b", "a / b")]
  [TestCase("double", "a % b", "a % b")]
  [TestCase("double", "-a", "-a")]
  [TestCase("double", "a + 1", "a + 1.0")]
  [TestCase("double", "a + 0.1", "a + 0.1")]
  [TestCase("double", "a - -0.0", "a - -0.0")]
  [TestCase("double", "a * 1e300", "a * 1E+300")]
  [TestCase("double", "a / 0", "a / 0.0")]
  [TestCase("double", "a + double.NaN", "a + double.NaN")]
  [TestCase("double", "a + double.PositiveInfinity", "a + double.PositiveInfinity")]
  [TestCase("double", "a + double.NegativeInfinity", "a + double.NegativeInfinity")]
  [TestCase("double", "a + double.Epsilon", "a + 5E-324")]
  public void WideOperator_IsWritten(string type, string expression, string expected)
    => Assert.That(Body($"public static {type} F({type} a, {type} b, int n) => {expression};"), Is.EqualTo(new[] { $"return {expected};" }));

  /// <summary>Scenario: Float arithmetic is not widened - every binary32 result is cast to float where it arises.</summary>
  [TestCase("a + b", "(float)(a + b)")]
  [TestCase("a * b + a", "(float)((float)(a * b) + a)")]
  [TestCase("a / (b - a)", "(float)(a / (float)(b - a))")]
  [TestCase("a % 1.5f", "(float)(a % 1.5f)")]
  [TestCase("-a", "-a")]
  [TestCase("a + float.NaN", "(float)(a + float.NaN)")]
  [TestCase("a - float.NegativeInfinity", "(float)(a - float.NegativeInfinity)")]
  [TestCase("a * -0f", "(float)(a * -0f)")]
  [TestCase("a * 16777216", "(float)(a * 16777216f)")]
  [TestCase("a + 1e-45f", "(float)(a + 1E-45f)")]
  public void FloatOperator_IsCastToFloat(string expression, string expected)
    => Assert.That(Body($"public static float F(float a, float b) => {expression};"), Is.EqualTo(new[] { $"return {expected};" }));

  [TestCase("float", "a < b", "a < b")]
  [TestCase("double", "a == b", "a == b")]
  [TestCase("double", "!(a >= b)", "!(a >= b)")]
  [TestCase("ulong", "a != b", "a != b")]
  [TestCase("bool", "a == b", "a == b")]
  [TestCase("bool", "a != b", "a != b")]
  public void WideComparison_IsWritten(string type, string expression, string expected)
    => Assert.That(Body($"public static bool F({type} a, {type} b) => {expression};"), Is.EqualTo(new[] { $"return {expected};" }));

  /// <summary>
  /// Scenario: A checked addition stays checked - as an expression of its own, so that neither the
  /// project nor the <c>unchecked</c> block around the body decides; what wraps inside it says so.
  /// </summary>
  [TestCase("int", "checked(a + b)", "checked(a + b)", false)]
  [TestCase("int", "checked(a - b)", "checked(a - b)", false)]
  [TestCase("int", "checked(a * b)", "checked(a * b)", false)]
  [TestCase("int", "checked(-a)", "checked(-a)", false)]
  [TestCase("int", "checked(a * b - a)", "checked((a * b) - a)", false)]
  [TestCase("int", "checked(a + unchecked(a * b))", "checked(a + unchecked(a * b))", false)]
  [TestCase("int", "checked(a + unchecked(a * (b - checked(a + b))))", "checked(a + unchecked(a * (b - checked(a + b))))", false)]
  [TestCase("int", "checked(a + b) * b", "checked(a + b) * b", true)]
  [TestCase("int", "checked(a / b)", "a / b", false)]
  [TestCase("int", "checked((a >>> b) + b)", "checked((a >>> b) + b)", false)]
  [TestCase("uint", "checked(a + 1)", "checked(a + 1u)", false)]
  [TestCase("long", "checked(a * b)", "checked(a * b)", false)]
  [TestCase("ulong", "checked(a - b)", "checked(a - b)", false)]
  [TestCase("double", "checked(a + b)", "a + b", false)]
  public void CheckedOperator_IsACheckedExpression(string type, string expression, string expected, bool block) {
    var module = Import($"public static {type} F({type} a, {type} b) => {expression};");
    Assert.That(Body(module), Is.EqualTo(new[] { $"return {expected};" }));
    Assert.That(EmittedCSharp.IsUnchecked(Emit(module)), Is.EqualTo(block));
  }

  /// <summary>
  /// Test design 3: a cast stands for every conversion except the one C# applies by itself, a narrow
  /// integer becoming an <c>int</c> where it is computed with or stored; a conversion that traps is
  /// a checked cast, one that cannot lose anything a plain one whatever its contract.
  /// </summary>
  [TestCase("long", "int", "a", "(long)a")]
  [TestCase("ulong", "int", "(ulong)a", "(ulong)a")]
  [TestCase("int", "long", "(int)a", "(int)a")]
  [TestCase("uint", "ulong", "(uint)(a + 1)", "(uint)(a + 1UL)")]
  [TestCase("sbyte", "int", "(sbyte)a", "(sbyte)a")]
  [TestCase("short", "long", "(short)a", "(short)a")]
  [TestCase("char", "int", "(char)a", "(ushort)a")]
  [TestCase("double", "int", "a", "(double)a")]
  [TestCase("double", "ulong", "a", "(double)a")]
  [TestCase("float", "long", "a", "(float)a")]
  [TestCase("float", "double", "(float)a", "(float)a")]
  [TestCase("double", "float", "a", "(double)a")]
  [TestCase("double", "float", "a * a", "(double)(float)(a * a)")]
  [TestCase("int", "double", "(int)a", "(int)a")]
  [TestCase("ulong", "float", "(ulong)a", "(ulong)a")]
  [TestCase("byte", "double", "(byte)a", "(byte)(int)a")]
  [TestCase("int", "double", "checked((int)a)", "checked((int)a)")]
  [TestCase("ushort", "float", "checked((ushort)a)", "checked((ushort)a)")]
  [TestCase("byte", "int", "checked((byte)a)", "checked((byte)a)")]
  [TestCase("uint", "long", "checked((uint)(a + 1))", "checked((uint)(a + 1L))")]
  [TestCase("int", "long", "checked((int)unchecked(a * 2))", "checked((int)unchecked(a * 2L))")]
  [TestCase("long", "uint", "checked((long)a)", "(long)a")]
  [TestCase("int", "sbyte", "a", "a")]
  [TestCase("int", "short", "a + a", "a + a")]
  [TestCase("int", "ushort", "-a", "-a")]
  [TestCase("int", "sbyte", "~a", "~a")]
  [TestCase("int", "char", "a << 3", "a << 3")]
  [TestCase("bool", "sbyte", "a < 0", "a < 0")]
  [TestCase("long", "sbyte", "a", "(long)a")]
  [TestCase("int", "sbyte", "a < 0 ? a : 1", "(a < 0) ? (int)a : 1")]
  [TestCase("int", "sbyte", "System.Math.Abs((int)a)", "global::System.Math.Abs((int)a)")]
  [TestCase("int", "short", "new int[a].Length", "(new int[a]).Length")]
  [TestCase("int", "byte", "new[] { 4, 5 }[a]", "(new int[] { 4, 5 })[a]")]
  public void WideConversion_IsWritten(string to, string from, string expression, string expected)
    => Assert.That(Body($"public static {to} F({from} a) => {expression};"), Is.EqualTo(new[] { $"return {expected};" }));

  /// <summary>
  /// A constant where C# knows the type is its digits; as an operand it carries the suffix or the
  /// cast that makes it a value of exactly its type, which is what picks the overload of a library method.
  /// </summary>
  [TestCase("sbyte", "-5", "-5")]
  [TestCase("ushort", "65535", "65535")]
  [TestCase("uint", "uint.MaxValue", "4294967295")]
  [TestCase("long", "long.MinValue", "-9223372036854775808")]
  [TestCase("ulong", "ulong.MaxValue", "18446744073709551615")]
  [TestCase("float", "1", "1f")]
  [TestCase("float", "0.1f", "0.1f")]
  [TestCase("float", "float.MaxValue", "3.4028235E+38f")]
  [TestCase("double", "-2", "-2.0")]
  [TestCase("double", "double.MaxValue", "1.7976931348623157E+308")]
  [TestCase("bool", "true", "true")]
  [TestCase("sbyte", "System.Math.Max(a, (sbyte)-5)", "global::System.Math.Max(a, (sbyte)(-5))")]
  [TestCase("byte", "System.Math.Min(a, (byte)7)", "global::System.Math.Min(a, (byte)7)")]
  [TestCase("short", "System.Math.Max(a, (short)-300)", "global::System.Math.Max(a, (short)(-300))")]
  [TestCase("ushort", "System.Math.Min(a, (ushort)300)", "global::System.Math.Min(a, (ushort)300)")]
  [TestCase("uint", "System.Math.Min(a, 7u)", "global::System.Math.Min(a, 7u)")]
  [TestCase("long", "System.Math.Max(a, -7L)", "global::System.Math.Max(a, -7L)")]
  [TestCase("ulong", "System.Math.Max(a, 7UL)", "global::System.Math.Max(a, 7UL)")]
  [TestCase("float", "System.Math.Max(a, -1.5f)", "global::System.Math.Max(a, -1.5f)")]
  [TestCase("double", "System.Math.Min(a, 2.0)", "global::System.Math.Min(a, 2.0)")]
  public void Constant_IsWrittenInItsType(string type, string expression, string expected)
    => Assert.That(Body($"public static {type} F({type} a) => {expression};"), Is.EqualTo(new[] { $"return {expected};" }));

  /// <summary>Test design 5: each library operation is the call of the base library, named in full so that no name of the module can hide it.</summary>
  [TestCase("int", "int", "System.Math.Abs(a)", "Math.Abs(a)")]
  [TestCase("double", "double", "System.Math.Abs(a)", "Math.Abs(a)")]
  [TestCase("float", "float", "System.MathF.Abs(a)", "Math.Abs(a)")]
  [TestCase("int", "long", "System.Math.Sign(a)", "Math.Sign(a)")]
  [TestCase("ulong", "ulong", "System.Math.Min(a, a)", "Math.Min(a, a)")]
  [TestCase("double", "double", "System.Math.Sqrt(a)", "Math.Sqrt(a)")]
  [TestCase("float", "float", "System.MathF.Sqrt(a)", "MathF.Sqrt(a)")]
  [TestCase("double", "double", "System.Math.Floor(a)", "Math.Floor(a)")]
  [TestCase("float", "float", "System.MathF.Floor(a)", "MathF.Floor(a)")]
  [TestCase("double", "double", "System.Math.Ceiling(a)", "Math.Ceiling(a)")]
  [TestCase("float", "float", "System.MathF.Ceiling(a)", "MathF.Ceiling(a)")]
  [TestCase("double", "double", "System.Math.Truncate(a)", "Math.Truncate(a)")]
  [TestCase("float", "float", "System.MathF.Truncate(a)", "MathF.Truncate(a)")]
  [TestCase("double", "double", "System.Math.Round(a)", "Math.Round(a)")]
  [TestCase("float", "float", "System.MathF.Round(a)", "MathF.Round(a)")]
  [TestCase("double", "double", "System.Math.Round(a, System.MidpointRounding.AwayFromZero)", "Math.Round(a, global::System.MidpointRounding.AwayFromZero)")]
  [TestCase("float", "float", "System.MathF.Round(a, System.MidpointRounding.AwayFromZero)", "MathF.Round(a, global::System.MidpointRounding.AwayFromZero)")]
  [TestCase("uint", "uint", "System.Numerics.BitOperations.RotateLeft(a, 3)", "Numerics.BitOperations.RotateLeft(a, 3)")]
  [TestCase("ulong", "ulong", "System.Numerics.BitOperations.RotateRight(a, 3)", "Numerics.BitOperations.RotateRight(a, 3)")]
  [TestCase("int", "uint", "System.Numerics.BitOperations.PopCount(a)", "Numerics.BitOperations.PopCount(a)")]
  [TestCase("int", "ulong", "System.Numerics.BitOperations.LeadingZeroCount(a)", "Numerics.BitOperations.LeadingZeroCount(a)")]
  [TestCase("int", "uint", "System.Numerics.BitOperations.TrailingZeroCount(a)", "Numerics.BitOperations.TrailingZeroCount(a)")]
  [TestCase("int", "ulong", "System.Numerics.BitOperations.Log2(a)", "Numerics.BitOperations.Log2(a)")]
  [TestCase("ulong", "uint", "System.Math.BigMul(a, a)", "Math.BigMul(a, a)")]
  [TestCase("long", "double", "System.BitConverter.DoubleToInt64Bits(a)", "BitConverter.DoubleToInt64Bits(a)")]
  [TestCase("int", "float", "System.BitConverter.SingleToInt32Bits(a)", "BitConverter.SingleToInt32Bits(a)")]
  [TestCase("double", "long", "System.BitConverter.Int64BitsToDouble(a)", "BitConverter.Int64BitsToDouble(a)")]
  [TestCase("float", "int", "System.BitConverter.Int32BitsToSingle(a)", "BitConverter.Int32BitsToSingle(a)")]
  public void Intrinsic_IsTheCallOfTheBaseLibrary(string returns, string type, string expression, string expected)
    => Assert.That(Body($"public static {returns} F({type} a) => {expression};"), Is.EqualTo(new[] { $"return global::System.{expected};" }));

  [Test]
  public void ArrayOperations_AreTheCallsOfArray() {
    var body = Body("public static int[] F(int[] a, byte[] b) { System.Array.Copy(a, 1, a, 0, 2); System.Array.Fill(b, (byte)7); System.Array.Clear(a); return new[] { a[0], 2 }; }");
    Assert.That(body, Is.EqualTo(new[] {
      "global::System.Array.Copy(a, 1, a, 0, 2);",
      "global::System.Array.Fill(b, (byte)7);",
      "global::System.Array.Clear(a);",
      "return new int[] { a[0], 2 };"
    }));
  }

  /// <summary>A step is the C# operator, on a variable and on an element, before and behind.</summary>
  [TestCase("int", "a++ + a", "a++ + a", true)]
  [TestCase("int", "++a + a", "++a + a", true)]
  [TestCase("int", "a - --a", "a - --a", true)]
  [TestCase("int", "-a--", "-a--", true)]
  [TestCase("int", "-(--a)", "-(--a)", true)]
  [TestCase("int", "d[a]++ + ++d[a + 1]", "d[a]++ + ++d[a + 1]", true)]
  [TestCase("int", "checked(a++ + 1)", "checked(a++ + 1)", false)]
  [TestCase("int", "checked(++d[a])", "checked(++d[a])", false)]
  [TestCase("int", "checked(d[unchecked(a++ * 2)]--)", "checked(d[unchecked(a++ * 2)]--)", false)]
  [TestCase("double", "a++ + --a", "a++ + --a", false)]
  [TestCase("double", "checked(a++)", "a++", false)]
  public void Step_IsWritten(string type, string expression, string expected, bool block) {
    var module = Import($"public static {type} F({type} a, int[] d) => {expression};");
    Assert.That(Body(module), Is.EqualTo(new[] { $"return {expected};" }));
    Assert.That(EmittedCSharp.IsUnchecked(Emit(module)), Is.EqualTo(block));
  }

  /// <summary>
  /// The whole file for one small class: a table as a read-only field, a number as a <c>const</c>,
  /// a real <c>for</c>, a <c>do</c>, a switch whose sections are blocks, a checked sum.
  /// </summary>
  [Test]
  public void File_IsReadable() {
    var text = Emit(Import(
      "private static readonly byte[] Weights = { 1, 2, 3 }; private const long Limit = 1L << 40; "
      + "public static long Sum(byte[] data) { long sum = 0; for (int i = 0; i < data.Length; ++i) { if (data[i] == 0) continue; sum += Weights[i % Weights.Length] * data[i]; } "
      + "do { sum = checked(sum + sum); } while (sum < Limit); "
      + "switch (data.Length) { case 0: return -1; case 1: case 2: sum++; break; default: sum = Limit; break; } return sum; }"));
    Assert.That(text, Is.EqualTo(string.Join('\n',
      "// Snippet: generated by polypiler. Change the source, not this file.",
      "// Profile: net10.0, C# 14.",
      "",
      "public static class Snippet {",
      "",
      "  private static readonly byte[] Weights = { 1, 2, 3 };",
      "",
      "  private const long Limit = 1099511627776;",
      "",
      "  public static long Sum(byte[] data) {",
      "    unchecked {",
      "      long sum = 0;",
      "      {",
      "        int i = 0;",
      "        for (; i < data.Length; i = i + 1) {",
      "          if (data[i] == 0) {",
      "            continue;",
      "          }",
      "          sum = sum + (long)(Weights[i % Weights.Length] * data[i]);",
      "        }",
      "      }",
      "      do {",
      "        sum = checked(sum + sum);",
      "      } while (sum < 1099511627776L);",
      "      switch (data.Length) {",
      "        case 0: {",
      "          return -1;",
      "        }",
      "        case 1:",
      "        case 2: {",
      "          sum = sum + 1L;",
      "          break;",
      "        }",
      "        default: {",
      "          sum = 1099511627776;",
      "          break;",
      "        }",
      "      }",
      "      return sum;",
      "    }",
      "  }",
      "",
      "}",
      "")));
  }

  // ---------------------------------------------------------------- the scenarios of the feature file

  /// <summary>Scenario outline: A wide fixture survives the round trip C# to IR to C#.</summary>
  [TestCase("wide/Fnv1a64", "Hash", "a", "12638187200555641996")]
  [TestCase("wide/Crc16", "Compute", "123456789", "10673")]
  [TestCase("wide/Crc32Table", "Compute", "123456789", "3421780262")]
  public void WideChecksum_SurvivesTheRoundTrip(string fixture, string method, string text, string expected) {
    var call = Call(method, PrototypeCases.Text(text));
    var outcomes = Outcomes(PrototypeFixture.Imported(fixture), [call]);
    Assert.That(outcomes[call.ToString()], Is.EqualTo(PrototypeFixture.Outcome(null, call.Arguments).Replace("void", expected)));
    Assert.That(PrototypeFixture.CompiledOutcome(fixture, call), Is.EqualTo(outcomes[call.ToString()]));
  }

  [Test]
  public void SplitMix64_SurvivesTheRoundTrip() {
    var call = Call("First", 0UL);
    Assert.That(Outcomes(PrototypeFixture.Imported("wide/SplitMix64"), [call])[call.ToString()], Is.EqualTo("16294208416658607535"));
  }

  /// <summary>Scenario: A constant table is emitted once as a read-only field.</summary>
  [Test]
  public void ConstantTable_IsEmittedOnceAsAReadOnlyField() {
    var text = Emit(PrototypeFixture.Imported("wide/Crc32Table"));
    var declarations = Regex.Matches(text, @"private static readonly uint\[\] (\w+) = \{([^}]*)\};");
    Assert.That(declarations, Has.Count.EqualTo(1));
    Assert.That(Regex.Matches(text, "static readonly"), Has.Count.EqualTo(1));
    Assert.That(declarations[0].Groups[2].Value.Split(','), Has.Length.EqualTo(256));
    Assert.That(text.Split('\n').Select(line => line.Length), Has.All.LessThan(110));
    Assert.That(Regex.IsMatch(text, Regex.Escape(declarations[0].Groups[1].Value) + @"\[[^\]]*\]\s*(=[^=]|\+\+|--)"), Is.False);

    var field = GeneratedCSharp.Compile(text, true).Method("Compute").DeclaringType!.GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)!;
    Assert.That((field.IsInitOnly, ((uint[])field.GetValue(null)!).Length), Is.EqualTo((true, 256)));
  }

  /// <summary>Scenario: A checked addition stays checked whatever the project says.</summary>
  [Test]
  public void CheckedAddition_StaysCheckedWhateverTheProjectSays() {
    var text = Emit(Import("public static int F(int a, int b) => checked(a + b);"));
    foreach (var checking in _overflowChecking) {
      var method = GeneratedCSharp.Compile(text, checking).Method("F");
      Assert.That(() => method.Invoke(null, [int.MaxValue, 1]), Throws.TypeOf<TargetInvocationException>().With.InnerException.TypeOf<OverflowException>());
      Assert.That(method.Invoke(null, [int.MaxValue, 0]), Is.EqualTo(int.MaxValue));
    }
  }

  /// <summary>Scenario: Float arithmetic is not widened.</summary>
  [Test]
  public void FloatArithmetic_IsNotWidened() {
    var call = Call("F", 16777217f, 3f);
    var outcome = Outcomes(Import("public static float F(float a, float b) => a * b + a;"), [call])[call.ToString()];
    Assert.That(outcome, Is.EqualTo(PrototypeFixture.CompiledOutcome("wide/OperatorsWide", Call("MultiplyAdd", 16777217f, 3f))).And.EqualTo("67108864"));
  }

  /// <summary>Scenario: A for loop with continue keeps its step.</summary>
  [Test]
  public void ForLoopWithContinue_KeepsItsStep() {
    var module = Import("public static int F(int n) { int s = 0; for (int i = 0; i < n; i++) { if ((i & 1) == 0) continue; s += i; } return s; }");
    Assert.That(Emit(module), Does.Contain("for (; i < n; i = i + 1) {"));
    Assert.That(Outcomes(module, [Call("F", 10), Call("F", 0), Call("F", 1)]).Values, Is.EqualTo(new[] { "25", "0", "0" }));
  }

  // ---------------------------------------------------------------- shapes the fixtures do not reach

  /// <summary>
  /// Test design 4 and 10: the loops with a step in every form - one statement, several, a
  /// declaration, none - with a <c>continue</c> at the top, inside a switch and inside an inner loop,
  /// and a body that execution never leaves at its end.
  /// </summary>
  [Test]
  public void Loops_RunTheirStepOnEveryPass() {
    var n = new IrLocal("n", IrType.I32);
    var d = new IrLocal("d", IrType.Array(IrType.I32));
    var i = new IrLocal("i", IrType.I32);
    var j = new IrLocal("j", IrType.I32);
    var s = new IrLocal("s", IrType.I32);
    var t = new IrLocal("t", IrType.I32);
    IrExpr Below(IrLocal local, IrExpr limit) => Op(IrBinaryOp.Less, Ref(local), limit);
    IrExpr Equal(IrLocal local, long value) => Op(IrBinaryOp.Equal, Ref(local), Int(value));
    IrStmt Skip(IrExpr condition) => new IrIf(condition, Block(new IrContinue()), null);
    IrFunction Loop(string name, IrBlock step, params IrStmt[] body)
      => Function(name, IrType.I32, [n, d], new IrDeclare(i, Int(0)), new IrDeclare(s, Int(0)), new IrFor(Below(i, Ref(n)), Block(body), step), new IrReturn(Ref(s)));
    var twoSteps = Block(Add(s, 100), Add(i, 1));
    var module = new IrModule("Loops", [
      Loop("Assigned", Block(Add(i, 1)), Skip(Equal(i, 1)), Add(s, 1)),
      Loop("Stepped", Block(new IrExprStmt(new IrStep(Ref(i), true, false, false))), Skip(Equal(i, 1)), Add(s, 1)),
      Loop("SteppedChecked", Block(new IrExprStmt(new IrStep(Ref(i), true, true, true))), Skip(Equal(i, 1)), Add(s, 1)),
      Loop("Stored", Block(new IrStore(Ref(d), Int(0), Op(IrBinaryOp.Add, new IrArrayLoad(Ref(d), Int(0)), Int(1)))),
        new IrAssign(i, new IrArrayLoad(Ref(d), Int(0))), Skip(Equal(i, 2)), Add(s, 1)),
      Loop("Evaluated", Block(new IrExprStmt(Op(IrBinaryOp.Divide, Int(1), Op(IrBinaryOp.Subtract, Int(3), Ref(i))))), Add(i, 1), Skip(Equal(i, 1)), Add(s, 1)),
      Loop("Called", Block(new IrExprStmt(new IrCall("Assigned", IrType.I32, [Int(1), Ref(d)]))), Add(i, 1), Add(s, 1)),
      Loop("TwoSteps", twoSteps, Skip(Equal(i, 1)), Add(s, 1)),
      Loop("Declaring", Block(new IrDeclare(t, Op(IrBinaryOp.Add, Ref(i), Int(1))), new IrAssign(i, Ref(t))), Skip(Equal(i, 1)), Skip(Equal(i, 3)), Add(s, 1)),
      Loop("NoStep", Block(), Add(i, 1), Skip(Equal(i, 2)), Add(s, 1)),
      Loop("EndsInContinue", twoSteps, Add(s, 1), new IrContinue()),
      Loop("EndsInBreak", twoSteps, Add(s, 1), new IrBreak()),
      Loop("EndsInReturn", twoSteps, Add(s, 1), new IrReturn(Ref(s))),
      Loop("EndsInBothBranches", twoSteps, new IrIf(Equal(i, 1), Block(new IrContinue()), Block(Add(s, 1), new IrContinue()))),
      Loop("EndsInOneBranch", twoSteps, new IrIf(Equal(i, 1), Block(new IrContinue()), Block(Add(s, 1)))),
      Loop("ContinueInSwitch", twoSteps,
        new IrSwitch(Ref(i), [
          new IrSwitchSection([1], false, Block(new IrContinue())),
          new IrSwitchSection([2], false, Block(Add(s, 10), new IrBreak())),
          new IrSwitchSection([], true, Block(Add(s, 1)))
        ]),
        Add(s, 1000)),
      Loop("EndsInSwitch", twoSteps,
        new IrSwitch(Ref(i), [
          new IrSwitchSection([1], false, Block(new IrContinue())),
          new IrSwitchSection([], true, Block(Add(s, 1), new IrContinue()))
        ])),
      Loop("EndsInSwitchThatEnds", twoSteps, new IrSwitch(Ref(i), [new IrSwitchSection([], true, Block(Add(s, 1)))])),
      Loop("InnerLoops", twoSteps,
        new IrDeclare(j, Int(0)),
        new IrWhile(Below(j, Int(3)), Block(Add(j, 1), Skip(Equal(j, 2)), Add(s, 1))),
        new IrDoWhile(Block(Add(j, 1), Skip(Equal(j, 5)), Add(s, 10)), Below(j, Int(6))),
        new IrFor(Below(j, Int(9)), Block(Skip(Equal(j, 7)), Add(s, 1000)), Block(Add(j, 1))),
        new IrFor(Below(j, Int(12)), Block(Skip(Equal(j, 10)), Add(s, 10000)), Block(Add(s, 100000), Add(j, 1))),
        Skip(Equal(i, 0))),
      Loop("EndsInEndlessLoop", twoSteps, new IrWhile(Truth(true), Block(Add(s, 1), new IrIf(Equal(s, 3), Block(new IrReturn(Ref(s))), null)))),
      Loop("EndsInLoopWithBreak", twoSteps, new IrWhile(Truth(true), Block(Add(s, 1), new IrIf(Equal(i, 1), Block(Add(s, 10)), Block(new IrBreak())), new IrBreak()))),
      Loop("EndsInEndlessDo", twoSteps, new IrDoWhile(Block(Add(s, 1), new IrIf(Equal(s, 4), Block(new IrReturn(Ref(s))), null)), Truth(true))),
      Loop("EndsInEndlessFor", twoSteps, new IrFor(Truth(true), Block(new IrIf(Equal(s, 5), Block(new IrReturn(Ref(s))), null)), Block(Add(s, 1)))),
      Loop("EndsInDoWithBreak", twoSteps, new IrDoWhile(Block(Add(s, 1), new IrBreak()), Truth(true))),
      Loop("EndsInForWithBreak", twoSteps, new IrFor(Truth(true), Block(new IrIf(Op(IrBinaryOp.GreaterOrEqual, Ref(s), Int(5)), Block(new IrBreak()), null)), Block(Add(s, 1))))
    ]);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("for (; i < n; i = i + 1) {")
      .And.Contain("for (; i < n; ++i) {")
      .And.Contain("for (; i < n; _ = checked(i++)) {")
      .And.Contain("for (; i < n; d[0] = d[0] + 1) {")
      .And.Contain("for (; i < n; _ = 1 / (3 - i)) {")
      .And.Contain("for (; i < n; Assigned(1, d)) {")
      .And.Contain("for (; j < 9; j = j + 1) {")
      .And.Contain("          {\n            var t = i + 1;\n            i = t;\n          }\n          continue;\n"));

    var counts = module.Functions.SelectMany(function => new[] { 0, 1, 2, 3, 5 }.Select(count => Call(function.Name, count, new[] { 0 })));
    var outcomes = Outcomes(module, counts);
    Assert.That(outcomes["Assigned(5, [0])"], Is.EqualTo("4 | [0]"));
    Assert.That(outcomes["TwoSteps(5, [0])"], Is.EqualTo("504 | [0]"));
    Assert.That(outcomes["Declaring(5, [0])"], Is.EqualTo("3 | [0]"));
    Assert.That(outcomes["ContinueInSwitch(3, [0])"], Is.EqualTo("2311 | [0]"));
    Assert.That(outcomes["Evaluated(5, [0])"], Is.EqualTo("trap: divide by zero"));
  }

  /// <summary>
  /// Test design 4 and 10: a switch on every integer type with the smallest and the largest value as
  /// labels, grouped labels, the default first, last and absent, no section at all, and sections that
  /// end in every way a section can.
  /// </summary>
  [Test]
  public void Switches_ChooseTheSectionOfTheInterpreter() {
    var functions = new List<IrFunction>();
    var calls = new List<PrototypeCall>();
    foreach (var type in _integers) {
      var v = new IrLocal("v", type);
      var low = (long)IrOperators.Minimum(type);
      var high = (long)IrOperators.Maximum(type);
      functions.Add(Function("Edges" + type, IrType.I32, [v],
        new IrSwitch(Ref(v), [
          new IrSwitchSection([], true, Block(new IrReturn(Int(0)))),
          new IrSwitchSection([low], false, Block(new IrReturn(Int(1)))),
          new IrSwitchSection([high, 1], false, Block(new IrReturn(Int(2))))
        ])));
      calls.AddRange(new[] { low, high, 1L, 2L }.Select(value => Call("Edges" + type, Boxed(type, value))));
    }

    var a = new IrLocal("a", IrType.I32);
    var r = new IrLocal("r", IrType.I32);
    var x = new IrLocal("x", IrType.I32);
    IrFunction Choice(string name, params IrSwitchSection[] sections)
      => Function(name, IrType.I32, [a], new IrDeclare(r, Int(7)), new IrSwitch(Op(IrBinaryOp.Divide, Int(12), Ref(a)), sections), new IrReturn(Ref(r)));
    var returnsBoth = new IrIf(Op(IrBinaryOp.Less, Ref(a), Int(0)), Block(new IrReturn(Int(-1))), Block(new IrReturn(Int(-2))));
    functions.AddRange([
      Choice("NoSection"),
      Choice("NoDefault", new IrSwitchSection([12], false, Block(new IrAssign(r, Int(1))))),
      Choice("DefaultLast", new IrSwitchSection([12, 6], false, Block(new IrAssign(r, Int(1)))), new IrSwitchSection([4], true, Block(new IrAssign(r, Int(2))))),
      Choice("EmptySections", new IrSwitchSection([12], false, Block()), new IrSwitchSection([], true, Block(new IrBreak()))),
      Choice("OwnScopes",
        new IrSwitchSection([12], false, Block(new IrDeclare(x, Int(3)), new IrAssign(r, Ref(x)))),
        new IrSwitchSection([6], false, Block(new IrDeclare(x, Int(4)), new IrAssign(r, Ref(x)), new IrBreak()))),
      Choice("ReturnsInBothBranches", new IrSwitchSection([12], false, Block(returnsBoth)), new IrSwitchSection([], true, Block(new IrAssign(r, Int(3))))),
      Choice("BreaksInABranch",
        new IrSwitchSection([12], false, Block(new IrIf(Op(IrBinaryOp.Less, Ref(a), Int(0)), Block(new IrBreak()), Block(new IrAssign(r, Int(4)))), new IrAssign(r, Int(5))))),
      Choice("LoopsForever", new IrSwitchSection([12], false, Block(new IrWhile(Truth(true), Block(new IrReturn(Int(6))))))),
      Choice("NestedSwitch",
        new IrSwitchSection([12], false, Block(new IrSwitch(Ref(a), [
          new IrSwitchSection([1], false, Block(new IrReturn(Int(8)))),
          new IrSwitchSection([], true, Block(new IrReturn(Int(9))))
        ]))),
        new IrSwitchSection([6], false, Block(new IrSwitch(Ref(a), [
          new IrSwitchSection([2], false, Block(new IrBreak())),
          new IrSwitchSection([], true, Block(new IrReturn(Int(10))))
        ]))),
        new IrSwitchSection([4], false, Block(new IrSwitch(Ref(a), [new IrSwitchSection([3], false, Block(new IrReturn(Int(11))))]))))
    ]);
    calls.AddRange(functions.Skip(_integers.Length).SelectMany(function => new[] { 0, 1, 2, 3, 4, 12 }.Select(value => Call(function.Name, value))));

    var module = new IrModule("Switches", functions);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("    _ = 12 / a;\n")
      .And.Contain("      default: {\n        return 0;\n      }\n      case -9223372036854775808: {\n")
      .And.Contain("      case 9223372036854775807:\n      case 1: {\n")
      .And.Contain("      case 18446744073709551615:\n      case 1: {\n")
      .And.Contain("      case 4:\n      default: {\n")
      .And.Contain("      case 12: {\n        break;\n      }\n      default: {\n        break;\n      }\n"));

    var outcomes = Outcomes(module, calls);
    Assert.That(outcomes["NoSection(0)"], Is.EqualTo("trap: divide by zero"));
    Assert.That(outcomes["NoSection(1)"], Is.EqualTo("7"));
    Assert.That((outcomes["DefaultLast(2)"], outcomes["DefaultLast(4)"], outcomes["OwnScopes(2)"], outcomes["NestedSwitch(3)"]), Is.EqualTo(("1", "2", "4", "11")));
    Assert.That((outcomes["Edgesi8(-128)"], outcomes["Edgesu64(18446744073709551615)"], outcomes["Edgesu16(2)"]), Is.EqualTo(("1", "2", "0")));
  }

  /// <summary>An integer of the IR as the .NET value the generated method and the interpreter take.</summary>
  private static object Boxed(IrType type, long value) => type.Kind switch {
    IrTypeKind.I8 => (sbyte)value,
    IrTypeKind.U8 => (byte)value,
    IrTypeKind.I16 => (short)value,
    IrTypeKind.U16 => (ushort)value,
    IrTypeKind.I32 => (int)value,
    IrTypeKind.U32 => (uint)value,
    IrTypeKind.I64 => value,
    _ => (ulong)value
  };

  /// <summary>
  /// Test design 8, 9 and 11 for what no C# source produces because the compiler folds or refuses it
  /// first: constants in checked arithmetic and in conversions that trap or saturate, a divisor that
  /// is a constant of the module, a conversion that keeps every value under each contract.
  /// </summary>
  [Test]
  public void ConstantOperands_AreLeftToRunTimeWhereTheCompilerWouldJudgeThem() {
    var a = new IrLocal("a", IrType.I32);
    var zero = new IrModuleConst("Zero", IrType.I32, [0]);
    var minusOne = new IrModuleConst("MinusOne", IrType.I64, [-1]);
    IrConst Long(long value) => new(IrType.I64, value);
    IrConst Unsigned(long value) => new(IrType.U64, value);
    IrFunction Value(string name, IrExpr value) => Function(name, value.Type, [], new IrReturn(value));
    var module = new IrModule("Folded", [
      Value("SumOverflows", Op(IrBinaryOp.AddChecked, Int(int.MaxValue), Int(1))),
      Value("SumFits", Op(IrBinaryOp.AddChecked, Int(int.MaxValue), Int(-1))),
      Value("DifferenceOverflows", Op(IrBinaryOp.SubtractChecked, new IrConst(IrType.U32, 0), new IrConst(IrType.U32, 1))),
      Value("ProductOverflows", Op(IrBinaryOp.MultiplyChecked, Long(long.MinValue), Long(-1))),
      Value("ProductOfFoldedOverflows", Op(IrBinaryOp.MultiplyChecked, Op(IrBinaryOp.Add, Long(long.MaxValue), Long(1)), Op(IrBinaryOp.Subtract, Long(0), Long(1)))),
      Value("NegationOverflows", new IrUnary(IrUnaryOp.NegateChecked, Int(int.MinValue))),
      Value("NegationFits", new IrUnary(IrUnaryOp.NegateChecked, Long(5))),
      Value("WrapInsideChecked", Op(IrBinaryOp.AddChecked, Op(IrBinaryOp.Multiply, Int(65536), Int(65536)), Int(1))),
      Value("NarrowingTraps", new IrConvert(IrType.U8, Int(300), IrConversion.Trap)),
      Value("NarrowingFits", new IrConvert(IrType.U8, Int(200), IrConversion.Trap)),
      Value("SignTraps", new IrConvert(IrType.U64, Long(-1), IrConversion.Trap)),
      Value("NumberTraps", new IrConvert(IrType.I32, Real(1e10), IrConversion.Trap)),
      Value("NaNTraps", new IrConvert(IrType.I64, Real(double.NaN), IrConversion.Trap)),
      Value("NumberFits", new IrConvert(IrType.I16, Real(-32768.9), IrConversion.Trap)),
      Value("Saturates", new IrConvert(IrType.I32, Real(1e10), IrConversion.Saturate)),
      Value("SaturatesBelow", new IrConvert(IrType.I64, Real(double.NegativeInfinity), IrConversion.Saturate)),
      Value("SaturatesUnsigned", new IrConvert(IrType.U32, Real(-1), IrConversion.Saturate)),
      Value("SaturatesWide", new IrConvert(IrType.U64, Real(1e30), IrConversion.Saturate)),
      Value("NaNIsZero", new IrConvert(IrType.U64, Real(double.NaN), IrConversion.Saturate)),
      Value("Truncates", new IrConvert(IrType.I32, Real(-7.9), IrConversion.Saturate)),
      Value("KeepsUnderTrap", new IrConvert(IrType.I64, new IrConvert(IrType.I32, new IrConst(IrType.U8, 200), IrConversion.Trap), IrConversion.Trap)),
      Value("RoundsToFloat", new IrConvert(IrType.F32, Long(0x4000004000000001))),
      Value("RoundsUnsignedToFloat", new IrConvert(IrType.F32, Unsigned(unchecked((long)0x8000008000000001UL)))),
      Value("NarrowsToFloat", new IrConvert(IrType.F32, Real(1e300))),
      Value("WidensFromFloat", new IrConvert(IrType.F64, new IrConst(IrType.F32, IrOperators.Bits(IrType.F32, 0.1)))),
      Value("LargestDividedByItself", Op(IrBinaryOp.Divide, Unsigned(-1), Unsigned(-1))),
      Value("SmallestByMinusOne", Op(IrBinaryOp.Remainder, Long(long.MinValue), Long(-1))),
      Function("ByConstantZero", IrType.I32, [a], new IrReturn(Op(IrBinaryOp.Divide, Ref(a), new IrConstRef(zero)))),
      Function("ByConstantMinusOne", IrType.I64, [a], new IrReturn(Op(IrBinaryOp.Divide, new IrConvert(IrType.I64, Ref(a)), new IrConstRef(minusOne)))),
      Function("FloatByZero", IrType.F64, [a], new IrReturn(Op(IrBinaryOp.Divide, new IrConvert(IrType.F64, Ref(a)), Real(0)))),
      Function("ElementAtConstant", IrType.I32, [a], new IrReturn(new IrArrayLoad(new IrNewArray(IrType.Array(IrType.I32), Ref(a)), new IrConstRef(zero))))
    ], [zero, minusOne]);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("return checked(global::System.Math.Max(2147483647, 2147483647) + 1);")
      .And.Contain("return checked(-global::System.Math.Max(-2147483648, -2147483648));")
      .And.Contain("return checked(global::System.Math.Max(unchecked(65536 * 65536), unchecked(65536 * 65536)) + 1);")
      .And.Contain("return checked((byte)global::System.Math.Max(300, 300));")
      .And.Contain("return (int)global::System.Math.Max(10000000000.0, 10000000000.0);")
      .And.Contain("return (long)(int)(byte)200;")
      .And.Contain("return 18446744073709551615UL / 18446744073709551615UL;")
      .And.Contain("return a / global::System.Math.Max(Zero, Zero);")
      .And.Contain("return (double)a / 0.0;"));

    var calls = module.Functions.Select(function => function.Parameters.Count == 0 ? Call(function.Name) : Call(function.Name, 5));
    var outcomes = Outcomes(module, calls);
    Assert.That(outcomes.Where(outcome => outcome.Value == "trap: integer overflow").Select(outcome => outcome.Key), Is.EquivalentTo(new[] {
      "SumOverflows()", "DifferenceOverflows()", "ProductOverflows()", "ProductOfFoldedOverflows()", "NegationOverflows()", "NarrowingTraps()", "SignTraps()",
      "NumberTraps()", "NaNTraps()", "SmallestByMinusOne()"
    }));
    Assert.That(outcomes["ByConstantZero(5)"], Is.EqualTo("trap: divide by zero"));
    Assert.That((outcomes["Saturates()"], outcomes["SaturatesWide()"], outcomes["NaNIsZero()"], outcomes["Truncates()"], outcomes["NumberFits()"], outcomes["WrapInsideChecked()"]),
      Is.EqualTo(("2147483647", "18446744073709551615", "0", "-7", "-32768", "1")));
  }

  private static readonly double[] _numbers = [
    double.NaN, double.NegativeInfinity, -1e300, -4294967297.5, -32769.5, -32769, -32768.5, -32768, -32767.5, -129.5, -129, -128.9, -128, -127.9, -1, -0.9, -0.0, 0, 0.9,
    1, 126.9, 127, 127.5, 128, 254.9, 255, 255.5, 256, 32767, 32767.9, 32768, 65534.9, 65535, 65535.9, 65536, 2147483648, 1e300, double.PositiveInfinity, double.Epsilon
  ];

  /// <summary>
  /// Test design 9: a floating-point number saturating to an integer narrower than <c>int</c>, which
  /// no cast of C# does - the class gets a helper for it, and only then. A function keeps its name
  /// in every profile, so a helper of the same name is the one that gives way.
  /// </summary>
  [Test]
  public void SaturationToANarrowInteger_IsAHelper() {
    var d = new IrLocal("d", IrType.F64);
    var f = new IrLocal("f", IrType.F32);
    var shadow = new IrLocal("Saturated", IrType.F64);
    var narrow = new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16 };
    var module = new IrModule("Narrow", [
      .. narrow.Select(type => Function("Double" + type, type, [d], new IrReturn(new IrConvert(type, Ref(d), IrConversion.Saturate)))),
      .. narrow.Select(type => Function("Single" + type, type, [f], new IrReturn(new IrConvert(type, Ref(f), IrConversion.Saturate)))),
      Function("Constant", IrType.U8, [], new IrReturn(new IrConvert(IrType.U8, Real(300.5), IrConversion.Saturate))),
      Function("Saturated", IrType.I32, [shadow], new IrReturn(new IrConvert(IrType.I32, new IrConvert(IrType.I8, Ref(shadow), IrConversion.Saturate))))
    ]);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("return (sbyte)Saturated_(d, -128, 127);")
      .And.Contain("return (ushort)Saturated_(f, 0, 65535);")
      .And.Contain("return (byte)Saturated_(300.5, 0, 255);")
      .And.Contain("public static int Saturated(double Saturated__) {")
      .And.Contain("return (sbyte)Saturated_(Saturated__, -128, 127);")
      .And.EndWith("\n  private static int Saturated_(double value, int low, int high) {\n    return value >= high ? high : value > low ? (int)value : value <= low ? low : 0;\n  }\n\n}\n"));
    Assert.That(Regex.Matches(text, "private static"), Has.Count.EqualTo(1));

    var calls = narrow.SelectMany(type => _numbers.SelectMany(number => new[] { Call("Double" + type, number), Call("Single" + type, (float)number) }))
      .Append(Call("Constant"));
    var outcomes = Outcomes(module, calls);
    Assert.That((outcomes["Doublei8(NaN)"], outcomes["Doublei8(f:c060300000000000)"], outcomes["Doubleu8(f:406ff00000000000)"], outcomes["Doubleu8(256)"], outcomes["Constant()"]),
      Is.EqualTo(("0", "-128", "255", "255", "255")));
    // The function is still called by its own name; it is the helper that was given another.
    Assert.That(Builds(module).Select(build => build.Outcome(Call("Saturated", -1e9))),
      Has.All.EqualTo(PrototypeFixture.InterpretedOutcome(module, Call("Saturated", -1e9))).And.All.EqualTo("-128"));

    var plain = Emit(new IrModule("Wide", [
      Function("Saturated", IrType.I32, [d, shadow], new IrReturn(new IrConvert(IrType.I32, Op(IrBinaryOp.Add, Ref(d), Ref(shadow)), IrConversion.Saturate)))
    ]));
    Assert.That(plain, Does.Contain("public static int Saturated(double d, double Saturated_) => unchecked((int)(d + Saturated_));").And.Not.Contain("private"));
  }

  /// <summary>
  /// ECMA-262's <c>ToInt32</c>, <c>ToUint32</c> and <c>Math.round</c>, which no cast and no method of
  /// the base library are: the class gets a helper for each one it uses, exact for a NaN, the
  /// infinities and numbers beyond 2^32 and 2^53, and the generated code does what the interpreter does.
  /// </summary>
  [Test]
  public void EcmaScriptConversions_AreHelpers() {
    var d = new IrLocal("d", IrType.F64);
    var f = new IrLocal("f", IrType.F32);
    var shadow = new IrLocal("ToInt32", IrType.F64);
    var module = new IrModule("Numbers", [
      Function("Signed", IrType.I32, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [Ref(d)]))),
      Function("Unsigned", IrType.U32, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [Ref(d)]))),
      Function("Rounded", IrType.F64, [d], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundHalfUp, IrType.F64, [Ref(d)]))),
      Function("RoundedSingle", IrType.F32, [f], new IrReturn(new IrIntrinsic(IrIntrinsicOp.RoundHalfUp, IrType.F32, [Ref(f)]))),
      Function("ToInt32", IrType.I32, [shadow], new IrReturn(Op(IrBinaryOp.Xor, new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [Ref(shadow)]), Int(1))))
    ]);
    var text = Emit(module);
    Assert.That(text, Does.Contain("return ToInt32(d);")
      .And.Contain("return ToUint32(d);")
      .And.Contain("return RoundHalfUp(d);")
      .And.Contain("return (float)RoundHalfUp(f);")
      .And.Contain("public static int ToInt32_(double ToInt32__) {")
      .And.Contain("return ToInt32(ToInt32__) ^ 1;")
      .And.Contain("  private static int ToInt32(double value) {\n    return double.IsNaN(value) || double.IsInfinity(value) ? 0 : unchecked((int)(long)(global::System.Math.Truncate(value) % 4294967296.0));\n  }\n")
      .And.Contain("  private static uint ToUint32(double value) {\n    return double.IsNaN(value) || double.IsInfinity(value) ? 0u : unchecked((uint)(long)(global::System.Math.Truncate(value) % 4294967296.0));\n  }\n"));
    Assert.That(Regex.Matches(text, "private static"), Has.Count.EqualTo(3));

    double[] numbers = [
      .. _numbers, 0.5, -0.5, 1.5, -1.5, 2.5, -2.5, 0.49999999999999994, -0.2, 2147483647, -2147483648, -2147483649, 4294967295, 4294967296, 4294967297,
      9007199254740992, 9007199254740994, -9007199254740994, 1e21, -1e21, 4503599627370495.5, double.MaxValue
    ];
    var calls = numbers.SelectMany(number => new[] { Call("Signed", number), Call("Unsigned", number), Call("Rounded", number), Call("RoundedSingle", (float)number) }).ToList();
    var outcomes = Outcomes(module, calls);
    Assert.That(outcomes, Has.Count.EqualTo(calls.Select(call => call.ToString()).Distinct().Count()));
    Assert.That((outcomes["Signed(2147483648)"], outcomes["Signed(f:444b1ae4d6e2ef50)"], outcomes["Signed(NaN)"], outcomes["Unsigned(-1)"], outcomes["Unsigned(f:c44b1ae4d6e2ef50)"]),
      Is.EqualTo(("-2147483648", "-559939584", "0", "4294967295", "559939584")));
    Assert.That((outcomes["Rounded(f:3fe0000000000000)"], outcomes["Rounded(f:bfe0000000000000)"], outcomes["Rounded(f:4004000000000000)"], outcomes["Rounded(f:c004000000000000)"]),
      Is.EqualTo(("1", "f:8000000000000000", "3", "-2")));

    // The function a helper took the name of is called by the name it was given instead.
    Assert.That(Builds(module).Select(build => build.Outcome(Call("ToInt32_", 4294967298.5))), Has.All.EqualTo("3"));

    // A module that uses none of the three gets none of them.
    Assert.That(Emit(new IrModule("Plain", [Function("F", IrType.F64, [d], new IrReturn(Ref(d)))])), Does.Not.Contain("private"));
  }

  /// <summary>
  /// Test design 3 and 9: every conversion between two numeric types under every contract the IR
  /// allows for the pair, on the edges of both types - the same bits, or the same exception.
  /// </summary>
  [Test, Category("Slow")]
  public void EveryConversion_AgreesWithTheInterpreter() {
    IrType[] types = [.. _integers, IrType.F32, IrType.F64];
    var functions = new List<IrFunction>();
    var calls = new List<PrototypeCall>();
    foreach (var from in types)
      foreach (var to in types) {
        var x = new IrLocal("x", from);
        IrConversion[] contracts = to.IsFloat ? [IrConversion.Wrap] : from.IsFloat ? [IrConversion.Saturate, IrConversion.Trap] : [IrConversion.Wrap, IrConversion.Trap];
        foreach (var contract in contracts) {
          var name = $"{from}To{to}{contract}";
          functions.Add(Function(name, to, [x], new IrReturn(new IrConvert(to, Ref(x), contract))));
          calls.AddRange(Operands(from).Select(operand => Call(name, operand)));
        }
      }

    var module = new IrModule("Conversions", functions);
    var outcomes = Outcomes(module, calls);
    Assert.That(functions, Has.Count.EqualTo(8 * 8 * 2 + 2 * 8 * 2 + 10 * 2));
    Assert.That(outcomes.Values, Has.Member("trap: integer overflow").And.Member("NaN").And.Member("-9223372036854775808").And.Member("18446744073709551615"));
    Assert.That(outcomes, Has.Count.GreaterThan(2000));
  }

  private static object[] Operands(IrType type) {
    if (type == IrType.F64)
      return [.. _numbers.Cast<object>(), 9007199254740993.0, 9223372036854775807.0, 18446744073709551615.0, -9223372036854777856.0, 2147483647.5, -2147483648.9, 4294967295.5];

    if (type == IrType.F32)
      return [.. _numbers.Select(number => (object)(float)number), 16777217f, 2147483648f, 4294967296f, 9223372036854775807f, -9223372036854775808f, 18446744073709551615f];

    long[] values = [(long)IrOperators.Minimum(type), (long)IrOperators.Maximum(type), 0, 1, -1, 127, 128, 255, 256, 32767, 32768, 65535, 65536, int.MaxValue, 1L << 31, uint.MaxValue, 1L << 32, long.MaxValue, long.MinValue, 16777217, 9007199254740993];
    return [.. values.Select(value => Boxed(type, IrOperators.Wrap(type, value))).Distinct()];
  }

  /// <summary>
  /// Test design 2, 8 and 11: the operators on hand-built operands the C# frontend never produces -
  /// a step that traps as a statement, checked arithmetic on every wide integer type at its edges,
  /// the logical shift of a signed 64-bit number, an operand that is a narrow integer made an int.
  /// </summary>
  [Test]
  public void Operators_AgreeWithTheInterpreterAtTheirEdges() {
    var functions = new List<IrFunction>();
    var calls = new List<PrototypeCall>();
    var n = new IrLocal("n", IrType.I32);
    foreach (var type in new[] { IrType.I32, IrType.U32, IrType.I64, IrType.U64 }) {
      var a = new IrLocal("a", type);
      var b = new IrLocal("b", type);
      var edges = new[] { (long)IrOperators.Minimum(type), (long)IrOperators.Maximum(type), 0, 1, -1, 2 }.Select(value => Boxed(type, IrOperators.Wrap(type, value))).Distinct().ToList();
      foreach (var op in new[] {
        IrBinaryOp.Add, IrBinaryOp.Subtract, IrBinaryOp.Multiply, IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked,
        IrBinaryOp.Divide, IrBinaryOp.Remainder, IrBinaryOp.And, IrBinaryOp.Or, IrBinaryOp.Xor, IrBinaryOp.Less, IrBinaryOp.GreaterOrEqual, IrBinaryOp.Equal
      }) {
        functions.Add(Function($"{op}{type}", op >= IrBinaryOp.Equal ? IrType.Bool : type, [a, b], new IrReturn(Op(op, Ref(a), Ref(b)))));
        calls.AddRange(from x in edges from y in edges select Call($"{op}{type}", x, y));
      }

      foreach (var op in new[] { IrBinaryOp.ShiftLeft, IrBinaryOp.ShiftRight, IrBinaryOp.ShiftRightLogical }) {
        functions.Add(Function($"{op}{type}", type, [a, n], new IrReturn(Op(op, Ref(a), Ref(n)))));
        calls.AddRange(from x in edges from count in new[] { 0, 1, type.Bits - 1, type.Bits, type.Bits + 1, -1 } select Call($"{op}{type}", x, count));
      }

      functions.Add(Function($"Not{type}", type, [a], new IrReturn(new IrUnary(IrUnaryOp.BitNot, Ref(a)))));
      calls.AddRange(edges.Select(x => Call($"Not{type}", x)));
      if (!type.IsSigned)
        continue;

      foreach (var op in new[] { IrUnaryOp.Negate, IrUnaryOp.NegateChecked }) {
        functions.Add(Function($"{op}{type}", type, [a], new IrReturn(new IrUnary(op, Ref(a)))));
        calls.AddRange(edges.Select(x => Call($"{op}{type}", x)));
      }
    }

    foreach (var type in _integers) {
      var a = new IrLocal("a", type);
      var d = new IrLocal("d", IrType.Array(type));
      var edges = new[] { (long)IrOperators.Minimum(type), (long)IrOperators.Maximum(type), 0 }.Select(value => Boxed(type, value)).ToList();
      foreach (var (increment, postfix, trap) in from increment in _overflowChecking from postfix in _overflowChecking from trap in _overflowChecking select (increment, postfix, trap)) {
        var name = $"Step{type}{(increment ? "Up" : "Down")}{(postfix ? "After" : "Before")}{(trap ? "Trap" : "Wrap")}";
        functions.Add(Function(name, type, [a], new IrExprStmt(new IrStep(Ref(a), increment, postfix, trap)), new IrReturn(Ref(a))));
        functions.Add(Function(name + "Element", type, [d], new IrReturn(new IrStep(new IrArrayLoad(Ref(d), Int(0)), increment, postfix, trap))));
        calls.AddRange(edges.Select(x => Call(name, x)));
        calls.AddRange(edges.Select(x => Call(name + "Element", ArrayOf(x))));
      }
    }

    var s = new IrLocal("s", IrType.I8);
    var c = new IrLocal("c", IrType.U16);
    var m = new IrLocal("m", IrType.I32);
    IrExpr Wide(IrLocal local) => new IrConvert(IrType.I32, Ref(local));
    functions.AddRange([
      Function("AbsOfPromoted", IrType.I32, [s], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [Wide(s)]))),
      Function("NegatedPromoted", IrType.I32, [s], new IrReturn(new IrUnary(IrUnaryOp.NegateChecked, Wide(s)))),
      Function("ChosenPromoted", IrType.I32, [s, c], new IrReturn(new IrConditional(Op(IrBinaryOp.Less, Wide(s), Wide(c)), Wide(s), Wide(c)))),
      Function("ShiftedPromoted", IrType.I32, [s, c], new IrReturn(Op(IrBinaryOp.ShiftRightLogical, Wide(s), Wide(c)))),
      Function("IndexedByPromoted", IrType.I32, [c], new IrReturn(new IrArrayLength(new IrNewArray(IrType.Array(IrType.F32), Wide(c))))),
      Function("Difference", IrType.I32, [n, m], new IrReturn(Op(IrBinaryOp.Subtract, Ref(n), Ref(m)))),
      Function("PromotedArgument", IrType.I32, [s, c], new IrReturn(new IrCall("Difference", IrType.I32, [Wide(s), Wide(c)]))),
      Function("StoredPromoted", IrType.I32, [s], new IrDeclare(n, Wide(s)), new IrAssign(n, Op(IrBinaryOp.Multiply, Ref(n), Wide(s))), new IrReturn(Ref(n))),
      Function("SwitchedPromoted", IrType.I32, [s], new IrSwitch(Wide(s), [new IrSwitchSection([-128], false, Block(new IrReturn(Int(1))))]), new IrReturn(Int(0)))
    ]);
    calls.AddRange(new sbyte[] { -128, -1, 0, 127 }.SelectMany(value => new[] {
      Call("AbsOfPromoted", value), Call("NegatedPromoted", value), Call("ChosenPromoted", value, (ushort)3), Call("ShiftedPromoted", value, (ushort)33),
      Call("PromotedArgument", value, (ushort)65535), Call("StoredPromoted", value), Call("SwitchedPromoted", value)
    }));
    calls.AddRange(new ushort[] { 0, 1, 65535 }.Select(value => Call("IndexedByPromoted", value)));

    var module = new IrModule("Edges", functions);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("return global::System.Math.Abs((int)s);")
      .And.Contain("return checked(-s);")
      .And.Contain("return (s < c) ? (int)s : (int)c;")
      .And.Contain("return s >>> c;")
      .And.Contain("return (new float[c]).Length;")
      .And.Contain("return Difference(s, c);")
      .And.Contain("int n = s;")
      .And.Contain("switch ((int)s) {")
      .And.Contain("return a >>> n;")
      .And.Contain("    _ = checked(a++);\n")
      .And.Contain("      --a;\n"));

    var outcomes = Outcomes(module, calls);
    Assert.That(outcomes["AbsOfPromoted(-128)"], Is.EqualTo("128"));
    Assert.That(outcomes["Stepi8UpAfterTrap(127)"], Is.EqualTo("trap: integer overflow"));
    Assert.That(outcomes["Stepi8UpAfterWrap(127)"], Is.EqualTo("-128"));
    Assert.That(outcomes["Stepu64DownBeforeTrapElement([0])"], Is.EqualTo("trap: integer overflow"));
    Assert.That(outcomes["Stepu64DownBeforeWrapElement([0])"], Is.EqualTo("18446744073709551615 | [18446744073709551615]"));
    Assert.That(outcomes["ShiftRightLogicali64(-9223372036854775808, 65)"], Is.EqualTo("4611686018427387904"));
    Assert.That(outcomes["MultiplyCheckedu64(18446744073709551615, 2)"], Is.EqualTo("trap: integer overflow"));
    Assert.That(outcomes["Dividei64(-9223372036854775808, -1)"], Is.EqualTo("trap: integer overflow"));
    Assert.That(outcomes, Has.Count.GreaterThan(1500));
  }

  private static Array ArrayOf(object value) {
    var array = Array.CreateInstance(value.GetType(), 1);
    array.SetValue(value, 0);
    return array;
  }

  /// <summary>
  /// Test design 5, 9 and 10: the constants of a module of every type - the edges of the integers,
  /// the special floating-point numbers - and tables of no, one, nine and 256 elements, read,
  /// measured and copied from; and the library operations on constants of each type.
  /// </summary>
  [Test]
  public void ConstantsAndTables_AreWhatTheModuleSays() {
    var i = new IrLocal("i", IrType.I32);
    var constants = new List<IrModuleConst>();
    var functions = new List<IrFunction>();
    var calls = new List<PrototypeCall>();
    foreach (var type in _integers) {
      var low = (long)IrOperators.Minimum(type);
      var high = (long)IrOperators.Maximum(type);
      constants.Add(new IrModuleConst("Low" + type, type, [low]));
      constants.Add(new IrModuleConst("High" + type, type, [high]));
      constants.Add(new IrModuleConst("Table" + type, IrType.Array(type), [low, 0, 1, high, 2, 3, 4, 5, 6]));
      functions.Add(Function("Low" + type + "Value", type, [], new IrReturn(new IrConstRef(constants[^3]))));
      functions.Add(Function("High" + type + "Value", type, [], new IrReturn(new IrConstRef(constants[^2]))));
      functions.Add(Function("Entry" + type, type, [i], new IrReturn(new IrArrayLoad(new IrConstRef(constants[^1]), Ref(i)))));
      functions.Add(Function("Greatest" + type, type, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Max, type, [new IrConst(type, low), new IrConst(type, high)]))));
      calls.AddRange([Call("Low" + type + "Value"), Call("High" + type + "Value"), Call("Greatest" + type), .. new[] { -1, 0, 3, 8, 9 }.Select(index => Call("Entry" + type, index))]);
    }

    double[] reals = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0, 0.1, -1.5, 1e-45, 3.4028234663852886e38, 5e-324, double.MaxValue, 16777217, 1e21];
    foreach (var type in new[] { IrType.F32, IrType.F64 }) {
      var bits = reals.Select(real => IrOperators.Bits(type, real)).ToList();
      var table = new IrModuleConst("Reals" + type, IrType.Array(type), bits);
      constants.Add(table);
      functions.Add(Function("Real" + type, type, [i], new IrReturn(new IrArrayLoad(new IrConstRef(table), Ref(i)))));
      calls.AddRange(Enumerable.Range(0, reals.Length).Select(index => Call("Real" + type, index)));
      for (var index = 0; index < bits.Count; ++index) {
        var constant = new IrModuleConst($"Real{type}{index}", type, [bits[index]]);
        constants.Add(constant);
        functions.Add(Function($"Real{type}Constant{index}", type, [], new IrReturn(new IrConstRef(constant))));
        functions.Add(Function($"Real{type}Literal{index}", type, [], new IrReturn(new IrConst(type, bits[index]))));
        functions.Add(Function($"Real{type}Negated{index}", type, [], new IrReturn(new IrUnary(IrUnaryOp.Negate, new IrConst(type, bits[index])))));
        calls.AddRange([Call($"Real{type}Constant{index}"), Call($"Real{type}Literal{index}"), Call($"Real{type}Negated{index}")]);
      }
    }

    var truth = new IrModuleConst("Enabled", IrType.Bool, [1]);
    var none = new IrModuleConst("None", IrType.Array(IrType.F64), []);
    var one = new IrModuleConst("One", IrType.Array(IrType.I64), [long.MinValue]);
    var many = new IrModuleConst("Many", IrType.Array(IrType.U8), [.. Enumerable.Range(0, 256).Select(value => (long)value)]);
    var keyword = new IrModuleConst("class", IrType.I32, [7]);
    var named = new IrModuleConst("Tables", IrType.I32, [8]);
    var unused = new IrModuleConst("Unused", IrType.Array(IrType.I32), [1, 2]);
    constants.AddRange([truth, none, one, many, keyword, named, unused]);
    var t = new IrLocal("t", IrType.Array(IrType.U8));
    var hiding = new IrLocal("Enabled", IrType.I32);
    functions.AddRange([
      Function("Lengths", IrType.I32, [], new IrReturn(Op(IrBinaryOp.Add,
        Op(IrBinaryOp.Multiply, new IrArrayLength(new IrConstRef(none)), Int(1000)),
        Op(IrBinaryOp.Add, Op(IrBinaryOp.Multiply, new IrArrayLength(new IrConstRef(one)), Int(100)), new IrArrayLength(new IrConstRef(many)))))),
      Function("NoEntry", IrType.F64, [i], new IrReturn(new IrArrayLoad(new IrConstRef(none), Ref(i)))),
      Function("OnlyEntry", IrType.I64, [i], new IrReturn(new IrArrayLoad(new IrConstRef(one), Ref(i)))),
      Function("Copied", IrType.Array(IrType.U8), [i],
        new IrDeclare(t, new IrNewArray(IrType.Array(IrType.U8), Int(4))),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayCopy, IrType.Void, [new IrConstRef(many), Ref(i), Ref(t), new IrConst(IrType.U32, 1), new IrConst(IrType.U32, 3)])),
        new IrReturn(Ref(t))),
      Function("Filled", IrType.Array(IrType.U8), [],
        new IrDeclare(t, new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [new IrConst(IrType.U8, 1), new IrConst(IrType.U8, 2), new IrConst(IrType.U8, 3)])),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayFill, IrType.Void, [Ref(t), new IrConst(IrType.U8, 250)])),
        new IrReturn(Ref(t))),
      Function("Empty", IrType.I32, [], new IrReturn(new IrArrayLength(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.F32), [])))),
      Function("Chosen", IrType.I32, [hiding], new IrReturn(new IrConditional(new IrConstRef(truth), Op(IrBinaryOp.Add, new IrConstRef(keyword), new IrConstRef(named)), Ref(hiding)))),
      Function("Discarded", IrType.Void, [i],
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I32, [Ref(i)])),
        new IrExprStmt(new IrConstRef(keyword)),
        new IrExprStmt(new IrIntrinsic(IrIntrinsicOp.ArrayClear, IrType.Void, [new IrNewArray(IrType.Array(IrType.I32), Ref(i))]))),
      Function("AbsOfSmallest", IrType.I8, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Abs, IrType.I8, [new IrConst(IrType.I8, -128)]))),
      Function("SignOfNaN", IrType.I32, [], new IrReturn(new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [Real(double.NaN)])))
    ]);
    calls.AddRange([
      Call("Lengths"), Call("NoEntry", 0), Call("OnlyEntry", 0), Call("OnlyEntry", 1), Call("Copied", 0), Call("Copied", 253), Call("Copied", 254), Call("Copied", -1),
      Call("Filled"), Call("Empty"), Call("Chosen", 1), Call("Discarded", 3), Call("Discarded", int.MinValue), Call("Discarded", -1), Call("AbsOfSmallest"), Call("SignOfNaN")
    ]);

    var module = new IrModule("Tables", functions, constants);
    var text = EmittedCSharp.WithStatementBodies(Emit(module));
    Assert.That(text, Does.Contain("\n  private const sbyte Lowi8 = -128;\n")
      .And.Contain("\n  private const ulong Highu64 = 18446744073709551615;\n")
      .And.Contain("\n  private static readonly long[] Tablei64 = {\n    -9223372036854775808, 0, 1, 9223372036854775807, 2, 3, 4, 5,\n    6\n  };\n")
      .And.Contain("\n  private const float Realf320 = float.NaN;\n")
      .And.Contain("\n  private const double Realf642 = double.NegativeInfinity;\n")
      .And.Contain("\n  private const double Realf643 = -0.0;\n")
      .And.Contain("\n  private const bool Enabled = true;\n")
      .And.Contain("\n  private static readonly double[] None = { };\n")
      .And.Contain("\n  private static readonly long[] One = { -9223372036854775808 };\n")
      .And.Contain("\n  private const int @class = 7;\n")
      .And.Contain("\n  private const int Tables_ = 8;\n")
      .And.Contain("global::System.Array.Copy(Many, i, t, 1u, 3u);")
      .And.Contain("var t = new byte[] { 1, 2, 3 };")
      .And.Contain("global::System.Array.Fill(t, (byte)250);")
      .And.Contain("return (new float[] { }).Length;")
      .And.Contain("public static int Chosen(int Enabled_) {")
      .And.Contain("return Enabled ? (@class + Tables_) : Enabled_;")
      .And.Contain("    _ = global::System.Math.Abs(i);\n    _ = @class;\n    global::System.Array.Clear(new int[i]);\n")
      .And.Contain("return global::System.Math.Abs((sbyte)(-128));")
      .And.Contain("return -(-0.0);"));

    var outcomes = Outcomes(module, calls);
    Assert.That((outcomes["Lengths()"], outcomes["NoEntry(0)"], outcomes["OnlyEntry(0)"], outcomes["Copied(253)"], outcomes["Copied(254)"], outcomes["Filled()"]),
      Is.EqualTo(("356", "trap: index out of range", "-9223372036854775808", "[0,253,254,255]", "trap: index out of range", "[250,250,250]")));
    Assert.That((outcomes["Chosen(1)"], outcomes["Discarded(3)"], outcomes["Discarded(-2147483648)"], outcomes["AbsOfSmallest()"], outcomes["SignOfNaN()"]),
      Is.EqualTo(("15", "void", "trap: integer overflow", "trap: integer overflow", "trap: invalid operation")));
    Assert.That((outcomes["Realf64Constant3()"], outcomes["Realf32Literal7()"], outcomes["Realf64Negated3()"], outcomes["Realf32(11)"]),
      Is.EqualTo(("f:8000000000000000", "f:36a0000000000000", "0", "16777216")));
  }

  /// <summary>Test design 12: an intrinsic operation the backend has no method for is refused by name, as a later IR may add one.</summary>
  [Test]
  public void UnknownIntrinsic_IsRefused() {
    var a = new IrLocal("a", IrType.I32);
    var module = new IrModule("M", [Function("F", IrType.I32, [a], new IrReturn(new IrIntrinsic((IrIntrinsicOp)99, IrType.I32, [Ref(a)])))]);
    Assert.That(CSharpEmitter.Check(module).Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY1052: '99' is not supported by the C# backend yet" }));
    Assert.That(() => CSharpEmitter.Emit(module), Throws.ArgumentException.With.Message.Contains("'99' is not supported by the C# backend yet"));
  }

  /// <summary>A constant of a type the backend does not know is refused like a local of that type.</summary>
  [Test]
  public void UnknownTypeOfAConstant_IsRefused() {
    var unheard = (IrType)Activator.CreateInstance(typeof(IrType), BindingFlags.Instance | BindingFlags.NonPublic, null, [(IrTypeKind)99, null], null)!;
    var module = new IrModule("M", [], [new IrModuleConst("N", unheard, [1])]);
    Assert.That(CSharpEmitter.Check(module).Select(problem => problem.ToString()), Is.EqualTo(new[] { "error PLY1052: '99' is not supported by the C# backend yet" }));
  }

}
