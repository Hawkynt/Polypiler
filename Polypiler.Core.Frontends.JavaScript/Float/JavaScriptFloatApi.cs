using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// One function of <c>Math</c> or <c>Number</c>, or the global <c>isNaN</c> or <c>isFinite</c>
/// (ECMA-262 §21.3.2, §21.1.2, §19.2), and the IR it means. Every operand is a double: the frontend
/// converts each argument the way it converts the operand of <c>Math.sqrt</c>.
/// </summary>
/// <param name="Name">The name as the source spells it: <c>Math.sin</c>, <c>Number.isInteger</c>, <c>isNaN</c>.</param>
/// <param name="Arity">The number of arguments, or <see langword="null"/> for a function that takes any number.</param>
/// <param name="Operations">The operations of the IR the call becomes, in the order they appear.</param>
/// <param name="Result">The type of the call: <c>f64</c> for a number, <c>bool</c> for a predicate.</param>
/// <param name="Lower">The IR of a call from its arguments as doubles, or <see langword="null"/> when the call is outside the subset.</param>
public sealed record JavaScriptFloatMember(string Name, int? Arity, IReadOnlyList<IrIntrinsicOp> Operations, IrType Result, System.Func<IReadOnlyList<IrExpr>, IrExpr?> Lower) {

  /// <summary>
  /// Whether the function is a global one, <c>isNaN</c> or <c>isFinite</c>, which applies
  /// <c>ToNumber</c> to its operand: the subset has that only for an operand that is a number already.
  /// </summary>
  public bool Coerces => !this.Name.Contains('.');

  /// <summary>The member as a diagnostic names it.</summary>
  public override string ToString() => this.Name;

}

/// <summary>A value property of <c>Math</c> or <c>Number</c> (ECMA-262 §21.3.1, §21.1.2) and the double it is.</summary>
/// <param name="Name">The name as the source spells it: <c>Math.PI</c>, <c>Number.EPSILON</c>.</param>
/// <param name="Value">The value.</param>
public sealed record JavaScriptFloatConstant(string Name, double Value) {

  /// <summary>The constant as a diagnostic names it.</summary>
  public override string ToString() => this.Name;

}

/// <summary>
/// The table of the floating-point functions and constants of JavaScript's library the frontend
/// translates, read by the frontend and by the tests. A member of <c>Math</c> or <c>Number</c> that
/// is neither here nor one the frontend handles itself (<c>Math.imul</c>, <c>clz32</c>, <c>fround</c>,
/// <c>trunc</c>, <c>floor</c>, <c>ceil</c>, <c>round</c>, <c>abs</c>, <c>sign</c>, <c>sqrt</c>,
/// <c>min</c>, <c>max</c>) is refused by its name, <c>Math.random</c> among them.
/// </summary>
public static class JavaScriptFloatApi {

  /// <summary>The largest integer whose neighbours are both doubles: <c>2^53 - 1</c>.</summary>
  private const double MaxSafeInteger = 9007199254740991;

  /// <summary>Every function, by name.</summary>
  public static IReadOnlyList<JavaScriptFloatMember> Members { get; } = BuildMembers();

  /// <summary>Every constant, by name.</summary>
  public static IReadOnlyList<JavaScriptFloatConstant> Constants { get; } = [
    // Math (§21.3.1): the doubles nearest to the real values, as an engine prints them.
    new("Math.E", 2.718281828459045),
    new("Math.LN10", 2.302585092994046),
    new("Math.LN2", 0.6931471805599453),
    new("Math.LOG10E", 0.4342944819032518),
    new("Math.LOG2E", 1.4426950408889634),
    new("Math.PI", 3.141592653589793),
    new("Math.SQRT1_2", 0.7071067811865476),
    new("Math.SQRT2", 1.4142135623730951),

    // Number (§21.1.2).
    new("Number.EPSILON", 2.220446049250313e-16),
    new("Number.MAX_VALUE", double.MaxValue),
    new("Number.MIN_VALUE", double.Epsilon),
    new("Number.MAX_SAFE_INTEGER", MaxSafeInteger),
    new("Number.MIN_SAFE_INTEGER", -MaxSafeInteger),
    new("Number.NaN", double.NaN),
    new("Number.POSITIVE_INFINITY", double.PositiveInfinity),
    new("Number.NEGATIVE_INFINITY", double.NegativeInfinity)
  ];

  private static readonly Dictionary<string, JavaScriptFloatMember> _members = Members.ToDictionary(member => member.Name);
  private static readonly Dictionary<string, JavaScriptFloatConstant> _constants = Constants.ToDictionary(constant => constant.Name);

  /// <summary>The function of a name, or <see langword="null"/>.</summary>
  /// <param name="name">The name as the source spells it.</param>
  /// <returns>The entry.</returns>
  public static JavaScriptFloatMember? Find(string name) => _members.GetValueOrDefault(name);

  /// <summary>The constant of a name, or <see langword="null"/>.</summary>
  /// <param name="name">The name as the source spells it.</param>
  /// <returns>The entry.</returns>
  public static JavaScriptFloatConstant? FindConstant(string name) => _constants.GetValueOrDefault(name);

  private static List<JavaScriptFloatMember> BuildMembers() {
    var members = new List<JavaScriptFloatMember>();
    void Add(string name, int? arity, IrIntrinsicOp[] operations, IrType result, System.Func<IReadOnlyList<IrExpr>, IrExpr?> lower)
      => members.Add(new JavaScriptFloatMember(name, arity, operations, result, lower));

    // One operation of the IR each, of the same meaning.
    foreach (var (name, op) in new[] {
      ("sin", IrIntrinsicOp.Sin), ("cos", IrIntrinsicOp.Cos), ("tan", IrIntrinsicOp.Tan),
      ("asin", IrIntrinsicOp.Asin), ("acos", IrIntrinsicOp.Acos), ("atan", IrIntrinsicOp.Atan),
      ("sinh", IrIntrinsicOp.Sinh), ("cosh", IrIntrinsicOp.Cosh), ("tanh", IrIntrinsicOp.Tanh),
      ("asinh", IrIntrinsicOp.Asinh), ("acosh", IrIntrinsicOp.Acosh), ("atanh", IrIntrinsicOp.Atanh),
      ("exp", IrIntrinsicOp.Exp), ("expm1", IrIntrinsicOp.ExpM1), ("log", IrIntrinsicOp.Log), ("log2", IrIntrinsicOp.Log2),
      ("log10", IrIntrinsicOp.Log10), ("log1p", IrIntrinsicOp.Log1P), ("cbrt", IrIntrinsicOp.Cbrt)
    })
      Add("Math." + name, 1, [op], IrType.F64, arguments => Number(op, arguments));

    Add("Math.atan2", 2, [IrIntrinsicOp.Atan2], IrType.F64, arguments => Number(IrIntrinsicOp.Atan2, arguments));

    // Math.pow is the ** operator (§21.3.2.26, §6.1.6.1.3), which differs from IEEE 754's pow at a NaN exponent and at ±1 ** ±Infinity.
    Add("Math.pow", 2, [IrIntrinsicOp.PowEcma], IrType.F64, arguments => Number(IrIntrinsicOp.PowEcma, arguments));
    Add("Math.hypot", null, [IrIntrinsicOp.Abs, IrIntrinsicOp.Hypot], IrType.F64, Hypot);

    foreach (var library in new[] { "Number.", "" }) {
      Add(library + "isNaN", 1, [IrIntrinsicOp.IsNaN], IrType.Bool, arguments => Predicate(IrIntrinsicOp.IsNaN, arguments[0]));
      Add(library + "isFinite", 1, [IrIntrinsicOp.IsFinite], IrType.Bool, arguments => Predicate(IrIntrinsicOp.IsFinite, arguments[0]));
    }

    Add("Number.isInteger", 1, [IrIntrinsicOp.IsInteger], IrType.Bool, arguments => Predicate(IrIntrinsicOp.IsInteger, arguments[0]));

    // An integer whose magnitude is at most 2^53 - 1 (§21.1.2.5). The operand is read twice, so it has to be a variable or a constant.
    Add("Number.isSafeInteger", 1, [IrIntrinsicOp.IsInteger, IrIntrinsicOp.Abs], IrType.Bool, arguments => IsReadable(arguments[0])
      ? new IrBinary(IrBinaryOp.LogicalAnd,
        Predicate(IrIntrinsicOp.IsInteger, arguments[0]),
        new IrBinary(IrBinaryOp.LessOrEqual, Number(IrIntrinsicOp.Abs, [arguments[0]]), Double(MaxSafeInteger)))
      : null);
    return members;
  }

  /// <summary>
  /// <c>Math.hypot</c> (§21.3.2.18): +0 without an argument, the magnitude of one, the IR's
  /// <see cref="IrIntrinsicOp.Hypot"/> of two. Of more it is a left fold of <c>Hypot</c>, which rounds
  /// after every step where an engine rounds once: the result can differ from the engine's in the
  /// last place. An infinity still wins over a NaN, as the specification asks, because each step lets it.
  /// </summary>
  private static IrExpr Hypot(IReadOnlyList<IrExpr> arguments) => arguments.Count switch {
    0 => Double(0),
    1 => Number(IrIntrinsicOp.Abs, arguments),
    _ => arguments.Aggregate((first, second) => Number(IrIntrinsicOp.Hypot, [first, second]))
  };

  /// <summary>Whether an operand that a lowering reads more than once can be read twice: a variable or a constant, also converted to a double.</summary>
  private static bool IsReadable(IrExpr operand) => operand is IrLocalRef or IrConst || operand is IrConvert { Operand: IrLocalRef };

  private static IrIntrinsic Number(IrIntrinsicOp op, IReadOnlyList<IrExpr> arguments) => new(op, IrType.F64, arguments);

  private static IrIntrinsic Predicate(IrIntrinsicOp op, IrExpr operand) => new(op, IrType.Bool, [operand]);

  private static IrConst Double(double value) => new(IrType.F64, IrOperators.Bits(IrType.F64, value));

}
