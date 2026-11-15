using System.Linq;
using NUnit.Framework;
using Polypiler.Core.Frontends.Python;
using Polypiler.Core.IR;
using Polypiler.Prototype.Testing;

namespace Polypiler.Tests.Frontends.Python;

/// <summary>
/// The scenarios and the test design of #452: Python's <c>int</c>, <c>float</c> and <c>bool</c>
/// arithmetic imported with the value and type CPython computes, every failing operation raising
/// CPython's exception, type hints recorded as contracts and never trusted. The expected values are
/// CPython's, as the issue states them; the differential tests compare whole fixtures with recordings.
/// </summary>
[TestFixture]
public sealed class ScalarLoweringTests {

  private static string F(string parameters, string returns, string expression) => $"def f({parameters}) -> {returns}: return {expression}\n";

  /// <summary>Scenario outline: imported expressions compute the value and type python computes.</summary>
  [TestCase("a: int, b: int", "int", "a // b", new object[] { -7L, 2L }, "int -4")]
  [TestCase("a: int, b: int", "int", "a % b", new object[] { -7L, 3L }, "int 2")]
  [TestCase("a: int, b: int", "int", "a % b", new object[] { 7L, -3L }, "int -2")]
  [TestCase("a: int, b: int", "float", "a / b", new object[] { 7L, 2L }, "float 3.5")]
  [TestCase("a: int, b: int", "int", "a * b", new object[] { 4294967295L, 4294967295L }, "int 18446744065119617025")]
  [TestCase("a: int, b: int", "int", "(a + b) & 0xFFFFFFFF", new object[] { 4294967295L, 1L }, "int 0")]
  [TestCase("a: int, b: int", "int", "a >> b", new object[] { -5L, 1L }, "int -3")]
  [TestCase("a: int", "int", "~a", new object[] { 5L }, "int -6")]
  [TestCase("a: bool, b: bool", "int", "a + b", new object[] { true, true }, "int 2")]
  [TestCase("a: float", "int", "int(a)", new object[] { -2.9 }, "int -2")]
  [TestCase("a: float", "int", "round(a)", new object[] { 2.5 }, "int 2")]
  [TestCase("a: float, b: float", "float", "a + b", new object[] { 0.1, 0.2 }, "float 0.30000000000000004")]
  [TestCase("a: int, b: float", "bool", "a == b", new object[] { 9007199254740993L, 9007199254740992.0 }, "bool False")]
  [TestCase("a: int, b: int", "int", "a or b", new object[] { 0L, 9L }, "int 9")]
  public void Expression_ComputesWhatPythonComputes(string parameters, string returns, string expression, object[] arguments, string expected) {
    var outcome = PythonSnippet.Run(F(parameters, returns, expression), "f", arguments);
    // An int beyond 64 bits raises OverflowError (py-int-64); every other outcome is CPython's.
    Assert.That(outcome, Is.EqualTo(PythonOutcome.Expected(expected)));
  }

  /// <summary>
  /// Scenario: an integer result beyond 64 bits. The IR has no unbounded integer yet, so the import
  /// raises OverflowError where CPython gives 2^128 + 1, and never wraps (py-int-64); within 64 bits
  /// the same function is exact.
  /// </summary>
  [Test]
  public void IntegerBeyond64Bits_RaisesOverflowErrorInsteadOfWrapping() {
    const string source = "def f(a: int) -> int: return a ** 2 + 1\n";
    Assert.That(PythonSnippet.Run(source, "f", 4294967296L), Is.EqualTo("raise OverflowError"));
    Assert.That(PythonSnippet.Run(source, "f", 3037000499L), Is.EqualTo("int 9223372030926249002"));
    var result = PythonSnippet.Import(source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.Int64));
  }

  /// <summary>Scenario outline: failing operations trap with python's exception class at line 1.</summary>
  [TestCase("a: int, b: int", "int", "a // b", new object[] { 1L, 0L }, "ZeroDivisionError")]
  [TestCase("a: float, b: float", "float", "a / b", new object[] { 1.0, 0.0 }, "ZeroDivisionError")]
  [TestCase("a: int, b: int", "int", "a << b", new object[] { 1L, -1L }, "ValueError")]
  public void FailingOperation_RaisesPythonsException(string parameters, string returns, string expression, object[] arguments, string exception)
    => Assert.That(PythonSnippet.Run(F(parameters, returns, expression), "f", arguments), Is.EqualTo("raise " + exception));

  /// <summary>Scenario: a type hint is recorded as a contract, not trusted.</summary>
  [Test]
  public void TypeHint_IsRecordedAsAContractAndNotChecked() {
    const string source = "def f(a: int) -> int: return a + 1\n";
    var result = PythonSnippet.Import(source);
    Assert.That(result.Contracts, Is.EqualTo(new[] { "f.a: int", "f.return: int" }));
    var function = result.Module!.Functions.Single(function => function.Name == "f");
    Assert.That(IrTree.Nodes(function.Body).OfType<IrBinary>().Select(binary => binary.Op), Is.EqualTo(new[] { IrBinaryOp.AddChecked }));
    Assert.That(PythonSnippet.Statements(function.Body).OfType<IrIf>(), Is.Empty, "no check of the parameter");
  }

  /// <summary>Test design 19: contract recording - record, ignore, and the hints of locals.</summary>
  [Test]
  public void Contracts_AreRecordedOrIgnoredAsTheOptionsSay() {
    const string source = "def f(a: float, b) -> float:\n    x: int = 1\n    return a + x\n\ndef g() -> float:\n    return f(1.5, 2)\n";
    Assert.That(PythonSnippet.Import(source).Contracts, Is.EqualTo(new[] { "f.a: float", "f.return: float", "f.x: int", "g.return: float" }));
    Assert.That(PythonSnippet.Import(source, new PythonImportOptions { RecordAnnotations = false }).Contracts, Is.Empty);
  }

  /// <summary>Test design 16: an annotation contradicting the value is recorded as written; the value decides.</summary>
  [Test]
  public void AnnotationContradictingTheValue_IsRecordedAndTheValueWins() {
    const string source = "def f(a: int, b: int) -> int:\n    x: int = 1.5\n    return a / b + x\n";
    var result = PythonSnippet.Import(source);
    Assert.That(result.Contracts, Is.EqualTo(new[] { "f.a: int", "f.b: int", "f.return: int", "f.x: int" }));
    Assert.That(result.Module!.Functions.Single(function => function.Name == "f").ReturnType, Is.EqualTo(IrType.F64));
    Assert.That(PythonOutcome.Interpreted(result.Module, new PythonCall("f", [7L, 2L])), Is.EqualTo("float 5.0"));
  }

  /// <summary>Scenario: a parameter without a hint has the type the module's own calls give it.</summary>
  [Test]
  public void ParameterWithoutHint_TakesTheTypeOfItsCalls() {
    const string source = "def f(a, b):\n    return a + b\n\ndef g() -> int:\n    return f(2, 3)\n";
    var module = PythonSnippet.Module(source);
    Assert.That(module.Functions.Single(function => function.Name == "f").Parameters.Select(parameter => parameter.Type), Is.EqualTo(new[] { IrType.I64, IrType.I64 }));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("g", [])), Is.EqualTo("int 5"));
    Assert.That(PythonSnippet.Import(source).Contracts, Is.EqualTo(new[] { "g.return: int" }));
  }

  /// <summary>Scenario: an unannotated parameter nothing gives a type is refused; there is no dynamic type yet.</summary>
  [Test]
  public void ParameterWithoutHintAndCall_IsRefused()
    => Assert.That(PythonSnippet.Refusals("def f(a, b): return a + b\n"), Is.EqualTo(new[] {
      "snippet.py(1,1): error PLY1801: the result type of 'f' is not known: it has no return type hint and no return value of a known type",
      "snippet.py(1,7): error PLY1801: the type of 'a' in 'f' is not known: it has no type hint and no call of the module passes it a value",
      "snippet.py(1,10): error PLY1801: the type of 'b' in 'f' is not known: it has no type hint and no call of the module passes it a value"
    }));

  /// <summary>Scenario: an unsupported numeric type is refused.</summary>
  [Test]
  public void ComplexLiteral_IsRefusedWithPLY1802()
    => Assert.That(PythonSnippet.Refusals("def f(a: float) -> complex: return a + 2j\n"), Has.Some.Contains("error PLY1802: '2j' is not supported by the Python frontend").And.Some.StartsWith("snippet.py(1,"));

  /// <summary>Scenario: code python rejects is rejected, with python's message.</summary>
  [Test]
  public void SyntaxError_IsPLY1800CarryingPythonsMessage() {
    var refusals = PythonSnippet.Refusals("def f(:\n");
    Assert.That(refusals, Has.Length.EqualTo(1));
    Assert.That(refusals[0], Does.Match(@"^snippet\.py\(1,\d+\): error PLY1800: SyntaxError: "));
  }

  /// <summary>Test design 1: a 32-bit hash step written with a mask.</summary>
  [Test]
  public void MaskedHashStep_GivesThePythonValue()
    => Assert.That(PythonSnippet.Run(F("h: int, c: int", "int", "((h * 33) ^ c) & 0xFFFFFFFF"), "f", 5381L, 97L), Is.EqualTo("int 177604"));

  /// <summary>Test design 4: the sign table of // and % on int and float.</summary>
  [TestCase(7, 3, "int 2", "int 1", "float 2.0", "float 1.0")]
  [TestCase(-7, 3, "int -3", "int 2", "float -3.0", "float 2.0")]
  [TestCase(7, -3, "int -3", "int -2", "float -3.0", "float -2.0")]
  [TestCase(-7, -3, "int 2", "int -1", "float 2.0", "float -1.0")]
  public void FloorDivisionAndModulo_FollowTheSignOfTheDivisor(long a, long b, string quotient, string remainder, string floatQuotient, string floatRemainder) {
    const string source = "def q(a: int, b: int) -> int: return a // b\ndef r(a: int, b: int) -> int: return a % b\n"
      + "def fq(a: float, b: float) -> float: return a // b\ndef fr(a: float, b: float) -> float: return a % b\n";
    var module = PythonSnippet.Module(source);
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("q", [a, b])), Is.EqualTo(quotient));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("r", [a, b])), Is.EqualTo(remainder));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("fq", [(double)a, (double)b])), Is.EqualTo(floatQuotient));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("fr", [(double)a, (double)b])), Is.EqualTo(floatRemainder));
  }

  /// <summary>Test design 5: true division is correctly rounded, not the quotient of the rounded operands.</summary>
  [TestCase(9007199254740993L, 1L, "float 9007199254740992.0")]
  [TestCase(9007199254740993L, 3L, "float 3002399751580331.0")]
  [TestCase(long.MaxValue, 3L, "float 3.0744573456182584e+18")]
  [TestCase(long.MaxValue, long.MinValue, "float -1.0")]
  [TestCase(1L, long.MaxValue, "float 1.0842021724855044e-19")]
  [TestCase(0L, long.MinValue, "float -0.0")]
  [TestCase(-6L, 4L, "float -1.5")]
  public void TrueDivisionOfIntegers_IsCorrectlyRounded(long a, long b, string expected)
    => Assert.That(PythonSnippet.Run(F("a: int, b: int", "float", "a / b"), "f", a, b), Is.EqualTo(expected));

  /// <summary>Test design 6: shift counts 0, 1, 63, 64, 1000 and -1.</summary>
  [TestCase(0L, "int 1", "int -8")]
  [TestCase(1L, "int 2", "int -4")]
  [TestCase(62L, "int 4611686018427387904", "int -1")]
  [TestCase(63L, "raise OverflowError", "int -1")]
  [TestCase(64L, "raise OverflowError", "int -1")]
  [TestCase(1000L, "raise OverflowError", "int -1")]
  [TestCase(-1L, "raise ValueError", "raise ValueError")]
  public void Shifts_AreExactOrRaise(long count, string left, string right) {
    const string source = "def l(n: int) -> int: return 1 << n\ndef r(n: int) -> int: return -8 >> n\n";
    Assert.That(PythonSnippet.Run(source, "l", count), Is.EqualTo(left));
    Assert.That(PythonSnippet.Run(source, "r", count), Is.EqualTo(right));
  }

  /// <summary>Test design 7: int and float conversions at their edges.</summary>
  [TestCase(double.NaN, "raise ValueError", "raise ValueError")]
  [TestCase(double.PositiveInfinity, "raise OverflowError", "raise OverflowError")]
  [TestCase(double.NegativeInfinity, "raise OverflowError", "raise OverflowError")]
  [TestCase(1e300, "raise OverflowError", "raise OverflowError")]
  [TestCase(-0.0, "int 0", "int 0")]
  [TestCase(-9223372036854775808.0, "int -9223372036854775808", "int -9223372036854775808")]
  [TestCase(9223372036854775808.0, "raise OverflowError", "raise OverflowError")]
  public void FloatToInt_TruncatesOrRaises(double value, string truncated, string rounded) {
    const string source = "def t(a: float) -> int: return int(a)\ndef r(a: float) -> int: return round(a)\n";
    Assert.That(PythonSnippet.Run(source, "t", value), Is.EqualTo(truncated));
    Assert.That(PythonSnippet.Run(source, "r", value), Is.EqualTo(rounded));
  }

  /// <summary>Test design 7: int to float rounds to nearest even.</summary>
  [TestCase(9007199254740992L, "float 9007199254740992.0")]
  [TestCase(9007199254740993L, "float 9007199254740992.0")]
  [TestCase(9007199254740995L, "float 9007199254740996.0")]
  [TestCase(long.MaxValue, "float 9.223372036854776e+18")]
  public void IntToFloat_RoundsToNearestEven(long value, string expected)
    => Assert.That(PythonSnippet.Run(F("a: int", "float", "float(a)"), "f", value), Is.EqualTo(expected));

  /// <summary>Test design 8: round at the halves.</summary>
  [TestCase(0.5, "int 0")]
  [TestCase(1.5, "int 2")]
  [TestCase(2.5, "int 2")]
  [TestCase(-0.5, "int 0")]
  [TestCase(-2.5, "int -2")]
  public void Round_RoundsHalfToEven(double value, string expected)
    => Assert.That(PythonSnippet.Run(F("a: float", "int", "round(a)"), "f", value), Is.EqualTo(expected));

  /// <summary>Test design 9: int and float compare exactly, NaN with nothing.</summary>
  [TestCase(9007199254740993L, 9007199254740992.0, "bool False", "bool False", "bool True")]
  [TestCase(9007199254740991L, 9007199254740992.0, "bool True", "bool False", "bool True")]
  [TestCase(long.MaxValue, 9223372036854775808.0, "bool True", "bool False", "bool True")]
  [TestCase(long.MinValue, -9223372036854775808.0, "bool False", "bool True", "bool False")]
  [TestCase(1L, double.NaN, "bool False", "bool False", "bool True")]
  [TestCase(0L, double.PositiveInfinity, "bool True", "bool False", "bool True")]
  [TestCase(0L, double.NegativeInfinity, "bool False", "bool False", "bool True")]
  [TestCase(2L, 2.5, "bool True", "bool False", "bool True")]
  [TestCase(3L, 2.5, "bool False", "bool False", "bool True")]
  [TestCase(-3L, -2.5, "bool True", "bool False", "bool True")]
  public void IntAndFloat_CompareExactly(long a, double b, string less, string equal, string notEqual) {
    const string source = "def lt(a: int, b: float) -> bool: return a < b\ndef eq(a: int, b: float) -> bool: return a == b\n"
      + "def ne(a: float, b: int) -> bool: return b != a\ndef ge(a: float, b: int) -> bool: return a >= b\n"
      + "def le(a: int, b: float) -> bool: return a <= b\ndef gt(a: int, b: float) -> bool: return a > b\n";
    var module = PythonSnippet.Module(source);
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("lt", [a, b])), Is.EqualTo(less));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("eq", [a, b])), Is.EqualTo(equal));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("ne", [b, a])), Is.EqualTo(notEqual));
    var lessOrEqual = less == "bool True" || equal == "bool True";
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("le", [a, b])), Is.EqualTo(lessOrEqual ? "bool True" : "bool False"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("ge", [b, a])), Is.EqualTo(lessOrEqual ? "bool True" : "bool False"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("gt", [a, b])), Is.EqualTo(!lessOrEqual && !double.IsNaN(b) ? "bool True" : "bool False"));
  }

  /// <summary>Test design 10: the literal forms.</summary>
  [Test]
  public void Literals_HaveTheirPythonValues()
    => Assert.That(PythonSnippet.Run("def f() -> float: return 0xFFFF_FFFF + 0o17 + 0b101 + 1_000 + 1e3 + -9223372036854775808 // 2 ** 62\n", "f"), Is.EqualTo("float 4294969313.0"));

  /// <summary>Test design 10: a 200-digit literal needs more than 64 bits and is refused.</summary>
  [Test]
  public void HugeLiteral_IsRefusedWithPLY1804()
    => Assert.That(PythonSnippet.Refusals("def f() -> int:\n    return " + new string('9', 200) + "\n"), Is.EqualTo(new[] {
      $"snippet.py(2,12): error PLY1804: the integer '{new string('9', 200)}' needs more than 64 bits, which the import does not hold yet"
    }));

  /// <summary>Test design 11: and/or return an operand, a chained comparison evaluates its middle once.</summary>
  [Test]
  public void BooleanOperatorsAndChains_FollowPython() {
    const string source = "def g(x: int) -> int:\n    return x * 2\n\n"
      + "def chain(a: int, b: int, c: int) -> bool:\n    return a < g(b) <= c\n\n"
      + "def pick(a: float, b: float) -> float:\n    return a and b\n\n"
      + "def first(a: int, b: int) -> int:\n    return g(a) or b\n\n"
      + "def cond(c: bool, a: int, b: int) -> int:\n    return a if c else b\n";
    var module = PythonSnippet.Module(source);
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("chain", [1L, 2L, 4L])), Is.EqualTo("bool True"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("chain", [5L, 2L, 4L])), Is.EqualTo("bool False"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("chain", [1L, 3L, 4L])), Is.EqualTo("bool False"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("pick", [0.0, 2.5])), Is.EqualTo("float 0.0"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("pick", [1.0, 2.5])), Is.EqualTo("float 2.5"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("first", [0L, 7L])), Is.EqualTo("int 7"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("first", [3L, 7L])), Is.EqualTo("int 6"));
    Assert.That(PythonOutcome.Interpreted(module, new PythonCall("cond", [true, 1L, 2L])), Is.EqualTo("int 1"));
    // The middle operand of the chain and the left operand of 'or' are calls: each is evaluated once, into a temporary.
    Assert.That(module.Functions.Single(function => function.Name == "chain").Locals.Select(local => local.Name), Does.Contain("value"));
  }

  /// <summary>Test design 12: truth of int, float and bool, a NaN is true.</summary>
  [TestCase("int", 0L, "bool True")]
  [TestCase("int", -1L, "bool False")]
  [TestCase("float", 0.0, "bool True")]
  [TestCase("float", -0.0, "bool True")]
  [TestCase("float", double.NaN, "bool False")]
  [TestCase("bool", false, "bool True")]
  public void Not_TestsTruth(string type, object value, string expected)
    => Assert.That(PythonSnippet.Run(F("a: " + type, "bool", "not a"), "f", value), Is.EqualTo(expected));

  /// <summary>Test design 13: powers of every kind.</summary>
  [TestCase("a: int, b: int", "a ** b", 2L, 10L, "int 1024")]
  [TestCase("a: int, b: int", "a ** b", 2L, 0L, "int 1")]
  [TestCase("a: int, b: int", "a ** b", -2L, 63L, "int -9223372036854775808")]
  [TestCase("a: int, b: int", "a ** b", 2L, 63L, "raise OverflowError")]
  [TestCase("a: float, b: float", "a ** b", 2.0, 0.5, "float 1.4142135623730951")]
  [TestCase("a: float, b: float", "a ** b", -8.0, 0.5, "raise ArithmeticError")]
  [TestCase("a: float, b: float", "a ** b", 0.0, -1.0, "raise ZeroDivisionError")]
  [TestCase("a: float, b: float", "a ** b", 10.0, 400.0, "raise OverflowError")]
  [TestCase("a: float, b: float", "pow(a, b)", -1e300, 1.5, "raise OverflowError")]
  [TestCase("a: int, b: float", "a ** b", 4L, 0.5, "float 2.0")]
  public void Power_IsExactOrFloatAsPythonSays(string parameters, string expression, object a, object b, string expected)
    => Assert.That(PythonSnippet.Run(F(parameters, "float", expression), "f", a, b), Is.EqualTo(expected));

  /// <summary>Test design 13: an integer to a negative constant power is a float; to an int of unknown sign it is an int that raises for a negative power.</summary>
  [Test]
  public void IntegerPowerWithNegativeExponent_IsAFloatOrACaveat() {
    Assert.That(PythonSnippet.Run(F("a: int", "float", "a ** -1"), "f", 2L), Is.EqualTo("float 0.5"));
    Assert.That(PythonSnippet.Run(F("a: int", "float", "a ** -1"), "f", 0L), Is.EqualTo("raise ZeroDivisionError"));
    const string unknown = "def f(a: int, b: int) -> int: return pow(a, b)\n";
    Assert.That(PythonSnippet.Run(unknown, "f", 2L, -1L), Is.EqualTo("raise ValueError"));
    Assert.That(PythonSnippet.Import(unknown).Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.PowerSign));
    Assert.That(PythonFrontend.Describe(PythonFrontend.PowerSign), Does.Contain("ValueError"));
  }

  /// <summary>Test design 14: zero divisors for / // % on int and float, and divmod.</summary>
  [TestCase("a: int, b: int", "a / b", 1L, 0L)]
  [TestCase("a: int, b: int", "a // b", 1L, 0L)]
  [TestCase("a: int, b: int", "a % b", 1L, 0L)]
  [TestCase("a: float, b: float", "a // b", 1.0, 0.0)]
  [TestCase("a: float, b: float", "a % b", 1.0, -0.0)]
  [TestCase("a: int, b: float", "a / b", 1L, 0.0)]
  public void ZeroDivisor_RaisesZeroDivisionError(string parameters, string expression, object a, object b)
    => Assert.That(PythonSnippet.Run(F(parameters, "float", expression), "f", a, b), Is.EqualTo("raise ZeroDivisionError"));

  [Test]
  public void DivMod_RaisesBeforeAnythingIsAssigned() {
    const string source = "def f(a: int, b: int) -> int:\n    q = 5\n    q, r = divmod(a, b)\n    return q * 10 + r\n\n"
      + "def g(a: float, b: float) -> float:\n    q, r = divmod(a + 0.0, b)\n    return q * 10 + r\n";
    Assert.That(PythonSnippet.Run(source, "f", 7L, 0L), Is.EqualTo("raise ZeroDivisionError"));
    Assert.That(PythonSnippet.Run(source, "f", -7L, 2L), Is.EqualTo("int -39"));
    Assert.That(PythonSnippet.Run(source, "g", -7.5, 2.0), Is.EqualTo("float -39.5"));
  }

  /// <summary>Test design 15: a variable that holds an int on one path and a float on another is a float, as a caveat says.</summary>
  [Test]
  public void IntAndFloatInOneVariable_WidenToFloatWithACaveat() {
    const string source = "def f(c: bool) -> float:\n    x = 1\n    if c:\n        x = 1.5\n    return x * 2\n";
    var result = PythonSnippet.Import(source);
    Assert.That(result.Caveats.Select(caveat => caveat.Id), Does.Contain(PythonFrontend.Widened));
    Assert.That(PythonOutcome.Interpreted(result.Module!, new PythonCall("f", [true])), Is.EqualTo("float 3.0"));
    // CPython gives int 2 here; the value is the same, the type the wider one.
    Assert.That(PythonOutcome.Interpreted(result.Module!, new PythonCall("f", [false])), Is.EqualTo("float 2.0"));
  }

  /// <summary>Test design 2: bool operands of the bitwise operators give a bool, mixed with an int an int.</summary>
  [TestCase("a & b", true, true, "bool True")]
  [TestCase("a | b", false, false, "bool False")]
  [TestCase("a ^ b", true, false, "bool True")]
  [TestCase("a & 3", true, false, "int 1")]
  [TestCase("-a", true, false, "int -1")]
  [TestCase("+a", true, false, "int 1")]
  [TestCase("abs(a)", true, false, "int 1")]
  [TestCase("int(a) + round(b)", true, true, "int 2")]
  [TestCase("float(a) + float(b)", true, false, "float 1.0")]
  [TestCase("a < b", false, true, "bool True")]
  [TestCase("a == b", false, true, "bool False")]
  [TestCase("a * 2.5", true, false, "float 2.5")]
  [TestCase("not a or b", true, false, "bool False")]
  public void BoolOperands_ComputeAsPython(string expression, bool a, bool b, string expected)
    => Assert.That(PythonSnippet.Run(F("a: bool, b: bool", "float", expression), "f", a, b), Is.EqualTo(expected));

  /// <summary>Test design 2: the built-ins on int and float.</summary>
  [TestCase("abs(a)", -5L, "int 5")]
  [TestCase("abs(a)", long.MinValue, "raise OverflowError")]
  [TestCase("int(a)", 7L, "int 7")]
  [TestCase("round(a)", 7L, "int 7")]
  [TestCase("bool(a)", 0L, "bool False")]
  [TestCase("-a", long.MinValue, "raise OverflowError")]
  [TestCase("+a", 3L, "int 3")]
  [TestCase("a % -1", long.MinValue, "int 0")]
  [TestCase("a % 5", -7L, "int 3")]
  [TestCase("a >> 70", -7L, "int -1")]
  [TestCase("a >> 70", 7L, "int 0")]
  [TestCase("a << 0", 0L, "int 0")]
  public void BuiltInsOnInt_ComputeAsPython(string expression, long a, string expected)
    => Assert.That(PythonSnippet.Run(F("a: int", "int", expression), "f", a), Is.EqualTo(expected));

  [TestCase("abs(a)", -2.5, "float 2.5")]
  [TestCase("-a", 0.0, "float -0.0")]
  [TestCase("+a", 1.5, "float 1.5")]
  [TestCase("float(a)", 1.5, "float 1.5")]
  [TestCase("a // 0.5", -1.25, "float -3.0")]
  [TestCase("a // float('inf')", -1.0, "float -1.0")]
  [TestCase("1.0 // a", double.PositiveInfinity, "float 0.0")]
  [TestCase("a % -2", 7.5, "float -0.5")]
  [TestCase("a % 2", -0.0, "float 0.0")]
  public void BuiltInsOnFloat_ComputeAsPython(string expression, double a, string expected)
    => Assert.That(PythonSnippet.Run(F("a: float", "float", expression.Replace("float('inf')", "1e400", System.StringComparison.Ordinal)), "f", a), Is.EqualTo(expected));

  /// <summary>Test design 17: the unsupported constructs, each with the diagnostic of its family.</summary>
  [TestCase("a: float", "a + 2j", "error PLY1802: '2j' is not supported by the Python frontend")]
  [TestCase("a: int, b: int", "pow(a, b, 7)", "error PLY1802: 'pow(a, b, 7)' is not supported by the Python frontend")]
  [TestCase("a: int, b: int", "a is b", "error PLY1802: 'is' is not supported by the Python frontend")]
  [TestCase("a: int, b: int", "a @ b", "error PLY1802: '@' is not supported by the Python frontend")]
  [TestCase("a: int", "round(a, 2)", "error PLY1802: 'round(a, 2)' is not supported by the Python frontend")]
  [TestCase("a: int", "divmod(a, 2)", "error PLY1802: 'divmod(a, 2)' is not supported by the Python frontend")]
  [TestCase("a: int", "Decimal(a)", "error PLY1833: 'Decimal' is neither part of this import nor in the Python call map")]
  [TestCase("a: int", "'text'", "error PLY1802: ''text'' is not supported by the Python frontend")]
  [TestCase("a: int", "[a]", "error PLY1802: '[a]' is not supported by the Python frontend")]
  [TestCase("a: int", "(b := a)", "error PLY1802: 'b := a' is not supported by the Python frontend")]
  [TestCase("a: float", "a << 1", "error PLY1802: '<< with a float operand, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("a: float", "a & 1", "error PLY1802: '& with a float operand, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("a: float", "~a", "error PLY1802: '~ with a float operand, which raises TypeError,' is not supported by the Python frontend")]
  [TestCase("a: int", "f", "error PLY1832: the function value 'f' escapes")]
  [TestCase("a: int", "int", "error PLY1832: the function value 'int' escapes")]
  [TestCase("a: int", "(lambda: 1)()", "error PLY1833: 'lambda: 1' is neither part of this import nor in the Python call map")]
  [TestCase("a: int", "f(a=1)", "error PLY1830: 'keyword or unpacked arguments in f(a=1)' is not supported for functions by the Python frontend")]
  [TestCase("a: int", "f(1, 2)", "error PLY1830: 'a call of f with 2 of its 1 arguments' is not supported for functions by the Python frontend")]
  [TestCase("a: int", "a(1)", "error PLY1832: the function value 'a' escapes")]
  public void UnsupportedConstruct_IsRefused(string parameters, string expression, string diagnostic)
    => Assert.That(PythonSnippet.Refusals(F(parameters, "int", expression)), Has.Some.Contains(diagnostic));

  /// <summary>Test design 18: a module that rebinds a built-in's name calls its own function.</summary>
  [Test]
  public void RebindingABuiltIn_IsNotTreatedAsTheBuiltIn() {
    const string source = "def int(x: float) -> float:\n    return x * 2\n\ndef f(a: float) -> float:\n    return int(a)\n";
    Assert.That(PythonSnippet.Run(source, "f", 1.25), Is.EqualTo("float 2.5"));
    Assert.That(PythonSnippet.Refusals("def f(a: float) -> int:\n    round = 3\n    return round(a)\n"), Has.Some.Contains("error PLY1832: the function value 'round' escapes"));
  }

  /// <summary>Test design 2: and/or of mixed types widen; of None and a number they are refused.</summary>
  [Test]
  public void MixedOperandTypes_WidenOrAreRefused() {
    Assert.That(PythonSnippet.Run(F("a: int, b: float", "float", "a or b"), "f", 0L, 2.5), Is.EqualTo("float 2.5"));
    Assert.That(PythonSnippet.Run(F("a: int, b: float", "float", "a if a else b"), "f", 3L, 2.5), Is.EqualTo("float 3.0"));
    const string none = "def g() -> None:\n    pass\n\ndef f(a: int) -> int:\n    return a or g()\n";
    Assert.That(PythonSnippet.Refusals(none), Is.EqualTo(new[] { "snippet.py(5,12): error PLY1803: 'a or g()' gives values of types no type of the import holds both of" }));
    const string used = "def g() -> None:\n    pass\n\ndef f(a: int) -> int:\n    return g() + a\n";
    Assert.That(PythonSnippet.Refusals(used), Is.EqualTo(new[] { "snippet.py(5,12): error PLY1802: 'the value None of g()' is not supported by the Python frontend" }));
  }

}
