using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core;
using Polypiler.Core.Frontends.JavaScript;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.JavaScript;

/// <summary>
/// The floating-point library of JavaScript (ECMA-262 §21.3 Math, §21.1.2 Number): every function of
/// the mapping table imports into the operations the table names, every constant is the double an
/// engine has, <c>**</c> is ECMA's exponentiation, and what the table does not hold is refused by name.
/// The expected values of the translated functions come from the definitions of the specification.
/// </summary>
[TestFixture]
public sealed class JavaScriptFloatApiTests {

  private static IEnumerable<TestCaseData> Members
    => JavaScriptFloatApi.Members.Select(member => new TestCaseData(member).SetArgDisplayNames(member.ToString()));

  private static IEnumerable<TestCaseData> Constants
    => JavaScriptFloatApi.Constants.Select(constant => new TestCaseData(constant).SetArgDisplayNames(constant.ToString()));

  private static ImportResult Imported(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("; ", result.Diagnostics));
    return result;
  }

  /// <summary>The module of a source, verified unless it holds an operation the IR does not type yet.</summary>
  private static IrModule Module(string source) {
    var module = Imported(source).Module!;
    if (IsTyped(module))
      Assert.That(IrVerifier.Verify(module), Is.Empty);

    return module;
  }

  /// <summary>Whether the IR has the typing rule and the implementation of every operation of a module: <c>PowEcma</c> it does not have yet.</summary>
  private static bool IsTyped(IrModule module)
    => module.Functions.SelectMany(function => Intrinsics(function.Body)).All(intrinsic => FloatOperationTable.Of(intrinsic.Op) is not null);

  private static Diagnostic Refusal(string source) {
    var result = JavaScriptFrontend.Import("snippet.mjs", source);
    Assert.That(result.Module, Is.Null);
    Assert.That(result.Diagnostics, Has.Count.EqualTo(1), string.Join("; ", result.Diagnostics));
    return result.Diagnostics[0];
  }

  private static object? Call(IrModule module, params object[] arguments) => IrInterpreter.Call(module, "f", arguments);

  private static string Signature(IrFunction function)
    => $"({string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}"))}) -> {function.ReturnType}";

  private static IrExpr Returned(IrModule module) => ((IrReturn)module.Functions.Single().Body.Statements.Single()).Value!;

  [TestCaseSource(nameof(Members))]
  public void Member_ImportsIntoTheOperationsTheTableNames(object entry) {
    var member = (JavaScriptFloatMember)entry;
    var count = member.Arity ?? 2;
    var parameters = string.Join(", ", Enumerable.Range(0, count).Select(index => $"x{index}"));
    var module = Module($"export function f({parameters}) {{ return {member.Name}({parameters}); }}");
    var function = module.Functions.Single();
    Assert.That(Signature(function), Is.EqualTo($"({string.Join(", ", Enumerable.Range(0, count).Select(index => $"x{index}: f64"))}) -> {member.Result}"));
    var used = Intrinsics(function.Body).Select(intrinsic => intrinsic.Op).ToHashSet();
    Assert.That(used, Is.SubsetOf(member.Operations));
    Assert.That(used.Overlaps(member.Operations), Is.True);
  }

  [Test]
  public void TheTable_HasEveryNameOnce_AndNothingElse() {
    Assert.That(JavaScriptFloatApi.Members.Select(member => member.Name), Is.Unique);
    Assert.That(JavaScriptFloatApi.Constants.Select(constant => constant.Name), Is.Unique);
    Assert.That(JavaScriptFloatApi.Find("Math.sin")!.Operations, Is.EqualTo(new[] { IrIntrinsicOp.Sin }));
    Assert.That(JavaScriptFloatApi.Find("Math.pow")!.Operations, Is.EqualTo(new[] { IrIntrinsicOp.PowEcma }));
    Assert.That(JavaScriptFloatApi.Find("Math.hypot")!.Arity, Is.Null);
    Assert.That((JavaScriptFloatApi.Find("isNaN")!.Coerces, JavaScriptFloatApi.Find("Number.isNaN")!.Coerces), Is.EqualTo((true, false)));
    Assert.That(JavaScriptFloatApi.Find("Math.random"), Is.Null);
    Assert.That(JavaScriptFloatApi.Find("Math.PI"), Is.Null);
    Assert.That(JavaScriptFloatApi.FindConstant("Math.PI")!.ToString(), Is.EqualTo("Math.PI"));
    Assert.That(JavaScriptFloatApi.FindConstant("Math.random"), Is.Null);
  }

  /// <summary>Every unary function of Math is the operation of the IR of the same meaning.</summary>
  [TestCase("sin", IrIntrinsicOp.Sin)]
  [TestCase("cos", IrIntrinsicOp.Cos)]
  [TestCase("tan", IrIntrinsicOp.Tan)]
  [TestCase("asin", IrIntrinsicOp.Asin)]
  [TestCase("acos", IrIntrinsicOp.Acos)]
  [TestCase("atan", IrIntrinsicOp.Atan)]
  [TestCase("sinh", IrIntrinsicOp.Sinh)]
  [TestCase("cosh", IrIntrinsicOp.Cosh)]
  [TestCase("tanh", IrIntrinsicOp.Tanh)]
  [TestCase("asinh", IrIntrinsicOp.Asinh)]
  [TestCase("acosh", IrIntrinsicOp.Acosh)]
  [TestCase("atanh", IrIntrinsicOp.Atanh)]
  [TestCase("exp", IrIntrinsicOp.Exp)]
  [TestCase("expm1", IrIntrinsicOp.ExpM1)]
  [TestCase("log", IrIntrinsicOp.Log)]
  [TestCase("log2", IrIntrinsicOp.Log2)]
  [TestCase("log10", IrIntrinsicOp.Log10)]
  [TestCase("log1p", IrIntrinsicOp.Log1P)]
  [TestCase("cbrt", IrIntrinsicOp.Cbrt)]
  public void UnaryFunction_IsTheOperationOfTheSameMeaning(string name, IrIntrinsicOp op) {
    var module = Module($"/** @param {{int32}} a */ export function f(a) {{ return Math.{name}(a); }}");
    var call = (IrIntrinsic)Returned(module);
    Assert.That((call.Op, call.Type, call.Arguments.Single()), Is.EqualTo((op, IrType.F64, (IrExpr)new IrConvert(IrType.F64, new IrLocalRef(module.Functions[0].Parameters[0])))));
  }

  [TestCaseSource(nameof(Constants))]
  public void Constant_IsTheDoubleOfTheSpecification(object entry) {
    var constant = (JavaScriptFloatConstant)entry;
    var module = Module($"export function f() {{ return {constant.Name}; }}");
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(Returned(module), Is.EqualTo(new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(constant.Value))));
  }

  /// <summary>The values as node prints them, and as ECMA-262 defines the ones of Number.</summary>
  [TestCase("Math.E", 2.718281828459045)]
  [TestCase("Math.LN10", 2.302585092994046)]
  [TestCase("Math.LN2", 0.6931471805599453)]
  [TestCase("Math.LOG10E", 0.4342944819032518)]
  [TestCase("Math.LOG2E", 1.4426950408889634)]
  [TestCase("Math.PI", 3.141592653589793)]
  [TestCase("Math.SQRT1_2", 0.7071067811865476)]
  [TestCase("Math.SQRT2", 1.4142135623730951)]
  [TestCase("Number.EPSILON", 2.220446049250313e-16)]
  [TestCase("Number.MAX_VALUE", 1.7976931348623157e308)]
  [TestCase("Number.MIN_VALUE", 5e-324)]
  [TestCase("Number.MAX_SAFE_INTEGER", 9007199254740991.0)]
  [TestCase("Number.MIN_SAFE_INTEGER", -9007199254740991.0)]
  [TestCase("Number.POSITIVE_INFINITY", double.PositiveInfinity)]
  [TestCase("Number.NEGATIVE_INFINITY", double.NegativeInfinity)]
  public void Constant_HasItsValueBitForBit(string name, double expected) {
    var module = Module($"export function f() {{ return {name}; }}");
    Assert.That(BitConverter.DoubleToInt64Bits((double)Call(module)!), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
  }

  [Test]
  public void ConstantsOfTheEngine_AreThePowersOfTwoTheSpecificationNames() {
    Assert.That(JavaScriptFloatApi.FindConstant("Number.EPSILON")!.Value, Is.EqualTo(Math.ScaleB(1.0, -52)));
    Assert.That(JavaScriptFloatApi.FindConstant("Number.MAX_SAFE_INTEGER")!.Value, Is.EqualTo(Math.ScaleB(1.0, 53) - 1));
    Assert.That(JavaScriptFloatApi.FindConstant("Number.MIN_VALUE")!.Value, Is.EqualTo(Math.ScaleB(1.0, -1074)));
    Assert.That(double.IsNaN((double)Call(Module("export function f() { return Number.NaN; }"))!), Is.True);
  }

  /// <summary>A constant is a double wherever it goes: a local holding it is one, also the safe integers, which no 32-bit type holds.</summary>
  [Test]
  public void Constant_IsTypedAsADouble() {
    var result = Imported("export function f() { let k = Number.MAX_SAFE_INTEGER; let p = Math.PI; return k + p; }");
    var locals = result.Explanations.Where(explanation => explanation.Role == "local").Select(explanation => $"{explanation.Name}:{explanation.Type}");
    Assert.That(locals, Is.EqualTo(new[] { "k:f64", "p:f64" }));
  }

  [TestCase("export function f(x) { return Math.sin(x); }", "(x: f64) -> f64")]
  [TestCase("export function f(x, y) { return Math.atan2(y, x) + Math.hypot(x, y, 1); }", "(x: f64, y: f64) -> f64")]
  [TestCase("export function f(x) { return Number.isSafeInteger(x) && isFinite(x); }", "(x: f64) -> bool")]
  [TestCase("export function f(x) { const r = Math.cbrt(x); return r; }", "(x: f64) -> f64")]
  [TestCase("function g(x) { return Math.log(x); } export function f(a) { return g(a | 0); }", "(a: i32) -> f64")]
  public void UntypedCode_IsInferredFromTheResultTypes(string source, string signature)
    => Assert.That(Signature(Module(source).Functions.Last()), Is.EqualTo(signature));

  [Test]
  public void PowAndTheOperator_AreEcmasExponentiation() {
    var pow = Module("export function f(x, y) { return Math.pow(x, y); }");
    var power = Module("export function f(x, y) { return x ** y; }");
    var compound = Module("export function f(x, y) { x **= y; return x; }");
    var element = Module("/** @param {float64[]} d */ export function f(d) { d[0] **= 2; return d[0]; }");
    foreach (var module in new[] { pow, power, compound, element })
      Assert.That(Intrinsics(module.Functions[0].Body).Select(intrinsic => intrinsic.Op), Is.EqualTo(new[] { IrIntrinsicOp.PowEcma }));

    var (x, y) = (new IrLocalRef(power.Functions[0].Parameters[0]), new IrLocalRef(power.Functions[0].Parameters[1]));
    var returned = (IrIntrinsic)Returned(power);
    Assert.That((returned.Type, returned.Arguments[0], returned.Arguments[1]), Is.EqualTo((IrType.F64, (IrExpr)x, (IrExpr)y)));
    var squared = (IrIntrinsic)Returned(Module("/** @param {int32} a */ export function f(a) { return a ** 2; }"));
    Assert.That(squared.Arguments[1], Is.EqualTo(new IrConst(IrType.F64, BitConverter.DoubleToInt64Bits(2))));
    if (!IsTyped(pow))
      return;

    // ECMA-262 §6.1.6.1.3: 2 ** 10 is 1024; a NaN exponent and ±1 ** ±Infinity give NaN.
    Assert.That(Call(pow, 2.0, 10.0), Is.EqualTo(1024.0));
    Assert.That(Call(power, 2.0, -1.0), Is.EqualTo(0.5));
    Assert.That(double.IsNaN((double)Call(power, 1.0, double.PositiveInfinity)!), Is.True);
    Assert.That(double.IsNaN((double)Call(pow, 1.0, double.NaN)!), Is.True);
  }

  /// <summary>Math.hypot (§21.3.2.18): +0 of nothing, the magnitude of one, Hypot of two, a left fold of more.</summary>
  [Test]
  public void Hypot_ByTheNumberOfArguments() {
    var none = Module("export function f() { return Math.hypot(); }");
    Assert.That(Returned(none), Is.EqualTo(new IrConst(IrType.F64, 0)));
    var one = Module("export function f(x) { return Math.hypot(x); }");
    Assert.That(((IrIntrinsic)Returned(one)).Op, Is.EqualTo(IrIntrinsicOp.Abs));
    var two = Module("export function f(x, y) { return Math.hypot(x, y); }");
    Assert.That(((IrIntrinsic)Returned(two)).Op, Is.EqualTo(IrIntrinsicOp.Hypot));
    var three = Module("export function f(x, y, z) { return Math.hypot(x, y, z); }");
    var outer = (IrIntrinsic)Returned(three);
    Assert.That((outer.Op, ((IrIntrinsic)outer.Arguments[0]).Op, outer.Arguments[1]), Is.EqualTo((IrIntrinsicOp.Hypot, IrIntrinsicOp.Hypot, (IrExpr)new IrLocalRef(three.Functions[0].Parameters[2]))));

    Assert.That(BitConverter.DoubleToInt64Bits((double)Call(none)!), Is.EqualTo(0L));
    Assert.That(Call(one, -3.0), Is.EqualTo(3.0));
    Assert.That(BitConverter.DoubleToInt64Bits((double)Call(one, -0.0)!), Is.EqualTo(0L));
    Assert.That(Call(two, 3.0, 4.0), Is.EqualTo(5.0));
    Assert.That(Call(three, 3.0, 4.0, 12.0), Is.EqualTo(13.0));
    Assert.That(Call(three, double.NaN, 1.0, double.NegativeInfinity), Is.EqualTo(double.PositiveInfinity));
    Assert.That(double.IsNaN((double)Call(three, double.NaN, 1.0, 2.0)!), Is.True);
  }

  /// <summary>The predicates of Number (§21.1.2.2-5) and the global isNaN and isFinite (§19.2.2-3) on numbers.</summary>
  [TestCase("Number.isNaN", double.NaN, true)]
  [TestCase("Number.isNaN", 0.0, false)]
  [TestCase("Number.isNaN", double.PositiveInfinity, false)]
  [TestCase("isNaN", double.NaN, true)]
  [TestCase("isNaN", -1.5, false)]
  [TestCase("Number.isFinite", 1.0, true)]
  [TestCase("Number.isFinite", double.NegativeInfinity, false)]
  [TestCase("Number.isFinite", double.NaN, false)]
  [TestCase("isFinite", double.MaxValue, true)]
  [TestCase("isFinite", double.PositiveInfinity, false)]
  [TestCase("Number.isInteger", 5.0, true)]
  [TestCase("Number.isInteger", -0.0, true)]
  [TestCase("Number.isInteger", 5.5, false)]
  [TestCase("Number.isInteger", double.PositiveInfinity, false)]
  [TestCase("Number.isInteger", double.NaN, false)]
  [TestCase("Number.isSafeInteger", 9007199254740991.0, true)]
  [TestCase("Number.isSafeInteger", -9007199254740991.0, true)]
  [TestCase("Number.isSafeInteger", 9007199254740992.0, false)]
  [TestCase("Number.isSafeInteger", -9007199254740992.0, false)]
  [TestCase("Number.isSafeInteger", 0.5, false)]
  [TestCase("Number.isSafeInteger", 0.0, true)]
  [TestCase("Number.isSafeInteger", double.PositiveInfinity, false)]
  [TestCase("Number.isSafeInteger", double.NaN, false)]
  public void Predicate_IsWhatTheSpecificationDefines(string name, double x, bool expected) {
    var module = Module($"export function f(x) {{ return {name}(x); }}");
    Assert.That(module.Functions[0].ReturnType, Is.EqualTo(IrType.Bool));
    Assert.That(Call(module, x), Is.EqualTo(expected));
  }

  /// <summary>Number.isSafeInteger reads its operand twice: a variable, a constant, or an integer variable as a double.</summary>
  [TestCase("export function f(x) { return Number.isSafeInteger(x); }")]
  [TestCase("export function f() { return Number.isSafeInteger(3); }")]
  [TestCase("export function f() { return Number.isSafeInteger(0.5); }")]
  [TestCase("/** @param {int32} a */ export function f(a) { return Number.isSafeInteger(a); }")]
  public void IsSafeIntegerOfAPlainOperand_IsImported(string source)
    => Assert.That(Intrinsics(Module(source).Functions[0].Body).Select(intrinsic => intrinsic.Op), Is.EqualTo(new[] { IrIntrinsicOp.IsInteger, IrIntrinsicOp.Abs }));

  /// <summary>ECMA-262 §21.3.2.28: Math.round rounds a tie towards +Infinity, unchanged by the table.</summary>
  [TestCase(-2.5, -2.0)]
  [TestCase(2.5, 3.0)]
  [TestCase(-0.5, -0.0)]
  [TestCase(0.49999999999999994, 0.0)]
  public void Round_TakesATieUpwards(double x, double expected) {
    var module = Module("export function f(x) { return Math.round(x); }");
    Assert.That(BitConverter.DoubleToInt64Bits((double)Call(module, x)!), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected)));
  }

  /// <summary>A few translated functions compute what the specification defines for exact cases.</summary>
  [Test]
  public void TranslatedFunctions_ComputeTheExactCases() {
    Assert.That(Call(Module("export function f(x) { return Math.cbrt(x); }"), -27.0), Is.EqualTo(-3.0));
    Assert.That(Call(Module("export function f(x) { return Math.log2(x); }"), 1024.0), Is.EqualTo(10.0));
    Assert.That(Call(Module("export function f(x) { return Math.log10(x); }"), 1000.0), Is.EqualTo(3.0));
    Assert.That(Call(Module("export function f(x) { return Math.exp(x) + Math.expm1(x) + Math.log1p(x); }"), 0.0), Is.EqualTo(1.0));
    Assert.That(Call(Module("export function f(x) { return Math.sin(x) + Math.atan2(x, 1); }"), 0.0), Is.EqualTo(0.0));
    Assert.That(Call(Module("export function f(x) { return Math.cos(x) * Math.PI; }"), 0.0), Is.EqualTo(3.141592653589793));
  }

  /// <summary>Test design: what is outside the table is refused with PLY1102 by the name of the member.</summary>
  [TestCase("export function f() { return Math.random(); }", "Math.random")]
  [TestCase("export function f() { return Math.random; }", "Math.random")]
  [TestCase("export function f(x) { return Number.parseFloat(x); }", "Number.parseFloat")]
  [TestCase("export function f() { return Number.MAX_VALUE(1); }", "Number.MAX_VALUE")]
  [TestCase("export function f() { return Math.sin(); }", "Math.sin with 0 arguments")]
  [TestCase("export function f(x) { return Math.atan2(x); }", "Math.atan2 with 1 arguments")]
  [TestCase("export function f(x) { return Number.isNaN(x, x); }", "Number.isNaN with 2 arguments")]
  [TestCase("/** @param {boolean} t */ export function f(t) { return isNaN(t); }", "isNaN of boolean")]
  [TestCase("/** @param {boolean} t */ export function f(t) { return isFinite(t); }", "isFinite of boolean")]
  [TestCase("/** @param {boolean} t */ export function f(t) { return Number.isNaN(t); }", "boolean as a number")]
  [TestCase("/** @param {boolean} t */ export function f(t) { return Math.sin(t); }", "boolean as a number")]
  [TestCase("export function f(x) { return Number.isSafeInteger(x * 2); }", "Number.isSafeInteger of anything but a variable or a constant")]
  [TestCase("/** @param {int32} a */ export function f(a) { return Number.isSafeInteger(a | 1); }", "Number.isSafeInteger of anything but a variable or a constant")]
  [TestCase("/** @param {boolean} t */ export function f(t) { return t ** 2; }", "** on boolean and number")]
  [TestCase("export function f() { let Math = 1; return Math.PI; }", "Math.PI")]
  [TestCase("export function f(x) { let isNaN = 1; return isNaN(x); }", "a call of isNaN")]
  public void OutsideTheTable_IsRefusedByName(string source, string what) {
    var refusal = Refusal(source);
    Assert.That((refusal.Code, refusal.Message), Is.EqualTo((JavaScriptFrontend.Unsupported, $"'{what}' is not supported yet")));
  }

  /// <summary>A function of the file named like a global function is the one called.</summary>
  [Test]
  public void FunctionOfTheFile_HidesTheGlobalFunction() {
    var module = Module("function isNaN(x) { return x + 1; } export function f(x) { return isNaN(x); }");
    var returned = ((IrReturn)module.Functions.Single(function => function.Name == "f").Body.Statements.Single()).Value;
    Assert.That(returned, Is.InstanceOf<IrCall>());
    Assert.That(Call(module, 1.0), Is.EqualTo(2.0));
  }

  /// <summary>A property of an object literal named like a global function hides nothing: the call is the global function.</summary>
  [Test]
  public void PropertyOfTheFile_DoesNotHideTheGlobalFunction() {
    var module = Module("const O = { isNaN: function (x) { return x; } }; export function f(x) { return isNaN(x); }");
    var returned = ((IrReturn)module.Functions.Single(function => function.Name == "f").Body.Statements.Single()).Value;
    Assert.That(((IrIntrinsic)returned!).Op, Is.EqualTo(IrIntrinsicOp.IsNaN));
  }

  private static IEnumerable<IrIntrinsic> Intrinsics(IrBlock block) {
    var found = new List<IrIntrinsic>();
    void Walk(object? node) {
      switch (node) {
        case IrIntrinsic intrinsic:
          foreach (var argument in intrinsic.Arguments)
            Walk(argument);

          found.Add(intrinsic);
          break;
        case IrBinary binary:
          Walk(binary.Left);
          Walk(binary.Right);
          break;
        case IrConvert conversion:
          Walk(conversion.Operand);
          break;
        case IrReturn result:
          Walk(result.Value);
          break;
        case IrAssign assignment:
          Walk(assignment.Value);
          break;
        case IrStore store:
          Walk(store.Value);
          break;
        case IrDeclare declaration:
          Walk(declaration.Initializer);
          break;
        case IrBlock inner:
          foreach (var statement in inner.Statements)
            Walk(statement);

          break;
      }
    }

    Walk(block);
    return found;
  }

}
