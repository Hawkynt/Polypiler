using System;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Backends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Records;

/// <summary>
/// The JavaScript of value records: object literals, a copy written exactly where the place a struct
/// is read from stays live, and the helpers of the structs. Whether the code computes what .NET
/// computes is decided by node in the acceptance tests; here the text is checked, parsed at the
/// level it claims.
/// </summary>
[TestFixture]
public sealed class JavaScriptRecordEmissionTests {

  private const string Vec = "public struct Vec { public double X; public double Y; public Vec(double x, double y) { X = x; Y = y; } }";

  private static readonly JavaScriptProfile _es5 = JavaScriptProfile.Parse("es5");
  private static readonly JavaScriptProfile _es2015 = JavaScriptProfile.Parse("es2015");
  private static readonly JavaScriptProfile _es2020 = JavaScriptProfile.Parse("es2020");

  private static readonly JavaScriptProfile[] _profiles = [
    .. from level in JavaScriptProfile.LevelNames
       from typed in level == "es5" ? new[] { false, true } : [false]
       select JavaScriptProfile.Parse(level, typedArrays: typed)
  ];

  private static string Emit(IrModule module, JavaScriptProfile profile) {
    Assert.That(JavaScriptEmitter.Check(module, profile), Is.Empty);
    var text = JavaScriptEmitter.Emit(module, profile).Single().Content;
    Assert.That(GeneratedJavaScript.Problems(text, profile), Is.Empty, text);
    return text;
  }

  private static string Emit(string source, JavaScriptProfile? profile = null) => Emit(PrototypeFixture.Import("C.cs", source), profile ?? JavaScriptProfile.Default);

  /// <summary>The body of a function, one line per statement, without indentation.</summary>
  private static string[] Body(string text, string function) {
    var lines = text.Split('\n');
    var start = Array.FindIndex(lines, line => line.Contains($"function {function}("));
    return [.. lines.Skip(start + 1).TakeWhile(line => line != "}").Select(line => line.Trim())];
  }

  private static string[] Body(string methods) => Body(Emit(Vec + " public static class C { " + methods + " }"), "F");

  /// <summary>Test design 13: every node of the value records in every profile; nothing is inexpressible.</summary>
  [Test]
  public void EveryRecordNode_IsWrittenInEveryProfile() {
    foreach (var profile in _profiles) {
      var text = Emit(RecordModules.Everything(), profile);
      Assert.That(text, Does.Contain("function $equals_Outer(a, b) {").And.Contain("function $newArray_Outer(length) {").And.Contain("function $copy_Outer(value) {"));
    }
  }

  /// <summary>Test design 2: a copy at an assignment exactly where the place it is read from is used afterwards.</summary>
  [Test]
  public void Assignment_CopiesOnlyALivePlace() {
    Assert.That(Body("private static double F() { var p = new Vec(1, 2); var q = p; q.X = 5; return p.X + q.X; }"),
      Is.EqualTo(new[] { "let p = { X: 1, Y: 2 };", "let q = { X: p.X, Y: p.Y };", "q.X = 5;", "return p.X + q.X;" }));
    Assert.That(Body("private static double F() { var p = new Vec(1, 2); var q = p; q.X = 5; return q.X; }"),
      Is.EqualTo(new[] { "let p = { X: 1, Y: 2 };", "let q = p;", "q.X = 5;", "return q.X;" }));
  }

  /// <summary>Test design 2: an argument and a result; a fresh value and a call result are never copied.</summary>
  [Test]
  public void ArgumentAndResult_CopyOnlyALivePlace() {
    Assert.That(Body("private static double F() { var p = new Vec(1, 2); var r = G(p); return r.X + p.X; } private static Vec G(Vec v) { v.X = 3; return v; }"),
      Is.EqualTo(new[] { "let p = { X: 1, Y: 2 };", "let r = G({ X: p.X, Y: p.Y });", "return r.X + p.X;" }));
    Assert.That(Body("private static Vec F() { var p = new Vec(1, 2); return G(G(p)); } private static Vec G(Vec v) { v.X = 3; return v; }"),
      Is.EqualTo(new[] { "let p = { X: 1, Y: 2 };", "return G(G(p));" }));
  }

  /// <summary>
  /// Test design 2: an array store copies a live place, an array load always copies, and a field of
  /// an element is written in place. A later read of a variable keeps it live even where an
  /// assignment in between replaces its value: the analysis does not follow the paths, and errs
  /// towards a copy.
  /// </summary>
  [Test]
  public void Arrays_StoreAndLoadCopies() {
    Assert.That(Body("private static double F() { var a = new Vec[2]; var p = new Vec(1, 2); a[0] = p; a[1] = p; return a[1].X; }"),
      Is.EqualTo(new[] { "let a = $newArray_Vec($len(2));", "let p = { X: 1, Y: 2 };", "$set(a, 0, { X: p.X, Y: p.Y });", "$set(a, 1, p);", "return $get(a, 1).X;" }));
    Assert.That(Body("private static double F() { var a = new Vec[2]; var p = new Vec(1, 2); a[1] = p; p = a[0]; a[0].X = 3; return p.X; }"),
      Is.EqualTo(new[] {
        "let a = $newArray_Vec($len(2));", "let p = { X: 1, Y: 2 };", "$set(a, 1, { X: p.X, Y: p.Y });",
        "p = $copy_Vec($get(a, 0));", "$get(a, 0).X = 3;", "return p.X;" }));
  }

  /// <summary>Test design 2: a nested struct is copied by its helper; a field of a dead variable is not copied.</summary>
  [Test]
  public void NestedField_IsCopiedDeeply() {
    const string Line = "public struct Line { public Vec A; public Vec B; } ";
    var text = Emit(Vec + Line + "public static class C { private static Vec F(Line l) { var c = l; c.A.X = 1; var b = c.B; return l.A; } private static Vec G(Line l) => l.B; }");
    Assert.That(Body(text, "F"), Is.EqualTo(new[] { "let c = $copy_Line(l);", "c.A.X = 1;", "let b = c.B;", "return l.A;" }));
    Assert.That(Body(text, "G"), Is.EqualTo(new[] { "return l.B;" }));
    Assert.That(text, Does.Contain("function $copy_Line(value) {\n  return { A: $copy_Vec(value.A), B: $copy_Vec(value.B) };\n}"));
  }

  /// <summary>A variable a loop outlives is copied inside it on every pass; one the pass declares is not.</summary>
  [Test]
  public void Loops_CopyWhatOutlivesThePass() {
    Assert.That(Body("private static double F(int n) { var p = new Vec(1, 2); var s = 0.0; for (var i = 0; i < n; ++i) { var q = p; q.X += i; s += q.X; } return s; }"),
      Has.Some.EqualTo("let q = { X: p.X, Y: p.Y };"));
    Assert.That(Body("private static double F(int n) { var s = 0.0; for (var i = 0; i < n; ++i) { var p = new Vec(i, 2); var q = p; q.X += i; s += q.X; } return s; }"),
      Has.Some.EqualTo("let q = p;"));
  }

  /// <summary>
  /// A public function never writes the object it is given and never hands one back: a parameter
  /// it writes through is copied on entry, any other is copied wherever it is read as a value.
  /// </summary>
  [Test]
  public void PublicFunction_KeepsItsCallersObjects() {
    var text = Emit(Vec + " public static class C { public static Vec F(Vec v) { v.X = 2; return v; } public static Vec G(Vec v) => v; }");
    Assert.That(Body(text, "F"), Is.EqualTo(new[] { "v = { X: v.X, Y: v.Y };", "v.X = 2;", "return v;" }));
    Assert.That(Body(text, "G"), Is.EqualTo(new[] { "return { X: v.X, Y: v.Y };" }));
  }

  /// <summary>A key whose value is the variable of its name is written once from ECMAScript 2015 on, twice before.</summary>
  [Test]
  public void Shorthand_FollowsTheLevel() {
    const string Source = Vec + " public static class C { public static Vec F(double X, double y) => new Vec(X, y); }";
    Assert.That(Body(Emit(Source, _es2015), "F"), Is.EqualTo(new[] { "return { X, Y: y };" }));
    Assert.That(Body(Emit(Source, _es5), "F"), Is.EqualTo(new[] { "return { X: X, Y: y };" }));
  }

  /// <summary>
  /// Equality compares a float as double.Equals, a 64-bit integer as a BigInt or a pair of halves,
  /// a struct by its own helper and anything else with ===; an empty struct is always equal.
  /// </summary>
  [Test]
  public void Equality_ComparesEachFieldAsTheIrDefinesIt() {
    var es2020 = Emit(RecordModules.Everything(), _es2020);
    Assert.That(es2020, Does.Contain("function $equals_Inner(a, b) {\n  return a.A === b.A && a.B === b.B;\n}"));
    Assert.That(es2020, Does.Contain("function $equals_Outer(a, b) {\n  return $equals_Inner(a.In, b.In) && (a.F === b.F || (a.F !== a.F && b.F !== b.F)) && (a.D === b.D || (a.D !== a.D && b.D !== b.D)) && a.U === b.U;\n}"));
    Assert.That(es2020, Does.Contain("array[index] = { In: { A: 0n, B: false }, F: 0, D: 0, U: 0n };"));
    var es5 = Emit(RecordModules.Everything(), _es5);
    Assert.That(es5, Does.Contain("return $eq64(a.A, b.A) && a.B === b.B;").And.Contain("function $eq64(").And.Contain("var array = [];").And.Contain("for (var index = 0;"));
    Assert.That(es5, Does.Contain("{ In: { A: { hi: 0, lo: 0 }, B: false }, F: 0, D: 0, U: { hi: 0, lo: 0 } }"));
    var empty = new IrStructType("Empty", []);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [], [], new IrBlock([
      new IrReturn(new IrStructEquals(new IrStructNew(IrType.Struct(empty), []), new IrStructDefault(IrType.Struct(empty))))
    ]))]) { Types = [empty] };
    var text = Emit(module, _es5);
    Assert.That(text, Does.Contain("return $equals_Empty({}, {});").And.Contain("function $equals_Empty(a, b) {\n  return true;\n}"));
  }

  /// <summary>A struct in the header of a function is its name; an array of them an Array of it.</summary>
  [Test]
  public void Header_NamesTheStruct() {
    var text = Emit(Vec + " public static class C { public static Vec[] F(Vec v) => new Vec[] { v }; }", _es2015);
    Assert.That(text, Does.Contain("// F(v: Vec): Array<Vec>\n").And.Contain("return [{ X: v.X, Y: v.Y }];"));
    Assert.That(text, Does.Contain("// A struct is an object of its fields, copied wherever the place it is read from can still be used.\n"));
  }

  /// <summary>A field store is the step of a for, a field of a struct new value is read directly, and a struct value as a statement stands in parentheses.</summary>
  [Test]
  public void Statements_WriteFieldsAndValues() {
    var v = new IrLocal("v", IrType.Struct(RecordModules.Inner));
    var a = RecordModules.Inner.Fields[0];
    var body = new IrBlock([
      new IrDeclare(v, new IrStructDefault(RecordModules.InnerType)),
      new IrFor(new IrBinary(IrBinaryOp.Less, new IrFieldLoad(new IrLocalRef(v), a), new IrConst(IrType.I64, 3)), new IrBlock([new IrContinue()]),
        new IrBlock([new IrFieldStore(new IrLocalRef(v), a, new IrBinary(IrBinaryOp.Add, new IrFieldLoad(new IrLocalRef(v), a), new IrConst(IrType.I64, 1)))])),
      new IrExprStmt(new IrStructNew(RecordModules.InnerType, [new IrConst(IrType.I64, 1), new IrConst(IrType.Bool, 0)])),
      new IrReturn(new IrFieldLoad(new IrStructNew(RecordModules.InnerType, [new IrConst(IrType.I64, 1), new IrConst(IrType.Bool, 1)]), RecordModules.Inner.Fields[1]))
    ]);
    var module = new IrModule("M", [new IrFunction("F", true, IrType.Bool, [], [v], body)]) { Types = [RecordModules.Inner] };
    Assert.That(Body(Emit(module, _es2020), "F"), Is.EqualTo(new[] {
      "let v = { A: 0n, B: false };", "for (; v.A < 3n; v.A = BigInt.asIntN(64, v.A + 1n)) {", "continue;", "}",
      "({ A: 1n, B: false });", "return { A: 1n, B: true }.B;" }));
  }

}
