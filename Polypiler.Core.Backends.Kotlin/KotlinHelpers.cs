using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.Kotlin.Syntax;
using Polypiler.Core.IR;
using static Polypiler.Core.Backends.Kotlin.Syntax.Kt;

namespace Polypiler.Core.Backends.Kotlin;

/// <summary>
/// The helpers of the Kotlin backend: the operations of the IR Kotlin has no expression for, each a
/// private function of the file built as a tree, the same for every module. The quotient and the
/// remainder of a signed integer, which trap where the dividend is the smallest value and the divisor
/// -1 (Kotlin wraps there); the checked sum, difference and product of the integers
/// <c>Math.addExact</c> and its siblings do not take - the unsigned ones, and the 8- and 16-bit steps
/// of a checked increment; the checked conversion between integers and from
/// a floating-point number; the <c>u64</c> to <c>f32</c> conversion, which Kotlin rounds twice; and
/// the copy between arrays, which traps before it copies anything. Every trap is the
/// <c>ArithmeticException("integer overflow")</c> or <c>IndexOutOfBoundsException("index out of range")</c>
/// of the trap table. A file carries the helpers it calls and nothing else (O0501).
/// </summary>
public static class KotlinHelpers {

  /// <summary>The source file of the helpers, which the header names as their origin.</summary>
  public const string Folder = "Polypiler.Core.Backends.Kotlin/KotlinHelpers.cs";

  private const string Overflow = "integer overflow";

  private static readonly IrType[] _integers = [IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.I32, IrType.U32, IrType.I64, IrType.U64];
  private static readonly IrType[] _floats = [IrType.F32, IrType.F64];

  /// <summary>The names of every helper, in the order a file declares them.</summary>
  public static IReadOnlyList<string> Names { get; } = [
    .. new[] { IrType.I32, IrType.I64 }.SelectMany(type => new[] { Divide(type), Remainder(type) }),
    .. new[] { IrType.I8, IrType.U8, IrType.I16, IrType.U16, IrType.U32, IrType.U64 }
      .SelectMany(type => new[] { IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked }.Select(op => Checked(op, type))),
    .. _integers.SelectMany(to => _integers.Where(from => NeedsRangeCheck(from, to)).Select(from => Narrowed(from, to))),
    .. _floats.SelectMany(from => _integers.Select(to => Truncated(from, to))),
    UnsignedToSingle,
    Sign(IrType.F32), Sign(IrType.F64), RoundAway(IrType.F32), RoundAway(IrType.F64),
    .. new[] { IrType.Bool, IrType.I8, IrType.I16, IrType.I32, IrType.I64, IrType.F32, IrType.F64 }.Select(Copy)
  ];

  private static readonly IReadOnlySet<string> _known = new HashSet<string>(Names, StringComparer.Ordinal);

  /// <summary>The Kotlin name of a type, for the names of helpers: <c>Int</c>, <c>UByte</c>, …</summary>
  private static string Of(IrType type) => KotlinTypes.Scalar(type)!;

  private static KtNamedType TypeOf(IrType type) => Type(Of(type));

  /// <summary>The helper of the quotient of a signed type, <c>plyDivideInt</c>.</summary>
  public static string Divide(IrType type) => "plyDivide" + Of(type);

  /// <summary>The helper of the remainder of a signed type, <c>plyRemainderInt</c>.</summary>
  public static string Remainder(IrType type) => "plyRemainder" + Of(type);

  /// <summary>The helper of a checked sum, difference or product, <c>plyAddCheckedUInt</c>.</summary>
  public static string Checked(IrBinaryOp op, IrType type) => "ply" + op switch {
    IrBinaryOp.AddChecked => "Add",
    IrBinaryOp.SubtractChecked => "Subtract",
    IrBinaryOp.MultiplyChecked => "Multiply",
    _ => throw new ArgumentException($"'{op}' is no checked operation.", nameof(op))
  } + "Checked" + Of(type);

  /// <summary>The helper of a checked conversion between integers, <c>plyToByteCheckedFromInt</c>.</summary>
  public static string Narrowed(IrType from, IrType to) => $"plyTo{Of(to)}CheckedFrom{Of(from)}";

  /// <summary>The helper of a trapping conversion of a floating-point number to an integer, <c>plyToIntCheckedFromDouble</c>.</summary>
  public static string Truncated(IrType from, IrType to) => $"plyTo{Of(to)}CheckedFrom{Of(from)}";

  /// <summary>The helper of the conversion from <c>u64</c> to <c>f32</c>, rounded once.</summary>
  public const string UnsignedToSingle = "plyULongToFloat";

  /// <summary>The helper of the sign of a floating-point number, which traps for a NaN, <c>plySignDouble</c>.</summary>
  public static string Sign(IrType type) => "plySign" + Of(type);

  /// <summary>The helper of the rounding of a floating-point number to the nearest integral one, a tie away from zero, <c>plyRoundAwayDouble</c>.</summary>
  public static string RoundAway(IrType type) => "plyRoundAway" + Of(type);

  /// <summary>The helper of the copy between two arrays of an element type, <c>plyCopyInt</c>.</summary>
  public static string Copy(IrType element) => "plyCopy" + Of(KotlinTypes.Carrier(element));

  /// <summary>Whether a conversion between integers can leave the target type, so that its trapping form needs a check.</summary>
  public static bool NeedsRangeCheck(IrType from, IrType to)
    => from != to && (IrOperators.Minimum(from) < IrOperators.Minimum(to) || IrOperators.Maximum(from) > IrOperators.Maximum(to));

  /// <summary>The declaration of a helper.</summary>
  /// <param name="name">One of <see cref="Names"/>.</param>
  /// <returns>The private function.</returns>
  /// <exception cref="ArgumentException">There is no helper of that name.</exception>
  public static KtFunction Declaration(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (!_known.Contains(name))
      throw new ArgumentException($"There is no Kotlin helper '{name}'.", nameof(name));

    foreach (var type in new[] { IrType.I32, IrType.I64 }) {
      if (name == Divide(type))
        return Division(name, type, "/");

      if (name == Remainder(type))
        return Division(name, type, "%");
    }

    foreach (var type in _integers) {
      foreach (var op in new[] { IrBinaryOp.AddChecked, IrBinaryOp.SubtractChecked, IrBinaryOp.MultiplyChecked })
        if (name == Checked(op, type))
          return CheckedArithmetic(name, op, type);

      foreach (var from in _integers)
        if (name == Narrowed(from, to: type))
          return Range(name, from, type);

      foreach (var from in _floats)
        if (name == Truncated(from, type))
          return Truncation(name, from, type);
    }

    if (name == UnsignedToSingle)
      return Helper(name, [Parameter("a", TypeOf(IrType.U64))], TypeOf(IrType.F32),
        Return(new KtIfExpression(Binary(Method(Name("a"), "toLong"), ">=", Literal("0L")), Method(Method(Name("a"), "toLong"), "toFloat"),
          Binary(Method(Method(Binary(Binary(Name("a"), "shr", Int(1)), "or", Binary(Name("a"), "and", Literal("1uL"))), "toLong"), "toFloat"), "*", Literal("2.0f")))));

    foreach (var type in _floats)
      if (name == Sign(type))
        return Helper(name, [Parameter("a", TypeOf(type))], Type("Int"),
          new KtIf(Method(Name("a"), "isNaN"), Block(Throw("ArithmeticException", "Function does not accept floating point Not-a-Number values.")), null),
          Return(new KtIfExpression(Binary(Name("a"), ">", Literal(type == IrType.F32 ? "0.0f" : "0.0")), Int(1),
            new KtIfExpression(Binary(Name("a"), "<", Literal(type == IrType.F32 ? "0.0f" : "0.0")), Negate(Int(1)), Int(0)))));

    foreach (var type in _floats)
      if (name == RoundAway(type))
        return Rounding(name, type);

    var element = new[] { IrType.Bool, IrType.I8, IrType.I16, IrType.I32, IrType.I64, IrType.F32, IrType.F64 }.First(candidate => name == Copy(candidate));
    return ArrayCopy(name, element);
  }

  /// <summary>The smallest value of a type by its name, <c>Int.MIN_VALUE</c>.</summary>
  private static KtExpression Smallest(IrType type) => Member(Of(type), "MIN_VALUE");

  private static KtThrow Overflowed() => Throw("ArithmeticException", Overflow);

  /// <summary>
  /// <c>if (b == -1 &amp;&amp; a == T.MIN_VALUE) throw …; return a / b</c>: Kotlin's own operator throws on
  /// a zero divisor and wraps the one quotient that does not fit, which the IR traps.
  /// </summary>
  private static KtFunction Division(string name, IrType type, string op)
    => Helper(name, [Parameter("a", TypeOf(type)), Parameter("b", TypeOf(type))], TypeOf(type),
      new KtIf(Binary(Binary(Name("b"), "==", Negate(Literal("1" + KotlinTypes.Suffix(type)))), "&&", Binary(Name("a"), "==", Smallest(type))), Block(Overflowed()), null),
      Return(Binary(Name("a"), op, Name("b"))));

  /// <summary>A checked sum, difference or product of a type <c>Math.addExact</c> and its siblings do not take.</summary>
  private static KtFunction CheckedArithmetic(string name, IrBinaryOp op, IrType type) {
    var symbol = op switch {
      IrBinaryOp.AddChecked => "+",
      IrBinaryOp.SubtractChecked => "-",
      _ => "*"
    };
    var (a, b) = (Name("a"), Name("b"));
    var parameters = new[] { Parameter("a", TypeOf(type)), Parameter("b", TypeOf(type)) };
    if (KotlinTypes.IsNarrow(type)) {
      // Kotlin computes in Int or UInt, which holds every exact result of two values of 8 or 16 bits.
      var low = (long)IrOperators.Minimum(type);
      var high = (long)IrOperators.Maximum(type);
      var suffix = type.IsSigned ? "" : "u";
      KtExpression tooLow = type.IsSigned ? Binary(Name("r"), "<", Negate(Int(-low))) : Binary(a, "<", b);
      KtExpression tooHigh = Binary(Name("r"), ">", Literal(high.ToString(CultureInfo.InvariantCulture) + suffix));
      KtExpression test = type.IsSigned ? Binary(tooLow, "||", tooHigh) : op == IrBinaryOp.SubtractChecked ? tooLow : tooHigh;
      return Helper(name, parameters, TypeOf(type),
        new KtVariable(false, "r", null, Binary(a, symbol, b)),
        new KtIf(test, Block(Overflowed()), null),
        Return(Method(Name("r"), KotlinTypes.To(type))));
    }

    if (type == IrType.U32 && op == IrBinaryOp.MultiplyChecked)
      return Helper(name, parameters, TypeOf(type),
        new KtVariable(false, "r", null, Binary(Method(a, "toULong"), "*", Method(b, "toULong"))),
        new KtIf(Binary(Name("r"), ">", Literal("4294967295uL")), Block(Overflowed()), null),
        Return(Method(Name("r"), "toUInt")));

    if (op == IrBinaryOp.MultiplyChecked)
      return Helper(name, parameters, TypeOf(type),
        new KtIf(Binary(Binary(a, "!=", Literal("0uL")), "&&", Binary(b, ">", Binary(Member("ULong", "MAX_VALUE"), "/", a))), Block(Overflowed()), null),
        Return(Binary(a, "*", b)));

    // A sum of unsigned numbers wraps exactly when it is below an operand, a difference when the subtrahend is larger.
    return op == IrBinaryOp.AddChecked
      ? Helper(name, parameters, TypeOf(type),
        new KtVariable(false, "r", null, Binary(a, "+", b)),
        new KtIf(Binary(Name("r"), "<", a), Block(Overflowed()), null),
        Return(Name("r")))
      : Helper(name, parameters, TypeOf(type),
        new KtIf(Binary(a, "<", b), Block(Overflowed()), null),
        Return(Binary(a, "-", b)));
  }

  /// <summary>A literal of a value of a type of 32 or 64 bits, the smallest value by its name.</summary>
  private static KtExpression Bound(IrType type, Int128 value) {
    if (value == IrOperators.Minimum(type) && type.IsSigned)
      return Smallest(type);

    var text = (value < 0 ? -value : value).ToString(CultureInfo.InvariantCulture) + KotlinTypes.Suffix(type);
    return value < 0 ? Negate(Literal(text)) : Literal(text);
  }

  /// <summary>
  /// The checked conversion between integers: the value compared with the bounds of the target as far
  /// as the source reaches them, in the source type (widened to <c>Int</c> or <c>UInt</c> for 8 and 16
  /// bits), then converted, which changes nothing the check let through.
  /// </summary>
  private static KtFunction Range(string name, IrType from, IrType to) {
    var work = KotlinTypes.Promoted(from);
    KtExpression value = KotlinTypes.IsNarrow(from) ? Method(Name("a"), KotlinTypes.To(work)) : Name("a");
    var tests = new List<KtExpression>();
    if (IrOperators.Minimum(to) > IrOperators.Minimum(from))
      tests.Add(Binary(value, "<", Bound(work, IrOperators.Minimum(to))));

    if (IrOperators.Maximum(to) < IrOperators.Maximum(from))
      tests.Add(Binary(value, ">", Bound(work, IrOperators.Maximum(to))));

    return Helper(name, [Parameter("a", TypeOf(from))], TypeOf(to),
      new KtIf(tests.Aggregate((left, right) => Binary(left, "||", right)), Block(Overflowed()), null),
      Return(Method(Name("a"), KotlinTypes.To(to))));
  }

  /// <summary>
  /// The trapping conversion of a floating-point number to an integer: the truncated value must lie in
  /// the type, which holds exactly when the number lies strictly between the smallest value less one
  /// and the largest plus one (inclusive of the smallest at 64 bits, where less one is no double); a
  /// NaN fails every comparison. Then Kotlin's saturating conversion, which changes nothing the check
  /// let through.
  /// </summary>
  private static KtFunction Truncation(string name, IrType from, IrType to) {
    KtExpression value = from == IrType.F32 ? Method(Name("a"), "toDouble") : Name("a");
    var low = (double)IrOperators.Minimum(to);
    var high = (double)(IrOperators.Maximum(to) + 1);
    var lower = to.Bits == 64 && to.IsSigned
      ? Binary(value, ">=", Negate(Literal(Number(-low))))
      : Binary(value, ">", low - 1 < 0 ? Negate(Literal(Number(1 - low))) : Literal(Number(low - 1)));
    KtExpression converted = to.Bits >= 32 ? Method(Name("a"), KotlinTypes.To(to)) : Method(Method(Name("a"), "toInt"), KotlinTypes.To(to));
    return Helper(name, [Parameter("a", TypeOf(from))], TypeOf(to),
      new KtIf(Not(Binary(lower, "&&", Binary(value, "<", Literal(Number(high))))), Block(Overflowed()), null),
      Return(converted));
  }

  /// <summary>A double as a Kotlin literal: the shortest digits that read back as the same number, with a point or an exponent.</summary>
  public static string Number(double value) {
    var text = value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal);
    return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
  }

  /// <summary>A single as a Kotlin literal, with its <c>f</c>.</summary>
  public static string Number(float value) {
    var text = value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal);
    return (text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0") + "f";
  }

  /// <summary>
  /// The rounding to the nearest integral number with a tie away from zero: the number without its
  /// fraction, one further from zero where the fraction - which the subtraction gives exactly - is at
  /// least a half; a NaN, an infinity and every number from 2^52 on are integral already. A binary32
  /// number is rounded in binary64, which holds every step exactly.
  /// </summary>
  private static KtFunction Rounding(string name, IrType type) {
    KtExpression value = type == IrType.F32 ? Method(Name("a"), "toDouble") : Name("a");
    var truncated = Call(Member(Member(Name("kotlin"), "math"), "truncate"), Name("x"));
    KtExpression result = new KtIfExpression(Binary(Method(Name("Math"), "abs", Binary(Name("x"), "-", Name("t"))), ">=", Literal("0.5")),
      Binary(Name("t"), "+", Method(Name("Math"), "signum", Name("x"))), Name("t"));
    return Helper(name, [Parameter("a", TypeOf(type))], TypeOf(type),
      new KtVariable(false, "x", null, value),
      new KtVariable(false, "t", null, truncated),
      Return(type == IrType.F32 ? Method(result, "toFloat") : result));
  }

  /// <summary>The copy between two arrays, checked as a whole before anything is copied; <c>copyInto</c> copies as if through a buffer.</summary>
  private static KtFunction ArrayCopy(string name, IrType element) {
    var array = Type(KotlinTypes.PrimitiveArray(element)!);
    var (source, sourceIndex, target, targetIndex, length) = (Name("source"), Name("sourceIndex"), Name("target"), Name("targetIndex"), Name("length"));
    KtExpression test = Binary(length, "<", Int(0));
    foreach (var outside in new KtExpression[] {
      Binary(sourceIndex, "<", Int(0)), Binary(targetIndex, "<", Int(0)),
      Binary(sourceIndex, ">", Binary(Member(source, "size"), "-", length)), Binary(targetIndex, ">", Binary(Member(target, "size"), "-", length))
    })
      test = Binary(test, "||", outside);

    return Helper(name, [Parameter("source", array), Parameter("sourceIndex", Type("Int")), Parameter("target", array), Parameter("targetIndex", Type("Int")), Parameter("length", Type("Int"))], null,
      new KtIf(test, Block(Throw("IndexOutOfBoundsException", "index out of range")), null),
      new KtExpressionStatement(Method(source, "copyInto", target, targetIndex, sourceIndex, Binary(sourceIndex, "+", length))));
  }

}
