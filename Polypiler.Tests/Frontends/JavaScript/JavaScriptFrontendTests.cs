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

  /// <summary>Every fixture imports into IR the verifier accepts, named after its file.</summary>
  [TestCaseSource(typeof(JavaScriptFixture), nameof(JavaScriptFixture.Names))]
  public void Fixture_ImportsIntoWellTypedIr(string fixture) {
    var result = JavaScriptFrontend.Import(JavaScriptFixture.PathOf(fixture), JavaScriptFixture.SourceOf(fixture));
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(result.Module!.Name, Is.EqualTo(fixture));
    Assert.That(IrVerifier.Verify(result.Module), Is.Empty);
  }

  /// <summary>Scenario: An uncoerced sum is refused.</summary>
  [Test]
  public void UncoercedSum_IsRefused() {
    var refusal = Refusal(Ints + "function f(a, b) { return a + b; }");
    Assert.That(refusal.ToString(), Is.EqualTo("snippet.js(1,85): error PLY1103: the result of 'a + b' is a JavaScript number; coerce it with | 0 or >>> 0"));
    Assert.That((refusal.Code, refusal.Line), Is.EqualTo((JavaScriptFrontend.NotAnInteger, 1)));
  }

  /// <summary>Scenario: A coerced sum wraps.</summary>
  [Test]
  public void CoercedSum_Wraps() {
    var module = Import(Ints + "function f(a, b) { return (a + b) | 0; }");
    Assert.That(IrInterpreter.Call(module, "f", [2147483647, 1]), Is.EqualTo(-2147483648));
  }

  /// <summary>Scenario: Plain multiplication is refused, Math.imul is accepted.</summary>
  [Test]
  public void PlainMultiplication_IsRefused_MathImulIsAccepted() {
    var refusal = Refusal(Ints + "function f(a, b) { return (a * b) | 0; }");
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo(("PLY1103", "the result of 'a * b' is a JavaScript number; coerce it with | 0 or >>> 0")));

    var module = Import(Ints + "function f(a, b) { return Math.imul(a, b); }");
    Assert.That(IrInterpreter.Call(module, "f", [65537, 65537]), Is.EqualTo(131073));
  }

  /// <summary>Scenario: A function without JSDoc types is refused.</summary>
  [Test]
  public void FunctionWithoutJsDocTypes_IsRefused()
    => Assert.That(Refusal("function f(a) { return a | 0; }").ToString(),
      Is.EqualTo("snippet.js(1,12): error PLY1101: parameter 'a' of 'f' has no supported JSDoc type"));

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
  [TestCase("const O = { inner: { f: function (a) { return a; } } };", TestName = "No function: nested object literal")]
  [TestCase("(function () { function f(a) { return a; } })();", TestName = "No function: declaration inside an IIFE")]
  [TestCase("(function () { const A = { f: function (a) { return a; } }; })(); (function () { const B = { g: function (a) { return a; } }; })();", TestName = "No function: two IIFEs")]
  [TestCase("(() => ({ f: function (a) { return a; } }))();", TestName = "No function: IIFE without a body")]
  [TestCase("class C { f(a) { return a; } }", TestName = "No function: class")]
  [TestCase("export default function f(a) { return a; }", TestName = "No function: default export")]
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
    const string broken = "function broken(a) { return a; }";
    static IEnumerable<(string, bool)> Shown(IrModule module) => module.Functions.Select(function => (function.Name, function.IsPublic));

    Assert.That(Shown(Import(h + g + f)), Is.EqualTo(new[] { ("h", true), ("g", true), ("f", true) }));
    Assert.That(Shown(Import(h + "export " + g + f)), Is.EqualTo(new[] { ("h", false), ("g", true), ("f", false) }));
    Assert.That(Shown(Import(h + "export " + g + f + broken, ["g"])), Is.EqualTo(new[] { ("h", false), ("g", true) }));
    Assert.That(Shown(Import(h + g + f + broken, ["f", "h", "f"])), Is.EqualTo(new[] { ("h", true), ("g", false), ("f", true) }));
    Assert.That(Refusal(h + g + f + broken).Message, Is.EqualTo("parameter 'a' of 'broken' has no supported JSDoc type"));
    Assert.That(Refusal(h + g + f + broken, []).Message, Is.EqualTo("parameter 'a' of 'broken' has no supported JSDoc type"));
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

  /// <summary>Test design 4: each JSDoc spelling of each type, for a parameter and for the result.</summary>
  [TestCase("int32", "i32")]
  [TestCase("uint32", "u32")]
  [TestCase("uint8", "u8")]
  [TestCase("boolean", "bool")]
  [TestCase("uint8[]", "u8[]")]
  [TestCase("Uint8Array", "u8[]")]
  [TestCase("int32[]", "i32[]")]
  [TestCase("Int32Array", "i32[]")]
  [TestCase("uint32[]", "u32[]")]
  [TestCase("Uint32Array", "u32[]")]
  [TestCase(" int32 ", "i32")]
  public void JsDocType_IsMapped(string spelling, string type) {
    var function = Import($"/**\n * Returns what it is given.\n * @param {{{spelling}}} value - the value\n * @returns {{{spelling}}} the same value\n */\nfunction f(value) {{ return value; }}").Functions[0];
    Assert.That((function.Parameters[0].Type.ToString(), function.ReturnType.ToString()), Is.EqualTo((type, type)));
  }

  /// <summary>A function without <c>@returns</c>, or with <c>void</c>, returns nothing; <c>@return</c> is the same tag.</summary>
  [TestCase("/** @param {int32} a */ function f(a) { }", "void")]
  [TestCase("/** @param {int32} a @returns {void} */ function f(a) { return; }", "void")]
  [TestCase("/** @param {int32} a @returns nothing */ function f(a) { }", "void")]
  [TestCase("/** @param {int32} a @return {uint32} */ function f(a) { return a >>> 0; }", "u32")]
  [TestCase("/** Nothing to say. */ function f() { }", "void")]
  [TestCase("function f() { }", "void")]
  public void JsDocResult_IsReadOrAbsent(string source, string type)
    => Assert.That(Import(source).Functions[0].ReturnType.ToString(), Is.EqualTo(type));

  /// <summary>Test design 10: a parameter with a type the IR does not have yet, or with none, is PLY1101.</summary>
  [TestCase("/** @param {number} a */ function f(a) { }")]
  [TestCase("/** @param {BigInt} a */ function f(a) { }")]
  [TestCase("/** @param {string} a */ function f(a) { }")]
  [TestCase("/** @param {uint16} a */ function f(a) { }")]
  [TestCase("/** @param {int8} a */ function f(a) { }")]
  [TestCase("/** @param {int16} a */ function f(a) { }")]
  [TestCase("/** @param {int64} a */ function f(a) { }")]
  [TestCase("/** @param {uint64} a */ function f(a) { }")]
  [TestCase("/** @param {Int32} a */ function f(a) { }")]
  [TestCase("/** @param {{x: int32}} a */ function f(a) { }")]
  [TestCase("/** @param {int32} [a] */ function f(a) { }")]
  [TestCase("/** @param {int32} b */ function f(a) { }")]
  [TestCase("/** @param a {int32} */ function f(a) { }")]
  [TestCase("/** @returns {int32} */ function f(a) { }")]
  [TestCase("/* @param {int32} a */ function f(a) { }")]
  [TestCase("// @param {int32} a\nfunction f(a) { }")]
  [TestCase("/** @param {int32} a */ // in between\nfunction f(a) { }")]
  [TestCase("/** @param {int32} a */ const size = 4; function f(a) { }")]
  [TestCase("function f(a) { }")]
  public void ParameterWithoutSupportedType_IsRefused(string source) {
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.MissingType, "parameter 'a' of 'f' has no supported JSDoc type")));
    Assert.That(refusal.Column, Is.EqualTo(source[(source.LastIndexOf('\n') + 1)..].IndexOf("f(a)", System.StringComparison.Ordinal) + 3));
  }

  /// <summary>Test design 10: a result with a type the IR does not have yet is PLY1101, at the function.</summary>
  [TestCase("number")]
  [TestCase("BigInt")]
  [TestCase("string")]
  [TestCase("uint16")]
  [TestCase("(h: uint32, l: uint32)")]
  public void ResultWithoutSupportedType_IsRefused(string spelling)
    => Assert.That(Refusal($"const O = {{\n  /** @returns {{{spelling}}} */\n  f: function () {{ return 1; }}\n}};").ToString(),
      Is.EqualTo("snippet.js(3,3): error PLY1101: the result of 'f' has no supported JSDoc type"));

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

  /// <summary>The value of one expression as IR, by the type of the result it is returned as.</summary>
  private static IEnumerable<TestCaseData> Expressions() {
    static TestCaseData Case(string expression, string result, IrExpr expected) => new TestCaseData(expression, result, expected).SetName($"Expression: {expression} as {result}");

    // Literals: an int32 where it is one, otherwise a uint32; a constant takes the type it is used as when it fits.
    yield return Case("2147483647", "int32", Int(int.MaxValue));
    yield return Case("-2147483648", "int32", Int(int.MinValue));
    yield return Case("0x7F", "int32", Int(127));
    yield return Case("2147483648", "uint32", new IrConst(IrType.U32, 2147483648));
    yield return Case("4294967295", "uint32", new IrConst(IrType.U32, uint.MaxValue));
    yield return Case("7", "uint32", new IrConst(IrType.U32, 7));
    yield return Case("255", "uint8", new IrConst(IrType.U8, 255));
    yield return Case("true", "boolean", new IrConst(IrType.Bool, 1));
    yield return Case("false", "boolean", new IrConst(IrType.Bool, 0));

    // The coercions: | 0 is ToInt32 and >>> 0 is ToUint32, each the value itself where it already has the type.
    yield return Case("a | 0", "int32", A);
    yield return Case("u | 0", "int32", new IrConvert(IrType.I32, U));
    yield return Case("c | 0", "int32", new IrConvert(IrType.I32, C));
    yield return Case("a >>> 0", "uint32", new IrConvert(IrType.U32, A));
    yield return Case("u >>> 0", "uint32", U);
    yield return Case("c >>> 0", "uint32", new IrConvert(IrType.U32, C));
    yield return Case("(a + 1) >>> 0", "uint32", new IrConvert(IrType.U32, new IrBinary(IrBinaryOp.Add, A, Int(1))));
    yield return Case("0 | 0", "int32", Int(0));
    yield return Case("7 >>> 0", "uint32", new IrConst(IrType.U32, 7));
    yield return Case("-1 >>> 0", "uint32", new IrConst(IrType.U32, uint.MaxValue));

    // Bitwise operators and shifts: ToInt32 of both operands, also of a constant, which wraps exactly.
    yield return Case("a ^ 0xEDB88320", "int32", new IrBinary(IrBinaryOp.Xor, A, Int(-306674912)));
    yield return Case("u | a", "int32", new IrBinary(IrBinaryOp.Or, new IrConvert(IrType.I32, U), A));
    yield return Case("~u", "int32", new IrUnary(IrUnaryOp.BitNot, new IrConvert(IrType.I32, U)));
    yield return Case("a << u", "int32", new IrBinary(IrBinaryOp.ShiftLeft, A, new IrConvert(IrType.I32, U)));
    yield return Case("u >> c", "int32", new IrBinary(IrBinaryOp.ShiftRight, new IrConvert(IrType.I32, U), new IrConvert(IrType.I32, C)));
    yield return Case("a >>> 1", "uint32", new IrBinary(IrBinaryOp.ShiftRightLogical, new IrConvert(IrType.U32, A), Int(1)));
    yield return Case("a >>> a", "uint32", new IrBinary(IrBinaryOp.ShiftRightLogical, new IrConvert(IrType.U32, A), A));

    // + and - are computed modulo 2^32, which is all the coercion around them can see; -x is 0 - x.
    yield return Case("(a - u + c) | 0", "int32", new IrBinary(
      IrBinaryOp.Add,
      new IrBinary(IrBinaryOp.Subtract, A, new IrConvert(IrType.I32, U)),
      new IrConvert(IrType.I32, C)));
    yield return Case("(-a) | 0", "int32", new IrBinary(IrBinaryOp.Subtract, Int(0), A));
    yield return Case("Math.imul(u, 3)", "int32", new IrBinary(IrBinaryOp.Multiply, new IrConvert(IrType.I32, U), Int(3)));

    // Division is one of two forms, on operands of one signedness; the remainder keeps the type of its operands.
    yield return Case("(a / 3) | 0", "int32", new IrBinary(IrBinaryOp.Divide, A, Int(3)));
    yield return Case("(u / 3) >>> 0", "uint32", new IrBinary(IrBinaryOp.Divide, U, new IrConst(IrType.U32, 3)));
    yield return Case("(u / c) | 0", "int32", new IrConvert(IrType.I32, new IrBinary(IrBinaryOp.Divide, U, new IrConvert(IrType.U32, C))));
    yield return Case("(c / c) >>> 0", "uint32", new IrConvert(IrType.U32, new IrBinary(IrBinaryOp.Divide, new IrConvert(IrType.I32, C), new IrConvert(IrType.I32, C))));
    yield return Case("7 % a", "int32", new IrBinary(IrBinaryOp.Remainder, Int(7), A));
    yield return Case("u % 7", "uint32", new IrBinary(IrBinaryOp.Remainder, U, new IrConst(IrType.U32, 7)));

    // Comparisons: a uint8 or a constant that fits takes the type of the other operand.
    yield return Case("c < a", "boolean", new IrBinary(IrBinaryOp.Less, new IrConvert(IrType.I32, C), A));
    yield return Case("u <= c", "boolean", new IrBinary(IrBinaryOp.LessOrEqual, U, new IrConvert(IrType.U32, C)));
    yield return Case("u > 0", "boolean", new IrBinary(IrBinaryOp.Greater, U, new IrConst(IrType.U32, 0)));
    yield return Case("4294967295 >= u", "boolean", new IrBinary(IrBinaryOp.GreaterOrEqual, new IrConst(IrType.U32, uint.MaxValue), U));
    yield return Case("1 === u", "boolean", new IrBinary(IrBinaryOp.Equal, new IrConst(IrType.U32, 1), U));
    yield return Case("c !== c", "boolean", new IrBinary(IrBinaryOp.NotEqual, new IrConvert(IrType.I32, C), new IrConvert(IrType.I32, C)));
    yield return Case("t === true", "boolean", new IrBinary(IrBinaryOp.Equal, T, new IrConst(IrType.Bool, 1)));
    yield return Case("1 < 4294967295", "boolean", new IrBinary(IrBinaryOp.Less, new IrConst(IrType.U32, 1), new IrConst(IrType.U32, uint.MaxValue)));

    // Truth values.
    yield return Case("!t && (t || a > 1)", "boolean", new IrBinary(
      IrBinaryOp.LogicalAnd,
      new IrUnary(IrUnaryOp.LogicalNot, T),
      new IrBinary(IrBinaryOp.LogicalOr, T, new IrBinary(IrBinaryOp.Greater, A, Int(1)))));
    yield return Case("t ? a : 7", "int32", new IrConditional(T, A, Int(7)));
    yield return Case("t ? 7 : u", "uint32", new IrConditional(T, new IrConst(IrType.U32, 7), U));
    yield return Case("t ? c : a", "int32", new IrConditional(T, new IrConvert(IrType.I32, C), A));
    yield return Case("(t ? d : d).length", "int32", new IrArrayLength(new IrConditional(T, D, D)));

    // A uint8 is a result where the value cannot leave 0…255: a uint8, a mask that small, or a choice of two such.
    yield return Case("c", "uint8", C);
    yield return Case("a & 0xFF", "uint8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, A, Int(255))));
    yield return Case("15 & a", "uint8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, Int(15), A)));
    yield return Case("a & c", "uint8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, A, new IrConvert(IrType.I32, C))));
    yield return Case("c | d[0]", "uint8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Or, new IrConvert(IrType.I32, C), new IrConvert(IrType.I32, new IrArrayLoad(D, Int(0))))));
    yield return Case("c ^ c", "uint8", new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Xor, new IrConvert(IrType.I32, C), new IrConvert(IrType.I32, C))));
    yield return Case("c | 0", "uint8", new IrConvert(IrType.U8, new IrConvert(IrType.I32, C)));
    yield return Case("c >>> 0", "uint8", new IrConvert(IrType.U8, new IrConvert(IrType.U32, C)));
    yield return Case("t ? a & 1 : 200", "uint8", new IrConvert(IrType.U8, new IrConditional(T, new IrBinary(IrBinaryOp.And, A, Int(1)), Int(200))));
    yield return Case("t ? c : 9", "uint8", new IrConditional(T, C, new IrConst(IrType.U8, 9)));

    // Arrays: any integer is an index, and a typed array takes its length as one.
    yield return Case("d[a]", "uint8", new IrArrayLoad(D, A));
    yield return Case("d[u]", "int32", new IrConvert(IrType.I32, new IrArrayLoad(D, U)));
    yield return Case("d[c]", "uint32", new IrConvert(IrType.U32, new IrArrayLoad(D, new IrConvert(IrType.I32, C))));
    yield return Case("d[a - 1]", "uint8", new IrArrayLoad(D, new IrBinary(IrBinaryOp.Subtract, A, Int(1))));
    yield return Case("d.length", "int32", new IrArrayLength(D));
    yield return Case("new Uint8Array(a)", "uint8[]", new IrNewArray(IrType.Array(IrType.U8), A));
    yield return Case("new Int32Array(u)", "int32[]", new IrNewArray(IrType.Array(IrType.I32), U));
    yield return Case("new Uint32Array(c)", "Uint32Array", new IrNewArray(IrType.Array(IrType.U32), new IrConvert(IrType.I32, C)));
    yield return Case("new Uint8Array(16)", "Uint8Array", new IrNewArray(IrType.Array(IrType.U8), Int(16)));
  }

  /// <summary>Test design 2 and 4: each accepted expression becomes the IR that JavaScript's rules give it.</summary>
  [TestCaseSource(nameof(Expressions))]
  public void Expression_IsLoweredByJavaScriptsRules(string expression, string result, IrExpr expected)
    => Assert.That(Returned(expression, result), Is.EqualTo(expected));

  /// <summary>Test design 5: each context that coerces makes the result of + and - acceptable.</summary>
  [TestCase("return (a + a) | 0;")]
  [TestCase("return ((a + a) >>> 0) | 0;")]
  [TestCase("return (a + a) & 0xFFFF;")]
  [TestCase("return (a + a) & a;")]
  [TestCase("return (a + a) ^ a;")]
  [TestCase("return a | (a + a);")]
  [TestCase("return ~(a + a);")]
  [TestCase("return (a + a) << 1;")]
  [TestCase("return 1 << (a + a);")]
  [TestCase("return (a - a) >> 1;")]
  [TestCase("return ((a + a) >>> 1) | 0;")]
  [TestCase("return d[a + a];")]
  [TestCase("d[0] = a + a; return 0;")]
  [TestCase("d[a - 1] = a - a; return 0;")]
  [TestCase("d[0] += a; return 0;")]
  [TestCase("d[0] -= 1; return 0;")]
  [TestCase("return (-a) | 0;")]
  [TestCase("return (a + a - -a + 1) | 0;")]
  public void CoercedArithmetic_IsAccepted(string body)
    => Assert.That(Lowered(body), Is.Not.Empty);

  /// <summary>
  /// Test design 5 and 12: without such a context the result of + and - is PLY1103, and so are a * b
  /// and a / b wherever they stand, and an integer used as one of another type.
  /// </summary>
  [TestCase("return a + a;", "a + a")]
  [TestCase("return a - a;", "a - a")]
  [TestCase("return -a;", "-a")]
  [TestCase("return (a * a) | 0;", "a * a")]
  [TestCase("return (a * a) >>> 0;", "a * a")]
  [TestCase("return a * a;", "a * a")]
  [TestCase("return 2 * 3;", "2 * 3")]
  [TestCase("return a / a;", "a / a")]
  [TestCase("return (a / a) & 1;", "a / a")]
  [TestCase("return (a / a) >> 0;", "a / a")]
  [TestCase("return (a / a) | 1;", "a / a")]
  [TestCase("a *= 2; return a;", "a *= 2")]
  [TestCase("a /= 2; return a;", "a /= 2")]
  [TestCase("a += 1; return a;", "a += 1")]
  [TestCase("a -= 1; return a;", "a -= 1")]
  [TestCase("const x = a + 1; return 1;", "a + 1")]
  [TestCase("a = a + 1; return a;", "a + 1")]
  [TestCase("return f(a + 1, u, c, t, d);", "a + 1")]
  [TestCase("return a + 1 < 3 ? 1 : 0;", "a + 1")]
  [TestCase("return 3 > a + 1 ? 1 : 0;", "a + 1")]
  [TestCase("return a + 1 === 3 ? 1 : 0;", "a + 1")]
  [TestCase("return t ? a + 1 : a;", "a + 1")]
  [TestCase("return t ? a : a + 1;", "a + 1")]
  [TestCase("return Math.imul(a + 1, a);", "a + 1")]
  [TestCase("return Math.imul(a, a + 1);", "a + 1")]
  [TestCase("const n = new Uint8Array(a + 1); return 1;", "a + 1")]
  [TestCase("return ((a + 1) / a) | 0;", "a + 1")]
  [TestCase("return (a / (a + 1)) | 0;", "a + 1")]
  [TestCase("return (a + 1) % a;", "a + 1")]
  [TestCase("return a % (a + 1);", "a + 1")]
  [TestCase("if (a + 1) return 1; return 0;", "a + 1")]
  [TestCase("return u;", "u")]
  [TestCase("return a >>> 0;", "a >>> 0")]
  [TestCase("return 4294967295;", "4294967295")]
  [TestCase("u = a; return a;", "a")]
  [TestCase("u = -1; return a;", "-1")]
  [TestCase("u ^= 1; return a;", "u ^= 1")]
  [TestCase("c = a; return a;", "a")]
  [TestCase("c = 256; return a;", "256")]
  [TestCase("c = a & 0x1FF; return a;", "a & 0x1FF")]
  [TestCase("c <<= 1; return a;", "c <<= 1")]
  [TestCase("return f(u, u, c, t, d);", "u")]
  public void UncoercedNumber_IsRefused(string body, string named) {
    var refusal = Refusal(WithBody(body));
    Assert.That((refusal.Code, refusal.Message, refusal.Line),
      Is.EqualTo((JavaScriptFrontend.NotAnInteger, $"the result of '{named}' is a JavaScript number; coerce it with | 0 or >>> 0", 1)));
    Assert.That(WithBody(body)[(refusal.Column - 1)..], Does.StartWith(named));
  }

  /// <summary>Test design 11 and 12: every construct outside the subset is PLY1102, with its line and column.</summary>
  [TestCase("return 1n;", "1n")]
  [TestCase("return 1.5;", "1.5")]
  [TestCase("return 4294967296;", "4294967296")]
  [TestCase("return -2147483649;", "-2147483649")]
  [TestCase("return -0;", "-0")]
  [TestCase("return \"s\";", "\"s\"")]
  [TestCase("return null;", "null")]
  [TestCase("return /x/;", "/x/")]
  [TestCase("return `s`;", "a template string")]
  [TestCase("return a`s`;", "a template string")]
  [TestCase("const o = {}; return 1;", "an object literal")]
  [TestCase("const o = [1]; return 1;", "an array literal")]
  [TestCase("d.push(1); return 1;", "a call of d.push")]
  [TestCase("class K {} return 1;", "class")]
  [TestCase("const k = class {}; return 1;", "class")]
  [TestCase("function g() {} return 1;", "a function inside a function")]
  [TestCase("const g = function () {}; return 1;", "a function inside a function")]
  [TestCase("const g = () => 1; return 1;", "a function inside a function")]
  [TestCase("return this;", "this")]
  [TestCase("return this.a;", "this.a")]
  [TestCase("return this.length;", "this")]
  [TestCase("throw a;", "throw")]
  [TestCase("try { return 1; } catch (e) { return 2; }", "try")]
  [TestCase("return a == a ? 1 : 0;", "==")]
  [TestCase("return a != a ? 1 : 0;", "!=")]
  [TestCase("if (t) { var x = 1; } return x;", "x")]
  [TestCase("for (var i = 0; i < 3; i++) { } return i;", "i")]
  [TestCase("var x = 1; if (t) { var x = 2; } return x;", "a second declaration of x")]
  [TestCase("var x = 1; var x = 2; return x;", "a second declaration of x")]
  [TestCase("{ let a = 1; } return a;", "a second declaration of a")]
  [TestCase("while (t) { continue; } return 1;", "continue")]
  [TestCase("while (t) { break; } return 1;", "break")]
  [TestCase("switch (a) { default: return 1; }", "switch")]
  [TestCase("do { a = 1; } while (t); return 1;", "do")]
  [TestCase("for (const k in d) { } return 1;", "for-in")]
  [TestCase("for (const k of d) { } return 1;", "for-of")]
  [TestCase("x: while (t) { } return 1;", "a label")]
  [TestCase("with (d) { } return 1;", "with")]
  [TestCase("debugger; return 1;", "debugger")]
  [TestCase("return a++;", "++ or -- inside an expression")]
  [TestCase("return --a;", "++ or -- inside an expression")]
  [TestCase("return (a = 1);", "an assignment inside an expression")]
  [TestCase("return (a, 1);", "the comma operator")]
  [TestCase("for (a = 0, u = 0; t; ) { } return 1;", "the comma operator")]
  [TestCase("return a ** 2;", "**")]
  [TestCase("return a ?? 1;", "??")]
  [TestCase("return (a in d) ? 1 : 0;", "in")]
  [TestCase("return (d instanceof d) ? 1 : 0;", "instanceof")]
  [TestCase("return typeof a;", "typeof")]
  [TestCase("return void 0;", "void")]
  [TestCase("return delete d[0];", "delete")]
  [TestCase("return +a;", "unary +")]
  [TestCase("a **= 2; return a;", "**=")]
  [TestCase("a ??= 2; return a;", "??=")]
  [TestCase("t &&= t; return a;", "&&=")]
  [TestCase("t ||= t; return a;", "||=")]
  [TestCase("return d?.length;", "?.")]
  [TestCase("return new.target;", "new.target")]
  [TestCase("return undefined;", "undefined")]
  [TestCase("let x; return 1;", "a declaration without an initializer")]
  [TestCase("const [x] = d; return 1;", "a destructuring declaration")]
  [TestCase("let x$ = 1; return 1;", "the name x$")]
  [TestCase("const k = 1; k = 2; return k;", "an assignment to the constant k")]
  [TestCase("const k = 1; k |= 2; return k;", "an assignment to the constant k")]
  [TestCase("const k = 1; k++; return k;", "an assignment to the constant k")]
  [TestCase("c++; return a;", "c++ on anything but an int32 or uint32 variable")]
  [TestCase("d[0]++; return a;", "d[0]++ on anything but an int32 or uint32 variable")]
  [TestCase("--zz; return a;", "--zz on anything but an int32 or uint32 variable")]
  [TestCase("zz = 1; return a;", "zz")]
  [TestCase("d.length = 1; return a;", "d.length")]
  [TestCase("[a] = d; return a;", "[a]")]
  [TestCase("a; return a;", "a as a statement")]
  [TestCase("a + 1; return a;", "a + 1 as a statement")]
  [TestCase("return g(a);", "a call of g")]
  [TestCase("return d[0](a);", "a call of d[0]")]
  [TestCase("return f(a);", "a call of f with 1 arguments for 5 parameters")]
  [TestCase("return f(...d);", "a spread argument")]
  [TestCase("return Math.imul(a);", "Math.imul with 1 arguments")]
  [TestCase("return Math.max(a, a);", "a call of Math.max")]
  [TestCase("return Math.PI;", "Math.PI")]
  [TestCase("return d.byteLength;", "d.byteLength")]
  [TestCase("return a.length;", "a as an array, which is int32")]
  [TestCase("return a[0];", "a as an array, which is int32")]
  [TestCase("a[0] = 1; return a;", "a as an array, which is int32")]
  [TestCase("const n = new Array(3); return 1;", "new Array")]
  [TestCase("const n = new d.x(1); return 1;", "new d.x")]
  [TestCase("const n = new Uint8Array(); return 1;", "new Uint8Array with anything but one length")]
  [TestCase("const n = new Uint8Array(1, 2); return 1;", "new Uint8Array with anything but one length")]
  [TestCase("const n = new Uint8Array(...d); return 1;", "new Uint8Array with anything but one length")]
  [TestCase("const n = new Uint8Array(t); return 1;", "new Uint8Array of boolean")]
  [TestCase("const n = new Uint8Array(d); return 1;", "new Uint8Array of uint8[]")]
  [TestCase("return t | 0;", "boolean as a number")]
  [TestCase("return t >>> 0;", "boolean as a number")]
  [TestCase("return (d + 1) | 0;", "uint8[] as a number")]
  [TestCase("return ~t;", "boolean as a number")]
  [TestCase("return (-t) | 0;", "boolean as a number")]
  [TestCase("return Math.imul(t, a);", "boolean as a number")]
  [TestCase("d[t] = 1; return 1;", "boolean as a number")]
  [TestCase("d[0] = t; return 1;", "boolean as a number")]
  [TestCase("if (a) return 1; return 0;", "int32 as a condition")]
  [TestCase("while (u) { } return 0;", "uint32 as a condition")]
  [TestCase("for (; c; ) { } return 0;", "uint8 as a condition")]
  [TestCase("return !a ? 1 : 0;", "int32 as a condition")]
  [TestCase("return (a && t) ? 1 : 0;", "int32 as a condition")]
  [TestCase("return (t || d) ? 1 : 0;", "uint8[] as a condition")]
  [TestCase("return a ? 1 : 0;", "int32 as a condition")]
  [TestCase("return a < u ? 1 : 0;", "< on int32 and uint32")]
  [TestCase("return u <= a ? 1 : 0;", "<= on uint32 and int32")]
  [TestCase("return a > u ? 1 : 0;", "> on int32 and uint32")]
  [TestCase("return a >= u ? 1 : 0;", ">= on int32 and uint32")]
  [TestCase("return a === u ? 1 : 0;", "=== on int32 and uint32")]
  [TestCase("return a !== u ? 1 : 0;", "!== on int32 and uint32")]
  [TestCase("return u > -1 ? 1 : 0;", "> on uint32 and int32")]
  [TestCase("return (a / u) | 0;", "/ on int32 and uint32")]
  [TestCase("return a % u;", "% on int32 and uint32")]
  [TestCase("a %= u; return a;", "% on int32 and uint32")]
  [TestCase("return t < t ? 1 : 0;", "< on boolean and boolean")]
  [TestCase("return d === d ? 1 : 0;", "=== on uint8[] and uint8[]")]
  [TestCase("return t === a ? 1 : 0;", "=== on boolean and int32")]
  [TestCase("return t ? a : u;", "?: on int32 and uint32")]
  [TestCase("return t ? a : t;", "?: on int32 and boolean")]
  [TestCase("return t;", "boolean where int32 is expected")]
  [TestCase("return d;", "uint8[] where int32 is expected")]
  [TestCase("t = a; return a;", "int32 where boolean is expected")]
  [TestCase("return;", "a return without a value")]
  public void UnsupportedConstruct_IsRefusedWithItsPosition(string body, string what) {
    var refusal = Refusal(WithBody(body));
    Assert.That((refusal.Code, refusal.Message, refusal.File, refusal.Line), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet", "snippet.js", 1)));
    Assert.That(refusal.Column, Is.GreaterThan(Mixed.Length));
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
  [TestCase("/** @returns {int32} */ function f() { }", "a function that can end without returning its value", 25)]
  [TestCase("/** @param {boolean} t @returns {int32} */ function f(t) { if (t) return 1; }", "a function that can end without returning its value", 44)]
  [TestCase("/** @param {boolean} t @returns {int32} */ function f(t) { while (t) { return 1; } }", "a function that can end without returning its value", 44)]
  [TestCase("/** @param {boolean} t @returns {int32} */ function f(t) { if (t) { return 1; } else { t = false; } }", "a function that can end without returning its value", 44)]
  [TestCase("function f() { return 1; }", "a return value in a function without @returns", 16)]
  [TestCase("function g() { } /** @returns {int32} */ function f() { return g(); }", "void where int32 is expected", 64)]
  [TestCase("function g() { } function f() { const x = g(); }", "void as the value of a variable", 43)]
  [TestCase("function g() { } /** @param {boolean} t */ function f(t) { const x = t ? g() : g(); }", "?: on void", 70)]
  [TestCase("function g(a) { } function f() { g(1); }", "a call of g, which cannot be imported", 34)]
  [TestCase("/** @param {int32} g */ function f(g) { g(1); } function g() { }", "a call of g", 41)]
  [TestCase("/** @param {int32} O */ function f(O) { O.g(); } const O = { g: function () { } };", "a call of O.g", 41)]
  [TestCase("/** @param {int32} Math @returns {int32} */ function f(Math) { return Math.imul(1, 2); }", "a call of Math.imul", 71)]
  [TestCase("/** @param {int32} Uint8Array */ function f(Uint8Array) { const n = new Uint8Array(1); }", "new Uint8Array", 69)]
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

  /// <summary>A function returns on every path when its last word is a return, however deep it stands.</summary>
  [TestCase("if (t) return 1; else return 2;")]
  [TestCase("{ if (t) { return 1; } else { { return 2; } } }")]
  [TestCase("return 1; a = 2;")]
  public void FunctionThatAlwaysReturns_IsAccepted(string body)
    => Assert.That(Lowered(body), Is.Not.Empty);

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

  /// <summary>Statements become the IR statements they are; a for is a block around a while.</summary>
  [Test]
  public void Statements_AreLowered() {
    var i = new IrLocal("i", IrType.I32);
    var n = new IrLocal("n", IrType.U32);
    var body = Lowered("'use strict'; ; let n = u; for (let i = 0; i < 3; i++) { n--; } if (t) { a = 1; } else a = 2; while (t) t = false; return a;");
    Assert.That(body, Is.EqualTo(new IrStmt[] {
      new IrDeclare(n, U),
      body[1],
      body[2],
      body[3],
      new IrReturn(A)
    }));

    var loop = (IrBlock)body[1];
    Assert.That(loop.Statements[0], Is.EqualTo(new IrDeclare(i, Int(0))));
    var repeated = (IrWhile)loop.Statements[1];
    Assert.That(repeated.Condition, Is.EqualTo(new IrBinary(IrBinaryOp.Less, new IrLocalRef(i), Int(3))));
    Assert.That(repeated.Body.Statements, Is.EqualTo(new IrStmt[] {
      new IrAssign(n, new IrBinary(IrBinaryOp.Subtract, new IrLocalRef(n), new IrConst(IrType.U32, 1))),
      new IrAssign(i, new IrBinary(IrBinaryOp.Add, new IrLocalRef(i), Int(1)))
    }));

    var choice = (IrIf)body[2];
    Assert.That((choice.Condition, choice.Then.Statements.Single(), choice.Else!.Statements.Single()),
      Is.EqualTo((T, (IrStmt)new IrAssign(_a, Int(1)), (IrStmt)new IrAssign(_a, Int(2)))));
    Assert.That(((IrWhile)body[3]).Body.Statements.Single(), Is.EqualTo(new IrAssign(_t, new IrConst(IrType.Bool, 0))));
  }

  /// <summary>A for without initializer, condition and update runs while true; its initializer may be an assignment.</summary>
  [Test]
  public void For_MayLeaveItsPartsOut() {
    var bare = (IrBlock)Lowered("for (;;) { return; }", "")[0];
    Assert.That(((IrWhile)bare.Statements.Single()).Condition, Is.EqualTo(new IrConst(IrType.Bool, 1)));

    var assigned = (IrBlock)Lowered("for (a = 5; t; a = 6) t = false; return a;")[0];
    Assert.That(assigned.Statements[0], Is.EqualTo(new IrAssign(_a, Int(5))));
    Assert.That(((IrWhile)assigned.Statements[1]).Body.Statements, Has.Count.EqualTo(2));
  }

  /// <summary>
  /// A compound assignment to an element evaluates array and index once: what is more than a
  /// variable or a constant goes into a temporary. The store converts to the element type.
  /// </summary>
  [Test]
  public void CompoundElementAssignment_EvaluatesItsTargetOnce() {
    var function = Import(WithBody("(t ? d : d)[a & 3] ^= u; d[0] |= 1; d[a] = u; return 0;")).Functions[0];
    var array = new IrLocal("$t0", IrType.Array(IrType.U8));
    var index = new IrLocal("$t1", IrType.I32);
    Assert.That(function.Locals, Is.EqualTo(new[] { array, index }));
    Assert.That(function.Body.Statements.Take(5), Is.EqualTo(new IrStmt[] {
      new IrDeclare(array, new IrConditional(T, D, D)),
      new IrDeclare(index, new IrBinary(IrBinaryOp.And, A, Int(3))),
      new IrStore(new IrLocalRef(array), new IrLocalRef(index), new IrConvert(IrType.U8, new IrBinary(
        IrBinaryOp.Xor,
        new IrConvert(IrType.I32, new IrArrayLoad(new IrLocalRef(array), new IrLocalRef(index))),
        new IrConvert(IrType.I32, U)))),
      new IrStore(D, Int(0), new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.Or, new IrConvert(IrType.I32, new IrArrayLoad(D, Int(0))), Int(1)))),
      new IrStore(D, A, new IrConvert(IrType.U8, U))
    }));
  }

  /// <summary>
  /// A local has the type of its initializer and is visible until its block ends; the same name may
  /// then be declared again, also with another type, and a <c>var</c> is no different.
  /// </summary>
  [Test]
  public void Locals_TakeTheTypeOfTheirInitializerAndEndWithTheirBlock() {
    var function = Import(WithBody("{ const x = a; var y = u; } { let x = t; const y = d; const z = c; const w = d[0]; } return 0;")).Functions[0];
    Assert.That(function.Locals.Select(local => (local.Name, local.Type.ToString())), Is.EqualTo(new[] {
      ("x", "i32"), ("y", "u32"), ("x", "bool"), ("y", "u8[]"), ("z", "u8"), ("w", "u8")
    }));
  }

  /// <summary>A function calls a declaration by its name, converting what cannot change by it; a call can stand as a statement.</summary>
  [Test]
  public void Calls_ConvertTheirArgumentsToTheParameterTypes() {
    const string callee = "/** @param {uint32} x @param {int32} y @param {uint8} z @returns {uint8} */ function g(x, y, z) { return z; } ";
    var module = Import(callee + WithBody("g(c, c, 3); return g(7, 7, a & 7);"));
    var statements = module.Functions[1].Body.Statements;
    Assert.That(((IrCall)((IrExprStmt)statements[0]).Expression).Arguments,
      Is.EqualTo(new IrExpr[] { new IrConvert(IrType.U32, C), new IrConvert(IrType.I32, C), new IrConst(IrType.U8, 3) }));
    var returned = (IrConvert)((IrReturn)statements[1]).Value!;
    Assert.That(((IrCall)returned.Operand).Arguments,
      Is.EqualTo(new IrExpr[] { new IrConst(IrType.U32, 7), Int(7), new IrConvert(IrType.U8, new IrBinary(IrBinaryOp.And, A, Int(7))) }));
    Assert.That((returned.Type, returned.Operand.Type), Is.EqualTo((IrType.I32, IrType.U8)));
  }

  /// <summary>A function that is refused is refused once, and what calls it is not blamed for it.</summary>
  [Test]
  public void RefusedCallee_IsReportedOnceAndInSourceOrder() {
    const string source = Ints + "function f(a, b) { return g(a, b); }\n"
      + Ints + "function g(a, b) { return a + b; }\n"
      + Ints + "function h(a, b) { return g(b, a) | f(a, b); }\n"
      + "function k(a) { return h(a, a); }";
    Assert.That(JavaScriptFrontend.Import("snippet.js", source, ["h"]).Diagnostics.Select(diagnostic => diagnostic.ToString()), Is.EqualTo(new[] {
      "snippet.js(2,85): error PLY1103: the result of 'a + b' is a JavaScript number; coerce it with | 0 or >>> 0"
    }));
    Assert.That(JavaScriptFrontend.Import("snippet.js", source).Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Line)), Is.EqualTo(new[] {
      ("PLY1103", 2), ("PLY1101", 4)
    }));
  }

  /// <summary>The caveats name the differences a module can show, each once, and only the ones it can show.</summary>
  [TestCase("return a;", new string[0])]
  [TestCase("return d[0] | d[1];", new[] { "js-oob-read" })]
  [TestCase("d[0] = 1; d[1] = 2; return a;", new[] { "js-oob-read" })]
  [TestCase("return ((a / a) | 0) % a;", new[] { "js-div-zero" })]
  [TestCase("a %= 3; return a;", new[] { "js-div-zero" })]
  [TestCase("a++; u--; return a;", new[] { "js-step-wrap" })]
  [TestCase("for (let i = 0; i < d.length; ++i) d[i] = (d[i] / 2) | 0; return a;", new[] { "js-oob-read", "js-div-zero", "js-step-wrap" })]
  public void Caveats_NameTheDifferencesOfTheModule(string body, string[] expected) {
    var result = JavaScriptFrontend.Import("snippet.js", WithBody(body));
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Is.EqualTo(expected));
    Assert.That(result.Caveats.Select(caveat => caveat.Description), Has.All.Length.GreaterThan(40));
    Assert.That((JavaScriptFrontend.OutOfRangeAccess, JavaScriptFrontend.DivisionByZero, JavaScriptFrontend.StepWraps), Is.EqualTo(("js-oob-read", "js-div-zero", "js-step-wrap")));
  }

  /// <summary>Test design 13: a file with nothing to import is PLY1102; with an export list, PLY1104 for every name.</summary>
  [Test]
  public void FileWithNothingToImport_IsRefused() {
    Assert.That(Refusal("const size = 4;").Code, Is.EqualTo(JavaScriptFrontend.Unsupported));
    Assert.That(Refusal("const size = 4;", ["size"]).ToString(), Is.EqualTo("snippet.js(1,1): error PLY1104: the file has no function 'size' to export"));
    Assert.That(Refusal("const size = 4;", ["size"]).Code, Is.EqualTo(JavaScriptFrontend.UnknownExport));
    Assert.That(Refusal("function f( {").Code, Is.EqualTo(JavaScriptFrontend.SyntaxError));
  }

  /// <summary>The result and the caveat are plain data.</summary>
  [Test]
  public void ImportResult_CarriesModuleDiagnosticsAndCaveats() {
    var caveat = new ImportCaveat("id", "text");
    var result = new ImportResult(null, [], [caveat]);
    Assert.That((result.Module, result.Diagnostics.Count, result.Caveats.Single().Id, caveat.Description), Is.EqualTo(((IrModule?)null, 0, "id", "text")));
  }

}
