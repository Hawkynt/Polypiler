using System.Linq;
using NUnit.Framework;
using Polypiler.Backends.Testing;
using Polypiler.Core.Backends.Rust;
using Polypiler.Core.Backends.Rust.Printing;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.Optimize;

namespace Polypiler.Tests.Backends.Rust;

/// <summary>
/// Value records as Rust structs (#462): a struct of numbers is <c>Copy</c>, its derives are the
/// ones that hold, its equality is the IR's, and a field of a place is read and written in place.
/// </summary>
[TestFixture]
public sealed class TypeEmitTests {

  private static string Emitted(string source, OptimizationLevel level = OptimizationLevel.None)
    => RustCrates.ModuleText(RustCrates.Emit(Polypiler.Prototype.Testing.PrototypeFixture.Import("Snippet.cs", source), level: level));

  private static string[] Problems(string source)
    => [.. RustCrates.Check(Polypiler.Prototype.Testing.PrototypeFixture.Import("Snippet.cs", source)).Select(problem => $"{problem.Code} {problem.Message}")];

  private const string Point = "public struct Point { public int X; public int Y; public Point(int x, int y) { X = x; Y = y; } } ";

  [Test]
  public void StructOfIntegers_DerivesEquality() {
    var text = Emitted(Point + "public static class S { public static int F(int a) { var p = new Point(a, 2); return p.X + p.Y; } }");
    Assert.That(text, Does.Contain("#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]\npub struct Point {\n    pub x: i32,\n    pub y: i32,\n}"));
    Assert.That(text, Does.Contain("let p: Point = Point { x: a, y: 2 };"));
    Assert.That(text, Does.Contain("p.x.wrapping_add(p.y)"));
  }

  [Test]
  public void StructOfFloats_ImplementsTheEqualityOfTheIr() {
    var text = Emitted("public readonly record struct P2(double X, double Y); public static class S { public static bool Same(double a, double b) => new P2(a, b) == new P2(b, a); }");
    Assert.That(text, Does.Contain("#[derive(Clone, Copy, Debug, Default)]\npub struct P2 {"));
    Assert.That(text, Does.Contain("impl PartialEq for P2 {\n    fn eq(&self, other: &Self) -> bool {\n        (self.x == other.x || (self.x.is_nan() && other.x.is_nan()))\n            && (self.y == other.y || (self.y.is_nan() && other.y.is_nan()))\n    }\n}"));
    Assert.That(text, Does.Contain("P2 { x: a, y: b } == P2 { x: b, y: a }"));
  }

  [Test]
  public void FieldWritten_MakesItsVariableMutable_AndAParameterRebound() {
    var text = Emitted(Point + "public static class S { public static Point Move(Point p, int d) { p.X += d; p.Y |= 1; return p; } public static int F() { var q = new Point(); q.Y = 3; q.X++; return q.Y; } }");
    Assert.That(text, Does.Contain("pub fn move_(p: Point, d: i32) -> Point {\n    let mut p = p;\n    p.x = p.x.wrapping_add(d);\n    p.y |= 1;\n    p\n}").Or.Contain("let mut p = p;"));
    Assert.That(text, Does.Contain("let mut q: Point = Point::default();"));
    Assert.That(text, Does.Contain("q.x = q.x.wrapping_add(1);"));
  }

  [Test]
  public void ArrayOfStructs_IsAVectorOfDefaults_WrittenInPlace() {
    var text = Emitted(Point + "public static class S { public static int F(int n) { var a = new Point[n]; a[0].X = 3; a[0].Y++; return a[0].X + a[0].Y; } }");
    Assert.That(text, Does.Contain("let mut a: Vec<Point> = vec![Point::default(); usize::try_from(n).expect(\"IntegerOverflow\")];"));
    Assert.That(text, Does.Contain("a[0].x = 3;"));
    Assert.That(text, Does.Contain("a[0].y = a[0].y.wrapping_add(1);"));
  }

  [Test]
  public void FieldOfAnElementWrittenWithAValueThatMayTrap_IsRefused() {
    Assert.That(Problems(Point + "public static class S { public static void F(Point[] a, int b) { a[0].X = 10 / b; } }"), Has.Some.Contain("a field of an array element written with a value that may trap or have an effect"));
  }

  [Test]
  public void StructWithAnArrayField_OwnsAVector_AndIsMovedNotCopied() {
    var text = Emitted("public struct Buffer { public int[] Data; } public static class S { public static int F(Buffer b) => b.Data[0]; }");
    Assert.That(text, Does.Contain("#[derive(Clone, Debug)]\npub struct Buffer {\n    pub data: Vec<i32>,\n}"));
    Assert.That(text, Does.Contain("pub fn f(b: Buffer) -> i32 {\n    b.data[0]\n}"));
  }

  [Test]
  public void ElementLoopOverStructs_CopiesEachElement_ButNotWhereTheBodyWritesOne() {
    Assert.That(Emitted(Point + "public static class S { public static int F(Point[] a) { int s = 0; foreach (var p in a) s += p.X; return s; } }"), Does.Contain("a.iter()\n        .map(|&p| p.x)\n        .fold(0i32, |acc, x| acc.wrapping_add(x))"));
    Assert.That(Emitted(Point + "public static class S { public static void F(Point[] a) { for (int i = 0; i < a.Length; i++) a[i].X = a[i].Y; } }"), Does.Not.Contain("for &"));
  }

  [Test]
  public void StructSyntax_PrintsAndReadsBack() {
    var file = new RsFile([], [], [
      new RsStruct("Unit", "pub", []),
      new RsStruct("Flags", "pub", [new RsFieldDeclaration("", "pub", "u32")]),
      new RsStruct("Pair", "pub", [new RsFieldDeclaration("a", "pub", "i32"), new RsFieldDeclaration("b", "", "f64")]) { Attributes = ["derive(Clone, Copy)"] },
      new RsImpl("PartialEq", "Pair", [new RsFunction("eq", "", [new RsParameter("&self", ""), new RsParameter("other", "&Self")], "bool", new RsBlock([], new RsLiteral("true")))]),
      new RsImpl(null, "Unit", []),
      new RsFunction("f", "", [new RsParameter("a", "i32")], "Pair", new RsBlock([
        new RsLet("p", false, null, new RsStructLiteral("Pair", [new RsFieldValue("a", new RsPath("a")), new RsFieldValue("b", new RsLiteral("1.0"))])),
        new RsExpressionStatement(new RsIf(new RsBinary(new RsField(new RsPath("p"), "a"), ">", new RsLiteral("0")), RsBlock.Empty, null))
      ], new RsStructLiteral("Pair", [new RsFieldValue("a", new RsPath("long_name_of_a_value")), new RsFieldValue("b", new RsLiteral("2.0"))])))
    ]);
    var text = RustPrinter.Print(file);
    Assert.That(text, Does.Contain("pub struct Unit;\n\npub struct Flags(pub u32);\n\n#[derive(Clone, Copy)]\npub struct Pair {\n    pub a: i32,\n    b: f64,\n}"));
    Assert.That(text, Does.Contain("impl Unit {}"));
    Assert.That(text, Does.Contain("let p = Pair { a, b: 1.0 };\n    if p.a > 0 {}\n    Pair {\n        a: long_name_of_a_value,\n        b: 2.0,\n    }\n"));
    Assert.That(RustPrinter.Print(RustSyntaxReader.Read(text)), Is.EqualTo(text));
  }

  private const string UseOfU = "public static class S { public static int F(int a) { var u = new U(a); return u.A; } }";

  [Test]
  public void SequentialLayout_IsReprC() {
    var text = Emitted("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct U { public int A; public byte B; public U(int a) { A = a; B = 1; } } " + UseOfU);
    Assert.That(text, Does.Contain("#[repr(C)]\n#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]\npub struct U {"));
    Assert.That(Emitted(Point + "public static class S { public static int F() => new Point(1, 2).X; }"), Does.Not.Contain("repr(C)"));
  }

  [TestCase("[StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public int A; [FieldOffset(0)] public float B; public U(int a) { B = 0; A = a; } }", "overlapping fields, an untagged union", TestName = "OverlappingFields_AreRefused")]
  [TestCase("[StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public int A; [FieldOffset(8)] public byte B; public U(int a) { A = a; B = 1; } }", "explicit field offsets", TestName = "ExplicitOffsets_AreRefused")]
  [TestCase("[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct U { public int A; public byte B; public U(int a) { A = a; B = 1; } }", "packed to 1 bytes", TestName = "PackedLayout_IsRefused")]
  [TestCase("[StructLayout(LayoutKind.Sequential, Size = 16)] public struct U { public int A; public byte B; public U(int a) { A = a; B = 1; } }", "a size of 16 bytes", TestName = "LayoutOfItsOwnSize_IsRefused")]
  public void LayoutWithoutASafeForm_IsRefusedByName(string type, string problem)
    => Assert.That(Problems("using System.Runtime.InteropServices; " + type + " " + UseOfU), Has.Some.EqualTo($"PLY1584 layout of 'U' ({problem}) has no safe Rust representation"));

}
