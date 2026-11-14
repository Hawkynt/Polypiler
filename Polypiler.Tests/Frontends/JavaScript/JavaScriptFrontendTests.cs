using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// What the JavaScript importer accepts, which IR it makes of it and what it refuses. What the
/// imported code computes is the subject of <see cref="JavaScriptDifferentialTests"/>.
/// </summary>
[TestFixture]
public sealed class JavaScriptFrontendTests {

  private const string Ints = "/** @param {int32} a @param {int32} b @returns {int32} */ ";

  /// <summary>One parameter of every kind, so that a body can use whichever it needs.</summary>
  private const string Mixed = "/** @param {int32} a @param {uint32} u @param {uint8} c @param {boolean} t @param {uint8[]} d @returns {int32} */ ";

  private static readonly IrLocal _a = new("a", IrType.I32);
  private static readonly IrLocal _u = new("u", IrType.U32);
  private static readonly IrLocal _c = new("c", IrType.U8);
  private static readonly IrLocal _t = new("t", IrType.Bool);
  private static readonly IrLocal _d = new("d", IrType.Array(IrType.U8));

  private static IrExpr A => new IrLocalRef(_a);

  private static IrExpr U => new IrLocalRef(_u);

  private static IrExpr C => new IrLocalRef(_c);

  private static IrExpr T => new IrLocalRef(_t);

  private static IrExpr D => new IrLocalRef(_d);

  private static IrConst Int(long value) => new(IrType.I32, value);

  private static string WithBody(string body, string result = "int32")
    => Mixed.Replace("@returns {int32}", result.Length == 0 ? "" : $"@returns {{{result}}}") + "function f(a, u, c, t, d) { " + body + " }";

  private static Diagnostic Refusal(string source, IReadOnlyCollection<string>? exports = null) {
    var result = JavaScriptFrontend.Import("snippet.js", source, exports);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Caveats, Is.Empty);
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1), string.Join("; ", result.Diagnostics));
    return result.Diagnostics[0];
  }

  private static IrModule Import(string source, IReadOnlyCollection<string>? exports = null) {
    var module = JavaScriptFixture.Import("snippet.js", source, exports);
    Assert.That(IrVerifier.Verify(module), Is.Empty);
    return module;
  }

  /// <summary>The statements of the one function a body makes, verified.</summary>
  private static IReadOnlyList<IrStmt> Lowered(string body, string result = "int32") => Import(WithBody(body, result)).Functions[0].Body.Statements;

  /// <summary>The value a body that is one <c>return</c> returns.</summary>
  private static IrExpr Returned(string expression, string result = "int32") => ((IrReturn)Lowered($"return {expression};", result)[0]).Value!;

  private static IrConst Double(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

  /// <summary>
  /// IR written out with everything inside it. Two nodes of the IR are equal as records only when
  /// their lists are the same lists, so nodes that hold lists are compared by what this writes.
  /// </summary>
  private static string Show(object? node) => node switch {
    null => "null",
    string or IrType or bool or long or System.Enum => node.ToString()!,
    System.Collections.IEnumerable items => $"[{string.Join(", ", items.Cast<object?>().Select(Show))}]",
    _ => $"{node.GetType().Name}({string.Join(", ", node.GetType().GetProperties().Where(property => property.Name != "EqualityContract").Select(property => Show(property.GetValue(node))))})"
  };

  private static void AssertSame(object? actual, object? expected) => Assert.That(Show(actual), Is.EqualTo(Show(expected)));

  private static IrExpr AsDouble(IrExpr value) => new IrConvert(IrType.F64, value);

  private static IrExpr ToInt32(IrExpr value) => new IrIntrinsic(IrIntrinsicOp.ToInt32, IrType.I32, [value]);

  /// <summary>Every fixture imports into IR the verifier accepts, named after its file.</summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void Fixture_ImportsIntoWellTypedIr(string fixture) {
    var result = JavaScriptFrontend.Import(JavaScriptFixture.PathOf(fixture), JavaScriptFixture.SourceOf(fixture));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Name, Is.EqualTo(fixture[(fixture.LastIndexOf('/') + 1)..]));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  /// <summary>Scenario: An uncoerced sum is a double.</summary>
  [Test]
  public void UncoercedSum_IsADouble() {
    var module = Import(Ints + "function f(a, b) { return a + b; }");
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(IrInterpreter.Call(module, "f", [2147483647, 1]), Is.EqualTo(2147483648.0));
  }

  /// <summary>Scenario: A coerced sum wraps.</summary>
  [Test]
  public void CoercedSum_Wraps() {
    var module = Import(Ints + "function f(a, b) { return (a + b) | 0; }");
    Assert.That(IrInterpreter.Call(module, "f", [2147483647, 1]), Is.EqualTo(-2147483648));
  }

  /// <summary>Scenario: A coerced product is computed in doubles, Math.imul in 32 bits.</summary>
  [TestCase("(a * b) | 0")]
  [TestCase("Math.imul(a, b)")]
  public void CoercedProduct_IsComputedInDoubles_MathImulIn32Bits(string product)
    => Assert.That(IrInterpreter.Call(Import(Ints + $"function f(a, b) {{ return {product}; }}"), "f", [65537, 65537]), Is.EqualTo(131073));

  /// <summary>Scenario: A function without JSDoc types is typed by what it does.</summary>
  [Test]
  public void FunctionWithoutJsDocTypes_IsTypedByWhatItDoes() {
    var result = JavaScriptFrontend.Import("snippet.js", "function f(a) { return a | 0; }");
    var function = result.Module!.Functions.Single();
    Assert.That((function.Parameters.Single().Type, function.ReturnType), Is.EqualTo((IrType.I32, IrType.I32)));
    Assert.That(result.Caveats.Single().Id, Is.EqualTo(JavaScriptFrontend.IntegerParameter));
    Assert.That(result.Caveats.Single().Description, Does.EndWith(": f(a)"));
  }

  /// <summary>Test design 3: each shape a function can stand in.</summary>
  [TestCase(Ints + "function f(a, b) { return a; }", TestName = "Shape: declaration")]
  [TestCase(Ints + "export function f(a, b) { return a; }", TestName = "Shape: exported declaration")]
  [TestCase("const O = { " + Ints + "f: function (a, b) { return a; }, size: 4, ...{} };", TestName = "Shape: property of an object literal")]
  [TestCase("var O = { " + Ints + "f(a, b) { return a; } };", TestName = "Shape: method of an object literal")]
  [TestCase("export const O = { " + Ints + "f: function (a, b) { return a; } };", TestName = "Shape: property of an exported object literal")]
  [TestCase("module.exports = { " + Ints + "f: function (a, b) { return a; } };", TestName = "Shape: property of an assigned object literal")]
  [TestCase("let O; O = { " + Ints + "f: function (a, b) { return a; } };", TestName = "Shape: property of an object literal assigned to a variable")]
  [TestCase("const { size } = { " + Ints + "f: function (a, b) { return a; }, size: 4 };", TestName = "Shape: property of a destructured object literal")]
  [TestCase("(function (global) { 'use strict'; const O = { " + Ints + "f: function (a, b) { return a; } }; global.O = O; })(this);", TestName = "Shape: property inside an IIFE")]
  [TestCase("(() => { return { " + Ints + "f: function (a, b) { return a; } }; })();", TestName = "Shape: property of the object an IIFE returns")]
  [TestCase("(function () { this.O = { " + Ints + "f: function (a, b) { return a; } }; })();", TestName = "Shape: property of an object assigned inside an IIFE")]
  [TestCase("const O = { inner: { deeper: { " + Ints + "f: function (a, b) { return a; } } } };", TestName = "Shape: property of an object literal inside an object literal")]
  [TestCase("module.exports = { inner: { " + Ints + "f(a, b) { return a; } } };", TestName = "Shape: method of a nested literal without a name")]
  public void Function_IsImportedInEveryShape(string source) {
    var function = Import(source).Functions.Single();
    Assert.That((function.Name, function.IsPublic, function.ReturnType), Is.EqualTo(("f", true, IrType.I32)));
    Assert.That(function.Parameters, Is.EqualTo(new[] { _a, new IrLocal("b", IrType.I32) }));
    Assert.That(function.Body.Statements, Is.EqualTo(new IrStmt[] { new IrReturn(A) }));
  }

  /// <summary>What is not one of those shapes is not a function of the file, so a file of only such has none.</summary>
  [TestCase("", TestName = "No function: empty file")]
  [TestCase("const size = 4;", TestName = "No function: only a constant")]
  [TestCase("const f = function (a) { return a; };", TestName = "No function: function expression in a variable")]
  [TestCase("(function () { const A = { f: function (a) { return a; } }; })(); (function () { const B = { g: function (a) { return a; } }; })();", TestName = "No function: two IIFEs")]
  [TestCase("(() => ({ f: function (a) { return a; } }))();", TestName = "No function: IIFE without a body")]
  [TestCase("class C { f(a) { return a; } }", TestName = "No function: class")]
  [TestCase("export default function f(a) { return a; }", TestName = "No function: default export")]
  [TestCase("(function () { if (true) { function f(a) { return a; } } })();", TestName = "No function: declaration deeper inside an IIFE")]
  public void FileWithoutAFunction_IsRefused(string source)
    => Assert.That(Refusal(source).ToString(), Is.EqualTo("snippet.js(1,1): error PLY1102: 'a file without a function' is not supported yet"));

  /// <summary>
  /// A script shows all its declarations; a module only what it exports. With an export list the
  /// named functions are public, the ones they call come along as private ones, and nothing else
  /// is looked at.
  /// </summary>
  [Test]
  public void Visibility_FollowsTheFileOrTheExportList() {
    const string f = Ints + "function f(a, b) { return g(a, b); } ";
    const string g = Ints + "function g(a, b) { return h(b, a); } ";
    const string h = Ints + "function h(a, b) { return a; } ";
    const string broken = "function broken(a) { return this; }";
    static IEnumerable<(string, bool)> Shown(IrModule module) => module.Functions.Select(function => (function.Name, function.IsPublic));

    Assert.That(Shown(Import(h + g + f)), Is.EqualTo(new[] { ("h", true), ("g", true), ("f", true) }));
    Assert.That(Shown(Import(h + "export " + g + f)), Is.EqualTo(new[] { ("h", false), ("g", true), ("f", false) }));
    Assert.That(Shown(Import(h + "export " + g + f + broken, ["g"])), Is.EqualTo(new[] { ("h", false), ("g", true) }));
    Assert.That(Shown(Import(h + g + f + broken, ["f", "h", "f"])), Is.EqualTo(new[] { ("h", true), ("g", false), ("f", true) }));
    Assert.That(Refusal(h + g + f + broken).Message, Is.EqualTo("'this' is not supported yet"));
    Assert.That(Refusal(h + g + f + broken, []).Message, Is.EqualTo("'this' is not supported yet"));
  }

  /// <summary>A function declared inside the function that wraps the file is a helper: it imports, and nothing outside sees it.</summary>
  [Test]
  public void HelperInsideTheWrapper_IsAPrivateFunction() {
    var module = Import("(function () { function twice(x) { return x * 2; } const O = { f: function (a) { return twice(a | 0); } }; })();");
    Assert.That(module.Functions.Select(function => (function.Name, function.IsPublic)), Is.EqualTo(new[] { ("twice", false), ("f", true) }));
    Assert.That(module.Functions[0].Parameters.Single().Type, Is.EqualTo(IrType.I32));
    Assert.That(IrInterpreter.Call(module, "f", [21]), Is.EqualTo(42.0));
  }

  /// <summary>Functions of an object literal call each other through the variable the literal is assigned to.</summary>
  [Test]
  public void PropertyFunctions_CallEachOtherThroughTheirObject() {
    const string source = "const O = { " + Ints + "f: function (a, b) { return O.g(b, a); }, " + Ints + "g: function (a, b) { return a; } }; ";
    var module = Import(source, ["f"]);
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "f", "g" }));
    var call = (IrCall)((IrReturn)module.Functions[0].Body.Statements[0]).Value!;
    Assert.That((call.Function, call.Type), Is.EqualTo(("g", IrType.I32)));
    Assert.That(call.Arguments, Is.EqualTo(new IrExpr[] { new IrLocalRef(new IrLocal("b", IrType.I32)), A }));
    Assert.That(module.Functions[1].Body.Statements, Is.EqualTo(new IrStmt[] { new IrReturn(A) }));

    // A name that two functions share is refused for both: which one a call means is not for the importer to choose.
    const string twice = source + Ints + "function g(a, b) { return b; }";
    Assert.That(JavaScriptFrontend.Import("snippet.js", twice).Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "'a call of g, which cannot be imported' is not supported yet",
      "'a second function named g' is not supported yet",
      "'a second function named g' is not supported yet"
    }));
    Assert.That(Refusal(twice, ["g"]).Message, Is.EqualTo("'a second function named g' is not supported yet"));
    Assert.That(Refusal(Ints + "function h(a, b) { return a; } " + Ints + "function h(a, b) { return b; }", ["h"]).Column, Is.EqualTo(59));

    // A property is not reached by its bare name, nor a declaration through an object.
    Assert.That(Refusal("const O = { " + Ints + "f: function (a, b) { return g(b, a); }, " + Ints + "g: function (a, b) { return a; } };").Message,
      Is.EqualTo("'a call of g' is not supported yet"));
    Assert.That(Refusal(Ints + "function f(a, b) { return O.g(b, a); } " + Ints + "function g(a, b) { return a; }", ["f"]).Message,
      Is.EqualTo("'a call of O.g' is not supported yet"));
  }

  /// <summary>A function of a literal inside the literal is called through the properties that lead to it.</summary>
  [Test]
  public void NestedPropertyFunctions_AreCalledThroughTheirPath() {
    const string source = "const O = { Bits: { low: function (x) { return x & 0xFF; } }, f: function (a) { return O.Bits.low(a) + 1; } };";
    var module = Import(source);
    Assert.That(module.Functions.Select(function => function.Name), Is.EqualTo(new[] { "low", "f" }));
    Assert.That(IrInterpreter.Call(module, "f", [511.0]), Is.EqualTo(256.0));

    // The path has to be the one that leads to the function.
    Assert.That(Refusal("const O = { Bits: { low: function (x) { return x; } }, f: function (a) { return O.low(a); } };", ["f"]).Message,
      Is.EqualTo("'a call of O.low' is not supported yet"));
    Assert.That(Refusal("const O = { Bits: { low: function (x) { return x; } }, f: function (a) { return O[0].low(a); } };", ["f"]).Message,
      Is.EqualTo("'a call of O[0].low' is not supported yet"));
  }

  /// <summary>Test design 4: each JSDoc spelling of each type, for a parameter and for the result.</summary>
  [TestCase("int32", "i32")]
  [TestCase("uint32", "u32")]
  [TestCase("uint8", "u8")]
  [TestCase("int8", "i8")]
  [TestCase("int16", "i16")]
  [TestCase("uint16", "u16")]
  [TestCase("int64", "i64")]
  [TestCase("uint64", "u64")]
  [TestCase("number", "f64")]
  [TestCase("float64", "f64")]
  [TestCase("float32", "f32")]
  [TestCase("boolean", "bool")]
  [TestCase("uint8[]", "u8[]")]
  [TestCase("Uint8Array", "u8[]")]
  [TestCase("int32[]", "i32[]")]
  [TestCase("Int32Array", "i32[]")]
  [TestCase("uint32[]", "u32[]")]
  [TestCase("Uint32Array", "u32[]")]
  [TestCase("Int8Array", "i8[]")]
  [TestCase("Int16Array", "i16[]")]
  [TestCase("Uint16Array", "u16[]")]
  [TestCase("Float32Array", "f32[]")]
  [TestCase("Float64Array", "f64[]")]
  [TestCase("number[]", "f64[]")]
  [TestCase("BigInt64Array", "i64[]")]
  [TestCase("BigUint64Array", "u64[]")]
  [TestCase("uint64[]", "u64[]")]
  [TestCase(" int32 ", "i32")]
  public void JsDocType_IsMapped(string spelling, string type) {
    var function = Import($"/**\n * Returns what it is given.\n * @param {{{spelling}}} value - the value\n * @returns {{{spelling}}} the same value\n */\nfunction f(value) {{ return value; }}").Functions[0];
    Assert.That((function.Parameters[0].Type.ToString(), function.ReturnType.ToString()), Is.EqualTo((type, type)));
  }

  /// <summary>
  /// The result has the type of what is returned. What <c>@returns</c> states is taken where it holds
  /// every returned value - a wider integer, or a double for an integer - and is not believed where
  /// it does not.
  /// </summary>
  [TestCase("/** @param {int32} a */ function f(a) { }", "void")]
  [TestCase("/** @param {int32} a @returns {void} */ function f(a) { return; }", "void")]
  [TestCase("/** @param {int32} a @returns nothing */ function f(a) { }", "void")]
  [TestCase("/** @param {int32} a @return {uint32} */ function f(a) { return a >>> 0; }", "u32")]
  [TestCase("/** Nothing to say. */ function f() { }", "void")]
  [TestCase("function f() { }", "void")]
  [TestCase("function f() { return 1; }", "i32")]
  [TestCase("function f() { return 4294967295; }", "u32")]
  [TestCase("function f() { return 0.5; }", "f64")]
  [TestCase("function f() { return true; }", "bool")]
  [TestCase("function f() { return 1n; }", "i64")]
  [TestCase("function f() { return Math.fround(0.5); }", "f32")]
  [TestCase("/** @returns {number} */ function f() { return 1; }", "f64")]
  [TestCase("/** @returns {number} */ function f() { return Math.fround(0.5); }", "f64")]
  [TestCase("/** @param {uint8} a @returns {int32} */ function f(a) { return a; }", "i32")]
  [TestCase("/** @param {uint8} a @returns {uint32} */ function f(a) { return a; }", "u32")]
  [TestCase("/** @param {int32} a @returns {uint8} */ function f(a) { return a; }", "i32")]
  [TestCase("/** @param {int32} a @returns {int32} */ function f(a) { return a / 2; }", "f64")]
  [TestCase("/** @param {int32} a @returns {boolean} */ function f(a) { return a; }", "i32")]
  [TestCase("/** @returns {string} */ function f() { return 1; }", "i32")]
  [TestCase("/** @returns {int64} */ function f() { return 1; }", "i32")]
  [TestCase("/** @returns {int32} */ function f() { }", "void")]
  public void Result_HasTheTypeOfWhatIsReturned(string source, string type)
    => Assert.That(Import(source).Functions[0].ReturnType.ToString(), Is.EqualTo(type));

  /// <summary>Test design 12: a parameter whose JSDoc states a type the IR does not have is PLY1101, at the parameter.</summary>
  [TestCase("/** @param {BigInt} a */ function f(a) { }")]
  [TestCase("/** @param {symbol} a */ function f(a) { }")]
  [TestCase("/** @param {Int32} a */ function f(a) { }")]
  [TestCase("/** @param {{x: int32}} a */ function f(a) { }")]
  [TestCase("/** @param {boolean[]} a */ function f(a) { }")]
  [TestCase("/** @param {Uint8ClampedArray} a */ function f(a) { }")]
  public void ParameterWithAnUnsupportedType_IsRefused(string source) {
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.MissingType, "parameter 'a' of 'f' has no supported JSDoc type")));
    Assert.That(refusal.Column, Is.EqualTo(source.IndexOf("f(a)", System.StringComparison.Ordinal) + 3));
  }

  /// <summary>A parameter JSDoc says nothing about is a double: a comment that is no JSDoc, or names another parameter, states nothing.</summary>
  [TestCase("/** @param {int32} [a] */ function f(a) { }")]
  [TestCase("/** @param {int32} b */ function f(a) { }")]
  [TestCase("/** @param a {int32} */ function f(a) { }")]
  [TestCase("/** @returns {int32} */ function f(a) { }")]
  [TestCase("/* @param {int32} a */ function f(a) { }")]
  [TestCase("// @param {int32} a\nfunction f(a) { }")]
  [TestCase("/** @param {int32} a */ // in between\nfunction f(a) { }")]
  [TestCase("/** @param {int32} a */ const size = 4; function f(a) { }")]
  [TestCase("function f(a) { }")]
  public void ParameterWithoutAType_IsADouble(string source)
    => Assert.That(Import(source).Functions[0].Parameters.Single().Type, Is.EqualTo(IrType.F64));

  /// <summary>
  /// The body of Cipher's RotL32, node for node: the self-coercion of a <c>uint32</c> leaves nothing
  /// to do, the masking is an ordinary and, and each operator has the type JavaScript gives it.
  /// </summary>
  [Test]
  public void Import_TypesOperatorsTheWayJavaScriptDoes() {
    var function = Import("/** @param {uint32} value @param {int32} positions @returns {uint32} */ function RotL32(value, positions) {"
      + " value = value >>> 0; positions &= 31; return ((value << positions) | (value >>> (32 - positions))) >>> 0; }").Functions[0];
    var value = new IrLocal("value", IrType.U32);
    var positions = new IrLocal("positions", IrType.I32);
    Assert.That(function.Body.Statements, Is.EqualTo(new IrStmt[] {
      new IrAssign(positions, new IrBinary(IrBinaryOp.And, new IrLocalRef(positions), Int(31))),
      new IrReturn(new IrConvert(IrType.U32, new IrBinary(
        IrBinaryOp.Or,
        new IrBinary(IrBinaryOp.ShiftLeft, new IrConvert(IrType.I32, new IrLocalRef(value)), new IrLocalRef(positions)),
        new IrConvert(IrType.I32, new IrBinary(
          IrBinaryOp.ShiftRightLogical,
          new IrLocalRef(value),
          new IrBinary(IrBinaryOp.Subtract, Int(32), new IrLocalRef(positions)))))))
    }));
  }

  /// <summary>The value of one expression as IR, with the type the function then returns.</summary>
  private static IEnumerable<TestCaseData> Expressions() {
    static TestCaseData Case(string expression, string type, IrExpr expected) => new TestCaseData(expression, type, expected).SetName($"Expression: {expression}");
    IrExpr one = Double(1);

    // Arithmetic is double arithmetic; an integer operand is the double it is.
    yield return Case("a + 1", "f64", new IrBinary(IrBinaryOp.Add, AsDouble(A), one));
    yield return Case("a - u", "f64", new IrBinary(IrBinaryOp.Subtract, AsDouble(A), AsDouble(U)));
    yield return Case("a * c", "f64", new IrBinary(IrBinaryOp.Multiply, AsDouble(A), AsDouble(C)));
    yield return Case("a / 2", "f64", new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2)));
    yield return Case("a % 2", "f64", new IrBinary(IrBinaryOp.Remainder, AsDouble(A), Double(2)));
    yield return Case("-a", "f64", new IrUnary(IrUnaryOp.Negate, AsDouble(A)));
    yield return Case("+a", "i32", A);
    yield return Case("0.5", "f64", Double(0.5));
    yield return Case("-0", "f64", Double(-0.0));
    yield return Case("4294967296", "f64", Double(4294967296));
    yield return Case("-2147483649", "f64", Double(-2147483649));
    yield return Case("4294967295", "u32", new IrConst(IrType.U32, 4294967295));
    yield return Case("-2147483648", "i32", Int(-2147483648));

    // Under a coercion + and - of 32-bit integers are the wrapping operations of the IR; * never is.
    yield return Case("(a + 1) | 0", "i32", new IrBinary(IrBinaryOp.Add, A, Int(1)));
    yield return Case("(a - c) | 0", "i32", new IrBinary(IrBinaryOp.Subtract, A, new IrConvert(IrType.I32, C)));
    yield return Case("(u + a) >>> 0", "u32", new IrBinary(IrBinaryOp.Add, U, new IrConvert(IrType.U32, A)));
    yield return Case("(a + u + 1) | 0", "i32", new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Add, A, new IrConvert(IrType.I32, U)), Int(1)));
    yield return Case("-a | 0", "i32", new IrBinary(IrBinaryOp.Subtract, Int(0), A));
    yield return Case("(a + 0.5) | 0", "i32", ToInt32(new IrBinary(IrBinaryOp.Add, AsDouble(A), Double(0.5))));
    yield return Case("(a * a) | 0", "i32", ToInt32(new IrBinary(IrBinaryOp.Multiply, AsDouble(A), AsDouble(A))));
    yield return Case("(a * a + 1) | 0", "i32", ToInt32(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Multiply, AsDouble(A), AsDouble(A)), one)));
    yield return Case("(a * a) >>> 0", "u32", new IrIntrinsic(IrIntrinsicOp.ToUint32, IrType.U32, [new IrBinary(IrBinaryOp.Multiply, AsDouble(A), AsDouble(A))]));
    yield return Case("Math.imul(a, u)", "i32", new IrBinary(IrBinaryOp.Multiply, A, new IrConvert(IrType.I32, U)));
    yield return Case("Math.imul(a + 1, 3)", "i32", new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Add, A, Int(1)), Int(3)));

    // A division of integers of one signedness under a coercion is the division of the IR.
    yield return Case("(a / 2) | 0", "i32", new IrBinary(IrBinaryOp.Divide, A, Int(2)));
    yield return Case("(a % c) | 0", "i32", new IrBinary(IrBinaryOp.Remainder, A, new IrConvert(IrType.I32, C)));
    yield return Case("(u / 3) >>> 0", "u32", new IrBinary(IrBinaryOp.Divide, U, new IrConst(IrType.U32, 3)));
    yield return Case("(u / c) | 0", "i32", new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Divide, U, new IrConvert(IrType.U32, C))));
    yield return Case("(a / u) | 0", "i32", ToInt32(new IrBinary(IrBinaryOp.Divide, AsDouble(A), AsDouble(U))));
    yield return Case("(a / 2 + 1) | 0", "i32", ToInt32(new IrBinary(IrBinaryOp.Add, new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2)), one)));

    // The bitwise operators take ToInt32 of their operands, >>> ToUint32 of its left one.
    yield return Case("a | u", "i32", new IrBinary(IrBinaryOp.Or, A, new IrConvert(IrType.I32, U)));
    yield return Case("a ^ c", "i32", new IrBinary(IrBinaryOp.Xor, A, new IrConvert(IrType.I32, C)));
    yield return Case("a << 3", "i32", new IrBinary(IrBinaryOp.ShiftLeft, A, Int(3)));
    yield return Case("a >> c", "i32", new IrBinary(IrBinaryOp.ShiftRight, A, new IrConvert(IrType.I32, C)));
    yield return Case("a >>> 3", "u32", new IrBinary(IrBinaryOp.ShiftRightLogical, new IrConvert(IrType.U32, A), Int(3)));
    yield return Case("a >>> 0", "u32", new IrConvert(IrType.U32, A));
    yield return Case("u | 0", "i32", new IrConvert(IrType.I32, U));
    yield return Case("~u", "i32", new IrUnary(IrUnaryOp.BitNot, new IrConvert(IrType.I32, U)));
    yield return Case("(a / 3) | 1", "i32", new IrBinary(IrBinaryOp.Or, new IrBinary(IrBinaryOp.Divide, A, Int(3)), Int(1)));
    yield return Case("0.5 | 0", "i32", ToInt32(Double(0.5)));
    yield return Case("Math.clz32(a)", "i32", new IrIntrinsic(IrIntrinsicOp.LeadingZeroCount, IrType.I32, [new IrConvert(IrType.U32, A)]));

    // A mask proves a byte or a 16-bit integer; the operation itself is one of 32 bits.
    yield return Case("a & 0xFF", "u8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, A, Int(255))));
    yield return Case("a & 0xFFFF", "u16", new IrConvert(IrType.U16, new IrBinary(IrBinaryOp.And, A, Int(65535))));
    yield return Case("a & 0x1FFFF", "i32", new IrBinary(IrBinaryOp.And, A, Int(0x1FFFF)));
    yield return Case("a & -1", "i32", new IrBinary(IrBinaryOp.And, A, Int(-1)));
    yield return Case("a & c", "u8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, A, new IrConvert(IrType.I32, C))));
    yield return Case("c | 0x0F", "u8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Or, new IrConvert(IrType.I32, C), Int(15))));
    yield return Case("c ^ 0x100", "u16", new IrConvert(IrType.U16, new IrBinary(IrBinaryOp.Xor, new IrConvert(IrType.I32, C), Int(256))));
    yield return Case("c | a", "i32", new IrBinary(IrBinaryOp.Or, new IrConvert(IrType.I32, C), A));

    // Integers of one 32-bit type are compared as such, anything else as doubles.
    yield return Case("a < 3", "bool", new IrBinary(IrBinaryOp.Less, A, Int(3)));
    yield return Case("a <= c", "bool", new IrBinary(IrBinaryOp.LessOrEqual, A, new IrConvert(IrType.I32, C)));
    yield return Case("u > c", "bool", new IrBinary(IrBinaryOp.Greater, U, new IrConvert(IrType.U32, C)));
    yield return Case("u >= 4294967295", "bool", new IrBinary(IrBinaryOp.GreaterOrEqual, U, new IrConst(IrType.U32, 4294967295)));
    yield return Case("a === -1", "bool", new IrBinary(IrBinaryOp.Equal, A, Int(-1)));
    yield return Case("a !== u", "bool", new IrBinary(IrBinaryOp.NotEqual, AsDouble(A), AsDouble(U)));
    yield return Case("a == u", "bool", new IrBinary(IrBinaryOp.Equal, AsDouble(A), AsDouble(U)));
    yield return Case("u != -1", "bool", new IrBinary(IrBinaryOp.NotEqual, AsDouble(U), Double(-1)));
    yield return Case("a < 0.5", "bool", new IrBinary(IrBinaryOp.Less, AsDouble(A), Double(0.5)));
    yield return Case("t === true", "bool", new IrBinary(IrBinaryOp.Equal, T, new IrConst(IrType.Bool, 1)));
    yield return Case("t != false", "bool", new IrBinary(IrBinaryOp.NotEqual, T, new IrConst(IrType.Bool, 0)));

    // A condition: a truth value as it is, an integer against zero, a double by its magnitude.
    yield return Case("!t", "bool", new IrUnary(IrUnaryOp.LogicalNot, T));
    yield return Case("!a", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.NotEqual, A, Int(0))));
    yield return Case("!c", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.NotEqual, new IrConvert(IrType.I32, C), Int(0))));
    yield return Case("!u", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.NotEqual, U, new IrConst(IrType.U32, 0))));
    yield return Case("!(a / 2)", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.Greater, new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2))]), Double(0))));
    yield return Case("t && a < 1", "bool", new IrBinary(IrBinaryOp.LogicalAnd, T, new IrBinary(IrBinaryOp.Less, A, Int(1))));
    yield return Case("t || !t", "bool", new IrBinary(IrBinaryOp.LogicalOr, T, new IrUnary(IrUnaryOp.LogicalNot, T)));
    yield return Case("!(a && t)", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.LogicalAnd, new IrBinary(IrBinaryOp.NotEqual, A, Int(0)), T)));

    // && and || of numbers yield one of the operands; ?: the type that holds both branches.
    yield return Case("a || 7", "i32", new IrConditional(new IrBinary(IrBinaryOp.NotEqual, A, Int(0)), A, Int(7)));
    yield return Case("a && u", "f64", new IrConditional(new IrBinary(IrBinaryOp.NotEqual, A, Int(0)), AsDouble(U), AsDouble(A)));
    yield return Case("t ? a : c", "i32", new IrConditional(T, A, new IrConvert(IrType.I32, C)));
    yield return Case("t ? a : u", "f64", new IrConditional(T, AsDouble(A), AsDouble(U)));
    yield return Case("t ? 1 : 0.5", "f64", new IrConditional(T, one, Double(0.5)));
    yield return Case("a ? t : !t", "bool", new IrConditional(new IrBinary(IrBinaryOp.NotEqual, A, Int(0)), T, new IrUnary(IrUnaryOp.LogicalNot, T)));

    // Arrays: an integer index as it is, a double one without its fraction and trapping.
    yield return Case("d[a]", "u8", new IrArrayLoad(D, A));
    yield return Case("d[u]", "u8", new IrArrayLoad(D, U));
    yield return Case("d[c]", "u8", new IrArrayLoad(D, new IrConvert(IrType.I32, C)));
    yield return Case("d[a + 1]", "u8", new IrArrayLoad(D, new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Add, AsDouble(A), one), IrConversion.Trap)));
    yield return Case("d[(a + 1) | 0]", "u8", new IrArrayLoad(D, new IrBinary(IrBinaryOp.Add, A, Int(1))));
    yield return Case("d.length", "i32", new IrArrayLength(D));
    yield return Case("new Uint8Array(3).length", "i32", new IrArrayLength(new IrNewArray(IrType.Array(IrType.U8), Int(3))));
    yield return Case("new Int32Array(u).length", "i32", new IrArrayLength(new IrNewArray(IrType.Array(IrType.I32), U)));
    yield return Case("new Uint8Array(a / 2).length", "i32", new IrArrayLength(new IrNewArray(IrType.Array(IrType.U8), new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2)), IrConversion.Saturate))));
    yield return Case("new Float32Array([0.5, a])[1]", "f64", AsDouble(new IrArrayLoad(
      new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.F32), [new IrConvert(IrType.F32, Double(0.5)), new IrConvert(IrType.F32, AsDouble(A))]), Int(1))));
    yield return Case("new Uint8Array([a, 300, 0.5])[0]", "u8", new IrArrayLoad(
      new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [new IrConvert(IrType.U8, A), new IrConst(IrType.U8, 44), new IrConvert(IrType.U8, ToInt32(Double(0.5)))]), Int(0)));
    yield return Case("[1, a][0]", "i32", new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I32), [Int(1), A]), Int(0)));
    yield return Case("[1, 0.5][0]", "f64", new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.F64), [one, Double(0.5)]), Int(0)));
    yield return Case("[c, 7][0]", "u8", new IrArrayLoad(new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.U8), [C, new IrConst(IrType.U8, 7)]), Int(0)));

    // Math, with JavaScript's meaning.
    yield return Case("Math.round(a / 2)", "f64", new IrIntrinsic(IrIntrinsicOp.RoundHalfUp, IrType.F64, [new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2))]));
    yield return Case("Math.trunc(a)", "f64", new IrIntrinsic(IrIntrinsicOp.Truncate, IrType.F64, [AsDouble(A)]));
    yield return Case("Math.floor(0.5)", "f64", new IrIntrinsic(IrIntrinsicOp.Floor, IrType.F64, [Double(0.5)]));
    yield return Case("Math.ceil(0.5)", "f64", new IrIntrinsic(IrIntrinsicOp.Ceiling, IrType.F64, [Double(0.5)]));
    yield return Case("Math.abs(a / 2)", "f64", new IrIntrinsic(IrIntrinsicOp.Abs, IrType.F64, [new IrBinary(IrBinaryOp.Divide, AsDouble(A), Double(2))]));
    yield return Case("Math.abs(a)", "u32", new IrConvert(IrType.U32, new IrIntrinsic(IrIntrinsicOp.AbsWrap, IrType.I32, [A])));
    yield return Case("Math.sqrt(u)", "f64", new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [AsDouble(U)]));
    yield return Case("Math.fround(a)", "f32", new IrConvert(IrType.F32, AsDouble(A)));
    yield return Case("Math.min(a, 0.5)", "f64", new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [AsDouble(A), Double(0.5)]));
    yield return Case("Math.max(a, u, c)", "f64", new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [AsDouble(A), AsDouble(U)]), AsDouble(C)]));
    yield return Case("Math.max(0.5)", "f64", Double(0.5));
    yield return Case("Math.max(a)", "i32", A);
    yield return Case("Math.max()", "f64", Double(double.NegativeInfinity));
    yield return Case("Math.min()", "f64", Double(double.PositiveInfinity));
    yield return Case("Math.sign(0.5)", "f64", new IrIntrinsic(IrIntrinsicOp.Max, IrType.F64, [
      new IrIntrinsic(IrIntrinsicOp.Min, IrType.F64, [
        new IrBinary(IrBinaryOp.Multiply, new IrBinary(IrBinaryOp.Multiply, Double(0.5), Double(8.98846567431158e307)), Double(8.98846567431158e307)), one]),
      Double(-1)]));
    yield return Case("Math.sign(a)", "i32", new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [A]));

    // 64-bit integers under their two wrappers.
    yield return Case("123n", "i64", new IrConst(IrType.I64, 123));
    yield return Case("-1n", "i64", new IrConst(IrType.I64, -1));
    yield return Case("18446744073709551615n", "u64", new IrConst(IrType.U64, -1));
    yield return Case("BigInt.asUintN(64, 3n * 5n)", "u64", new IrBinary(IrBinaryOp.Multiply, new IrConst(IrType.U64, 3), new IrConst(IrType.U64, 5)));
    yield return Case("BigInt.asIntN(64, BigInt(a) << 3n)", "i64", new IrBinary(IrBinaryOp.ShiftLeft, new IrConvert(IrType.I64, A), Int(3)));
    yield return Case("BigInt.asUintN(64, BigInt(u) >> 1n)", "u64", new IrConvert(IrType.U64, new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(IrType.I64, U), Int(1))));
    yield return Case("BigInt.asUintN(64, -BigInt(c))", "u64", new IrBinary(IrBinaryOp.Subtract, new IrConst(IrType.U64, 0), new IrConvert(IrType.U64, new IrConvert(IrType.I64, C))));
    yield return Case("BigInt.asIntN(64, ~7n)", "i64", new IrUnary(IrUnaryOp.BitNot, new IrConst(IrType.I64, 7)));
    yield return Case("BigInt.asIntN(64, 7n / -2n)", "i64", new IrBinary(IrBinaryOp.Divide, new IrConst(IrType.I64, 7), new IrConst(IrType.I64, -2)));
    yield return Case("BigInt.asUintN(64, 7n % 2n)", "u64", new IrConvert(IrType.U64, new IrBinary(IrBinaryOp.Remainder, new IrConst(IrType.I64, 7), new IrConst(IrType.I64, 2))));
    yield return Case("BigInt.asUintN(64, 5n)", "u64", new IrConst(IrType.U64, 5));
    yield return Case("1n < 2n", "bool", new IrBinary(IrBinaryOp.Less, new IrConst(IrType.I64, 1), new IrConst(IrType.I64, 2)));
    yield return Case("18446744073709551615n === 2n", "bool", new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.U64, -1), new IrConst(IrType.U64, 2)));
    yield return Case("!5n", "bool", new IrUnary(IrUnaryOp.LogicalNot, new IrBinary(IrBinaryOp.NotEqual, new IrConst(IrType.I64, 5), new IrConst(IrType.I64, 0))));
  }

  /// <summary>Test design 3 and 5: each accepted expression becomes the IR that JavaScript's rules give it.</summary>
  [TestCaseSource(nameof(Expressions))]
  public void Expression_IsLoweredByJavaScriptsRules(string expression, string type, IrExpr expected) {
    var function = Import(WithBody($"return {expression};", "")).Functions[0];
    Assert.That(function.ReturnType.ToString(), Is.EqualTo(type));
    var returned = ((IrReturn)function.Body.Statements[0]).Value!;
    AssertSame(returned, returned.Type == expected.Type ? expected : new IrConvert(returned.Type, expected));
  }

  /// <summary>Test design 11: every construct outside the subset is PLY1102, with its line and column.</summary>
  [TestCase("return 'text' - a;", "- on string and number")]
  [TestCase("return null;", "null")]
  [TestCase("return /x/.length;", "a regular expression")]
  [TestCase("return `${a}`;", "number as a template hole, which converts a number to text, not supported until number-to-text conversion")]
  [TestCase("return this;", "this")]
  [TestCase("const p = { ...d }; return 1;", "a spread in an object literal (...)")]
  [TestCase("({ x: 1 }); return 1;", "an object literal")]
  [TestCase("return (a, u);", "the comma operator")]
  [TestCase("return (a = 1);", "an assignment inside an expression")]
  [TestCase("return a?.x;", "?.")]
  [TestCase("return function () { };", "a function inside a function")]
  [TestCase("return () => 1;", "a function inside a function")]
  [TestCase("return class { };", "class")]
  [TestCase("return zz;", "zz")]
  [TestCase("return undefined;", "undefined")]
  [TestCase("return arguments.length;", "arguments")]
  [TestCase("return a.x;", "a.x")]
  [TestCase("return d.x;", "d.x")]
  [TestCase("return a[0];", "a as an array, which is number")]
  [TestCase("return a.length;", "a as an array, which is number")]
  [TestCase("return d[t];", "boolean as an index")]
  [TestCase("return d[d];", "uint8[] as an index")]
  [TestCase("return typeof a;", "typeof")]
  [TestCase("return void a;", "void")]
  [TestCase("return delete a.x;", "delete")]
  [TestCase("return +t;", "unary + on boolean")]
  [TestCase("return -t;", "boolean as a number")]
  [TestCase("return ~t;", "boolean as a number")]
  [TestCase("return t | 0;", "| on boolean and number")]
  [TestCase("return t ^ a;", "^ on boolean and number")]
  [TestCase("return a & t;", "& on number and boolean")]
  [TestCase("return t << 1;", "<< on boolean and number")]
  [TestCase("return t >> 1;", ">> on boolean and number")]
  [TestCase("return a - t;", "- on number and boolean")]
  [TestCase("return t * 2;", "* on boolean and number")]
  [TestCase("return t / 2;", "/ on boolean and number")]
  [TestCase("return t % 2;", "% on boolean and number")]
  [TestCase("return t != 1;", "!= on boolean and number")]
  [TestCase("return t !== d;", "!== on boolean and uint8[]")]
  [TestCase("return t <= 1;", "<= on boolean and number")]
  [TestCase("return t > 1;", "> on boolean and number")]
  [TestCase("return t >= 1;", ">= on boolean and number")]
  [TestCase("return new.target;", "new.target")]
  [TestCase("with (d) { } return a;", "with")]
  [TestCase("const e = new Array(2); return e[0] + 1;", "+ on nothing and number")]
  [TestCase("return BigInt.asUintN(64, zz >> 1n);", "zz")]
  [TestCase("return [function () { g(); }] || [1];", "a function inside a function")]
  [TestCase("return t >>> 0;", ">>> on boolean and number")]
  [TestCase("return d + 1;", "+ on uint8[] and number")]
  [TestCase("return t ** 2;", "** on boolean and number")]
  [TestCase("return a in d;", "in on number and uint8[]")]
  [TestCase("return a instanceof d;", "instanceof on number and uint8[]")]
  [TestCase("return t === a;", "=== on boolean and number")]
  [TestCase("return t == 1;", "== on boolean and number")]
  [TestCase("return t < t;", "< on boolean and boolean")]
  [TestCase("return d === d;", "=== on uint8[] and uint8[]")]
  [TestCase("return a ?? 1;", "??")]
  [TestCase("return t && a;", "&& on boolean and number")]
  [TestCase("return d || d;", "uint8[] as a condition")]
  [TestCase("return f(a, u, c, t, d) || 1;", "|| as a value whose left operand has an effect")]
  [TestCase("let x = 0; return x++ || 1;", "|| as a value whose left operand has an effect")]
  [TestCase("return t ? a : t;", "?: on number and boolean")]
  [TestCase("return t ? d : a;", "?: on uint8[] and number")]
  [TestCase("if (d) return 1; return 0;", "uint8[] as a condition")]
  [TestCase("return f(...d);", "a spread argument")]
  [TestCase("return f(a);", "a call of f with 1 arguments for 5 parameters")]
  [TestCase("return g(a);", "a call of g")]
  [TestCase("return a(1);", "a call of a")]
  [TestCase("return d.reverse();", "a call of d.reverse")]
  [TestCase("d.push(1); return a;", "a call of d.push")]
  [TestCase("return d.pop();", "a call of d.pop")]
  [TestCase("return Math.imul(a);", "Math.imul with 1 arguments")]
  [TestCase("return Math.clz32();", "Math.clz32 with 0 arguments")]
  [TestCase("return Math.fround(a, a);", "Math.fround with 2 arguments")]
  [TestCase("return Math.round();", "Math.round with 0 arguments")]
  [TestCase("return Math.sign(a, a);", "Math.sign with 2 arguments")]
  [TestCase("return Math.random();", "Math.random")]
  [TestCase("return Math.sin(a, a);", "Math.sin with 2 arguments")]
  [TestCase("return Math.PI(a);", "Math.PI")]
  [TestCase("return Math.imul(t, a);", "boolean as a number")]
  [TestCase("return Math.abs(d);", "uint8[] as a number")]
  [TestCase("return BigInt.asUintN(65, 1n);", "BigInt.asUintN with a width other than a literal from 1 to 64")]
  [TestCase("return BigInt.asIntN(a, 1n);", "BigInt.asIntN with a width other than a literal from 1 to 64")]
  [TestCase("return BigInt.asUintN(64);", "BigInt.asUintN with 1 arguments")]
  [TestCase("return BigInt.asUintN(64, a);", "number as a bigint")]
  [TestCase("return BigInt.asUintN(64, 1n << 64n);", "<< on bigint and bigint")]
  [TestCase("return BigInt.asUintN(64, 1n << -1n);", "<< on bigint and bigint")]
  [TestCase("return BigInt.asUintN(64, 1n >> BigInt(a));", ">> on bigint and bigint")]
  [TestCase("return BigInt.asUintN(64, 1n >>> 1n);", ">>> on bigint and bigint")]
  [TestCase("return BigInt.asUintN(64, 1n ** 2n);", "** on bigint and bigint")]
  [TestCase("return BigInt.asUintN(64, 1n + a);", "+ on bigint and number")]
  [TestCase("return BigInt.asUintN(64, a + 1n);", "+ on number and bigint")]
  [TestCase("return BigInt.asUintN(64, 18446744073709551615n / -1n);", "/ on bigint and bigint")]
  [TestCase("return 18446744073709551615n < -1n;", "< on bigint and bigint")]
  [TestCase("return 1n < a;", "< on bigint and number")]
  [TestCase("return BigInt(a / 2);", "BigInt of number that is no proven integer")]
  [TestCase("return BigInt(1n);", "BigInt of bigint that is no proven integer")]
  [TestCase("return BigInt();", "BigInt with 0 arguments")]
  [TestCase("return BigInt.parse(a);", "BigInt.parse")]
  [TestCase("return new Uint8Array(t).length;", "an array of the length boolean")]
  [TestCase("return new Uint8Array().length;", "new Uint8Array with anything but one length or a list of elements")]
  [TestCase("return new Uint8Array(...d).length;", "new Uint8Array with anything but one length or a list of elements")]
  [TestCase("return new Uint8Array(1, 2).length;", "new Uint8Array with anything but one length or a list of elements")]
  [TestCase("return new Uint8Array(d).length;", "an array of the length uint8[]")]
  [TestCase("return new Uint8Array([1, , 2]).length;", "an array literal with a hole or a spread element")]
  [TestCase("return new Uint8Array([1n]).length;", "bigint as a number")]
  [TestCase("return new BigInt64Array([1]).length;", "number as an element of a int64 array")]
  [TestCase("return new Array(1, 2).length;", "new Array with anything but one length")]
  [TestCase("return new Array(a / 2).length;", "new Array of a length that is no proven integer, which is number")]
  [TestCase("return new Array(t).length;", "new Array of a length that is no proven integer, which is boolean")]
  [TestCase("return new Array(...d).length;", "new Array with anything but one length")]
  [TestCase("return new Map().size;", "new Map().size")]
  [TestCase("const m = new Map(); return a;", "new Map")]
  [TestCase("const m = new a.b(); return a;", "new a.b")]
  [TestCase("return [1, ...d].length;", "an array literal with a hole or a spread element")]
  [TestCase("return [[1], [2]].length;", "int32[] where float64 is expected")]
  [TestCase("return [1, true].length;", "boolean where float64 is expected")]
  [TestCase("const x = 'text' * 2; return 1;", "* on string and number")]
  [TestCase("const [x] = d; return 1;", "a destructuring declaration")]
  [TestCase("const [x] = d; return 1;", "a destructuring declaration")]
  [TestCase("let x = 1; { let x = 2; } return x;", "a second declaration of x")]
  [TestCase("let x$ = 1; return x$;", "the name x$")]
  [TestCase("const x = 1; x = 2; return x;", "an assignment to the constant x")]
  [TestCase("const x = 1; x += 2; return x;", "an assignment to the constant x")]
  [TestCase("const x = 1; x++; return x;", "an assignment to the constant x")]
  [TestCase("const x = 1; return x++;", "an assignment to the constant x")]
  [TestCase("zz = 1; return a;", "zz")]
  [TestCase("a.x = 1; return a;", "a.x")]
  [TestCase("d.length = 1; return a;", "d.length")]
  [TestCase("const p = new Proxy(d, d); return a;", "new Proxy")]
  [TestCase("function* g() { yield 1; } return a;", "a function inside a function")]
  [TestCase("try { a = 1; } catch (e) { a = 2; } return a;", "try")]
  [TestCase("[a, u] = [u, a]; return a;", "[a, u]")]
  [TestCase("t **= 2; return a;", "** on boolean and number")]
  [TestCase("a ??= 2; return a;", "??=")]
  [TestCase("a &&= 2; return a;", "&&=")]
  [TestCase("a ||= 2; return a;", "||=")]
  [TestCase("a[0] = 1; return a;", "a as an array, which is number")]
  [TestCase("d[t] = 1; return a;", "boolean as an index")]
  [TestCase("d[0] = t; return a;", "boolean as a number")]
  [TestCase("d[0] = d; return a;", "uint8[] as a number")]
  [TestCase("d[0] = 1n; return a;", "bigint as a number")]
  [TestCase("zz++; return a;", "zz")]
  [TestCase("a.x++; return a;", "a.x++")]
  [TestCase("return ++d[0];", "a prefix ++ or -- on an array element inside an expression")]
  [TestCase("return a.x++;", "a.x++")]
  [TestCase("a; return a;", "a as a statement")]
  [TestCase("a + 1; return a;", "a + 1 as a statement")]
  [TestCase("'use strict' + a; return a;", "'use strict' + a as a statement")]
  [TestCase("new Uint8Array(1); return a;", "new Uint8Array(1) as a statement")]
  [TestCase("throw a;", "throw")]
  [TestCase("try { } finally { } return a;", "try")]
  [TestCase("for (const x in d) { } return a;", "for-in")]
  [TestCase("for (const x of d) { } return a;", "a for...of over uint8[]")]
  [TestCase("outer: while (t) { } return a;", "a label")]
  [TestCase("debugger; return a;", "debugger")]
  [TestCase("class X { } return a;", "class")]
  [TestCase("function g() { } return a;", "a function inside a function")]
  [TestCase("switch (d) { } return a;", "a switch on uint8[]")]
  [TestCase("switch (a / 2) { } return a;", "a switch on number")]
  [TestCase("switch (t) { } return a;", "a switch on boolean")]
  [TestCase("switch (a) { case u: return 1; } return a;", "the case label u")]
  [TestCase("switch (a) { case 0.5: return 1; } return a;", "the case label 0.5")]
  [TestCase("switch (a) { case 4294967295: return 1; } return a;", "the case label 4294967295")]
  [TestCase("switch (u) { case -1: return 1; } return a;", "the case label -1")]
  [TestCase("switch (c) { case 1 + 1: return 1; } return a;", "the case label 1 + 1")]
  [TestCase("switch (a) { case 1: return 1; case 1: return 2; } return a;", "a second case label 1")]
  [TestCase("switch (a) { case 1: a = 2; case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("switch (a) { case 1: if (t) break; case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("switch (a) { case 1: if (t) break; else a = 2; case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("switch (a) { case 1: if (t) a = 2; else break; case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("switch (a) { case 1: { } case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("switch (a) { default: a = 2; case 2: return 2; } return a;", "a switch section that falls through into the next")]
  [TestCase("return g();", "a call of g")]
  public void UnsupportedConstruct_IsRefusedWithItsPosition(string body, string what) {
    var refusal = Refusal(WithBody(body, ""));
    Assert.That((refusal.Code, refusal.Message, refusal.File, refusal.Line), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet", "snippet.js", 1)));
    Assert.That(refusal.Column, Is.GreaterThan(Mixed.Length - "@returns {int32}".Length));
  }

  /// <summary>Test design 11: what makes a whole function unsupported, reported at the function or the parameter.</summary>
  [TestCase("function* f() { }", "a generator", 1)]
  [TestCase("async function f() { }", "an async function", 1)]
  [TestCase("function f(a = 1) { }", "a default parameter value", 12)]
  [TestCase("function f(...a) { }", "a rest parameter", 12)]
  [TestCase("function f({ a }) { }", "a destructured parameter", 12)]
  [TestCase("/** @param {int32} a */ function f(a, a) { }", "a second parameter named a", 39)]
  [TestCase("/** @param {int32} a$ */ function f(a$) { }", "the name a$", 37)]
  [TestCase("const O = { get f() { return 1; } };", "a getter", 13)]
  [TestCase("const O = { set f(v) { } };", "a setter", 13)]
  [TestCase("const O = { f: () => 1 };", "an arrow function", 13)]
  [TestCase("/** @param {boolean} t */ function f(t) { if (t) return 1; }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { while (t) { return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { if (t) { return 1; } else { t = false; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { while (true) { if (t) break; return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { for (;;) { while (t) { break; } return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {int32} a */ function f(a) { switch (a) { case 1: return 1; } }", "a function that can end without returning its value", 25)]
  [TestCase("/** @param {int32} a */ function f(a) { switch (a) { case 1: return 1; default: a = 2; } }", "a function that can end without returning its value", 25)]
  [TestCase("/** @param {boolean} t */ function f(t) { do { if (t) break; return 1; } while (t); }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { for (;;) { do { if (t) break; } while (t); return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { for (;;) { for (; t; t = !t) { if (t) continue; break; } return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { for (;;) { switch (1) { case 1: if (t) break; t = !t; break; } return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {boolean} t */ function f(t) { while (true) { if (t) { t = !t; } else { break; } return 1; } }", "a function that can end without returning its value", 27)]
  [TestCase("/** @param {int32} i */ function g(i) { return i; } function f() { const e = new Array(2); return g(e[0]); }", "float64 where int32 is expected", 101)]
  [TestCase("function g() { } function f() { return g(); }", "void as the value returned", 40)]
  [TestCase("function g() { } function f() { const x = g(); }", "void as the value of a variable", 43)]
  [TestCase("function g() { } /** @param {boolean} t */ function f(t) { const x = t ? g() : g(); }", "void as the value of a variable", 70)]
  [TestCase("function g() { } /** @param {boolean} t */ function f(t) { return (t ? g() : g()) + 1; }", "?: on undefined and undefined", 68)]
  [TestCase("function g() { } function f() { return g() + 1; }", "+ on undefined and number", 40)]
  [TestCase("/** @param {int32} g */ function f(g) { g(1); } function g() { }", "a call of g", 41)]
  [TestCase("/** @param {int32} O */ function f(O) { O.g(); } const O = { g: function () { } };", "a call of O.g", 41)]
  [TestCase("/** @param {int32} Math */ function f(Math) { return Math.imul(1, 2); }", "a call of Math.imul", 54)]
  [TestCase("/** @param {int32} BigInt */ function f(BigInt) { return BigInt(1); }", "a call of BigInt", 58)]
  [TestCase("/** @param {int32} Uint8Array */ function f(Uint8Array) { const n = new Uint8Array(1); }", "new Uint8Array", 69)]
  [TestCase("/** @param {int32} Array */ function f(Array) { const n = new Array(1); }", "new Array", 59)]
  [TestCase("/** @param {uint32} u */ function g(u) { return u; } /** @param {int32} a */ function f(a) { return g(a); }", "int32 where uint32 is expected", 103)]
  [TestCase("/** @param {int32} i */ function g(i) { return i; } function f() { return g(0.5); }", "float64 where int32 is expected", 77)]
  [TestCase("/** @param {uint8} c */ function g(c) { return c; } function f() { return g(256); }", "int32 where uint8 is expected", 77)]
  [TestCase("/** @param {int64} n */ function g(n) { return n; } function f() { return g(18446744073709551615n); }", "uint64 where int64 is expected", 77)]
  [TestCase("/** @param {float32} x */ function g(x) { return x; } function f() { return g(1); }", "int32 where float32 is expected", 79)]
  [TestCase("/** @param {Uint8Array} d */ function g(d) { return d.length; } function f() { return g(new Int8Array(1)); }", "int8[] where uint8[] is expected", 89)]
  [TestCase("const N = 1; function f() { N = 2; }", "an assignment to the constant N", 29)]
  [TestCase("const N = 1; function f() { N++; }", "an assignment to the constant N", 29)]
  [TestCase("const N = 1; function f() { return N++; }", "an assignment to the constant N", 36)]
  public void UnsupportedFunction_IsRefusedWithItsPosition(string source, string what, int column) {
    var refusal = JavaScriptFrontend.Import("snippet.js", source, ["f"]).Diagnostics.Single();
    Assert.That(refusal.ToString(), Is.EqualTo($"snippet.js(1,{column}): error PLY1102: '{what}' is not supported yet"));
  }

  /// <summary>A function is one of the file under a plain name only, and one without a <c>$</c>.</summary>
  [TestCase("const O = { 'f': function () { } };", "a computed or quoted property name", 13)]
  [TestCase("const O = { ['f']: function () { } };", "a computed or quoted property name", 13)]
  [TestCase("const O = { 1: function () { } };", "a computed or quoted property name", 13)]
  [TestCase("function $f() { }", "the name $f", 1)]
  [TestCase("const O = { f$: function () { } };", "the name f$", 13)]
  public void FunctionWithoutAPlainName_IsRefused(string source, string what, int column)
    => Assert.That(Refusal(source).ToString(), Is.EqualTo($"snippet.js(1,{column}): error PLY1102: '{what}' is not supported yet"));

  /// <summary>A function never ends without its value when it returns on every path, or loops until it returns.</summary>
  [TestCase("if (t) return 1; else return 2;")]
  [TestCase("{ if (t) { return 1; } else { { return 2; } } }")]
  [TestCase("return 1; a = 2;")]
  [TestCase("while (true) { if (t) return 1; }")]
  [TestCase("for (;;) { if (t) continue; return 1; }")]
  [TestCase("switch (a) { case 1: return 1; default: return 2; }")]
  [TestCase("switch (a) { case 1: case 2: if (t) return 1; else return 3; default: { return 2; } }")]
  [TestCase("switch (a) { case 1: if (t) { break; } else { return 3; } default: return 2; } return 4;")]
  [TestCase("while (true) { if (t) { return 1; } else { a = 2; } }")]
  [TestCase("for (;;) { while (t) { a = 1; } do { a = 2; } while (t); for (; t; a = (a + 1) | 0) { continue; } switch (a) { case 1: a = 3; } return 1; }")]
  public void FunctionThatAlwaysReturns_IsAccepted(string body)
    => Assert.That(Lowered(body, ""), Is.Not.Empty);

  /// <summary>Test design 9: a syntax error is PLY1100 with the parser's message and position.</summary>
  [TestCase("function f( {", "Unexpected end of input", 1, 14)]
  [TestCase("function f() {\n  return 1 +;\n}", "Unexpected token ';'", 2, 13)]
  [TestCase("export function f() { }\nfunction g( {", "Unexpected end of input", 2, 14)]
  [TestCase("with (x) { }\nfunction g( {", "Unexpected end of input", 2, 14)]
  [TestCase("function f(a) { let a = 1; }", "Identifier 'a' has already been declared", 1, 21)]
  public void SyntaxError_IsReportedWithTheParsersMessage(string source, string message, int line, int column)
    => Assert.That(Refusal(source).ToString(), Is.EqualTo($"snippet.js({line},{column}): error PLY1100: {message}"));

  /// <summary>A file is a script or a module: what only one of the two allows is accepted where it stands.</summary>
  [TestCase("with (Math) { }\n" + Ints + "function f(a, b) { return a; }")]
  [TestCase("import { x } from './x.mjs';\n" + Ints + "export function f(a, b) { return a; }")]
  public void ScriptAndModule_AreBothParsed(string source)
    => Assert.That(Import(source).Functions.Single().Name, Is.EqualTo("f"));

  /// <summary>Statements become the IR statements they are; a for without a continue is a block around a while.</summary>
  [Test]
  public void Statements_AreLowered() {
    var x = new IrLocal("x", IrType.I32);
    var i = new IrLocal("i", IrType.F64);
    AssertSame(Lowered("let x = 1; if (t) { x = 2; } else x = 3; while (t) { x = (x + 1) | 0; } ; { } return x;"), new IrStmt[] {
      new IrDeclare(x, Int(1)),
      new IrIf(T, new IrBlock([new IrAssign(x, Int(2))]), new IrBlock([new IrAssign(x, Int(3))])),
      new IrWhile(T, new IrBlock([new IrAssign(x, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), Int(1)))])),
      new IrBlock([]),
      new IrReturn(new IrLocalRef(x))
    });

    var loop = new IrBlock([
      new IrDeclare(i, Double(0)),
      new IrWhile(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), AsDouble(A)), new IrBlock([
        new IrStore(D, new IrConvert(IrType.I32, new IrLocalRef(i), IrConversion.Trap), new IrConst(IrType.U8, 1)),
        new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), Double(1)))
      ]))
    ]);
    AssertSame(Lowered("for (let i = 0; i < a; i++) d[i] = 1; return a;"), new IrStmt[] { loop, new IrReturn(A) });
    var decrementing = ((IrWhile)loop.Statements[1]) with {
      Body = new IrBlock([((IrWhile)loop.Statements[1]).Body.Statements[0], new IrAssign(i, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(i), Double(1)))])
    };
    AssertSame(Lowered("for (let i = 0; i < a; --i) d[i] = 1; return a;")[0], loop with { Statements = [loop.Statements[0], decrementing] });
  }

  /// <summary>Test design 6: the loops and jumps the IR has of its own.</summary>
  [Test]
  public void LoopsAndJumps_AreTheOnesOfTheIr() {
    var x = new IrLocal("x", IrType.I32);
    var next = new IrAssign(x, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), Int(1)));
    AssertSame(Lowered("let x = 0; do { x = (x + 1) | 0; } while (t); return x;")[1], new IrDoWhile(new IrBlock([next]), T));
    AssertSame(Lowered("let x = 0; while (t) { if (x > 3) break; if (x > 1) continue; } return x;")[1], new IrWhile(T, new IrBlock([
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(x), Int(3)), new IrBlock([new IrBreak()]), null),
      new IrIf(new IrBinary(IrBinaryOp.Greater, new IrLocalRef(x), Int(1)), new IrBlock([new IrContinue()]), null)
    ])));

    // With a continue the update must also follow a pass the continue ended: that is the for of the IR.
    AssertSame(Lowered("let x = 0; for (; t; x = (x + 1) | 0) { if (t) continue; } return x;")[1], new IrBlock([
      new IrFor(T, new IrBlock([new IrIf(T, new IrBlock([new IrContinue()]), null)]), new IrBlock([next]))
    ]));
    AssertSame(Lowered("let x = 0; for (;;) { return x; }")[1], new IrBlock([
      new IrWhile(new IrConst(IrType.Bool, 1), new IrBlock([new IrReturn(new IrLocalRef(x))]))
    ]));

    // The parts of a for may hold several expressions.
    var y = new IrLocal("y", IrType.I32);
    AssertSame(Lowered("let x = 0; let y = 0; for (x = 1, y = 2; t; x = 3, y = 4) { } return x;")[2], new IrBlock([
      new IrAssign(x, Int(1)),
      new IrAssign(y, Int(2)),
      new IrWhile(T, new IrBlock([new IrAssign(x, Int(3)), new IrAssign(y, Int(4))]))
    ]));
  }

  /// <summary>Test design 6: a switch has one section per group of labels, without the break that ends it.</summary>
  [Test]
  public void Switch_HasOneSectionPerGroupOfLabels() {
    var x = new IrLocal("x", IrType.I32);
    var statement = (IrSwitch)Lowered("let x = 0; switch (c) { case 1: x = 1; break; case 2: case 3: default: x = 2; break; case 4: return 4; case 5: } return x;")[1];
    AssertSame(statement.Value, new IrConvert(IrType.I32, C));
    Assert.That(statement.Sections.Select(section => (string.Join(",", section.Labels), section.IsDefault)), Is.EqualTo(new[] { ("1", false), ("2,3", true), ("4", false), ("5", false) }));
    AssertSame(statement.Sections.Select(section => section.Body), new[] {
      new IrBlock([new IrAssign(x, Int(1))]),
      new IrBlock([new IrAssign(x, Int(2))]),
      new IrBlock([new IrReturn(Int(4))]),
      new IrBlock([])
    });
    AssertSame(((IrSwitch)Lowered("switch (u) { case 4294967295: return 1; } return 0;")[0]).Value, U);
    Assert.That(((IrSwitch)Lowered("const k = 3; switch (a) { case k: return 1; case -4: return 2; } return 0;")[1]).Sections.Select(section => section.Labels.Single()), Is.EqualTo(new long[] { 3, -4 }));
  }

  /// <summary>What a compound assignment to an element reads and writes is evaluated once.</summary>
  [Test]
  public void CompoundElementAssignment_EvaluatesItsTargetOnce() {
    var statements = Lowered("d[(a + 1) | 0] ^= 0x1FF; d[1] += 1; d[a] = u; return a;");
    var temporary = new IrLocal("$t0", IrType.I32);
    AssertSame(statements, new IrStmt[] {
      new IrDeclare(temporary, new IrBinary(IrBinaryOp.Add, A, Int(1))),
      new IrStore(D, new IrLocalRef(temporary), new IrConvert(IrType.U8, new IrBinary(
        IrBinaryOp.Xor, new IrConvert(IrType.I32, new IrArrayLoad(D, new IrLocalRef(temporary))), Int(0x1FF)))),
      new IrStore(D, Int(1), new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Add, new IrConvert(IrType.I32, new IrArrayLoad(D, Int(1))), Int(1)))),
      new IrStore(D, A, new IrConvert(IrType.U8, U)),
      new IrReturn(A)
    });

    // An array that is not a variable goes into a temporary as well.
    var array = new IrLocal("$t1", IrType.Array(IrType.U8));
    var chosen = Lowered("const e = d; (t ? d : e)[0] |= 1; return a;");
    AssertSame(chosen[1], new IrDeclare(array, new IrConditional(T, D, new IrLocalRef(new IrLocal("e", IrType.Array(IrType.U8))))));
    AssertSame(((IrStore)chosen[2]).Array, new IrLocalRef(array));
  }

  /// <summary>Test design 6: a step is an assignment as a statement, the step of the IR inside an expression and on an element.</summary>
  [Test]
  public void Steps_AreAssignmentsOrTheStepOfTheIr() {
    var x = new IrLocal("x", IrType.F64);
    var statements = Lowered("let x = 0; x++; --x; const y = x++ + --x; d[0]++; --d[1]; return d[2]++;");
    AssertSame(statements[1], new IrAssign(x, new IrBinary(IrBinaryOp.Add, new IrLocalRef(x), Double(1))));
    AssertSame(statements[2], new IrAssign(x, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(x), Double(1))));
    AssertSame(((IrDeclare)statements[3]).Initializer, new IrBinary(IrBinaryOp.Add, new IrStep(new IrLocalRef(x), true, true, false), new IrStep(new IrLocalRef(x), false, false, false)));
    AssertSame(statements[4], new IrExprStmt(new IrStep(new IrArrayLoad(D, Int(0)), true, true, false)));
    AssertSame(statements[5], new IrExprStmt(new IrStep(new IrArrayLoad(D, Int(1)), false, true, false)));
    AssertSame(statements[6], new IrReturn(new IrConvert(IrType.I32, new IrStep(new IrArrayLoad(D, Int(2)), true, true, false))));
  }

  /// <summary>
  /// A parameter keeps the type it is called with. When the body assigns it something else, the body
  /// works on a variable of its own, which starts with the argument.
  /// </summary>
  [Test]
  public void ParameterTheBodyWidens_GetsAVariableOfItsOwn() {
    var function = Import(Ints + "function f(a, b) { a = a / 2; b = b | 1; return a + b; }").Functions[0];
    var widened = new IrLocal("a$0", IrType.F64);
    var b = new IrLocal("b", IrType.I32);
    AssertSame(function.Parameters, new[] { _a, b });
    AssertSame(function.Locals, new[] { widened });
    AssertSame(function.Body.Statements, new IrStmt[] {
      new IrDeclare(widened, AsDouble(A)),
      new IrAssign(widened, new IrBinary(IrBinaryOp.Divide, new IrLocalRef(widened), Double(2))),
      new IrAssign(b, new IrBinary(IrBinaryOp.Or, new IrLocalRef(b), Int(1))),
      new IrReturn(new IrBinary(IrBinaryOp.Add, new IrLocalRef(widened), AsDouble(new IrLocalRef(b))))
    });
  }

  /// <summary>A local has the type of everything assigned to it and ends with its block.</summary>
  [Test]
  public void Locals_TakeTheTypeOfWhatIsAssignedAndEndWithTheirBlock() {
    static string Types(string body) => string.Join(" ", Import(WithBody(body, "")).Functions[0].Locals.Select(local => $"{local.Name}:{local.Type}"));
    Assert.That(Types("const x = a; var y = u; let z = c; const s = t; const e = d; return a;"), Is.EqualTo("x:i32 y:u32 z:u8 s:bool e:u8[]"));
    Assert.That(Types("let x = 0; x = 5; let y = 0; y = c; let z = 0; z = -1; z = u; return a;"), Is.EqualTo("x:i32 y:u8 z:f64"));
    Assert.That(Types("let x = c; x = a & 0x7FFF; let y = c; y = -1; let z = 4294967295; z = 1; return a;"), Is.EqualTo("x:u16 y:i16 z:u32"));
    Assert.That(Types("let x; let y; y = a | 0; let z = Math.fround(a); let w = Math.fround(1); w = 0.5; return a;"), Is.EqualTo("x:f64 y:i32 z:f32 w:f64"));
    Assert.That(Types("let x = 1n; x = BigInt.asUintN(64, x); let y = -1n; y = BigInt.asIntN(64, y); return a;"), Is.EqualTo("x:u64 y:i64"));
    Assert.That(Types("if (t) { const x = a; } else { const x = u; } { const x = c; } return a;"), Is.EqualTo("x:i32 x:u32 x:u8"));
    Assert.That(Types("const x = [1, 2]; const y = [1, 2]; y[0] = 0.5; const z = new Array(2); z[0] = c; const w = new Array(2); const v = []; return a;"),
      Is.EqualTo("x:i32[] y:f64[] z:u8[] w:f64[] v:f64[]"));
    Assert.That(Refusal(WithBody("if (t) { const x = a; } return x;", "")).Message, Is.EqualTo("'x' is not supported yet"));
    Assert.That(Refusal(WithBody("for (let i = 0; t; ) { } return i;", "")).Message, Is.EqualTo("'i' is not supported yet"));
    Assert.That(Refusal(WithBody("switch (a) { case 1: const x = 1; break; default: return x; } return a;", "")).Message, Is.EqualTo("'x' is not supported yet"));
  }

  /// <summary>Test design 4: a function nothing outside can call has the parameter types its callers pass, and its callers convert to them.</summary>
  [Test]
  public void PrivateFunction_HasTheParameterTypesItsCallersPass() {
    const string helper = "function g(x, y) { return y; } ";
    static string Signature(string source) {
      var function = Import(source).Functions.Single(candidate => candidate.Name == "g");
      return $"{string.Join(",", function.Parameters.Select(parameter => parameter.Type))}->{function.ReturnType}";
    }

    Assert.That(Signature(helper + "export function f(a) { return g(a | 0, a & 0xFF); }"), Is.EqualTo("i32,u8->u8"));
    Assert.That(Signature(helper + "export function f(a) { return g(a | 0, 1) + g(a >>> 0, 300); }"), Is.EqualTo("f64,i32->i32"));
    Assert.That(Signature(helper + "export function f(a) { return g(a | 0, 1) + g(a / 2, 0.5); }"), Is.EqualTo("f64,f64->f64"));
    Assert.That(Signature(helper + "export function f(a) { return g(1, true); }"), Is.EqualTo("i32,bool->bool"));
    Assert.That(Signature(helper + "export function f(a) { return g(1, new Uint8Array(1)).length; }"), Is.EqualTo("i32,u8[]->u8[]"));
    Assert.That(Signature("function g(x, y) { return x <= 1 ? y : g(x - 1, y); } export function f(a) { return g(a | 0, 1n); }"), Is.EqualTo("f64,i64->i64"));
    Assert.That(Signature("function g(x, y) { return g(x, y); } export function f(a) { return 1; }"), Is.EqualTo("f64,f64->f64"));

    // An argument is converted to the type of the parameter where that changes nothing.
    var call = (IrCall)((IrReturn)Import(helper + "export function f(a) { return g(a | 0, 1) + g(a / 2, 0.5); }").Functions[1].Body.Statements[0]).Value!.GetType()
      .GetProperty(nameof(IrBinary.Left))!.GetValue(((IrReturn)Import(helper + "export function f(a) { return g(a | 0, 1) + g(a / 2, 0.5); }").Functions[1].Body.Statements[0]).Value)!;
    AssertSame(call.Arguments[1], Double(1));
    AssertSame(call.Arguments[0], AsDouble(ToInt32(new IrLocalRef(new IrLocal("a", IrType.F64)))));
  }

  /// <summary>A function that is refused is refused once, and its callers are not blamed for it.</summary>
  [Test]
  public void RefusedCallee_IsReportedOnceAndInSourceOrder() {
    const string source = "function f(a) { return g(a) + g(a); }\nfunction g(a) { return this; }\nfunction h(a) { return g(a); }";
    Assert.That(JavaScriptFrontend.Import("snippet.js", source).Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] {
      "snippet.js(2,24): error PLY1102: 'this' is not supported yet"
    }));
    Assert.That(Refusal(source, ["h"]).Line, Is.EqualTo(2));

    // A function that cannot be imported whatever its body is, is refused for that, and so is every call of it.
    Assert.That(JavaScriptFrontend.Import("snippet.js", "function g(a = 1) { } function f() { g(1); }", ["f"]).Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] {
      "snippet.js(1,12): error PLY1102: 'a default parameter value' is not supported yet",
      "snippet.js(1,38): error PLY1102: 'a call of g, which cannot be imported' is not supported yet"
    }));
  }

  /// <summary>The caveats name the differences a module can show, each once, and only the ones it can show.</summary>
  [TestCase("return a;", new string[0])]
  [TestCase("return d[0] | d[1];", new[] { "js-oob-read" })]
  [TestCase("d[0] = 1; d[1] = 2; return a;", new[] { "js-oob-read" })]
  [TestCase("d[0]++; return a;", new[] { "js-oob-read" })]
  [TestCase("return ((a / a) | 0) + ((a % a) | 0);", new[] { "js-div-zero" })]
  [TestCase("return a / a + a % a;", new string[0])]
  [TestCase("return BigInt.asUintN(64, 1n / 1n);", new[] { "js-div-zero" })]
  [TestCase("return BigInt.asUintN(64, 1n >> 1n);", new string[0])]
  [TestCase("a++; u--; return a;", new string[0])]
  [TestCase("for (let i = 0; i < d.length; ++i) d[i] = (d[i] / 2) | 0; return a;", new[] { "js-oob-read", "js-div-zero" })]
  public void Caveats_NameTheDifferencesOfTheModule(string body, string[] expected) {
    var result = JavaScriptFrontend.Import("snippet.js", WithBody(body, ""));
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Is.EqualTo(expected));
    Assert.That(result.Caveats.Select(caveat => caveat.Description), Has.All.Length.GreaterThan(40));
    Assert.That((JavaScriptFrontend.OutOfRangeAccess, JavaScriptFrontend.DivisionByZero, JavaScriptFrontend.IntegerParameter, JavaScriptFrontend.ArrayParameter),
      Is.EqualTo(("js-oob-read", "js-div-zero", "js-param-integer-inferred", "js-param-array-inferred")));
  }

  /// <summary>Test design 13: a file with nothing to import is PLY1102; with an export list, PLY1104 for every name.</summary>
  [Test]
  public void FileWithNothingToImport_IsRefused() {
    Assert.That(Refusal("const size = 4;").Code, Is.EqualTo(JavaScriptFrontend.Unsupported));
    Assert.That(Refusal("const size = 4;", ["size"]).ToString(), Is.EqualTo("snippet.js(1,1): error PLY1104: the file has no function 'size' to export"));
    Assert.That(Refusal("const size = 4;", ["size"]).Code, Is.EqualTo(JavaScriptFrontend.UnknownExport));
    Assert.That(Refusal("function f( {").Code, Is.EqualTo(JavaScriptFrontend.SyntaxError));
  }

  /// <summary>The result, the caveat and the explanation are plain data.</summary>
  [Test]
  public void ImportResult_CarriesModuleDiagnosticsCaveatsAndExplanations() {
    var caveat = new ImportCaveat("id", "text");
    var explanation = new TypeExplanation("f", "local", "x", IrType.U32, "x >>> 0", 2, 5);
    var result = new ImportResult(null, [], [caveat], [explanation]);
    Assert.That((result.Module, result.Diagnostics.Count, result.Caveats.Single().Id, caveat.Description), Is.EqualTo(((IrModule?)null, 0, "id", "text")));
    Assert.That(result.Explanations.Single().ToString(), Is.EqualTo("f: local x: u32 from 'x >>> 0' at (2,5)"));
    Assert.That((explanation with { Role = "result", Name = "" }).ToString(), Is.EqualTo("f: result: u32 from 'x >>> 0' at (2,5)"));
  }

}
