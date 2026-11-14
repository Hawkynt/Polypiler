using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Integer;

/// <summary>
/// The members of the base library the C# frontend imports as integer operations, as the table
/// <see cref="CSharpIntegerApi"/> lists them. Every entry has calls here; each is imported, the IR is
/// checked to hold the construct the table names, and on the edges of its operands the interpreter has
/// the outcome of the same source as the C# compiler builds it for .NET - except where the table's
/// note says the source throws another exception than the trap of the IR, where both have to trap.
/// </summary>
[TestFixture]
public sealed class CSharpIntegerApiTests {

  private const string Bits = "System.Numerics.BitOperations";
  private const string Binary = "System.Buffers.Binary.BinaryPrimitives";

  private static readonly string[] _all = ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong"];
  private static readonly string[] _signed = ["sbyte", "short", "int", "long"];
  private static readonly string[] _sized = ["Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];

  private static readonly Dictionary<string, IrType> _types = new() {
    ["sbyte"] = IrType.I8, ["byte"] = IrType.U8, ["short"] = IrType.I16, ["ushort"] = IrType.U16,
    ["int"] = IrType.I32, ["uint"] = IrType.U32, ["long"] = IrType.I64, ["ulong"] = IrType.U64
  };

  private static string Keyword(string sized) => sized switch {
    "Int16" => "short",
    "UInt16" => "ushort",
    "Int32" => "int",
    "UInt32" => "uint",
    "Int64" => "long",
    _ => "ulong"
  };

  /// <summary>One method per use: the type and member of the table it uses, and its source.</summary>
  private static IEnumerable<(string Type, string Member, string Method)> Uses() {
    foreach (var name in new[] { "RotateLeft", "RotateRight" }) {
      foreach (var type in new[] { "uint", "ulong" })
        yield return (Bits, name, $"public static {type} F({type} a, int b) => {Bits}.{name}(a, b);");

      foreach (var type in _all)
        yield return (type, name, $"public static {type} F({type} a, int b) => {type}.{name}(a, b);");
    }

    foreach (var name in new[] { "PopCount", "LeadingZeroCount", "TrailingZeroCount", "Log2" }) {
      foreach (var type in name == "TrailingZeroCount" ? new[] { "int", "uint", "long", "ulong" } : ["uint", "ulong"])
        yield return (Bits, name, $"public static int F({type} a) => {Bits}.{name}(a);");

      foreach (var type in _all)
        yield return (type, name, $"public static {type} F({type} a) => {type}.{name}(a);");
    }

    foreach (var type in _all)
      yield return (Binary, "ReverseEndianness", $"public static {type} F({type} a) => {Binary}.ReverseEndianness(a);");

    foreach (var type in new[] { "int", "uint", "long", "ulong" })
      yield return (Bits, "IsPow2", $"public static bool F({type} a) => {Bits}.IsPow2(a);");

    foreach (var type in _all)
      yield return (type, "IsPow2", $"public static bool F({type} a) => {type}.IsPow2(a);");

    foreach (var type in new[] { "uint", "ulong" })
      yield return (Bits, "RoundUpToPowerOf2", $"public static {type} F({type} a) => {Bits}.RoundUpToPowerOf2(a);");

    yield return ("System.Math", "BigMul", "public static long F(int a, int b) => System.Math.BigMul(a, b);");
    yield return ("System.Math", "BigMul", "public static ulong F(uint a, uint b) => System.Math.BigMul(a, b);");
    foreach (var type in new[] { "long", "ulong" })
      yield return ("System.Math", "BigMul", $"public static {type} F({type} a, {type} b) {{ {type} high = System.Math.BigMul(a, b, out {type} low); return high ^ low; }}");

    yield return ("System.Math", "BigMul", "public static ulong F(ulong a, ulong b) { ulong low; ulong high; high = System.Math.BigMul(a, b, out low); return high ^ (low >> 1); }");
    yield return ("System.Math", "BigMul", "public static long F(long a, long b) { long low = 0; System.Math.BigMul(a, b, out low); return low; }");
    foreach (var type in new[] { "int", "long" }) {
      yield return ("System.Math", "DivRem", $"public static {type} F({type} a, {type} b) {{ {type} q = System.Math.DivRem(a, b, out {type} r); return q * 31 + r; }}");
      yield return ("System.Math", "DivRem", $"public static {type} F({type} a, {type} b) {{ {type} r; {type} q; q = System.Math.DivRem(a, b, out r); return q * 31 + r; }}");
      yield return ("System.Math", "DivRem", $"public static {type} F({type} a, {type} b) {{ {type} r = 7; System.Math.DivRem(a, b, out r); return r; }}");
    }

    foreach (var type in _all) {
      yield return ("System.Math", "DivRem", $"public static {type} F({type} a, {type} b) {{ var (q, r) = System.Math.DivRem(a, b); return ({type})(q * 31 + r); }}");
      yield return (type, "DivRem", $"public static {type} F({type} a, {type} b) {{ var (q, r) = {type}.DivRem(a, b); return ({type})(q * 31 + r); }}");
      yield return (type, "DivRem", $"public static {type} F({type} a, {type} b) {{ {type} q = 1; {type} r = 2; (q, r) = {type}.DivRem(a, b); return ({type})(q * 31 + r); }}");
      yield return (type, "DivRem", $"public static {type} F({type} a, {type} b) {{ ({type} q, {type} r) = {type}.DivRem(a, b); return ({type})(q * 31 + r); }}");
      yield return (type, "DivRem", $"public static {type} F({type} a, {type} b) {{ var (q, _) = {type}.DivRem(a, b); return q; }}");
      yield return ("System.Math", "Clamp", $"public static {type} F({type} a, {type} b, {type} c) => System.Math.Clamp(a, b, c);");
      yield return (type, "Clamp", $"public static {type} F({type} a, {type} b, {type} c) => {type}.Clamp(a, b, c);");
      yield return (type, "Min", $"public static {type} F({type} a, {type} b) => {type}.Min(a, b);");
      yield return (type, "Max", $"public static {type} F({type} a, {type} b) => {type}.Max(a, b);");
      yield return (type, "Sign", $"public static int F({type} a) => {type}.Sign(a);");
      foreach (var source in new[] { "sbyte", "int", "uint", "long", "ulong" }) {
        yield return (type, "CreateSaturating", $"public static {type} F({source} a) => {type}.CreateSaturating(a);");
        yield return (type, "CreateTruncating", $"public static {type} F({source} a) => {type}.CreateTruncating(a);");
        yield return (type, "CreateChecked", $"public static {type} F({source} a) => {type}.CreateChecked(a);");
      }

      yield return (type, "CreateSaturating", $"public static {type} F(double a) => {type}.CreateSaturating(a);");
      yield return (type, "CreateChecked", $"public static {type} F(double a) => {type}.CreateChecked(a);");
    }

    foreach (var type in _signed)
      yield return (type, "Abs", $"public static {type} F({type} a) => {type}.Abs(a);");

    foreach (var sized in _sized) {
      var type = Keyword(sized);
      var size = _types[type].Bits / 8;
      foreach (var order in new[] { "Little", "Big" }) {
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a, int b) => {Binary}.Read{sized}{order}Endian(a.AsSpan(b));");
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a, int b) => {Binary}.Read{sized}{order}Endian(System.MemoryExtensions.AsSpan(a, b, {size}));");
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a, int b) => {Binary}.Read{sized}{order}Endian(a.AsSpan(b, {size}));");
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a) => {Binary}.Read{sized}{order}Endian(a);");
        yield return (Binary, $"Write{sized}{order}Endian", $"public static byte[] F(byte[] a, int b, {type} c) {{ {Binary}.Write{sized}{order}Endian(a.AsSpan(b), c); return a; }}");
        yield return (Binary, $"Write{sized}{order}Endian", $"public static byte[] F(byte[] a, {type} c) {{ {Binary}.Write{sized}{order}Endian(a, ({type})(c ^ 1)); return a; }}");
      }

      yield return ("System.BitConverter", $"To{sized}", $"public static {type} F(byte[] a, int b) => System.BitConverter.To{sized}(a, b);");
      // A stored value made of operators that cannot trap: a choice, a negation, a complement, a conversion.
      yield return (Binary, $"Write{sized}BigEndian", $"public static byte[] F(byte[] a, int c) {{ {Binary}.Write{sized}BigEndian(a, ({type})(c > 0 ? -c : ~c)); return a; }}");
      yield return ("System.BitConverter", $"To{sized}", $"public static {type} F(byte[] a) => System.BitConverter.To{sized}(a.AsSpan());");
    }

    // The floating-point numbers of #1313, as their encoding.
    foreach (var (sized, type, size) in new[] { ("Single", "float", 4), ("Double", "double", 8) }) {
      foreach (var order in new[] { "Little", "Big" }) {
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a, int b) => {Binary}.Read{sized}{order}Endian(a.AsSpan(b));");
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a, int b) => {Binary}.Read{sized}{order}Endian(System.MemoryExtensions.AsSpan(a, b, {size}));");
        yield return (Binary, $"Read{sized}{order}Endian", $"public static {type} F(byte[] a) => {Binary}.Read{sized}{order}Endian(a);");
        yield return (Binary, $"Write{sized}{order}Endian", $"public static byte[] F(byte[] a, int b, {type} c) {{ {Binary}.Write{sized}{order}Endian(a.AsSpan(b, {size}), c); return a; }}");
        yield return (Binary, $"Write{sized}{order}Endian", $"public static byte[] F(byte[] a, {type} c) {{ {Binary}.Write{sized}{order}Endian(a, -c); return a; }}");
      }

      yield return ("System.BitConverter", $"To{sized}", $"public static {type} F(byte[] a, int b) => System.BitConverter.To{sized}(a, b);");
    }
  }

  /// <summary>A method as the one class of a source file that has <c>System</c> in scope, which <c>AsSpan</c> needs.</summary>
  private static string Wrap(string method) => "using System; " + PrototypeFixture.Wrap(method);

  private static IEnumerable<TestCaseData> Methods() => Uses().Select(use => new TestCaseData(use.Type, use.Member, use.Method).SetArgDisplayNames(use.Method));

  /// <summary>The values a parameter is tried with: the edges of an integer type, some numbers, a byte array of three lengths.</summary>
  private static object[] Operands(IrType type) => type.Kind switch {
    IrTypeKind.Array => [new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0x80, 0x7F }, new byte[] { 0xFF, 0x00 }, new byte[8]],
    IrTypeKind.F64 => [0.0, -1.5, 255.75, 1e10, -1e19, 1e20, double.NaN, double.PositiveInfinity],
    IrTypeKind.F32 => [0.0f, -0.0f, -1.5f, 0.1f, float.Epsilon, float.MaxValue, float.NaN, float.NegativeInfinity],
    IrTypeKind.I32 => [.. IntegerCases.Edges(IrType.I32), -1, 4, 9, 13, 33],
    _ => IntegerCases.Edges(type)
  };

  /// <summary>
  /// Test design 3: every member of the table has a use; the use is imported into the construct the
  /// table names; and on every combination of the edges of its operands the interpreter has the
  /// outcome .NET has, a documented difference in the kind of a trap aside.
  /// </summary>
  [TestCaseSource(nameof(Methods))]
  public void Member_IsTheOperationTheTableNames_AndComputesWhatDotNetDoes(string type, string member, string method) {
    var entry = CSharpIntegerApi.Find(type, member);
    Assert.That(entry, Is.Not.Null, $"{type}.{member} is not in the table");
    var source = Wrap(method);
    var module = PrototypeFixture.Import("Snippet.cs", source);
    var function = module.Functions.Single();
    var constructs = Constructs(function.Body).ToList();
    var forms = entry!.Ir.Split(" | ").Select(form => form.Split(", ")).ToList();
    // A conversion to the type the value already has is no construct: the value is returned as it is.
    var identity = constructs.Count == 0 && function.Parameters.Count == 1 && function.Parameters[0].Type == function.ReturnType;
    Assert.That(identity || forms.Any(form => form.All(constructs.Contains)), Is.True, $"{method} is {string.Join(", ", constructs)}, not {entry.Ir}");

    var compiled = GeneratedCSharp.Compile(source, false);
    IEnumerable<object[]> tuples = [[]];
    foreach (var parameter in module.Functions.Single().Parameters)
      tuples = tuples.SelectMany(tuple => Operands(parameter.Type).Select(operand => (object[])[.. tuple, operand])).ToList();

    var mismatches = new List<string>();
    var calls = 0;
    foreach (var tuple in tuples) {
      var call = new PrototypeCall("F", tuple);
      var expected = compiled.Outcome(call);
      var interpreted = PrototypeFixture.InterpretedOutcome(module, call);
      ++calls;
      var bothTrap = expected.StartsWith("trap:", StringComparison.Ordinal) && interpreted.StartsWith("trap:", StringComparison.Ordinal);
      if (interpreted != expected && !(bothTrap && entry.Note.Contains("where .NET throws")) && !(bothTrap && entry.Note.Contains("ArgumentOutOfRangeException")))
        mismatches.Add($"{call}: interpreter {interpreted}, .NET {expected}");
    }

    Assert.That(mismatches.Take(10), Is.Empty, $"{mismatches.Count} of {calls} calls differ:\n{method}");
  }

  /// <summary>Every member of the table has a use above.</summary>
  [Test]
  public void EveryMember_HasAUse()
    => Assert.That(Uses().Select(use => (use.Type, use.Member)).Distinct(), Is.EquivalentTo(CSharpIntegerApi.Members.Select(member => (member.Type, member.Member))));

  /// <summary>The names of the intrinsic operations, binary operators and conversions below a statement.</summary>
  private static IEnumerable<string> Constructs(IrStmt statement) => statement switch {
    IrBlock block => block.Statements.SelectMany(Constructs),
    IrDeclare { Initializer: { } value } => Constructs(value),
    IrAssign assign => Constructs(assign.Value),
    IrReturn { Value: { } value } => Constructs(value),
    IrExprStmt expression => Constructs(expression.Expression),
    _ => []
  };

  private static IEnumerable<string> Constructs(IrExpr expression) => expression switch {
    IrIntrinsic intrinsic => intrinsic.Arguments.SelectMany(Constructs).Prepend(intrinsic.Op.ToString()),
    IrBinary binary => Constructs(binary.Left).Concat(Constructs(binary.Right)).Prepend(binary.Op.ToString()),
    IrConvert conversion => Constructs(conversion.Operand).Prepend("Convert"),
    IrUnary unary => Constructs(unary.Operand),
    _ => []
  };

  /// <summary>
  /// Test design 10: what the table does not list, or a form of an argument it does not take, is
  /// refused by name: a member of BitOperations the IR has no operation for, a span that is not the
  /// array or its slice of the size of the value, a stored value that could trap, a pair that is
  /// not a statement, a result that is not a local.
  /// </summary>
  [TestCase("public static uint F(uint a) => System.Numerics.BitOperations.Crc32C(a, a);", "BitOperations.Crc32C")]
  [TestCase("public static int F(byte[] a, int b) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(b, 2));", "the bytes of BinaryPrimitives.ReadInt32LittleEndian")]
  [TestCase("public static int F(byte[] a, int b, int c) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(b, c));", "the bytes of BinaryPrimitives.ReadInt32LittleEndian")]
  [TestCase("public static int F(byte[] a) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new System.ReadOnlySpan<byte>(a));", "the bytes of BinaryPrimitives.ReadInt32LittleEndian")]
  [TestCase("public static void F(byte[] a, int b, int c) { System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(b), 10 / c); }", "with a value that can trap")]
  [TestCase("public static int F(int[] a) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));", "BinaryPrimitives.ReadInt32LittleEndian")]
  [TestCase("public static int F(int a, int b) => System.Math.DivRem(a, b).Quotient;", "SimpleMemberAccessExpression")]
  [TestCase("public static int F(int a, int b) => System.Math.DivRem(a, b, out a);", "Math.DivRem")]
  [TestCase("public static int F(int a, int b, int[] c) { int q = System.Math.DivRem(a, b, out c[0]); return q; }", "a result stored in ElementAccessExpression")]
  [TestCase("public static double F(double a) => System.Math.ReciprocalEstimate(a);", "Math.ReciprocalEstimate")]
  [TestCase("public static int F(double a) => int.CreateTruncating(a);", "Int32.CreateTruncating")]
  public void Unmapped_IsRefusedByName(string method, string named) {
    var result = CSharpFrontend.Import("Snippet.cs", Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Has.All.EqualTo(CSharpFrontend.Unsupported));
    Assert.That(result.Diagnostics.Single().Message, Does.Contain(named));
  }

}
