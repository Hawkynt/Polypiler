using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic.Net;

internal sealed partial class VbNetWriter {

  private const string Invariant = "System.Globalization.CultureInfo.InvariantCulture";

  private VnExpression Expression(IrExpr expression) {
    switch (expression) {
      case IrConst constant:
        return Literal(constant.Type, constant.Value);
      case IrStrConst text:
        return this.Text(text.Value);
      case IrLocalRef reference:
        return new VnName(this.LocalName(reference.Local));
      case IrGlobalRef global:
        return new VnName(this._globals[global.Global.Name]);
      case IrConstRef constant:
        return new VnName(this._constants[constant.Constant.Name]);
      case IrUnary unary:
        return this.Unary(unary);
      case IrBinary binary:
        return this.Binary(binary);
      case IrConvert convert:
        return this.Convert(convert.Operand.Type, convert.Type, this.Expression(convert.Operand), convert.Contract);
      case IrConditional conditional:
        return new VnTernary(this.Expression(conditional.Condition), this.Expression(conditional.WhenTrue), this.Expression(conditional.WhenFalse));
      case IrArrayLoad load:
        return new VnInvoke(this.Expression(load.Array), [this.Index(load.Index)]);
      case IrArrayLength length:
        return new VnMember(this.Expression(length.Array), "Length");
      case IrNewArray created:
        var upper = created.Length is IrConst { Value: var count } ? Literal(IrType.I32, count - 1) : new VnBinary(this.Expression(created.Length), "-", new VnLiteral("1"));
        return new VnNewArray(this.TypeName(created.Type.Element!), upper, []);
      case IrCall call:
        return new VnInvoke(new VnName(this.FunctionName(call.Function)), [.. call.Arguments.Select(this.Expression)]);
      case IrStep step:
        var stepKey = step.Type.IsFloat ? VbNetHelpers.Step(this.TypeName(step.Type))
          : step.Trap ? VbNetHelpers.StepChecked(this.TypeName(step.Type)) : this.Checks ? VbNetHelpers.StepWrapped(this.TypeName(step.Type)) : VbNetHelpers.Step(this.TypeName(step.Type));
        return new VnInvoke(this.Helper(stepKey),
          [this.Expression(step.Target), new VnLiteral(step.Increment ? "True" : "False"), new VnLiteral(step.Postfix ? "True" : "False")]);
      case IrStructNew created when created.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
        return new VnNew(this._typeNames[type], [.. created.Arguments.Select(this.Expression)]);
      case IrStructDefault created when created.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
        return new VnNew(this._typeNames[type], []);
      case IrFieldLoad load:
        return new VnMember(this.Expression(load.Target), this.FieldName(load.Field));
      case IrStructEquals equals when equals.Left.Type.Declaration is IrStructType type && this._typeNames.ContainsKey(type):
        return new VnInvoke(this.Helper(VbNetHelpers.EqualsOf(this._typeNames[type])), [this.Expression(equals.Left), this.Expression(equals.Right)]);
      case IrIntrinsic intrinsic:
        return this.Intrinsic(intrinsic);
      case IrBind bind:
        return new VnInvoke(this.Helper(VbNetHelpers.Bind), [new VnName(this.LocalName(bind.Local)), this.Expression(bind.Value)]);
      case IrThrowExpression thrown:
        return new VnInvoke(new VnName($"{this.HelperName(VbNetHelpers.Throw)}(Of {this.TypeName(thrown.Type)})"), [this.Expression(thrown.Exception)]);
      case IrNew created when created.Type.Declaration is IrClassType { IsLibrary: true } type:
        return new VnNew(this.ClassName(type), [.. created.Arguments.Select(this.Expression)]);
      case IrNull:
        return new VnLiteral("Nothing");
      case IrIsNull test:
        return new VnBinary(this.Expression(test.Value), "Is", new VnLiteral("Nothing"));
      case IrReferenceEquals equals:
        return new VnBinary(this.Expression(equals.Left), "Is", this.Expression(equals.Right));
      default:
        return this.Inexpressible(expression.GetType().Name[2..]);
    }
  }

  /// <summary>The Visual Basic name of a function of the module.</summary>
  private string FunctionName(string function) {
    if (this._functions.TryGetValue(function, out var name))
      return name;

    this.Refuse(VisualBasicEmitter.InexpressibleDescriptor, $"the call of '{function}', which the module does not define");
    return VbNetNaming.Valid(function, "Function");
  }

  /// <summary>
  /// A literal of a type in the form Visual Basic reads with that type: a suffix for <c>Short</c>,
  /// <c>UShort</c>, <c>UInteger</c>, <c>Long</c>, <c>ULong</c> and <c>Single</c>, a conversion for
  /// <c>Byte</c> and <c>SByte</c>, which have none, the smallest signed values in hexadecimal, whose
  /// decimal digits alone do not fit, and the special floating-point values by their names.
  /// </summary>
  internal static VnExpression Literal(IrType type, long value) {
    switch (type.Kind) {
      case IrTypeKind.Bool:
        return new VnLiteral(value != 0 ? "True" : "False");
      case IrTypeKind.I8:
        return new VnInvoke(new VnName("CSByte"), [new VnLiteral(Decimal(value))]);
      case IrTypeKind.U8:
        return new VnInvoke(new VnName("CByte"), [new VnLiteral(Decimal(value))]);
      case IrTypeKind.I16:
        return new VnLiteral(value == short.MinValue ? "&H8000S" : Decimal(value) + "S");
      case IrTypeKind.U16:
        return new VnLiteral(Decimal(value) + "US");
      case IrTypeKind.I32:
        return new VnLiteral(value == int.MinValue ? "&H80000000" : Decimal(value));
      case IrTypeKind.F32 or IrTypeKind.F64:
        return FloatLiteral(type, IrOperators.Float(type, value));
      case IrTypeKind.U32:
        return new VnLiteral(((uint)value).ToString(CultureInfo.InvariantCulture) + "UI");
      case IrTypeKind.I64:
        return new VnLiteral(value == long.MinValue ? "&H8000000000000000L" : Decimal(value) + "L");
      default:
        return new VnLiteral(((ulong)value).ToString(CultureInfo.InvariantCulture) + "UL");
    }
  }

  private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

  private static VnExpression FloatLiteral(IrType type, double value) {
    var single = type == IrType.F32;
    var name = single ? "Single" : "Double";
    if (double.IsNaN(value))
      return new VnLiteral(name + ".NaN");
    if (double.IsInfinity(value))
      return new VnLiteral(name + (value > 0 ? ".PositiveInfinity" : ".NegativeInfinity"));
    if (value == 0 && double.IsNegative(value))
      return new VnLiteral(single ? "CSng(System.BitConverter.Int64BitsToDouble(&H8000000000000000L))" : "System.BitConverter.Int64BitsToDouble(&H8000000000000000L)", false);

    var text = single ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
    if (!text.Contains('.', StringComparison.Ordinal) && !text.Contains('E', StringComparison.Ordinal))
      text += ".0";
    return new VnLiteral(single ? text + "F" : text);
  }

  /// <summary>
  /// A string: a literal of its printable characters, every other character through <c>PlyChr</c>,
  /// joined by <c>&amp;</c>. A text with such a character is no constant expression.
  /// </summary>
  private VnExpression Text(string value) {
    var parts = new List<VnExpression>();
    var run = new StringBuilder();
    foreach (var c in value) {
      if (c is >= ' ' and <= '~') {
        run.Append(c);
        continue;
      }

      if (run.Length > 0)
        parts.Add(new VnString(run.ToString()));
      run.Clear();
      parts.Add(new VnInvoke(this.Helper(VbNetHelpers.Chr), [new VnLiteral(((int)c).ToString(CultureInfo.InvariantCulture))]));
    }

    if (run.Length > 0 || parts.Count == 0)
      parts.Add(new VnString(run.ToString()));
    return parts.Aggregate((left, right) => new VnBinary(left, "&", right));
  }

  private VnExpression Unary(IrUnary unary) {
    var operand = this.Expression(unary.Operand);
    return unary.Op switch {
      IrUnaryOp.LogicalNot or IrUnaryOp.BitNot => new VnUnary("Not", operand),
      IrUnaryOp.NegateChecked when this.Checks => new VnUnary("-", operand),
      IrUnaryOp.NegateChecked => new VnInvoke(this.Helper(VbNetHelpers.NegateChecked(this.TypeName(unary.Type))), [operand]),
      IrUnaryOp.Negate when this.Checks && unary.Type.IsInteger => new VnInvoke(this.Helper(VbNetHelpers.NegateWrapping(this.TypeName(unary.Type))), [operand]),
      _ => new VnUnary("-", operand)
    };
  }

  private VnExpression Binary(IrBinary binary) {
    var left = this.Expression(binary.Left);
    var right = this.Expression(binary.Right);
    var type = binary.Left.Type;
    var name = this.TypeName(type);
    switch (binary.Op) {
      // With integer checks on the operators trap, and the wrapping ones are written out.
      case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked when this.Checks:
        return new VnBinary(left, binary.Op == IrBinaryOp.AddChecked ? "+" : binary.Op == IrBinaryOp.SubtractChecked ? "-" : "*", right);
      case IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply when this.Checks && type.IsInteger:
        return new VnInvoke(this.Helper(VbNetHelpers.Wrapping(binary.Op, name)), [left, right]);
      case IrBinaryOp.ShiftRightLogical when type.IsSigned && this.Checks:
        return new VnInvoke(this.Helper(VbNetHelpers.ShiftRightUnsigned(name)), [left, right]);
      case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked:
        return new VnInvoke(this.Helper(VbNetHelpers.Checked(binary.Op, name)), [left, right]);
      case IrBinaryOp.Divide:
        return new VnBinary(left, type.IsFloat ? "/" : "\\", right);
      case IrBinaryOp.ShiftRightLogical when type.IsSigned:
        var unsigned = type.IsLong ? "CULng" : "CUInt";
        return new VnInvoke(new VnName(type.IsLong ? "CLng" : "CInt"), [new VnBinary(new VnInvoke(new VnName(unsigned), [left]), ">>", right)]);
      default:
        return new VnBinary(left, Operator(binary.Op), right);
    }
  }

  private static string Operator(IrBinaryOp op) => op switch {
    IrBinaryOp.Add => "+",
    IrBinaryOp.Subtract => "-",
    IrBinaryOp.Multiply => "*",
    IrBinaryOp.Remainder => "Mod",
    IrBinaryOp.And => "And",
    IrBinaryOp.Or => "Or",
    IrBinaryOp.Xor => "Xor",
    IrBinaryOp.ShiftLeft => "<<",
    IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical => ">>",
    IrBinaryOp.Equal => "=",
    IrBinaryOp.NotEqual => "<>",
    IrBinaryOp.Less => "<",
    IrBinaryOp.LessOrEqual => "<=",
    IrBinaryOp.Greater => ">",
    IrBinaryOp.GreaterOrEqual => ">=",
    IrBinaryOp.LogicalAnd => "AndAlso",
    _ => "OrElse"
  };

  /// <summary>The conversion function of a numeric type: <c>CInt</c>, <c>CULng</c>, <c>CDbl</c>, ….</summary>
  internal static string Conversion(IrType type) => type.Kind switch {
    IrTypeKind.I8 => "CSByte",
    IrTypeKind.U8 => "CByte",
    IrTypeKind.I16 => "CShort",
    IrTypeKind.U16 => "CUShort",
    IrTypeKind.I32 => "CInt",
    IrTypeKind.U32 => "CUInt",
    IrTypeKind.I64 => "CLng",
    IrTypeKind.U64 => "CULng",
    IrTypeKind.F32 => "CSng",
    _ => "CDbl"
  };

  /// <summary>The signed integer type of a width in bits.</summary>
  private static IrType Signed(int bits) => bits switch {
    8 => IrType.I8,
    16 => IrType.I16,
    32 => IrType.I32,
    _ => IrType.I64
  };

  /// <summary>
  /// A numeric conversion. To floating point it is the conversion function. From floating point a
  /// saturating one is <c>PlySat…</c> and a trapping one <c>PlyFix…</c>. Between integers, where
  /// <c>RemoveIntegerChecks</c> makes the conversion functions keep the low bits, a wrapping one is
  /// the function - a signed value widened to an unsigned type first sign-extended in the signed type
  /// of the target's width - and a trapping one that may lose the value is <c>PlyChk…</c>.
  /// </summary>
  private VnExpression Convert(IrType from, IrType to, VnExpression value, IrConversion contract) {
    if (from.Kind == to.Kind)
      return value;

    VnInvoke Call(string function, VnExpression operand) => new(new VnName(function), [operand]);
    if (to.IsFloat)
      return Call(Conversion(to), value);
    if (from.IsFloat) {
      var wide = from == IrType.F32 ? Call("CDbl", value) : value;
      return new VnInvoke(this.Helper(contract == IrConversion.Trap ? VbNetHelpers.Fix(Scalar(to)!) : VbNetHelpers.Saturate(Scalar(to)!)), [wide]);
    }

    if (IrOperators.Contains(to, from))
      return Call(Conversion(to), value);
    if (contract == IrConversion.Trap)
      return new VnInvoke(this.Helper(VbNetHelpers.Check(Scalar(to)!)), [Call("CDec", value)]);
    // With integer checks on the conversion functions trap: the low bits are taken from the value's two's complement.
    if (this.Checks)
      return new VnInvoke(this.Helper(VbNetHelpers.Wrap(Scalar(to)!)), [from.IsSigned ? new VnInvoke(this.Helper(VbNetHelpers.Bits), [Call("CLng", value)]) : Call("CULng", value)]);

    return from.IsSigned && !to.IsSigned && to.Bits > from.Bits
      ? Call(Conversion(to), Call(Conversion(Signed(to.Bits)), value))
      : Call(Conversion(to), value);
  }

}
