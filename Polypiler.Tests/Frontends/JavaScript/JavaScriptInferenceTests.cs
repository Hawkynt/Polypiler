using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// The scenarios of <c>tests/features/prototype/javascript-inference.feature</c> without a process,
/// and what the type inference refuses: values of two kinds, a read before an assignment, a
/// <c>BigInt</c> without a bound, an array nothing says the type of.
/// </summary>
[TestFixture]
public sealed class JavaScriptInferenceTests {

  private static ImportResult Imported(string source, IReadOnlyCollection<string>? exports = null) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source, exports);
    Assert.That(result.Diagnostics, Is.Empty);
    Assert.That(IrVerifier.Verify(result.Module!), Is.Empty);
    return result;
  }

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That((result.Module, result.Caveats.Count, result.Explanations.Count), Is.EqualTo(((IrModule?)null, 0, 0)));
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1), string.Join("; ", result.Diagnostics));
    return result.Diagnostics[0];
  }

  private static string Signature(IrFunction function)
    => $"({string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}"))}) -> {function.ReturnType}";

  /// <summary>Scenario outline: An unannotated checksum is imported with integer types.</summary>
  [TestCase("adler32", "compute", "a", "u32", "Wikipedia", 300286872u)]
  [TestCase("crc32", "compute", "crc", "u32", "123456789", 3421780262u)]
  [TestCase("fnv1a", "hash", "h", "u32", "a", 3826002220u)]
  public void UnannotatedChecksum_IsImportedWithIntegerTypes(string fixture, string function, string local, string type, string text, uint expected) {
    var source = JavaScriptFixture.SourceOf("untyped/" + fixture);
    Assert.That(source, Does.Not.Contain("/**"));
    var result = Imported(source);
    var explained = result.Explanations.Single(explanation => (explanation.Function, explanation.Role, explanation.Name) == (function, "local", local));
    Assert.That(explained.Type.ToString(), Is.EqualTo(type));
    Assert.That(IrInterpreter.Call(result.Module!, function, [PrototypeCases.Text(text)]), Is.EqualTo(expected));
  }

  /// <summary>Scenario: A parameter used only under coercion is an integer, and the caveat says so.</summary>
  [Test]
  public void ParameterUsedOnlyUnderCoercion_IsAnInteger_AndTheCaveatSaysSo() {
    var result = Imported("export function f(a, b) { return (a ^ b) >>> 0; }");
    Assert.That(Signature(result.Module!.Functions.Single()), Is.EqualTo("(a: i32, b: i32) -> u32"));
    var caveat = result.Caveats.Single();
    Assert.That((caveat.Id, caveat.Description), Is.EqualTo((JavaScriptFrontend.IntegerParameter,
      "a parameter without a JSDoc type that its function reads only under ToInt32 or ToUint32 is that 32-bit integer; JavaScript "
      + "coerces whatever number is passed, the imported function takes the integer: f(a, b)")));
  }

  /// <summary>Scenario: A parameter used in plain arithmetic stays a double.</summary>
  [Test]
  public void ParameterUsedInPlainArithmetic_StaysADouble() {
    var result = Imported("export function f(a, b) { return a + b; }");
    Assert.That(Signature(result.Module!.Functions.Single()), Is.EqualTo("(a: f64, b: f64) -> f64"));
    Assert.That(result.Caveats, Is.Empty);
    Assert.That(BitConverter.DoubleToInt64Bits((double)IrInterpreter.Call(result.Module, "f", [0.1, 0.2])!), Is.EqualTo(0x3FD3333333333334));
  }

  /// <summary>Scenario: A coerced product is not narrowed to an integer multiplication.</summary>
  [Test]
  public void CoercedProduct_IsNotNarrowedToAnIntegerMultiplication() {
    var module = Imported("export function f(a, b) { a = a | 0; b = b | 0; return (a * b) | 0; }").Module!;
    Assert.That(Signature(module.Functions.Single()), Is.EqualTo("(a: i32, b: i32) -> i32"));
    Assert.That(IrInterpreter.Call(module, "f", [2147483647, 2147483647]), Is.EqualTo(0));

    // The coerced re-assignments leave nothing to do; the product is one of doubles, converted afterwards.
    var returned = (IrIntrinsic)((IrReturn)module.Functions.Single().Body.Statements.Single()).Value!;
    var product = (IrBinary)returned.Arguments.Single();
    Assert.That((returned.Op, product.Op, product.Type, product.Left.Type), Is.EqualTo((IrIntrinsicOp.ToInt32, IrBinaryOp.Multiply, IrType.F64, IrType.F64)));
  }

  /// <summary>Scenario: Math.imul is an integer multiplication.</summary>
  [Test]
  public void MathImul_IsAnIntegerMultiplication() {
    var module = Imported("export function f(a, b) { return Math.imul(a, b); }").Module!;
    Assert.That(Signature(module.Functions.Single()), Is.EqualTo("(a: i32, b: i32) -> i32"));
    Assert.That(IrInterpreter.Call(module, "f", [2147483647, 2147483647]), Is.EqualTo(1));
  }

  /// <summary>Scenario: An unbounded BigInt is refused, a bounded one is a 64-bit integer.</summary>
  [Test]
  public void UnboundedBigInt_IsRefused_ABoundedOneIsA64BitInteger() {
    Assert.That(Refusal("export function f(a) { return a * 1099511628211n; }").ToString(),
      Is.EqualTo("snippet.mjs(1,31): error PLY1103: the result of 'a * 1099511628211n' has no bounded type"));

    var module = Imported("/** @param {uint64} a */ export function f(a) { return BigInt.asUintN(64, a * 1099511628211n); }").Module!;
    Assert.That(Signature(module.Functions.Single()), Is.EqualTo("(a: u64) -> u64"));
    Assert.That(IrInterpreter.Call(module, "f", [14695981039346656037UL]), Is.EqualTo(12638153115695167455UL));
  }

  /// <summary>Scenario: A variable holding a number and an array is refused.</summary>
  [Test]
  public void VariableHoldingANumberAndAnArray_IsRefused() {
    var refusal = Refusal("export function f(n) { let x = 0; if (n > 1) x = new Uint8Array(n); return x; }");
    Assert.That(refusal.ToString(), Is.EqualTo("snippet.mjs(1,50): error PLY1105: 'x' holds both number and uint8[]"));
    Assert.That(refusal.Code, Is.EqualTo(JavaScriptFrontend.MixedKinds));
  }

  /// <summary>Scenario: A switch section that falls through into statements is refused.</summary>
  [Test]
  public void SwitchSectionThatFallsThroughIntoStatements_IsRefused()
    => Assert.That(Refusal("export function f(n) {\n  let x = 0;\n  switch (n | 0) {\n    case 1:\n      x = 1;\n    case 2:\n      x = 2;\n      break;\n  }\n  return x;\n}").ToString(),
      Is.EqualTo("snippet.mjs(4,5): error PLY1102: 'a switch section that falls through into the next' is not supported yet"));

  /// <summary>Test design 11: a place that values of two kinds reach is PLY1105, at the value that made it two.</summary>
  [TestCase("function f(n) { let x = 1; x = true; return 1; }", "'x' holds both number and boolean", 32)]
  [TestCase("function f(n) { let x = true; x = n > 1 ? 2 : 3; return 1; }", "'x' holds both boolean and number", 35)]
  [TestCase("function f(n) { let x = 1; x = 1n; return 1; }", "'x' holds both number and bigint", 32)]
  [TestCase("function f(n) { let x = new Uint8Array(1); x = new Int8Array(1); return 1; }", "'x' holds both uint8[] and int8[]", 48)]
  [TestCase("function f(n) { let x = [1]; x = new Int32Array(1); return 1; }", "'x' holds both int32[] and int32[]", 34)]
  [TestCase("function f(n) { let x = new Float64Array(1); x = [0.5]; return 1; }", "'x' holds both float64[] and float64[]", 50)]
  [TestCase("function f(n) { let x = 1; x = true; x = 1n; return 1; }", "'x' holds both number and boolean", 32)]
  [TestCase("function g() { } function f(n) { let x = 1; x = g(); return 1; }", "'x' holds both number and undefined", 49)]
  [TestCase("function f(n) { let t = n > 1; t++; return 1; }", "'t' holds both boolean and number", 32)]
  [TestCase("function f(n) { let t = n > 1; return t++; }", "'t' holds both boolean and number", 39)]
  [TestCase("function f(n) { if (n > 1) return 1; return true; }", "'f' holds both number and boolean", 45)]
  [TestCase("function f(n) { if (n > 1) return 1; return; }", "'f' holds both number and undefined", 38)]
  [TestCase("function f(n) { if (n > 1) return; return 1; }", "'f' holds both undefined and number", 43)]
  [TestCase("/** @param {boolean} t */ function f(t) { t = 1; return 1; }", "'t' holds both boolean and number", 47)]
  [TestCase("function g(x) { return 1; } export function f() { return g(1) + g(true); }", "'x' holds both number and boolean", 67)]
  public void ValuesOfTwoKinds_AreRefused(string source, string message, int column) {
    var diagnostics = JavaScriptFrontend.Import("snippet.mjs", source).Diagnostics;
    Assert.That(diagnostics.Select(diagnostic => diagnostic.ToString()), Does.Contain($"snippet.mjs(1,{column}): error PLY1105: {message}"));
  }

  /// <summary>A function whose parameter the callers disagree on is refused, and so is every call of it.</summary>
  [Test]
  public void CalleeTheCallersDisagreeOn_IsRefusedWithItsCalls() {
    Assert.That(JavaScriptFrontend.Import("snippet.mjs", "function g(x) { return 1; } export function f() { return g(1) + g(true); }").Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "'a call of g, which cannot be imported' is not supported yet",
      "'x' holds both number and boolean"
    }));

    // A parameter that is handed nothing has no type either.
    Assert.That(JavaScriptFrontend.Import("snippet.mjs", "function h() { } function g(x) { return 1; } export function f() { return g(h()); }").Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "'void as the value of a variable' is not supported yet",
      "'a call of g, which cannot be imported' is not supported yet"
    }));

    // A call whose value is not used does not care what the function returns; the function is refused for itself.
    Assert.That(Refusal("function g(n) { if (n > 1) return 1; return true; } export function f() { g(1); }").Message, Is.EqualTo("'g' holds both number and boolean"));

    // What a function without a bounded result returns has no bound in its callers either.
    Assert.That(JavaScriptFrontend.Import("snippet.mjs", "function g() { return 2n * 3n; } export function f() { return g(); }").Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "the result of '2n * 3n' has no bounded type",
      "the result of 'g()' has no bounded type"
    }));
  }

  /// <summary>Test design 11: a variable declared without a value is assigned before every read, or the read is PLY1106.</summary>
  [TestCase("let x; return x;")]
  [TestCase("let x; if (t) x = 1; return x;")]
  [TestCase("let x; if (t) { x = 1; } else { } return x;")]
  [TestCase("let x; if (t) { return 2; } return x;")]
  [TestCase("let x; while (t) { x = 1; } return x;")]
  [TestCase("let x; for (; t; ) { x = 1; } return x;")]
  [TestCase("let x; for (; t; x = 1) { } return x;")]
  [TestCase("let x; do { if (t) break; x = 1; } while (t); return x;")]
  [TestCase("let x; do { if (t) continue; x = 1; } while (x > 0); return 1;")]
  [TestCase("let x; switch (a) { case 1: x = 1; break; } return x;")]
  [TestCase("let x; switch (a) { case 1: x = 1; break; default: } return x;")]
  [TestCase("let x; switch (a) { case 1: if (t) break; x = 1; break; default: x = 2; } return x;")]
  [TestCase("let x; switch (a) { case 1: x = 1; break; default: return x; } return 1;")]
  [TestCase("let x; x += 1; return 1;")]
  [TestCase("let x; x++; return 1;")]
  [TestCase("let x; return x++;")]
  [TestCase("let x; const y = x; x = 1; return y;")]
  [TestCase("let x; x = x + 1; return x;")]
  public void ReadBeforeAssignment_IsRefused(string body) {
    var source = $"/** @param {{int32}} a @param {{boolean}} t */ function f(a, t) {{ {body} }}";
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message, refusal.Line), Is.EqualTo((JavaScriptFrontend.ReadBeforeAssignment, "'x' may be read before it is assigned", 1)));
    Assert.That(source[refusal.Column - 1], Is.EqualTo('x'));
    Assert.That(source[..(refusal.Column - 1)], Does.Contain("let x;"));
  }

  /// <summary>A variable declared without a value is accepted where an assignment stands before every read on every path.</summary>
  [TestCase("let x; x = 1; return x;", 1)]
  [TestCase("let x; if (t) x = 1; else x = 2; return x;", 2)]
  [TestCase("let x; if (t) { x = 1; } else { return 5; } return x;", 5)]
  [TestCase("let x; if (t) { return 5; } else { x = 1; } return x;", 1)]
  [TestCase("let x; if (t) return 3; else return 4;", 4)]
  [TestCase("let x; do { x = 7; } while (t); return x;", 7)]
  [TestCase("let x; x = 0; while (x < 3) { x = (x + 1) | 0; } return x;", 3)]
  [TestCase("let x; switch (a) { case 1: x = 1; break; case 2: return 9; default: x = 2; } return x;", 2)]
  [TestCase("let x; switch (a) { default: x = 6; } return x;", 6)]
  [TestCase("let x; let y; x = 1; y = x; return y;", 1)]
  [TestCase("let x; return 8;", 8)]
  public void AssignmentBeforeEveryRead_IsAccepted(string body, int expected) {
    var module = Imported($"/** @param {{int32}} a @param {{boolean}} t */ function f(a, t) {{ {body} }}").Module!;
    Assert.That(Convert.ToInt32(IrInterpreter.Call(module, "f", [0, false])), Is.EqualTo(expected));
  }

  /// <summary>Test design 11: a BigInt that nothing bounds is PLY1103, at the expression without a bound.</summary>
  [TestCase("return 2n * 3n;", "2n * 3n")]
  [TestCase("return 2n + 3n;", "2n + 3n")]
  [TestCase("return 2n & 3n;", "2n & 3n")]
  [TestCase("return 2n << 3n;", "2n << 3n")]
  [TestCase("return 2n >> 3n;", "2n >> 3n")]
  [TestCase("return 2n / 3n;", "2n / 3n")]
  [TestCase("return ~2n;", "~2n")]
  [TestCase("let x = 2n; return -x;", "-x")]
  [TestCase("let x = 2n; return x++;", "x++")]
  [TestCase("let x = 2n; x++; return 1;", "x++")]
  [TestCase("let x = 2n; x += 1n; return 1;", "x += 1n")]
  [TestCase("let x = 2n; x = x * 3n; x = 5n; return 1;", "x * 3n")]
  [TestCase("let x = -1n; x = 18446744073709551615n; return 1;", "18446744073709551615n")]
  [TestCase("return 36893488147419103232n;", "36893488147419103232n")]
  [TestCase("return -36893488147419103232n;", "-36893488147419103232n")]
  [TestCase("return BigInt.asUintN(64, (2n * 3n) >> 1n);", "2n * 3n")]
  [TestCase("return BigInt.asUintN(64, (2n * 3n) / 1n);", "2n * 3n")]
  [TestCase("return 2n * 3n < 7n;", "2n * 3n")]
  [TestCase("return 2n * a;", "2n * a")]
  [TestCase("if (2n * 3n) return 1; return 0;", "2n * 3n")]
  [TestCase("if (36893488147419103232n) return 1; return 0;", "36893488147419103232n")]
  [TestCase("if (~2n) return 1; return 0;", "~2n")]
  [TestCase("let x = 2n; if (-x) return 1; return 0;", "-x")]
  public void BigIntWithoutABound_IsRefused(string body, string named) {
    var source = $"function f(a) {{ {body} }}";
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.NotBounded, $"the result of '{named}' has no bounded type")));
    Assert.That(source[(refusal.Column - 1)..], Does.StartWith(named));
  }

  /// <summary>
  /// Test design 12: a parameter used as an array needs a type. JSDoc states one; without it the
  /// arrays the file itself passes decide, and the module says so; with neither it is PLY1101.
  /// </summary>
  [Test]
  public void ParameterUsedAsAnArray_NeedsAType() {
    Assert.That(Refusal("export function f(x, i) { return x[i]; }").ToString(),
      Is.EqualTo("snippet.mjs(1,19): error PLY1101: parameter 'x' of 'f' is used as an array and needs a JSDoc type"));
    Assert.That(Refusal("export function f(x) { return x.length; }").Code, Is.EqualTo(JavaScriptFrontend.MissingType));
    Assert.That(Refusal("function f(x) { x[0] = 1; }").Message, Is.EqualTo("parameter 'x' of 'f' is used as an array and needs a JSDoc type"));
    Assert.That(JavaScriptFrontend.Import("snippet.mjs", "export function f(x) { return x[0]; } export function g() { return f(1); }").Diagnostics.Select(diagnostic => diagnostic.Message), Is.EqualTo(new[] {
      "parameter 'x' of 'f' is used as an array and needs a JSDoc type",
      "'a call of f, which cannot be imported' is not supported yet"
    }));

    var documented = Imported("/** @param {Int16Array} x */ export function f(x) { return x[0]; }");
    Assert.That((Signature(documented.Module!.Functions[0]), documented.Caveats.Select(caveat => caveat.Id)), Is.EqualTo(("(x: i16[]) -> i16", new[] { "js-oob-read" })));

    var called = Imported("export function f(x) { return x[0]; } export function g() { return f(new Float32Array(1)); }");
    Assert.That(Signature(called.Module!.Functions[0]), Is.EqualTo("(x: f32[]) -> f64"));
    Assert.That(called.Caveats.Select(caveat => caveat.Id), Is.EqualTo(new[] { "js-oob-read", "js-param-array-inferred" }));
    Assert.That(called.Caveats[1].Description, Does.EndWith("the imported function that one: f(x)"));

    // A function nothing outside can call has all its callers in the file: there is nothing to announce.
    var helper = Imported("function f(x) { return x[0]; } export function g() { return f(new Uint8Array(1)); }");
    Assert.That(helper.Caveats.Select(caveat => caveat.Id), Is.EqualTo(new[] { "js-oob-read" }));
    Assert.That(helper.Module!.Functions[0].IsPublic, Is.False);
  }

  /// <summary>Test design 4: the ways a parameter of an exported function gets its type, and what the caveat names.</summary>
  [TestCase("function f(a) { return a | 0; }", "(a: i32) -> i32", "f(a)")]
  [TestCase("function f(a) { return a >>> 0; }", "(a: u32) -> u32", "f(a)")]
  [TestCase("function f(a) { return (a >>> 1) + (a >>> 2); }", "(a: u32) -> f64", "f(a)")]
  [TestCase("function f(a) { return (a >>> 1) + (a << 2); }", "(a: i32) -> f64", "f(a)")]
  [TestCase("function f(a, b) { return Math.imul(a, 3) + Math.clz32(b); }", "(a: i32, b: u32) -> f64", "f(a, b)")]
  [TestCase("function f(a, b) { a ^= b; return a | 0; }", "(a: i32, b: i32) -> i32", "f(a, b)")]
  [TestCase("function f(a, b) { a >>>= b; return a >>> 0; }", "(a: u32, b: i32) -> u32", "f(a, b)")]
  [TestCase("function f(a, b) { a ^= b; return a; }", "(a: f64, b: i32) -> f64", "f(b)")]
  [TestCase("function f(a) { return ~a; }", "(a: i32) -> i32", "f(a)")]
  [TestCase("function f(a, b) { a = a | 0; b = b >>> 0; return a * b; }", "(a: i32, b: u32) -> f64", "f(a, b)")]
  [TestCase("function f(a) { a = a | 0; a = a + 0.5; return a; }", "(a: i32) -> f64", "f(a)")]
  [TestCase("function f(a, b) { a = a | 0; return a + (b | 0) + b; }", "(a: i32, b: f64) -> f64", "f(a)")]
  [TestCase("function f(a) { return a + 1; }", "(a: f64) -> f64", "")]
  [TestCase("function f(a) { return (a | 0) + a; }", "(a: f64) -> f64", "")]
  [TestCase("function f(a) { return -a | 0; }", "(a: f64) -> i32", "")]
  [TestCase("function f(a) { a++; return a | 0; }", "(a: f64) -> i32", "")]
  [TestCase("function f(a) { a += 1; return a | 0; }", "(a: f64) -> i32", "")]
  [TestCase("function f(a) { return 1; }", "(a: f64) -> i32", "")]
  [TestCase("function f(a) { let x = 1; x = x | 0; a = a | 0; return a; }", "(a: f64) -> f64", "")]
  [TestCase("function f(a) { a = 1 | 0; return a; }", "(a: f64) -> f64", "")]
  [TestCase("function f(a) { a = a | 1; return a; }", "(a: f64) -> f64", "")]
  [TestCase("function f(a) { a = a | 0; a = a | 0; return a; }", "(a: i32) -> i32", "f(a)")]
  [TestCase("function f(a) { return g(a | 0); } function g(x) { return x | 0; }", "(a: i32) -> i32", "f(a), g(x)")]
  [TestCase("function f(a) { return g(a / 2); } function g(x) { return x | 0; }", "(a: f64) -> i32", "")]
  [TestCase("/** @param {int32} a */ function f(a) { return a | 0; }", "(a: i32) -> i32", "")]
  public void ExportedParameter_IsTypedByJsDocOrByItsUses(string source, string signature, string named) {
    var result = Imported(source);
    Assert.That(Signature(result.Module!.Functions[0]), Is.EqualTo(signature));
    var caveat = result.Caveats.SingleOrDefault(candidate => candidate.Id == JavaScriptFrontend.IntegerParameter);
    Assert.That(caveat?.Description[(caveat.Description.LastIndexOf(": ", StringComparison.Ordinal) + 2)..] ?? "", Is.EqualTo(named));
  }

  /// <summary>Every parameter, local and result is explained: by JSDoc, by the lack of it, or by the first expression that made the type what it is.</summary>
  [Test]
  public void Explanations_NameWhatDecidedEveryType() {
    const string source = "/** @param {int32} n */\nfunction f(n, d, k) {\n  let sum = 0;\n  let later;\n  later = d * 2;\n  for (let i = 0; i < n; i++)\n    sum = (sum + (k | 0)) | 0;\n  n = n / 2;\n  sum = sum >>> 0;\n  return sum;\n}";
    Assert.That(Imported(source).Explanations.Select(explanation => explanation.ToString()), Is.EqualTo(new[] {
      "f: parameter n: i32 from '@param {int32}' at (2,12)",
      "f: parameter d: f64 from 'no JSDoc type' at (2,15)",
      "f: parameter k: i32 from 'k | 0' at (7,19)",
      "f: local n: f64 from 'n / 2' at (8,7)",
      "f: local sum: f64 from 'sum >>> 0' at (9,9)",
      "f: local later: f64 from 'd * 2' at (5,11)",
      "f: local i: f64 from 'i++' at (6,26)",
      "f: result: f64 from 'sum' at (10,10)"
    }));

    // A parameter of a function nothing outside can call is explained by the argument that decided it.
    Assert.That(Imported("function g(x, y) { let unused; return x; }\nexport function f() { return g(1, 2) + g(0.5, 3); }").Explanations.Select(explanation => explanation.ToString()), Is.EqualTo(new[] {
      "g: parameter x: f64 from '0.5' at (2,42)",
      "g: parameter y: i32 from '2' at (2,35)",
      "g: local unused: f64 from 'no value' at (1,24)",
      "g: result: f64 from 'x' at (1,39)",
      "f: result: f64 from 'g(1, 2) + g(0.5, 3)' at (2,30)"
    }));
  }

  /// <summary>
  /// A <c>const</c> of the scope that encloses the functions is a constant of the module when it is a
  /// number or a table of numbers; only the ones the imported functions use are carried along.
  /// </summary>
  [Test]
  public void EnclosingConstants_AreModuleConstants() {
    const string constants = "const MASK = 0xFF; const LOW = -5; const HALF = 0.5; const BIG = 0xFFFFFFFFFFFFFFFFn; const SMALL = -7n; export const WORDS = new Uint32Array([1, 4294967297, -1]); "
      + "const LIST = [1, 2, 3]; const RATIOS = [0.5, 2]; const SINGLES = new Float32Array([0.1, -2]); const LONGS = new BigInt64Array([1n, -2n]); const UNUSED = 9; ";
    var module = Imported(constants + "const O = { LIST: 1 }; O.LIST = 2; export function f(a) { return (a & MASK) + LOW + HALF + WORDS[1] + WORDS.length + LIST[a & 1] + RATIOS[0] + SINGLES[0]; }"
      + " export function g() { return BigInt.asUintN(64, BIG + SMALL + LONGS[1]); }").Module!;
    Assert.That(module.Constants.Select(constant => $"{constant.Name}: {constant.Type} = {string.Join(",", constant.Values)}"), Is.EqualTo(new[] {
      "MASK: i32 = 255",
      "LOW: i32 = -5",
      "HALF: f64 = " + BitConverter.DoubleToInt64Bits(0.5),
      "BIG: u64 = -1",
      "SMALL: i64 = -7",
      "WORDS: u32[] = 1,1,4294967295",
      "LIST: i32[] = 1,2,3",
      "RATIOS: f64[] = " + BitConverter.DoubleToInt64Bits(0.5) + "," + BitConverter.DoubleToInt64Bits(2),
      "SINGLES: f32[] = " + BitConverter.SingleToInt32Bits(0.1f) + "," + BitConverter.SingleToInt32Bits(-2f),
      "LONGS: i64[] = 1,-2"
    }));
    Assert.That(IrInterpreter.Call(module, "f", [0x1FF]), Is.EqualTo(255 - 5 + 0.5 + 1 + 3 + 2 + 0.5 + (double)0.1f));
    Assert.That(IrInterpreter.Call(module, "g", []), Is.EqualTo(ulong.MaxValue - 9));

    // The constants of the function that wraps the file count as well, and a local of the same name hides one.
    var wrapped = Imported("(function () { const K = 3; const O = { f: function (a) { return (a | 0) + K; }, g: function (K) { return K; } }; })();").Module!;
    Assert.That(wrapped.Constants.Single().Name, Is.EqualTo("K"));
    Assert.That((IrInterpreter.Call(wrapped, "f", [4]), IrInterpreter.Call(wrapped, "g", [0.5])), Is.EqualTo((7.0, 0.5)));
  }

  /// <summary>What is no number and no table of numbers is no constant: its name is unknown to the functions.</summary>
  [TestCase("const C = 1 + 1;")]
  [TestCase("let C = 1;")]
  [TestCase("var C = 1;")]
  [TestCase("const C = 'text';")]
  [TestCase("const C = [];")]
  [TestCase("const C = [1, , 2];")]
  [TestCase("const C = [1, x];")]
  [TestCase("const C = [1, 2n];")]
  [TestCase("const C = new Uint8Array(4);")]
  [TestCase("const C = new Uint8Array([1n]);")]
  [TestCase("const C = new BigInt64Array([1]);")]
  [TestCase("const C = new Map([1]);")]
  [TestCase("const C = 36893488147419103232n;")]
  [TestCase("(function () { const C = 1; })(); function C() { }")]
  [TestCase("const { C } = { C: 1 };")]
  [TestCase("{ const C = 2; }")]
  [TestCase("(function () { const C = 1; if (true) { } })(); const C = 2;")]
  public void WhatIsNoNumberAndNoTable_IsNoConstant(string declaration)
    => Assert.That(JavaScriptFrontend.Import("snippet.js", declaration + " function f() { return C; }", ["f"]).Diagnostics.Single().Message, Is.EqualTo("'C' is not supported yet"));

  /// <summary>
  /// A table is a constant only while the file does nothing with it but read an element or the
  /// length: stored into, handed on or given another name, it need not be the list its declaration shows.
  /// </summary>
  [TestCase("C[0] = 5;")]
  [TestCase("function g(i) { C[i]++; }")]
  [TestCase("(function () { C[1] ^= 1; })();")]
  [TestCase("const D = C;")]
  [TestCase("C.fill(0);")]
  [TestCase("C[0]();")]
  [TestCase("C.length = 1;")]
  [TestCase("function g(x) { x[0] = 1; } g(C);")]
  [TestCase("export { C };")]
  [TestCase("function g() { const C = 1; return C; }")]
  public void TableTheFileDoesMoreWithThanRead_IsNoConstant(string use) {
    const string table = "const C = new Uint8Array([1, 2]); ";
    Assert.That(JavaScriptFrontend.Import("snippet.mjs", table + use + " function f() { return C[0] + C.length; }", ["f"]).Diagnostics.Single().Message, Is.EqualTo("'C' is not supported yet"));
  }

  /// <summary>Reading leaves a table what it is, wherever in the file it is read, and a property or a variable of another object is not the table.</summary>
  [TestCase("")]
  [TestCase("const first = C[0], size = C.length;")]
  [TestCase("function g(i) { return C[i | 0] + C.length; } g(1);")]
  [TestCase("const O = { C: 1 }; O.C = 2; O.C++;")]
  [TestCase("console.log(C[0].toString(), C.length.toString());")]
  public void TableTheFileOnlyReads_IsAConstant(string use) {
    const string table = "const C = new Uint8Array([1, 2]); ";
    var module = Imported(table + use + " function f() { return C[0] + C.length; }", ["f"]).Module!;
    Assert.That((module.Constants.Single().Name, IrInterpreter.Call(module, "f", [])), Is.EqualTo(("C", 3.0)));
  }

  /// <summary>A constant whose name carries a <c>$</c> is none: the IR builds its own names with it.</summary>
  [Test]
  public void ConstantWithADollar_IsNoConstant()
    => Assert.That(JavaScriptFrontend.Import("snippet.js", "const C$ = 2; function f() { return C$; }").Diagnostics.Single().Message, Is.EqualTo("'C$' is not supported yet"));

  /// <summary>Test design 8: + and - inside one another are integer operations up to the depth at which their exact value is still a double.</summary>
  [Test]
  public void SumsUnderACoercion_AreIntegerOperationsWhileTheyAreExact() {
    static string Sum(int terms) => "/** @param {int32} i */ function f(i) { return (" + string.Join(" + ", Enumerable.Repeat("i", terms)) + ") | 0; }";
    static IrExpr Returned(string source) => ((IrReturn)Imported(source).Module!.Functions[0].Body.Statements[0]).Value!;

    Assert.That(Returned(Sum(21)), Is.InstanceOf<IrBinary>().With.Property(nameof(IrExpr.Type)).EqualTo(IrType.I32));
    Assert.That(Returned(Sum(22)), Is.InstanceOf<IrIntrinsic>());
    Assert.That(Returned("/** @param {int32} i */ function f(i) { return " + string.Concat(Enumerable.Repeat("- ", 20)) + "i | 0; }"), Is.InstanceOf<IrBinary>());
    Assert.That(Returned("/** @param {int32} i */ function f(i) { return " + string.Concat(Enumerable.Repeat("- ", 21)) + "i | 0; }"), Is.InstanceOf<IrIntrinsic>());
    foreach (var terms in new[] { 21, 22 })
      Assert.That(IrInterpreter.Call(Imported(Sum(terms)).Module!, "f", [int.MaxValue]), Is.EqualTo(unchecked(int.MaxValue * terms)));
  }

}
