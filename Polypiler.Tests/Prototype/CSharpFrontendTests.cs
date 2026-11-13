using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.CSharp;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Prototype;

[TestFixture]
public sealed class CSharpFrontendTests {

  private static Diagnostic Refusal(string source) {
    var result = CSharpFrontend.Import("Snippet.cs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1), string.Join("; ", result.Diagnostics));
    return result.Diagnostics[0];
  }

  /// <summary>Every fixture imports into IR the verifier accepts.</summary>
  [TestCaseSource(typeof(PrototypeFixture), nameof(PrototypeFixture.Names))]
  public void Fixture_ImportsIntoWellTypedIr(string fixture) {
    var result = CSharpFrontend.Import(PrototypeFixture.ClassOf(fixture) + ".cs", PrototypeFixture.SourceOf(fixture));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Name, Is.EqualTo(PrototypeFixture.ClassOf(fixture)));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  [Test]
  public void Import_KeepsNamesTypesAndVisibility() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static uint F(byte[] data, int n) { uint x = 1; return x; } "
      + "private static void G(bool b, uint[] u, int[] i) { } internal static byte H() => 1;"));
    var f = module.Functions[0];
    Assert.That(module.Functions.Select(function => (function.Name, function.IsPublic, function.ReturnType)),
      Is.EqualTo(new[] { ("F", true, IrType.U32), ("G", false, IrType.Void), ("H", false, IrType.U8) }));
    Assert.That(f.Parameters, Is.EqualTo(new[] { new IrLocal("data", IrType.Array(IrType.U8)), new IrLocal("n", IrType.I32) }));
    Assert.That(f.Locals, Is.EqualTo(new[] { new IrLocal("x", IrType.U32) }));
    Assert.That(module.Functions[1].Parameters.Select(parameter => parameter.Type.ToString()), Is.EqualTo(new[] { "bool", "u32[]", "i32[]" }));
  }

  /// <summary>A namespace around the class, in either form, and using directives do not matter.</summary>
  [TestCase("namespace A.B; public static class C { public static int F() => 1; }")]
  [TestCase("using System; namespace A { public static class C { public static int F() => 1; } }")]
  [TestCase("public static class C { public static int F() => 1; }")]
  public void Import_AcceptsTheClassInsideANamespace(string source)
    => Assert.That(PrototypeFixture.Import("C.cs", source).Name, Is.EqualTo("C"));

  /// <summary>Byte arithmetic is promoted to 32 bits and truncated only where the source says so.</summary>
  [Test]
  public void Import_MakesPromotionAndTruncationExplicit() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap("public static byte F(byte a, byte b) => (byte)(a + b);"));
    var result = (IrReturn)module.Functions[0].Body.Statements[0];
    var a = new IrLocalRef(new IrLocal("a", IrType.U8));
    var b = new IrLocalRef(new IrLocal("b", IrType.U8));
    Assert.That(result.Value, Is.EqualTo(
      new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I32, a), new IrConvert(IrType.I32, b)))));
  }

  /// <summary>A constant takes the type the compiler converts it to, with its value wrapped exactly.</summary>
  [TestCase("public static uint F() => unchecked((uint)-1);", IrTypeKind.U32, 4294967295)]
  [TestCase("public static byte F() => unchecked((byte)0x1FF);", IrTypeKind.U8, 255)]
  [TestCase("public static int F() => 1 << 33;", IrTypeKind.I32, 2)]
  [TestCase("public static uint F() => 7;", IrTypeKind.U32, 7)]
  [TestCase("public static bool F() => !false;", IrTypeKind.Bool, 1)]
  [TestCase("public static long F() => 7;", IrTypeKind.I64, 7)]
  [TestCase("public static long F() => long.MinValue;", IrTypeKind.I64, long.MinValue)]
  [TestCase("public static ulong F() => ulong.MaxValue;", IrTypeKind.U64, -1)]
  [TestCase("public static ulong F() => 0x8000000000000000;", IrTypeKind.U64, long.MinValue)]
  [TestCase("public static char F() => 'a';", IrTypeKind.U16, 97)]
  [TestCase("public static int F() => 'a' + 1;", IrTypeKind.I32, 98)]
  [TestCase("public static sbyte F() => sbyte.MinValue;", IrTypeKind.I8, -128)]
  [TestCase("public static short F() => unchecked((short)0x8000);", IrTypeKind.I16, -32768)]
  [TestCase("public static double F() => 1.5;", IrTypeKind.F64, 0x3FF8000000000000)]
  [TestCase("public static float F() => 1.5f;", IrTypeKind.F32, 0x3FC00000)]
  [TestCase("public static float F() => 1;", IrTypeKind.F32, 0x3F800000)]
  [TestCase("public static double F() => 0.5f;", IrTypeKind.F64, 0x3FE0000000000000)]
  [TestCase("public static float F() => ulong.MaxValue;", IrTypeKind.F32, 0x5F800000)]
  [TestCase("public static double F() => ulong.MaxValue;", IrTypeKind.F64, 0x43F0000000000000)]
  [TestCase("public static double F() => long.MinValue;", IrTypeKind.F64, unchecked((long)0xC3E0000000000000))]
  [TestCase("public static float F() => -16777217;", IrTypeKind.F32, unchecked((int)0xCB800000))]
  [TestCase("public static double F() => double.NaN;", IrTypeKind.F64, unchecked((long)0xFFF8000000000000))]
  [TestCase("public static int F() => (int)2.9;", IrTypeKind.I32, 2)]
  public void Import_TakesConstantsFromTheCompiler(string method, IrTypeKind kind, long value) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    var constant = (IrConst)((IrReturn)module.Functions[0].Body.Statements[0]).Value!;
    Assert.That((constant.Type.Kind, constant.Value), Is.EqualTo((kind, value)));
  }

  /// <summary>
  /// Test design 11: every construct the issue lists as out of scope is refused with PLY1002 and the
  /// position of the construct; the snippets are valid C#, so the compiler does not get there first.
  /// </summary>
  [TestCase("public static decimal F(decimal a) => a + 1;", "decimal", 15, TestName = "decimal")]
  [TestCase("public static System.Half F(System.Half a) => a;", "System.Half", 15, TestName = "Half")]
  [TestCase("public static System.Int128 F(System.Int128 a) => a;", "System.Int128", 15, TestName = "Int128")]
  [TestCase("public static nint F(nint a) => a;", "nint", 15, TestName = "nint")]
  [TestCase("public static int F(int[] a, long i) => a[i];", "an array index or length of the type i64", 43, TestName = "64-bit index")]
  [TestCase("public static void F(int[] a, ulong i) { a[i] = 1; }", "an array index or length of the type u64", 44, TestName = "64-bit index of a write")]
  [TestCase("public static int[] F(long n) => new int[n];", "an array index or length of the type i64", 42, TestName = "64-bit length")]
  [TestCase("public static string F() => \"a\";", "string", 15, TestName = "string")]
  [TestCase("public static bool F(char c) => char.IsDigit(c);", "InvocationExpression", 33, TestName = "char text operation")]
  [TestCase("public static int F() { int s = 0; foreach (int x in new System.Collections.Generic.List<int>()) s += x; return s; }", "System.Collections.Generic.List<int>", 54, TestName = "foreach over a list")]
  [TestCase("public static int F(int a) { switch (a) { case > 1: return 1; default: return 0; } }", "CasePatternSwitchLabel", 43, TestName = "switch with a pattern")]
  [TestCase("public static int F(int a) { switch (a) { case 1: goto default; default: return 0; } }", "GotoDefaultStatement", 51, TestName = "goto default")]
  [TestCase("public static int F(int a) { switch (a) { case 1: goto case 2; case 2: return 0; } return 1; }", "GotoCaseStatement", 51, TestName = "goto case")]
  [TestCase("public static int F(bool a) { switch (a) { case true: return 1; default: return 0; } }", "a switch on the type bool", 39, TestName = "switch on a truth value")]
  [TestCase("public static int F(double a) { switch (a) { case 1.5: return 1; default: return 0; } }", "a switch on the type f64", 41, TestName = "switch on a float")]
  [TestCase("public static int F(int a) { const string s = \"x\"; return a; }", "string", 36, TestName = "local constant of a type outside the subset")]
  [TestCase("public static int F(int a) => (a)++;", "ParenthesizedExpression", 31, TestName = "step of something that is not a variable")]
  [TestCase("public static int F(int a) => a switch { _ => 1 };", "SwitchExpression", 31, TestName = "switch expression")]
  [TestCase("public static int F(int a) { goto end; end: return a; }", "GotoStatement", 30, TestName = "goto")]
  [TestCase("public static int F(int[,] a) => 1;", "int[*,*]", 21, TestName = "multi-dimensional array")]
  [TestCase("public static int F(int[][] a) => 1;", "int[][]", 21, TestName = "jagged array")]
  [TestCase("public static int F() { int[] a = new int[2] { 1, 2 }; return a[0]; }", "ArrayCreationExpression", 35, TestName = "array initializer")]
  [TestCase("public static int F(System.Span<int> a) => 1;", "System.Span<int>", 21, TestName = "span")]
  [TestCase("public static int F<T>(T a) => 1;", "a generic method", 20, TestName = "generic method")]
  [TestCase("public static int F() { System.Func<int, int> f = x => x; return 1; }", "System.Func<int, int>", 25, TestName = "lambda")]
  [TestCase("public static int F() { int G() => 1; return G(); }", "LocalFunctionStatement", 25, TestName = "local function")]
  [TestCase("public static int F() { throw new System.Exception(); }", "ThrowStatement", 25, TestName = "throw")]
  [TestCase("public static int F() { try { return 1; } finally { } }", "TryStatement", 25, TestName = "try")]
  [TestCase("public static int F(ref int a) => a;", "ref", 21, TestName = "ref parameter")]
  [TestCase("public static void F(out int a) { a = 1; }", "out", 22, TestName = "out parameter")]
  [TestCase("public static int F(params int[] a) => 1;", "params", 21, TestName = "params parameter")]
  [TestCase("public static int F(int a = 1) => a;", "a default parameter value", 27, TestName = "default parameter value")]
  [TestCase("public static int F(this int a) => a;", "this", 21, TestName = "extension method")]
  [TestCase("public static int Field;", "a class member other than a static method", 1, TestName = "field")]
  [TestCase("public static int Property => 1;", "a class member other than a static method", 1, TestName = "property")]
  [TestCase("public static extern int F();", "extern", 1, TestName = "extern")]
  [TestCase("public static int F() => 1; public static int F(int a) => a;", "method overloading", 29, TestName = "overload")]
  [TestCase("public static int F() { using var m = new System.IO.MemoryStream(); return 1; }", "using", 25, TestName = "modifier of a local")]
  [TestCase("public static int F(int a) { int b = a = 1; return b; }", "SimpleAssignmentExpression", 38, TestName = "assignment as a value")]
  [TestCase("public static int F(object o) => 1;", "object", 21, TestName = "object")]
  [TestCase("public static void F() { new object(); }", "ObjectCreationExpression", 26, TestName = "object creation statement")]
  [TestCase("public static int F(int a) => System.Math.Abs(a);", "InvocationExpression", 31, TestName = "call outside the class")]
  [TestCase("public static int G(int a) => a; public static int F(int a) => G(a: a);", "InvocationExpression", 64, TestName = "named argument")]
  [TestCase("public static bool F(bool a, bool b) => a & b;", "BitwiseAndExpression on bool and bool", 41, TestName = "bitwise and of truth values")]
  [TestCase("public static bool F(bool a, bool b) { a |= b; return a; }", "OrAssignmentExpression on bool and bool", 40, TestName = "compound assignment of truth values")]
  [TestCase("public static bool F(int[] a, int[] b) => a == b;", "object", 43, TestName = "array identity")]
  [TestCase("public static int F(int[] a) => a?.Length ?? 0;", "CoalesceExpression", 33, TestName = "null-conditional")]
  [TestCase("public static int[] F() => null;", "NullLiteralExpression", 28, TestName = "null")]
  [TestCase("public static void F(int[] a) { (a) = a; }", "ParenthesizedExpression", 33, TestName = "parenthesized target")]
  [TestCase("public static void F(int[] a) { a.Length.ToString(); }", "string", 33, TestName = "member call")]
  [TestCase("public static int F(int a) => int.MaxValue.CompareTo(a);", "InvocationExpression", 31, TestName = "call on a value")]
  [TestCase("public static int F(int? a) { a ??= 1; return 1; }", "int?", 21, TestName = "nullable")]
  [TestCase("public static void F(int[] a) { a ??= new int[1]; }", "CoalesceAssignmentExpression", 33, TestName = "coalescing assignment")]
  public void UnsupportedConstruct_IsRefusedWithItsPosition(string method, string what, int column) {
    var diagnostic = Refusal(PrototypeFixture.Wrap(method));
    Assert.That(diagnostic, Is.EqualTo(new Diagnostic(
      "PLY1002", DiagnosticSeverity.Error, $"'{what}' is not supported yet", "Snippet.cs", 1, column + 30)));
  }

  /// <summary>What a source file may consist of.</summary>
  [TestCase("public class C { public static int F() => 1; }", "a class that is not static", 1, 1)]
  [TestCase("public static class C<T> { public static int F() => 1; }", "a generic class", 1, 22)]
  [TestCase("public struct S { }", "a file that is not exactly one static class", 1, 1)]
  [TestCase("public static class A { }\npublic static class B { }", "a file that is not exactly one static class", 2, 1)]
  [TestCase("", "a file that is not exactly one static class", 1, 1)]
  [TestCase("namespace N;\n", "a file that is not exactly one static class", 1, 1)]
  [TestCase("public static class C {\n  public static class Inner { }\n}", "a class member other than a static method", 2, 3)]
  public void FileShape_IsRefusedWithItsPosition(string source, string what, int line, int column)
    => Assert.That(Refusal(source), Is.EqualTo(new Diagnostic(
      "PLY1002", DiagnosticSeverity.Error, $"'{what}' is not supported yet", "Snippet.cs", line, column)));

  private static IrModule Import(string methods) => PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(methods));

  private static IrExpr Returned(IrFunction function) => ((IrReturn)function.Body.Statements[^1]).Value!;

  /// <summary>Every numeric type of C#, and <c>char</c>, has its IR type, alone and as the element of an array.</summary>
  [TestCase("sbyte", "i8")]
  [TestCase("byte", "u8")]
  [TestCase("short", "i16")]
  [TestCase("ushort", "u16")]
  [TestCase("char", "u16")]
  [TestCase("int", "i32")]
  [TestCase("uint", "u32")]
  [TestCase("long", "i64")]
  [TestCase("ulong", "u64")]
  [TestCase("float", "f32")]
  [TestCase("double", "f64")]
  public void Import_MapsEveryNumericType(string keyword, string type) {
    var function = Import($"public static {keyword} F({keyword} a, {keyword}[] b) => a;").Functions[0];
    Assert.That(function.ReturnType.ToString(), Is.EqualTo(type));
    Assert.That(function.Parameters.Select(parameter => parameter.Type.ToString()), Is.EqualTo(new[] { type, type + "[]" }));
  }

  /// <summary>uint with int is computed in 64 bits, as C# computes it, and so is the negation of a uint.</summary>
  [Test]
  public void UIntMixedWithInt_IsWidenedTo64Bits() {
    var module = Import(
      "public static long F(uint a, int b) => a + b; public static long G(uint a) => -a; public static bool H(uint a, int b) => a < b;");
    IrExpr a = new IrLocalRef(new IrLocal("a", IrType.U32));
    IrExpr b = new IrLocalRef(new IrLocal("b", IrType.I32));
    Assert.That(Returned(module.Functions[0]), Is.EqualTo(new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I64, a), new IrConvert(IrType.I64, b))));
    Assert.That(Returned(module.Functions[1]), Is.EqualTo(new IrUnary(IrUnaryOp.Negate, new IrConvert(IrType.I64, a))));
    Assert.That(Returned(module.Functions[2]), Is.EqualTo(new IrBinary(IrBinaryOp.Less, new IrConvert(IrType.I64, a), new IrConvert(IrType.I64, b))));
  }

  /// <summary>The nearest enclosing checked or unchecked decides whether an operator traps; division never changes.</summary>
  [TestCase("a + b", IrBinaryOp.Add)]
  [TestCase("checked(a + b)", IrBinaryOp.AddChecked)]
  [TestCase("checked(a - b)", IrBinaryOp.SubtractChecked)]
  [TestCase("checked(a * b)", IrBinaryOp.MultiplyChecked)]
  [TestCase("checked(a / b)", IrBinaryOp.Divide)]
  [TestCase("checked(a << b)", IrBinaryOp.ShiftLeft)]
  [TestCase("unchecked(a + b)", IrBinaryOp.Add)]
  [TestCase("checked(unchecked(a + b))", IrBinaryOp.Add)]
  [TestCase("unchecked(checked(a + b))", IrBinaryOp.AddChecked)]
  public void CheckedContext_SelectsTheTrappingOperator(string expression, IrBinaryOp op)
    => Assert.That(((IrBinary)Returned(Import($"public static int F(int a, int b) => {expression};").Functions[0])).Op, Is.EqualTo(op));

  /// <summary>A checked block reaches negation, steps, compound assignments and casts inside it.</summary>
  [Test]
  public void CheckedStatement_AppliesToEverythingInside() {
    var function = Import(
      "public static int F(int a, byte b) { checked { a = -a; b++; b += 1; unchecked { a += 1; } return (byte)a; } }").Functions[0];
    IrExpr a = new IrLocalRef(function.Parameters[0]);
    IrExpr b = new IrLocalRef(function.Parameters[1]);
    IrExpr one = new IrConst(IrType.I32, 1);
    var stepped = new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.AddChecked, new IrConvert(IrType.I32, b), one), IrConversion.Trap);
    Assert.That(((IrBlock)function.Body.Statements[0]).Statements, Is.EqualTo(new IrStmt[] {
      new IrAssign(function.Parameters[0], new IrUnary(IrUnaryOp.NegateChecked, a)),
      new IrAssign(function.Parameters[1], stepped),
      new IrAssign(function.Parameters[1], stepped),
      new IrBlock([new IrAssign(function.Parameters[0], new IrBinary(IrBinaryOp.Add, a, one))]),
      new IrReturn(new IrConvert(IrType.I32, new IrConvert(IrType.U8, a, IrConversion.Trap)))
    }).Using<IrStmt>(Same));
  }

  /// <summary>A block is compared by the statements in it: the list of a record has no equality of its own.</summary>
  private static bool Same(IrStmt left, IrStmt right)
    => left is IrBlock first && right is IrBlock second ? first.Statements.SequenceEqual(second.Statements) : left.Equals(right);

  /// <summary>
  /// A cast from floating point saturates as the runtime does, through int for a narrow target, and
  /// traps in a checked context; a cast to floating point has nothing to lose.
  /// </summary>
  [Test]
  public void FloatingPointCast_CarriesItsContract() {
    var module = Import(
      "public static int A(double d) => (int)d; public static byte B(double d) => (byte)d; public static ulong C(float d) => (ulong)d; "
      + "public static byte D(double d) => checked((byte)d); public static float E(double d) => checked((float)d); "
      + "public static double G(long d) => d; public static float H(float f) { f++; return f; } public static double I(int a) => a + 1.5;");
    IrExpr d = new IrLocalRef(new IrLocal("d", IrType.F64));
    IrExpr f = new IrLocalRef(new IrLocal("f", IrType.F32));
    Assert.That(Returned(module.Functions[0]), Is.EqualTo(new IrConvert(IrType.I32, d, IrConversion.Saturate)));
    Assert.That(Returned(module.Functions[1]), Is.EqualTo(new IrConvert(IrType.U8, new IrConvert(IrType.I32, d, IrConversion.Saturate))));
    Assert.That(Returned(module.Functions[2]), Is.EqualTo(new IrConvert(IrType.U64, new IrLocalRef(new IrLocal("d", IrType.F32)), IrConversion.Saturate)));
    Assert.That(Returned(module.Functions[3]), Is.EqualTo(new IrConvert(IrType.U8, d, IrConversion.Trap)));
    Assert.That(Returned(module.Functions[4]), Is.EqualTo(new IrConvert(IrType.F32, d)));
    Assert.That(Returned(module.Functions[5]), Is.EqualTo(new IrConvert(IrType.F64, new IrLocalRef(new IrLocal("d", IrType.I64)))));
    Assert.That(module.Functions[6].Body.Statements[0], Is.EqualTo(
      new IrAssign(new IrLocal("f", IrType.F32), new IrBinary(IrBinaryOp.Add, f, new IrConst(IrType.F32, 0x3F800000)))));
    Assert.That(Returned(module.Functions[7]), Is.EqualTo(new IrBinary(IrBinaryOp.Add,
      new IrConvert(IrType.F64, new IrLocalRef(new IrLocal("a", IrType.I32))), new IrConst(IrType.F64, 0x3FF8000000000000))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>
  /// A for without a continue is a while whose body ends with the step, as before. A continue of the
  /// loop itself - not one of a loop nested in it - keeps body and step apart, so that the step runs.
  /// </summary>
  [Test]
  public void For_KeepsItsStepApartOnlyWhenAContinueNeedsIt() {
    var module = Import(
      "public static void A(int n) { for (int i = 0; i < n; ++i) { } } "
      + "public static void B(int n) { for (int i = 0; i < n; ++i) { if (i == 1) continue; } } "
      + "public static void C(int n) { for (int i = 0; i < n; ++i) { while (i < 0) { continue; } } } "
      + "public static void D(int n) { for (int i = 0; i < n; ++i) switch (i) { case 1: continue; } } "
      + "public static void E(int n) { for (int i = 0; i < n; ++i) continue; }");
    IrStmt Loop(int function) => ((IrBlock)module.Functions[function].Body.Statements[0]).Statements[^1];
    Assert.That(Loop(0), Is.TypeOf<IrWhile>());
    Assert.That(((IrWhile)Loop(0)).Body.Statements, Has.Count.EqualTo(1));
    Assert.That(Loop(1), Is.TypeOf<IrFor>());
    var loop = (IrFor)Loop(1);
    var i = module.Functions[1].Locals[0];
    Assert.That(loop.Condition, Is.EqualTo(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), new IrLocalRef(module.Functions[1].Parameters[0]))));
    Assert.That(loop.Body.Statements.Select(statement => statement.GetType()), Is.EqualTo(new[] { typeof(IrIf) }));
    Assert.That(loop.Step.Statements, Is.EqualTo(new IrStmt[] {
      new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), new IrConst(IrType.I32, 1)))
    }));
    Assert.That(Loop(2), Is.TypeOf<IrWhile>());
    Assert.That(Loop(3), Is.TypeOf<IrFor>());
    Assert.That(Loop(4), Is.TypeOf<IrFor>());
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>A foreach reads the array once, counts in a temporary and converts each element as the source says.</summary>
  [Test]
  public void ForEach_BecomesACountedLoop() {
    var module = Import(
      "public static int[] G(int[] a) => a; "
      + "public static int F(int[] a) { int s = 0; foreach (byte x in G(a)) { if (x == 0) continue; s += x; } return s; } "
      + "public static int H(long[] a) { int s = 0; foreach (var x in a) s++; return s; }");
    var f = module.Functions[1];
    Assert.That(f.Locals.Select(local => (local.Name, local.Type.ToString())),
      Is.EqualTo(new[] { ("s", "i32"), ("$t1", "i32[]"), ("$t2", "i32"), ("x", "u8") }));
    var block = (IrBlock)f.Body.Statements[1];
    Assert.That(block.Statements.Select(statement => statement.GetType()), Is.EqualTo(new[] { typeof(IrDeclare), typeof(IrDeclare), typeof(IrFor) }));
    var loop = (IrFor)block.Statements[2];
    IrExpr index = new IrLocalRef(f.Locals[2]);
    IrExpr array = new IrLocalRef(f.Locals[1]);
    Assert.That(loop.Condition, Is.EqualTo(new IrBinary(IrBinaryOp.Less, index, new IrArrayLength(array))));
    Assert.That(loop.Body.Statements[0], Is.EqualTo(new IrDeclare(f.Locals[3], new IrConvert(IrType.U8, new IrArrayLoad(array, index)))));
    Assert.That(loop.Step.Statements, Is.EqualTo(new IrStmt[] { new IrAssign(f.Locals[2], new IrBinary(IrBinaryOp.Add, index, new IrConst(IrType.I32, 1))) }));

    var h = module.Functions[2];
    Assert.That(h.Locals.Select(local => local.Name), Is.EqualTo(new[] { "s", "$t1", "x" }));
    Assert.That(((IrBlock)h.Body.Statements[1]).Statements[^1], Is.TypeOf<IrWhile>());
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>
  /// A switch keeps its labels and its default; a local declared in a section is declared in front of
  /// the switch, because the other sections may use it.
  /// </summary>
  [Test]
  public void Switch_KeepsLabelsAndMovesDeclarationsInFront() {
    var f = Import(
      "public static int F(char c) { switch (c) { case 'a': case 'b': int x = 1; return x; default: x = 2; int y; y = x; return y; case 'c': break; } return 0; }")
      .Functions[0];
    Assert.That(f.Body.Statements.Select(statement => statement.GetType()),
      Is.EqualTo(new[] { typeof(IrDeclare), typeof(IrDeclare), typeof(IrSwitch), typeof(IrReturn) }));
    Assert.That(f.Body.Statements.Take(2), Is.EqualTo(new IrStmt[] { new IrDeclare(f.Locals[0], null), new IrDeclare(f.Locals[1], null) }));
    var choice = (IrSwitch)f.Body.Statements[2];
    Assert.That(choice.Value, Is.EqualTo(new IrLocalRef(f.Parameters[0])));
    Assert.That(choice.Sections.Select(section => (string.Join(",", section.Labels), section.IsDefault)),
      Is.EqualTo(new[] { ("97,98", false), ("", true), ("99", false) }));
    Assert.That(choice.Sections[0].Body.Statements[0], Is.EqualTo(new IrAssign(f.Locals[0], new IrConst(IrType.I32, 1))));
    Assert.That(choice.Sections[2].Body.Statements, Is.EqualTo(new IrStmt[] { new IrBreak() }));
  }

  /// <summary>A do loop, and the steps whose value is used, with the flags the source gives them.</summary>
  [Test]
  public void DoAndSteps_KeepTheirForm() {
    var module = Import(
      "public static int A(int a) { do { a--; } while (a > 0); return a; } "
      + "public static int B(int a) => a++; public static int C(int a) => --a; "
      + "public static int D(byte[] d, int i) => checked(d[i + 1]--); public static double E(double a) => checked(++a); "
      + "public static int G(int a) { const int K = 5; const long L = K; return a + K; }");
    var loop = (IrDoWhile)module.Functions[0].Body.Statements[0];
    IrExpr a = new IrLocalRef(new IrLocal("a", IrType.I32));
    Assert.That(loop.Condition, Is.EqualTo(new IrBinary(IrBinaryOp.Greater, a, new IrConst(IrType.I32, 0))));
    Assert.That(loop.Body.Statements, Has.Count.EqualTo(1));
    Assert.That(Returned(module.Functions[1]), Is.EqualTo(new IrStep(a, true, true, false)));
    Assert.That(Returned(module.Functions[2]), Is.EqualTo(new IrStep(a, false, false, false)));
    var d = module.Functions[3];
    Assert.That(Returned(d), Is.EqualTo(new IrConvert(IrType.I32, new IrStep(
      new IrArrayLoad(new IrLocalRef(d.Parameters[0]), new IrBinary(IrBinaryOp.AddChecked, new IrLocalRef(d.Parameters[1]), new IrConst(IrType.I32, 1))),
      false, true, true))));
    Assert.That(Returned(module.Functions[4]), Is.EqualTo(new IrStep(new IrLocalRef(new IrLocal("a", IrType.F64)), true, false, false)));
    Assert.That(module.Functions[5].Locals, Is.Empty);
    Assert.That(Returned(module.Functions[5]), Is.EqualTo(new IrBinary(IrBinaryOp.Add, a, new IrConst(IrType.I32, 5))));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

  /// <summary>The compilation default: everything outside an explicit context traps, a lossless cast still cannot.</summary>
  [Test]
  public void CheckedByDefault_AppliesWhereTheSourceSaysNothing() {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static int F(int a, int b) => a + b; public static int G(int a, int b) => unchecked(a + b); "
      + "public static long H(int a) => (long)a; public static byte I(int a) => (byte)a;"), true);
    var functions = result.Module!.Functions;
    Assert.That(((IrBinary)Returned(functions[0])).Op, Is.EqualTo(IrBinaryOp.AddChecked));
    Assert.That(((IrBinary)Returned(functions[1])).Op, Is.EqualTo(IrBinaryOp.Add));
    Assert.That(((IrConvert)Returned(functions[2])).Contract, Is.EqualTo(IrConversion.Wrap));
    Assert.That(((IrConvert)Returned(functions[3])).Contract, Is.EqualTo(IrConversion.Trap));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  /// <summary>A constant expression that overflows is the compiler's error, under a code of its own.</summary>
  [TestCase("public static int F() => int.MaxValue + 1;", "CS0220")]
  [TestCase("public static int F() => checked(int.MaxValue * 2);", "CS0220")]
  [TestCase("public static byte F() => (byte)300;", "CS0221")]
  public void ConstantOverflow_IsReportedWithTheCompilersMessage(string method, string id) {
    var diagnostic = Refusal(PrototypeFixture.Wrap(method));
    Assert.That((diagnostic.Code, diagnostic.Severity, diagnostic.File, diagnostic.Line), Is.EqualTo(("PLY1003", DiagnosticSeverity.Error, "Snippet.cs", 1)));
    Assert.That(diagnostic.Message, Does.StartWith(id + ": "));
  }

  /// <summary>One method that is refused does not hide the next one.</summary>
  [Test]
  public void EveryRefusedMethod_GetsItsOwnDiagnostic() {
    var result = CSharpFrontend.Import("Snippet.cs",
      "public static class C {\n  public static decimal A() => 1;\n  public static int B() => 1;\n  public static string D() => \"x\";\n}");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Line, diagnostic.Message)),
      Is.EqualTo(new[] { (2, "'decimal' is not supported yet"), (4, "'string' is not supported yet") }));
  }

  /// <summary>Test design 12: invalid C# carries the compiler's own id, text and position.</summary>
  [TestCase("public static int F( => 1;", "CS1001", TestName = "syntax error")]
  [TestCase("public static int F() => true;", "CS0029", TestName = "type error")]
  [TestCase("public static int F() => Missing();", "CS0103", TestName = "unknown name")]
  [TestCase("public int F() => 1;", "CS0708", TestName = "instance member of a static class")]
  public void InvalidSource_IsReportedWithTheCompilersMessage(string method, string id) {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Is.Not.Empty);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Severity, diagnostic.File, diagnostic.Line)),
      Has.All.EqualTo(("PLY1000", DiagnosticSeverity.Error, "Snippet.cs", 1)));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Message), Has.Some.StartsWith(id + ": "));
    Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Column), Has.All.GreaterThan(30));
  }

  /// <summary>A warning of the compiler does not stop the import.</summary>
  [Test]
  public void CompilerWarning_IsNotAnError() {
    var result = CSharpFrontend.Import("Snippet.cs", PrototypeFixture.Wrap("public static int F() { int unused = 1; return 2; }"));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module, Is.Not.Null);
  }

  /// <summary>An array and an index that are read and written are evaluated once.</summary>
  [Test]
  public void CompoundAssignmentToAnElement_EvaluatesArrayAndIndexOnce() {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(
      "public static void F(int[] a, int i) { a[i + 1] += 2; a[i]++; a[0] -= 1; } "
      + "public static int[] G(int[] a) => a; public static void H(int[] a) { G(a)[0]++; }"));
    var f = module.Functions[0];
    var h = module.Functions[2];
    Assert.That(f.Locals, Is.EqualTo(new[] { new IrLocal("$t0", IrType.I32) }));
    Assert.That(f.Body.Statements.Select(statement => statement.GetType()),
      Is.EqualTo(new[] { typeof(IrDeclare), typeof(IrStore), typeof(IrStore), typeof(IrStore) }));
    Assert.That(h.Locals, Is.EqualTo(new[] { new IrLocal("$t0", IrType.Array(IrType.I32)) }));
    Assert.That(IrVerifier.Verify(module), Is.Empty);
  }

}
