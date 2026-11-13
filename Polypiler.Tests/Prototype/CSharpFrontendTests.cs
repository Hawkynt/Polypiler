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
    var result = CSharpFrontend.Import(fixture + ".cs", PrototypeFixture.SourceOf(fixture));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Name, Is.EqualTo(fixture));
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
  public void Import_TakesConstantsFromTheCompiler(string method, IrTypeKind kind, long value) {
    var module = PrototypeFixture.Import("Snippet.cs", PrototypeFixture.Wrap(method));
    var constant = (IrConst)((IrReturn)module.Functions[0].Body.Statements[0]).Value!;
    Assert.That((constant.Type.Kind, constant.Value), Is.EqualTo((kind, value)));
  }

  /// <summary>
  /// Test design 11: every construct the issue lists as out of scope is refused with PLY1002 and the
  /// position of the construct; the snippets are valid C#, so the compiler does not get there first.
  /// </summary>
  [TestCase("public static long F(long a) => a + 1;", "long", 15, TestName = "long")]
  [TestCase("public static ulong F() => 1;", "ulong", 15, TestName = "ulong")]
  [TestCase("public static double F() => 1.5;", "double", 15, TestName = "double")]
  [TestCase("public static float F(int a) => a;", "float", 15, TestName = "float")]
  [TestCase("public static int F(int a) => checked(a + 1);", "CheckedExpression", 31, TestName = "checked expression")]
  [TestCase("public static int F(int a) { checked { return a + 1; } }", "CheckedStatement", 30, TestName = "checked statement")]
  [TestCase("public static int F(int a) => unchecked(a + 1);", "UncheckedExpression", 31, TestName = "unchecked expression")]
  [TestCase("public static string F() => \"a\";", "string", 15, TestName = "string")]
  [TestCase("public static char F() => 'a';", "char", 15, TestName = "char")]
  [TestCase("public static int F(int[] a) { int s = 0; foreach (int x in a) s += x; return s; }", "ForEachStatement", 43, TestName = "foreach")]
  [TestCase("public static int F(int a) { switch (a) { default: return 1; } }", "SwitchStatement", 30, TestName = "switch statement")]
  [TestCase("public static int F(int a) => a switch { _ => 1 };", "SwitchExpression", 31, TestName = "switch expression")]
  [TestCase("public static int F(int a) { do { a++; } while (a < 3); return a; }", "DoStatement", 30, TestName = "do")]
  [TestCase("public static int F(int a) { while (true) { break; } return a; }", "BreakStatement", 45, TestName = "break")]
  [TestCase("public static int F(int a) { while (a < 0) { a++; continue; } return a; }", "ContinueStatement", 51, TestName = "continue")]
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
  [TestCase("public static int F() { const int a = 1; return a; }", "const", 25, TestName = "local constant")]
  [TestCase("public static int F(int a) { int b = a = 1; return b; }", "SimpleAssignmentExpression", 38, TestName = "assignment as a value")]
  [TestCase("public static int F(int a) => a++;", "PostIncrementExpression", 31, TestName = "increment as a value")]
  [TestCase("public static int F(int a) => ++a;", "PreIncrementExpression", 31, TestName = "prefix increment as a value")]
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

  /// <summary>Test design 15: uint with int is not widened to 64 bits behind the user's back.</summary>
  [TestCase("public static uint F(uint a, int b) => (uint)(a + b);", "long", 46)]
  [TestCase("public static bool F(uint a, int b) => a < b;", "long", 40)]
  [TestCase("public static uint F(uint a) => (uint)(-a);", "long", 39)]
  public void UIntMixedWithInt_IsRefused(string method, string what, int column) {
    var diagnostic = Refusal(PrototypeFixture.Wrap(method));
    Assert.That((diagnostic.Code, diagnostic.Message, diagnostic.Line, diagnostic.Column),
      Is.EqualTo(("PLY1002", $"'{what}' is not supported yet", 1, column + 30)));
  }

  /// <summary>One method that is refused does not hide the next one.</summary>
  [Test]
  public void EveryRefusedMethod_GetsItsOwnDiagnostic() {
    var result = CSharpFrontend.Import("Snippet.cs",
      "public static class C {\n  public static long A() => 1;\n  public static int B() => 1;\n  public static char D() => 'x';\n}");
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics.Select(diagnostic => (diagnostic.Line, diagnostic.Message)),
      Is.EqualTo(new[] { (2, "'long' is not supported yet"), (4, "'char' is not supported yet") }));
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
