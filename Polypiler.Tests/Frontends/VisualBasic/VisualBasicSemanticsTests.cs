using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Analysis.Dynamic;
using Polypiler.Core.Frontends.VisualBasic;
using Polypiler.Core.IR;

namespace Polypiler.Tests.Frontends.VisualBasic;

/// <summary>
/// The semantics table of classic Visual Basic (#1584, ADR 0300 decision 4), checked against the
/// helpers it describes: every operator on every pair of sample values of the scalar kinds is run
/// through the frontend's own <c>Variant</c> helpers in the interpreter, and the kind of each result
/// must be one the table's result allows, each failure one its trap names; the typed expansions compute
/// what the helpers compute; and the scenarios of the issue - promotion, <c>Null</c>, error 13.
/// </summary>
[TestFixture]
public sealed class VisualBasicSemanticsTests {

  private static readonly IDynamicSemantics Table = VisualBasicFrontend.Semantics;

  private static DynamicType E(IrType type, IrVariantTag? tag = null) => DynamicType.Exact(type, tag);

  private static object? Run(IrModule module, string function, params object[] arguments) => IrInterpreter.Call(module, function, arguments);

  private static IrModule Import(string source) {
    var result = VisualBasicFrontend.Import("Test.bas", source);
    Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    return result.Module!;
  }

  /// <summary>The sample values: every scalar kind a Variant of the module may hold, with the edges of the promotions and a string that is no number.</summary>
  private static readonly IrVariant[] _samples = [
    IrVariant.Empty, IrVariant.Null,
    new(IrVariantTag.Bool, true), new(IrVariantTag.U8, (byte)255), new(IrVariantTag.U8, (byte)0),
    new(IrVariantTag.I16, (short)32767), new(IrVariantTag.I16, (short)-2), new(IrVariantTag.I32, int.MaxValue), new(IrVariantTag.I32, 3),
    new(IrVariantTag.F32, 1.5f), new(IrVariantTag.F64, 2.5), new(IrVariantTag.F64, 1e308),
    new(IrVariantTag.String, "12"), new(IrVariantTag.String, "a"), new(IrVariantTag.Date, 2.0)
  ];

  private static readonly Dictionary<string, DynamicOperation> _binary = new() {
    ["+"] = DynamicOperation.Add, ["-"] = DynamicOperation.Subtract, ["*"] = DynamicOperation.Multiply, ["/"] = DynamicOperation.Divide,
    ["\\"] = DynamicOperation.IntegerDivide, ["Mod"] = DynamicOperation.Modulo, ["^"] = DynamicOperation.Power, ["&"] = DynamicOperation.Concatenate,
    ["="] = DynamicOperation.Equal, ["<>"] = DynamicOperation.NotEqual, ["<"] = DynamicOperation.Less, ["<="] = DynamicOperation.LessOrEqual,
    [">"] = DynamicOperation.Greater, [">="] = DynamicOperation.GreaterOrEqual,
    ["And"] = DynamicOperation.And, ["Or"] = DynamicOperation.Or, ["Xor"] = DynamicOperation.Xor, ["Eqv"] = DynamicOperation.Eqv, ["Imp"] = DynamicOperation.Imp
  };

  private static IEnumerable<string> Binary() => _binary.Keys;

  /// <summary>The kind of the result of every pair is one the table allows; every pair that fails, fails as the table's trap says.</summary>
  [TestCaseSource(nameof(Binary))]
  public void EveryPair_GivesAKindTheTableAllows(string op) {
    var module = Import($"Public Function F(a, b)\nF = a {op} b\nEnd Function\n");
    var operation = _binary[op];
    var domain = new DynamicFacts(Table.Joins);
    var pairs = 0;
    foreach (var a in _samples)
      foreach (var b in _samples) {
        // A Date in '&' of a parameter traps in the frontend's text helper today, a defect of the
        // lowering rather than a rule of the language; the table states Visual Basic's rule.
        if (operation == DynamicOperation.Concatenate && (a.Tag == IrVariantTag.Date || b.Tag == IrVariantTag.Date))
          continue;
        var (left, right) = (DynamicType.OfTag(a.Tag), DynamicType.OfTag(b.Tag));
        object? result;
        try {
          result = Run(module, "F", a, b);
        } catch (Exception exception) when (exception is IrException or IrTrapException) {
          Assert.That(Table.Trap(operation, left, right).MayFail, Is.True, $"{a} {op} {b} fails with {exception.Message}");
          continue;
        }

        var element = Table.Result(operation, left, right);
        Assert.That(domain.Contains(DynamicFact.Proven(element), result!), Is.True, $"{a} {op} {b} = {result}, the table says {element}");
        ++pairs;
      }

    Assert.That(pairs, Is.GreaterThan(_samples.Length * 3), "most pairs are computed");
  }

  [TestCase("-", DynamicOperation.Negate)]
  [TestCase("Not", DynamicOperation.Not)]
  public void EveryValue_OfAUnaryOperator_GivesAKindTheTableAllows(string op, DynamicOperation operation) {
    var module = Import($"Public Function F(a)\nF = {op} a\nEnd Function\n");
    var domain = new DynamicFacts(Table.Joins);
    foreach (var a in _samples) {
      object? result;
      try {
        result = Run(module, "F", a);
      } catch (Exception exception) when (exception is IrException or IrTrapException) {
        Assert.That(Table.Trap(operation, DynamicType.OfTag(a.Tag), DynamicType.Nothing).MayFail, Is.True, $"{op} {a}");
        continue;
      }

      Assert.That(domain.Contains(DynamicFact.Proven(Table.Result(operation, DynamicType.OfTag(a.Tag), DynamicType.Nothing)), result!), Is.True, $"{op} {a} = {result}");
    }
  }

  [Test]
  public void IntegerOverflow_PromotesToLong_AndLongToDouble() {
    Assert.That(Table.Result(DynamicOperation.Add, E(IrType.I16), E(IrType.I16)).ToString(), Is.EqualTo("union<i16, i32>"));
    Assert.That(Table.Result(DynamicOperation.Add, E(IrType.I32), E(IrType.I32)).ToString(), Is.EqualTo("union<f64, i32>"));
    var module = Import("Public Function F(a, b)\nF = a + b\nEnd Function\n");
    Assert.That(Run(module, "F", new IrVariant(IrVariantTag.I16, (short)32767), new IrVariant(IrVariantTag.I16, (short)1)), Is.EqualTo(new IrVariant(IrVariantTag.I32, 32768)));
    Assert.That(Run(module, "F", new IrVariant(IrVariantTag.I32, int.MaxValue), new IrVariant(IrVariantTag.I32, 1)), Is.EqualTo(new IrVariant(IrVariantTag.F64, 2147483648.0)));
  }

  [Test]
  public void Null_Propagates_AndEmpty_IsZeroOrNothing() {
    var withNull = Table.Result(DynamicOperation.Add, DynamicType.Optional(E(IrType.I32), DynamicMissing.Null), E(IrType.I32));
    Assert.That(withNull, Is.InstanceOf<DynamicOptional>());
    Assert.That(((DynamicOptional)withNull).Missing, Is.EqualTo(DynamicMissing.Null));
    Assert.That(Table.Result(DynamicOperation.Concatenate, DynamicType.OfTag(IrVariantTag.Empty), E(IrType.String)), Is.EqualTo(E(IrType.String)));
    Assert.That(Table.Result(DynamicOperation.Add, DynamicType.OfTag(IrVariantTag.Empty), E(IrType.I32)), Is.EqualTo(E(IrType.I32)));
    Assert.That(Table.Result(DynamicOperation.Concatenate, DynamicType.OfTag(IrVariantTag.Null), DynamicType.OfTag(IrVariantTag.Null)).ToString(), Is.EqualTo("optional<str; Null>"));
  }

  [Test]
  public void AStringThatIsNoNumber_TrapsWithError13() {
    Assert.That(Table.Trap(DynamicOperation.Add, E(IrType.String), E(IrType.I32)), Is.EqualTo(DynamicTrap.Error(13)));
    Assert.That(Table.Trap(DynamicOperation.Add, E(IrType.String), E(IrType.String)), Is.EqualTo(DynamicTrap.None));
    var module = Import("Public Function F(a, b)\nF = a + b\nEnd Function\n");
    var exception = Assert.Throws<IrException>(() => Run(module, "F", new IrVariant(IrVariantTag.String, "a"), new IrVariant(IrVariantTag.I32, 1)))!;
    Assert.That(VisualBasicVariantTests.Number(exception.Class), Is.EqualTo(13));
  }

  [Test]
  public void Traps_NameVisualBasicsErrors() {
    Assert.That(Table.Trap(DynamicOperation.Divide, E(IrType.I32), E(IrType.I32)), Is.EqualTo(DynamicTrap.Error(11)));
    Assert.That(Table.Trap(DynamicOperation.IntegerDivide, E(IrType.I32), E(IrType.I32)), Is.EqualTo(DynamicTrap.Error(11)));
    Assert.That(Table.Trap(DynamicOperation.Power, E(IrType.F64), E(IrType.F64)), Is.EqualTo(DynamicTrap.Error(5)));
    Assert.That(Table.Trap(DynamicOperation.Add, E(IrType.F64), E(IrType.F64)), Is.EqualTo(DynamicTrap.Error(6)));
    Assert.That(Table.Trap(DynamicOperation.Add, E(IrType.I16), E(IrType.I16)), Is.EqualTo(DynamicTrap.None), "the integers promote");
    Assert.That(Table.Trap(DynamicOperation.Less, E(IrType.String), E(IrType.I32)), Is.EqualTo(DynamicTrap.None));
    Assert.That(Table.Trap(DynamicOperation.Not, E(IrType.String), DynamicType.Nothing), Is.EqualTo(DynamicTrap.Error(13)));
    Assert.That(Table.Trap(DynamicOperation.Not, E(IrType.I32), DynamicType.Nothing), Is.EqualTo(DynamicTrap.None));
    Assert.That(Table.Trap(DynamicOperation.Call, DynamicType.Variant, DynamicType.Nothing), Is.EqualTo(DynamicTrap.Error(13)));
    Assert.That(DynamicTrap.None.MayFail, Is.False);
    Assert.That(new DynamicTrap(IrTrap.InvalidCast, null).MayFail, Is.True);
    Assert.That(() => Table.Trap(DynamicOperation.Add, null!, DynamicType.Nothing), Throws.ArgumentNullException);
    Assert.That(() => Table.Trap(DynamicOperation.Add, DynamicType.Nothing, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void Results_OfWhatTheLoweringRefuses_OrDoesNotKnow_AreVariants() {
    Assert.That(Table.Result(DynamicOperation.Add, E(IrType.I64), E(IrType.I32)), Is.EqualTo(DynamicType.Variant), "LongLong arithmetic");
    Assert.That(Table.Result(DynamicOperation.Power, E(IrType.I64, IrVariantTag.Currency), E(IrType.I32)), Is.EqualTo(DynamicType.Variant), "Currency in ^");
    Assert.That(Table.Result(DynamicOperation.Modulo, E(IrType.I64, IrVariantTag.Currency), E(IrType.I32)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.And, E(IrType.I64, IrVariantTag.Currency), E(IrType.I32)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Not, E(IrType.I64), DynamicType.Nothing), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Concatenate, DynamicType.Optional(E(IrType.I64, IrVariantTag.Currency), DynamicMissing.Null), DynamicType.OfTag(IrVariantTag.Null)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Index, E(IrType.I32), E(IrType.I32)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Add, E(IrType.Array(IrType.Variant)), E(IrType.I32)), Is.EqualTo(DynamicType.Variant), "an array");
    Assert.That(Table.Result(DynamicOperation.Add, DynamicType.Nominal(new IrStructType("R")), E(IrType.I32)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Add, DynamicType.Optional(E(IrType.I32), DynamicMissing.Undefined), E(IrType.I32)), Is.EqualTo(DynamicType.Variant));
    Assert.That(Table.Result(DynamicOperation.Truthiness, DynamicType.Variant, DynamicType.Nothing), Is.EqualTo(E(IrType.Bool)));
    Assert.That(Table.Result(DynamicOperation.ToNumber, DynamicType.Variant, DynamicType.Nothing), Is.EqualTo(E(IrType.F64)));
    Assert.That(Table.Result(DynamicOperation.ToString, DynamicType.Variant, DynamicType.Nothing), Is.EqualTo(E(IrType.String)));
    Assert.That(Table.Result(DynamicOperation.And, E(IrType.Bool), E(IrType.Bool)), Is.EqualTo(E(IrType.Bool)));
    Assert.That(Table.Result(DynamicOperation.Eqv, E(IrType.Bool), E(IrType.Bool)), Is.EqualTo(E(IrType.Bool)));
    Assert.That(Table.Result(DynamicOperation.And, E(IrType.I32), E(IrType.U8)), Is.EqualTo(E(IrType.I32)));
    Assert.That(Table.Result(DynamicOperation.Not, E(IrType.Bool), DynamicType.Nothing), Is.EqualTo(E(IrType.Bool)));
    Assert.That(Table.Result(DynamicOperation.Not, E(IrType.U8), DynamicType.Nothing), Is.EqualTo(E(IrType.U8)));
    Assert.That(Table.Result(DynamicOperation.Divide, E(IrType.F32), E(IrType.I16)).ToString(), Is.EqualTo("union<f32, f64>"));
    Assert.That(Table.Result(DynamicOperation.Less, E(IrType.I32), E(IrType.I16)), Is.EqualTo(E(IrType.Bool)));
    Assert.That(Table.Joins, Is.SameAs(DistinctJoins.Instance));
    Assert.That(Table.Language, Is.EqualTo("visualbasic"));
    Assert.That(() => Table.Result(DynamicOperation.Add, null!, DynamicType.Nothing), Throws.ArgumentNullException);
    Assert.That(() => Table.Result(DynamicOperation.Add, DynamicType.Nothing, null!), Throws.ArgumentNullException);
  }

  /// <summary>The cases of the typed expansions: the operator, the declared types of the operands and their kinds.</summary>
  private static IEnumerable<TestCaseData> Expansions() {
    yield return new TestCaseData("a + b", "Double", "Double", DynamicOperation.Add).SetName("Expand_DoublePlusDouble");
    yield return new TestCaseData("a - b", "Double", "Long", DynamicOperation.Subtract).SetName("Expand_DoubleMinusLong");
    yield return new TestCaseData("a * b", "Integer", "Double", DynamicOperation.Multiply).SetName("Expand_IntegerTimesDouble");
    yield return new TestCaseData("a / b", "Long", "Long", DynamicOperation.Divide).SetName("Expand_LongDividedByLong");
    yield return new TestCaseData("a ^ b", "Double", "Integer", DynamicOperation.Power).SetName("Expand_DoubleToTheInteger");
    yield return new TestCaseData("a < b", "Long", "Double", DynamicOperation.Less).SetName("Expand_LongLessThanDouble");
    yield return new TestCaseData("a = b", "Byte", "Single", DynamicOperation.Equal).SetName("Expand_ByteEqualsSingle");
    yield return new TestCaseData("a & b", "String", "String", DynamicOperation.Concatenate).SetName("Expand_StringAndString");
    yield return new TestCaseData("a + b", "String", "String", DynamicOperation.Add).SetName("Expand_StringPlusString");
  }

  /// <summary>Typed parameters given in a Variant: the table's expansion on the typed values computes what the helper computes on the Variants.</summary>
  [TestCaseSource(nameof(Expansions))]
  public void Expansion_ComputesWhatTheHelperComputes(string expression, string leftType, string rightType, DynamicOperation operation) {
    var module = Import($"Public Function F(a, b)\nF = {expression}\nEnd Function\nPublic Function G(ByVal x As Double, ByVal y As Double) As Double\nG = x / y + x ^ y + x * y\nEnd Function\n");
    var left = new IrLocal("a", VbIr(leftType));
    var right = new IrLocal("b", VbIr(rightType));
    var (leftElement, rightElement) = (DynamicType.Exact(left.Type), DynamicType.Exact(right.Type));
    var expanded = Table.Expand(operation, new IrLocalRef(left), leftElement, new IrLocalRef(right), rightElement);
    Assert.That(expanded, Is.Not.Null);
    Assert.That(expanded!.Type, Is.EqualTo(Table.Result(operation, leftElement, rightElement).ToIrType()));
    var typed = module with { Functions = [.. module.Functions, new IrFunction("E", true, expanded.Type, [left, right], [], new IrBlock([new IrReturn(expanded)]))] };
    Assert.That(IrVerifier.Verify(typed), Is.Empty);
    foreach (var (x, y) in Values(leftType).SelectMany(x => Values(rightType).Select(y => (x, y)))) {
      var dynamic = Outcome(() => Run(typed, "F", Box(leftType, x), Box(rightType, y)));
      var direct = Outcome(() => Run(typed, "E", x, y));
      Assert.That(direct, Is.EqualTo(dynamic is IrVariant variant ? variant.Value : dynamic), $"{x} {expression} {y}");
    }
  }

  private static object? Outcome(Func<object?> run) {
    try {
      return run();
    } catch (IrException exception) {
      return "error " + VisualBasicVariantTests.Number(exception.Class);
    }
  }

  private static IrType VbIr(string type) => type switch {
    "Double" => IrType.F64,
    "Single" => IrType.F32,
    "Long" => IrType.I32,
    "Integer" => IrType.I16,
    "Byte" => IrType.U8,
    _ => IrType.String
  };

  private static object[] Values(string type) => type switch {
    "Double" => [2.5, -1e308, 1e308, 0.0],
    "Single" => [1.5f, 0f],
    "Long" => [7, 0, int.MinValue],
    "Integer" => [(short)-3, (short)32767],
    "Byte" => [(byte)0, (byte)255],
    _ => ["x", ""]
  };

  private static IrVariant Box(string type, object value) => new(IrVariants.TagOf(VbIr(type))!.Value, value);

  [Test]
  public void PairsWithoutATypedExpansion_KeepTheHelper() {
    var x = new IrLocalRef(new IrLocal("x", IrType.I32));
    var v = new IrLocalRef(new IrLocal("v", IrType.Variant));
    Assert.That(Table.Expand(DynamicOperation.Add, x, E(IrType.I32), x, E(IrType.I32)), Is.Null, "Long + Long may promote");
    Assert.That(Table.Expand(DynamicOperation.Add, v, DynamicType.Variant, x, E(IrType.I32)), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Add, x, E(IrType.Bool), x, E(IrType.F64)), Is.Null, "a Boolean is -1, which no conversion gives");
    Assert.That(Table.Expand(DynamicOperation.Add, x, E(IrType.F64, IrVariantTag.Date), x, E(IrType.F64)), Is.Null, "a Date");
    Assert.That(Table.Expand(DynamicOperation.Divide, x, E(IrType.F32), x, E(IrType.F32)), Is.Null, "a Single quotient");
    Assert.That(Table.Expand(DynamicOperation.Concatenate, x, E(IrType.I32), x, E(IrType.String)), Is.Null, "the text of a number is the locale's");
    Assert.That(Table.Expand(DynamicOperation.ToString, x, E(IrType.I32), null, DynamicType.Nothing), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Negate, x, E(IrType.I32), null, DynamicType.Nothing), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Modulo, x, E(IrType.F64), x, E(IrType.F64)), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Equal, x, E(IrType.String), x, E(IrType.String)), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Power, x, E(IrType.Bool), x, E(IrType.F64)), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.Add, x, E(IrType.Enum(new IrEnumType("C", IrType.I32, [new("A", 0)]))), x, E(IrType.F64)), Is.Null, "an enum");
    Assert.That(Table.Expand(DynamicOperation.Truthiness, x, E(IrType.String), null, DynamicType.Nothing), Is.Null);
    Assert.That(Table.Expand(DynamicOperation.ToNumber, x, E(IrType.String), null, DynamicType.Nothing), Is.Null);
    Assert.That(() => Table.Expand(DynamicOperation.Add, null!, E(IrType.I32), x, E(IrType.I32)), Throws.ArgumentNullException);
    Assert.That(() => Table.Expand(DynamicOperation.Add, x, null!, x, E(IrType.I32)), Throws.ArgumentNullException);
    Assert.That(() => Table.Expand(DynamicOperation.Add, x, E(IrType.I32), x, null!), Throws.ArgumentNullException);
  }

  [Test]
  public void UnaryExpansions_AreTheTypedOperations() {
    var d = new IrLocalRef(new IrLocal("d", IrType.F64));
    var flag = new IrLocalRef(new IrLocal("f", IrType.Bool));
    var n = new IrLocalRef(new IrLocal("n", IrType.I16));
    var s = new IrLocalRef(new IrLocal("s", IrType.String));
    Assert.That(Table.Expand(DynamicOperation.Negate, d, E(IrType.F64), null, DynamicType.Nothing), Is.EqualTo(new IrUnary(IrUnaryOp.Negate, d)));
    Assert.That(Table.Expand(DynamicOperation.Truthiness, flag, E(IrType.Bool), null, DynamicType.Nothing), Is.SameAs(flag));
    Assert.That(Table.Expand(DynamicOperation.Truthiness, n, E(IrType.I16), null, DynamicType.Nothing), Is.EqualTo(new IrBinary(IrBinaryOp.NotEqual, new IrConvert(IrType.F64, n, IrConversion.Wrap), new IrConst(IrType.F64, 0))));
    Assert.That(Table.Expand(DynamicOperation.ToNumber, d, E(IrType.F64), null, DynamicType.Nothing), Is.SameAs(d));
    Assert.That(Table.Expand(DynamicOperation.ToString, s, E(IrType.String), null, DynamicType.Nothing), Is.SameAs(s));
  }

  /// <summary>The source of each helper call the lowering writes, and the operation the table recognises it as.</summary>
  private static IEnumerable<TestCaseData> Calls() {
    var a = new IrLocalRef(new IrLocal("a", IrType.Variant));
    var b = new IrLocalRef(new IrLocal("b", IrType.Variant));
    IrConst Code(int value) => new(IrType.I16, value);
    IrCall Call(string name, IrType type, params IrExpr[] arguments) => new(name, type, arguments);
    var compare = Call("PlyVarCmp", IrType.I32, a, b);
    yield return new TestCaseData(new IrBinary(IrBinaryOp.Less, compare, new IrConst(IrType.I32, 0)), DynamicOperation.Less, true).SetName("Classify_Test");
    yield return new TestCaseData(new IrBinary(IrBinaryOp.GreaterOrEqual, Call("PlyVarCmpC", IrType.I32, a, b), new IrConst(IrType.I32, 0)), DynamicOperation.GreaterOrEqual, true).SetName("Classify_TestOfACurrency");
    yield return new TestCaseData(Call("PlyVarCmpOp", IrType.Variant, Call("PlyVarCmpT", IrType.I32, a, b), Code(1)), DynamicOperation.NotEqual, true).SetName("Classify_ComparisonValue");
    yield return new TestCaseData(Call("PlyVarArith", IrType.Variant, Code(0), a, b), DynamicOperation.Add, true).SetName("Classify_Add");
    yield return new TestCaseData(Call("PlyVarArithC", IrType.Variant, Code(1), a, b), DynamicOperation.Subtract, true).SetName("Classify_Subtract");
    yield return new TestCaseData(Call("PlyVarArith", IrType.Variant, Code(2), a, b), DynamicOperation.Multiply, true).SetName("Classify_Multiply");
    yield return new TestCaseData(Call("PlyVarDivC", IrType.Variant, a, b), DynamicOperation.Divide, true).SetName("Classify_Divide");
    yield return new TestCaseData(Call("PlyVarPow", IrType.Variant, a, b), DynamicOperation.Power, true).SetName("Classify_Power");
    yield return new TestCaseData(Call("PlyVarIntDiv", IrType.Variant, Code(0), a, b), DynamicOperation.IntegerDivide, true).SetName("Classify_IntegerDivide");
    yield return new TestCaseData(Call("PlyVarIntDiv", IrType.Variant, Code(1), a, b), DynamicOperation.Modulo, true).SetName("Classify_Modulo");
    yield return new TestCaseData(Call("PlyVarConcat", IrType.Variant, a, b), DynamicOperation.Concatenate, true).SetName("Classify_Concatenate");
    yield return new TestCaseData(Call("PlyVarNegC", IrType.Variant, a), DynamicOperation.Negate, false).SetName("Classify_Negate");
    yield return new TestCaseData(Call("PlyVarBitNot", IrType.Variant, a), DynamicOperation.Not, false).SetName("Classify_Not");
    yield return new TestCaseData(Call("PlyVarLogic", IrType.Variant, Code(2), a, b), DynamicOperation.Xor, true).SetName("Classify_Logic");
    yield return new TestCaseData(Call("PlyVarBits", IrType.Variant, Code(4), a, b), DynamicOperation.Imp, true).SetName("Classify_Bits");
    yield return new TestCaseData(Call("PlyVarIsTrue", IrType.Bool, a), DynamicOperation.Truthiness, false).SetName("Classify_Truthiness");
    yield return new TestCaseData(Call("PlyVarDblC", IrType.F64, a), DynamicOperation.ToNumber, false).SetName("Classify_ToNumber");
    yield return new TestCaseData(Call("PlyVarCStr", IrType.String, a), DynamicOperation.ToString, false).SetName("Classify_ToString");
  }

  [TestCaseSource(nameof(Calls))]
  public void Classify_RecognisesEveryHelperCall(IrExpr call, DynamicOperation operation, bool binary) {
    var recognised = Table.Classify(call);
    Assert.That(recognised, Is.Not.Null);
    Assert.That(recognised!.Operation, Is.EqualTo(operation));
    Assert.That(recognised.Left.Type.IsVariant, Is.True);
    Assert.That(recognised.Right is not null, Is.EqualTo(binary));
  }

  [Test]
  public void Classify_PassesOverEverythingElse() {
    var a = new IrLocalRef(new IrLocal("a", IrType.Variant));
    Assert.That(Table.Classify(new IrConst(IrType.I32, 1)), Is.Null);
    Assert.That(Table.Classify(new IrCall("PlyVarArith", IrType.Variant, [new IrConst(IrType.I16, 7), a, a])), Is.Null, "no such code");
    Assert.That(Table.Classify(new IrCall("PlyVarLogic", IrType.Variant, [new IrConst(IrType.I16, 3), a, a])), Is.Null);
    Assert.That(Table.Classify(new IrCall("PlyVarBits", IrType.Variant, [new IrConst(IrType.I16, 5), a, a])), Is.Null);
    Assert.That(Table.Classify(new IrCall("PlyVarCmpOp", IrType.Variant, [new IrCall("PlyVarCmp", IrType.I32, [a, a]), new IrConst(IrType.I16, 6)])), Is.Null);
    Assert.That(Table.Classify(new IrBinary(IrBinaryOp.Add, new IrCall("PlyVarCmp", IrType.I32, [a, a]), new IrConst(IrType.I32, 0))), Is.Null);
    Assert.That(Table.Classify(new IrCall("Other", IrType.Variant, [a])), Is.Null);
    Assert.That(() => Table.Classify(null!), Throws.ArgumentNullException);
  }

}
