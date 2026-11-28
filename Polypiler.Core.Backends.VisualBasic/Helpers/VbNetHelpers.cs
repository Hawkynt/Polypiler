using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

/// <summary>
/// The helpers of the Visual Basic .NET backend: the operations Visual Basic has no expression for
/// under <c>RemoveIntegerChecks</c> - the checked arithmetic and conversions, which throw an
/// <c>OverflowException</c> as the IR traps, the conversions of floating point that saturate, a step
/// or an assignment as a value, a throw where a value stands - each written in Visual Basic itself and
/// carried by a module only when it calls it. A key names a helper and, after a colon, the type of an
/// overload: <c>PlyAddChk:Long</c>.
/// </summary>
public static class VbNetHelpers {

  /// <summary><c>PlyDiscard(value)</c>: evaluates an expression whose value is dropped.</summary>
  public const string Discard = "PlyDiscard";

  /// <summary><c>PlyBind(variable, value)</c>: an assignment as a value.</summary>
  public const string Bind = "PlyBind";

  /// <summary><c>PlyThrow(Of T)(exception)</c>: a throw where a value of <c>T</c> stands.</summary>
  public const string Throw = "PlyThrow";

  /// <summary><c>PlyChr(code)</c>: the string of one character that has no literal.</summary>
  public const string Chr = "PlyChr";

  /// <summary><c>PlyRoundHalfUp(x)</c>: the nearest integral number, a tie towards positive infinity, a result of zero with the sign of the argument.</summary>
  public const string RoundHalfUp = "PlyRoundHalfUp";

  /// <summary><c>PlyMin(a, b)</c>: the minimum of the IR, a NaN if either is one and -0 before +0, where the framework's <c>Math.Min</c> does not order the zeros.</summary>
  public const string Min = "PlyMin";

  /// <summary><c>PlyMax(a, b)</c>: the maximum of the IR, a NaN if either is one and +0 after -0.</summary>
  public const string Max = "PlyMax";

  /// <summary><c>PlyClear(array)</c>: every element zero.</summary>
  public const string Clear = "PlyClear";

  /// <summary><c>PlyFill(array, value)</c>: every element the value.</summary>
  public const string Fill = "PlyFill";

  /// <summary>The integer types the checked helpers are written for.</summary>
  public static IReadOnlyList<string> Integers { get; } = ["SByte", "Byte", "Short", "UShort", "Integer", "UInteger", "Long", "ULong"];

  /// <summary>The name of the helper a key names.</summary>
  /// <param name="key">The key.</param>
  /// <returns>The part before the colon.</returns>
  public static string NameOf(string key) {
    ArgumentNullException.ThrowIfNull(key);
    return key.Contains(':', StringComparison.Ordinal) ? key[..key.IndexOf(':')] : key;
  }

  /// <summary>The key of <c>PlyChk&lt;T&gt;(x As Decimal)</c>: the value as a <c>T</c>, an <c>OverflowException</c> outside it.</summary>
  public static string Check(string type) => "PlyChk" + type;

  /// <summary>The key of <c>PlyFix&lt;T&gt;(x As Double)</c>: the number truncated, an <c>OverflowException</c> for a NaN or outside <c>T</c>.</summary>
  public static string Fix(string type) => "PlyFix" + type;

  /// <summary>The key of <c>PlySat&lt;T&gt;(x As Double)</c>: the number truncated and saturated to <c>T</c>, 0 for a NaN.</summary>
  public static string Saturate(string type) => "PlySat" + type;

  /// <summary>The key of the checked sum, difference or product of a type.</summary>
  public static string Checked(IrBinaryOp op, string type) => op switch {
    IrBinaryOp.AddChecked => "PlyAddChk:",
    IrBinaryOp.SubtractChecked => "PlySubChk:",
    _ => "PlyMulChk:"
  } + type;

  /// <summary>The key of the checked negation of a type.</summary>
  public static string NegateChecked(string type) => "PlyNegChk:" + type;

  /// <summary>The key of a step by one of a variable of a type, which wraps.</summary>
  public static string Step(string type) => "PlyStep:" + type;

  /// <summary>The key of a step by one of a variable of an integer type, which wraps with integer checks on.</summary>
  public static string StepWrapped(string type) => "PlyStepWrap:" + type;

  /// <summary><c>PlyBits(x As Long) As ULong</c>: the two's complement bits of a number, which a conversion with integer checks on cannot give.</summary>
  public const string Bits = "PlyBits";

  /// <summary><c>PlyAddBits(x, y As ULong)</c>: the sum modulo 2^64, in halves that cannot overflow.</summary>
  public const string AddBits = "PlyAddBits";

  /// <summary><c>PlyMulBits(x, y As ULong)</c>: the product modulo 2^64, in halves that cannot overflow.</summary>
  public const string MulBits = "PlyMulBits";

  /// <summary>The key of <c>PlyWrap&lt;T&gt;(x As ULong)</c>: the low bits of x as a <c>T</c>, a signed one in two's complement.</summary>
  public static string Wrap(string type) => "PlyWrap" + type;

  /// <summary>The key of the wrapping sum, difference or product of a type of 32 or 64 bits, with integer checks on.</summary>
  public static string Wrapping(IrBinaryOp op, string type) => op switch {
    IrBinaryOp.Add => "PlyAddWrap:",
    IrBinaryOp.Subtract => "PlySubWrap:",
    _ => "PlyMulWrap:"
  } + type;

  /// <summary>The key of the wrapping negation of <c>Integer</c> or <c>Long</c>, with integer checks on.</summary>
  public static string NegateWrapping(string type) => "PlyNegWrap:" + type;

  /// <summary>The key of the logical right shift of <c>Integer</c> or <c>Long</c>, with integer checks on.</summary>
  public static string ShiftRightUnsigned(string type) => "PlyShrU:" + type;

  /// <summary>The key of a step by one of a variable of a type, which throws an <c>OverflowException</c> outside it.</summary>
  public static string StepChecked(string type) => "PlyStepChk:" + type;

  /// <summary>The key of <c>PlyIndex(x)</c> of an index of a type wider than <c>Integer</c> or unsigned: the index, or -1 outside <c>Integer</c>.</summary>
  public static string Index(string type) => "PlyIndex:" + type;

  /// <summary>The key of the field-wise equality of a structure.</summary>
  public static string EqualsOf(string structure) => "PlyEquals:" + structure;

  private static VnName N(string name) => new(name);

  private static VnLiteral L(string text) => new(text);

  private static VnInvoke Call(string function, params VnExpression[] arguments) => new(N(function), arguments);

  private static VnBinary B(VnExpression left, string op, VnExpression right) => new(left, op, right);

  private static VnThrow Overflow() => new(new VnNew("System.OverflowException", []));

  private static VnMethod Function(string name, string returns, VnParameter[] parameters, params VnStatement[] body) => new("Private", name, parameters, returns, body);

  /// <summary>The bounds of an integer type as Visual Basic literals of <c>Decimal</c> and <c>Double</c>.</summary>
  private static (string Low, string High) Bounds(string type) => type switch {
    "SByte" => ("-128", "127"),
    "Byte" => ("0", "255"),
    "Short" => ("-32768", "32767"),
    "UShort" => ("0", "65535"),
    "Integer" => ("-2147483648", "2147483647"),
    "UInteger" => ("0", "4294967295"),
    "Long" => ("-9223372036854775808", "9223372036854775807"),
    _ => ("0", "18446744073709551615")
  };

  private static bool IsWide(string type) => type is "Integer" or "UInteger" or "Long" or "ULong";

  /// <summary>The conversion function of an integer type, the only types a helper converts to.</summary>
  private static string Conversion(string type) => type switch {
    "SByte" => "CSByte",
    "Byte" => "CByte",
    "Short" => "CShort",
    "UShort" => "CUShort",
    "Integer" => "CInt",
    "UInteger" => "CUInt",
    "Long" => "CLng",
    _ => "CULng"
  };

  /// <summary>The literal one of a type a step adds it to: an integer of 32 or 64 bits or a floating-point number.</summary>
  private static VnExpression One(string type) => type switch {
    "Integer" => L("1"),
    "UInteger" => L("1UI"),
    "Long" => L("1L"),
    "ULong" => L("1UL"),
    "Single" => L("1.0F"),
    _ => L("1.0")
  };

  /// <summary>The helpers a helper calls, by key.</summary>
  /// <param name="key">The key.</param>
  /// <returns>The keys of the helpers it needs.</returns>
  public static IEnumerable<string> Needs(string key) {
    var (name, type) = Split(key);
    return name switch {
      "PlyAddChk" or "PlySubChk" or "PlyMulChk" or "PlyNegChk" => [Check(type)],
      "PlyStepChk" => IsWide(type) ? [Checked(IrBinaryOp.AddChecked, type), Checked(IrBinaryOp.SubtractChecked, type)] : [Check(type)],
      "PlyStepWrap" => IsWide(type) ? [Wrapping(IrBinaryOp.Add, type), Wrapping(IrBinaryOp.Subtract, type)] : [Bits, Wrap(type)],
      "PlyAddWrap" or "PlySubWrap" or "PlyMulWrap" or "PlyNegWrap" => type switch {
        "Integer" => [Bits, Wrap(type)],
        "UInteger" => name == "PlySubWrap" ? [Bits, Wrap(type)] : [Wrap(type)],
        "Long" => [Bits, name == "PlyMulWrap" ? MulBits : AddBits, Wrap(type)],
        _ => [name == "PlyMulWrap" ? MulBits : AddBits]
      },
      "PlyShrU" => [Bits, Wrap(type)],
      MulBits => [AddBits],
      _ => []
    };
  }

  private static (string Name, string Type) Split(string key)
    => key.Contains(':', StringComparison.Ordinal) ? (key[..key.IndexOf(':')], key[(key.IndexOf(':') + 1)..]) : (key, "");

  /// <summary>The declarations of a helper, overloads of one name together.</summary>
  /// <param name="key">The key; a structure's equality is built by the writer.</param>
  /// <returns>The declarations.</returns>
  /// <exception cref="ArgumentException">The key names no helper.</exception>
  public static IReadOnlyList<VnMethod> Declarations(string key) {
    ArgumentNullException.ThrowIfNull(key);
    var (name, type) = Split(key);
    VnParameter P(string parameter, string parameterType, bool byRef = false) => new(parameter, parameterType, byRef);
    foreach (var target in Integers) {
      var (low, high) = Bounds(target);
      var wide64 = target is "Long" or "ULong";
      if (key == Wrap(target))
        return [WrapDeclaration(target)];
      if (key == Check(target))
        return [Function(key, target, [P("x", "Decimal")],
          new VnIf(B(B(N("x"), "<", L(low + "D")), "OrElse", B(N("x"), ">", L(high + "D"))), [Overflow()], null),
          new VnReturn(Call(Conversion(target), N("x"))))];
      if (key == Fix(target))
        return [Function(key, target, [P("x", "Double")],
          new VnDim("t", "Double", Call("System.Math.Truncate", N("x"))),
          new VnIf(B(B(Call("Double.IsNaN", N("t")), "OrElse", B(N("t"), "<", L(low + ".0"))), "OrElse",
            wide64 ? B(N("t"), ">=", L(target == "Long" ? "9223372036854775808.0" : "18446744073709551616.0")) : B(N("t"), ">", L(high + ".0"))), [Overflow()], null),
          new VnReturn(Call(Conversion(target), N("t"))))];
      if (key == Saturate(target))
        return [Function(key, target, [P("x", "Double")],
          new VnIf(Call("Double.IsNaN", N("x")), [new VnReturn(L("0"))], null),
          new VnIf(B(N("x"), "<=", L(low + ".0")), [new VnReturn(N(target + ".MinValue"))], null),
          new VnIf(B(N("x"), ">=", L(wide64 ? (target == "Long" ? "9223372036854775808.0" : "18446744073709551616.0") : high + ".0")), [new VnReturn(N(target + ".MaxValue"))], null),
          new VnReturn(Call(Conversion(target), Call("System.Math.Truncate", N("x")))))];
    }

    switch (name) {
      case "PlyAddChk" or "PlySubChk" or "PlyMulChk":
        var op = name == "PlyAddChk" ? "+" : name == "PlySubChk" ? "-" : "*";
        return [Function(name, type, [P("a", type), P("b", type)], new VnReturn(Call(Check(type), B(Call("CDec", N("a")), op, Call("CDec", N("b"))))))];
      case "PlyAddWrap" or "PlySubWrap" or "PlyMulWrap" or "PlyNegWrap":
        return [WrappingDeclaration(name, type)];
      case "PlyShrU":
        // The count masked to the width, as the shift of the type masks it.
        var width = type == "Integer" ? "31" : "63";
        VnExpression bits = type == "Integer" ? B(Call(Bits, Call("CLng", N("x"))), "And", L("&HFFFFFFFFUL")) : Call(Bits, N("x"));
        return [Function(name, type, [P("x", type), P("n", "Integer")], new VnReturn(Call(Wrap(type), B(bits, ">>", B(N("n"), "And", L(width))))))];
      case Bits:
        return [Function(name, "ULong", [P("x", "Long")],
          new VnIf(B(N("x"), "<", L("0L")), [new VnReturn(B(Call("CULng", B(B(N("x"), "+", N("Long.MaxValue")), "+", L("1L"))), "+", L("9223372036854775808UL")))], null),
          new VnReturn(Call("CULng", N("x"))))];
      case AddBits:
        return [Function(name, "ULong", [P("x", "ULong"), P("y", "ULong")],
          new VnDim("lo", "ULong", B(B(N("x"), "And", L("&HFFFFFFFFUL")), "+", B(N("y"), "And", L("&HFFFFFFFFUL")))),
          new VnDim("hi", "ULong", B(B(B(B(N("x"), ">>", L("32")), "+", B(N("y"), ">>", L("32"))), "+", B(N("lo"), ">>", L("32"))), "And", L("&HFFFFFFFFUL"))),
          new VnReturn(B(B(N("hi"), "<<", L("32")), "Or", B(N("lo"), "And", L("&HFFFFFFFFUL")))))];
      case MulBits:
        return [Function(name, "ULong", [P("x", "ULong"), P("y", "ULong")],
          new VnDim("x0", "ULong", B(N("x"), "And", L("&HFFFFFFFFUL"))),
          new VnDim("y0", "ULong", B(N("y"), "And", L("&HFFFFFFFFUL"))),
          new VnDim("cross", "ULong", B(B(B(N("x0"), "*", B(N("y"), ">>", L("32"))), "And", L("&HFFFFFFFFUL")), "+", B(B(B(N("x"), ">>", L("32")), "*", N("y0")), "And", L("&HFFFFFFFFUL")))),
          new VnReturn(Call(AddBits, B(N("x0"), "*", N("y0")), B(B(N("cross"), "And", L("&HFFFFFFFFUL")), "<<", L("32")))))];
      case "PlyNegChk":
        return [Function(name, type, [P("a", type)], new VnReturn(Call(Check(type), new VnUnary("-", Call("CDec", N("a"))))))];
      case "PlyStep" or "PlyStepChk" or "PlyStepWrap":
        VnExpression Next(string sign) => name switch {
          "PlyStepChk" => IsWide(type) ? Call(sign == "+" ? "PlyAddChk" : "PlySubChk", N("old"), One(type)) : Call(Check(type), B(Call("CDec", N("old")), sign, L("1D"))),
          "PlyStepWrap" => IsWide(type) ? Call(sign == "+" ? "PlyAddWrap" : "PlySubWrap", N("old"), One(type)) : Call(Wrap(type), Call(Bits, Call("CLng", B(Call("CInt", N("old")), sign, L("1"))))),
          _ => IsWide(type) || type is "Single" or "Double" ? B(N("old"), sign, One(type)) : Call(Conversion(type), B(Call("CInt", N("old")), sign, L("1")))
        };
        return [Function(name, type, [P("x", type, true), P("up", "Boolean"), P("post", "Boolean")],
          new VnDim("old", type, N("x")),
          new VnIf(N("up"), [new VnAssign(N("x"), "=", Next("+"))], [new VnAssign(N("x"), "=", Next("-"))]),
          new VnReturn(new VnTernary(N("post"), N("old"), N("x"))))];
      case "PlyIndex":
        // A value outside Integer becomes -1, which is outside every array.
        VnExpression outside = B(N("x"), ">", L(type == "UInteger" ? "2147483647UI" : type == "ULong" ? "2147483647UL" : "2147483647L"));
        if (type == "Long")
          outside = B(outside, "OrElse", B(N("x"), "<", L("-2147483648L")));
        return [Function(name, "Integer", [P("x", type)], new VnIf(outside, [new VnReturn(L("-1"))], null), new VnReturn(Call("CInt", N("x"))))];
      case Discard:
        return [new VnMethod("Private", name, [P("value", "T")], null, []) { TypeParameter = "T" }];
      case Bind:
        return [new VnMethod("Private", name, [P("x", "T", true), P("value", "T")], "T", [new VnAssign(N("x"), "=", N("value")), new VnReturn(N("value"))]) { TypeParameter = "T" }];
      case Throw:
        return [new VnMethod("Private", name, [P("e", "System.Exception")], "T", [new VnThrow(N("e"))]) { TypeParameter = "T" }];
      case Chr:
        return [Function(name, "String", [P("code", "Integer")], new VnReturn(new VnNew("System.String", [Call("System.Convert.ToChar", N("code")), L("1")])))];
      case RoundHalfUp:
        return [Function(name, "Double", [P("x", "Double")],
          new VnDim("r", "Double", Call("System.Math.Floor", N("x"))),
          new VnIf(B(B(N("x"), "-", N("r")), ">=", L("0.5")), [new VnAssign(N("r"), "+=", L("1.0"))], null),
          new VnIf(B(B(N("r"), "=", L("0.0")), "AndAlso", B(Call("System.BitConverter.DoubleToInt64Bits", N("x")), "<", L("0L"))), [new VnReturn(new VnUnary("-", N("r")))], null),
          new VnReturn(N("r")))];
      case Min or Max:
        // Equal numbers are both zeros or the same number: the minimum takes the negative one, the maximum the other.
        VnMethod Of(string floating) => Function(name, floating, [P("a", floating), P("b", floating)],
          new VnIf(B(Call(floating + ".IsNaN", N("a")), "OrElse", Call(floating + ".IsNaN", N("b"))), [new VnReturn(N(floating + ".NaN"))], null),
          new VnIf(B(N("a"), name == Min ? "<" : ">", N("b")), [new VnReturn(N("a"))], null),
          new VnIf(B(N("b"), name == Min ? "<" : ">", N("a")), [new VnReturn(N("b"))], null),
          new VnReturn(new VnTernary(B(Call("System.BitConverter.DoubleToInt64Bits", N("a")), "<", L("0L")), N(name == Min ? "a" : "b"), N(name == Min ? "b" : "a"))));
        return [Of("Double"), Of("Single")];
      case Clear:
        return [new VnMethod("Private", name, [P("a", "T()")], null, [new VnCallStatement(Call("System.Array.Clear", N("a"), L("0"), new VnMember(N("a"), "Length")))]) { TypeParameter = "T" }];
      case Fill:
        return [new VnMethod("Private", name, [P("a", "T()"), P("value", "T")], null, [
          new VnDim("i", "Integer", L("0")),
          new VnDo(B(N("i"), "<", new VnMember(N("a"), "Length")), false, [new VnAssign(new VnInvoke(N("a"), [N("i")]), "=", N("value")), new VnAssign(N("i"), "+=", L("1"))])
        ]) { TypeParameter = "T" }];
      default:
        throw new ArgumentException($"'{key}' names no helper of the Visual Basic .NET backend.", nameof(key));
    }
  }

  /// <summary><c>PlyWrap&lt;T&gt;</c>: the low bits of a <c>ULong</c> as the type, above the largest value of a signed one less 2^bits.</summary>
  private static VnMethod WrapDeclaration(string type) {
    VnParameter[] parameters = [new("x", "ULong", false)];
    var (_, high) = Bounds(type);
    var bits = type switch { "SByte" or "Byte" => 8, "Short" or "UShort" => 16, "Integer" or "UInteger" => 32, _ => 64 };
    if (bits == 64)
      return type == "ULong" ? Function(Wrap(type), type, parameters, new VnReturn(N("x")))
        : Function(Wrap(type), type, parameters,
          new VnIf(B(N("x"), ">", L("9223372036854775807UL")), [new VnReturn(B(B(Call("CLng", B(N("x"), "-", L("9223372036854775808UL"))), "-", L("9223372036854775807L")), "-", L("1L")))], null),
          new VnReturn(Call("CLng", N("x"))));
    var mask = (1UL << bits) - 1;
    var low = new VnDim("u", "ULong", B(N("x"), "And", L("&H" + mask.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "UL")));
    if (type is "Byte" or "UShort" or "UInteger")
      return Function(Wrap(type), type, parameters, low, new VnReturn(Call(Conversion(type), N("u"))));
    return Function(Wrap(type), type, parameters, low,
      new VnIf(B(N("u"), ">", L(high + "UL")), [new VnReturn(Call(Conversion(type), B(Call("CLng", N("u")), "-", L((1L << bits).ToString(System.Globalization.CultureInfo.InvariantCulture) + "L"))))], null),
      new VnReturn(Call(Conversion(type), N("u"))));
  }

  /// <summary>The wrapping sum, difference or product of a type of 32 or 64 bits, or the negation of a signed one, with integer checks on: in a wider type, or in halves.</summary>
  private static VnMethod WrappingDeclaration(string name, string type) {
    VnExpression a = N("a"), b = N("b");
    var op = name switch { "PlyAddWrap" => "+", "PlySubWrap" => "-", _ => "*" };
    VnExpression Long(VnExpression x) => Call("CLng", x);
    VnExpression Unsigned(VnExpression x) => Call("CULng", x);
    // The bits of the difference x - y are x + (Not y) + 1.
    VnExpression Combined(VnExpression x, VnExpression y) => name switch {
      "PlyAddWrap" => Call(AddBits, x, y),
      "PlySubWrap" => Call(AddBits, x, Call(AddBits, new VnUnary("Not", y), L("1UL"))),
      "PlyMulWrap" => Call(MulBits, x, y),
      _ => Call(AddBits, new VnUnary("Not", x), L("1UL"))
    };
    VnExpression result = (type, name) switch {
      ("Integer", "PlyNegWrap") => Call(Wrap(type), Call(Bits, new VnUnary("-", Long(a)))),
      ("Integer", _) => Call(Wrap(type), Call(Bits, B(Long(a), op, Long(b)))),
      ("UInteger", "PlySubWrap") => Call(Wrap(type), Call(Bits, B(Long(a), "-", Long(b)))),
      ("UInteger", _) => Call(Wrap(type), B(Unsigned(a), op, Unsigned(b))),
      ("Long", "PlyNegWrap") => Call(Wrap(type), Combined(Call(Bits, a), L("0UL"))),
      ("Long", _) => Call(Wrap(type), Combined(Call(Bits, a), Call(Bits, b))),
      _ => Combined(a, b)
    };
    VnParameter[] parameters = name == "PlyNegWrap" ? [new("a", type, false)] : [new("a", type, false), new("b", type, false)];
    return Function(name, type, parameters, new VnReturn(result));
  }

  /// <summary>Every fixed helper name, in the order a module declares them.</summary>
  public static IReadOnlyList<string> Order { get; } = [
    .. Integers.Select(Check), .. Integers.Select(Fix), .. Integers.Select(Saturate), Bits, AddBits, MulBits, .. Integers.Select(Wrap),
    "PlyAddChk", "PlySubChk", "PlyMulChk", "PlyNegChk", "PlyAddWrap", "PlySubWrap", "PlyMulWrap", "PlyNegWrap", "PlyShrU", "PlyStep", "PlyStepChk", "PlyStepWrap", "PlyIndex", Discard, Bind, Throw, Chr, RoundHalfUp, Min, Max, Clear, Fill, "PlyEquals"
  ];

}
