using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.VisualBasic.Net;

internal sealed partial class VbNetLowerer {

  /// <summary>A Boolean condition.</summary>
  private IrExpr Condition(IOperation operation) => this.Value(operation);

  private IrExpr Value(IOperation operation) {
    if (operation.ConstantValue.HasValue && operation.Type is not null)
      return this.Constant(operation.ConstantValue.Value, operation.Type, operation.Syntax);

    switch (operation) {
      case IParenthesizedOperation parenthesized:
        return this.Value(parenthesized.Operand);
      case ILocalReferenceOperation local:
        return new IrLocalRef(this.Local(local.Local, local.Local.Type));
      case IParameterReferenceOperation parameter:
        return new IrLocalRef(this.Local(parameter.Parameter, parameter.Parameter.Type));
      case IFieldReferenceOperation { Instance: null } field when this.Global(field) is { } global:
        return new IrGlobalRef(global);
      case IFieldReferenceOperation { Instance: { } instance } field when this._fields.TryGetValue(field.Field, out var member):
        return new IrFieldLoad(this.Value(instance), member);
      case IArrayElementReferenceOperation { Indices.Length: 1 } element:
        return new IrArrayLoad(this.Value(element.ArrayReference), this.Value(element.Indices[0]));
      case IPropertyReferenceOperation property when this.Property(property) is { } read:
        return read;
      case IBinaryOperation { OperatorMethod: null } binary:
        return this.Binary(binary);
      case IUnaryOperation { OperatorMethod: null } unary:
        return this.Unary(unary);
      case IConversionOperation { OperatorMethod: null } conversion:
        return this.Convert(this.Value(conversion.Operand), conversion.Operand.Type!, conversion.Type!, conversion.Syntax);
      case IConditionalOperation { WhenFalse: { } whenFalse } conditional:
        return new IrConditional(this.Condition(conditional.Condition), this.Value(conditional.WhenTrue), this.Value(whenFalse));
      case IInvocationOperation call:
        return this.Invocation(call);
      case IObjectCreationOperation creation:
        return this.Creation(creation);
      case IArrayCreationOperation creation:
        return this.ArrayCreation(creation);
      default:
        this.Refuse(operation.Syntax, $"the expression '{operation.Syntax}'");
        return Default(operation.Type is null ? IrType.I32 : this.Type(operation.Type));
    }
  }

  /// <summary>A constant as Roslyn folded it, with Visual Basic's own rules: an enum as its underlying integer, a <c>Char</c> as its code unit, <c>Nothing</c> as the default.</summary>
  private IrExpr Constant(object? value, ITypeSymbol type, SyntaxNode syntax) {
    var target = this.Type(type);
    switch (value) {
      case null:
        return Default(target);
      case bool flag:
        return new IrConst(IrType.Bool, flag ? 1 : 0);
      case string text:
        return new IrStrConst(text);
      case char unit:
        return new IrConst(IrType.U16, unit);
      case float single:
        return new IrConst(target, IrOperators.Bits(target, single));
      case double number:
        return new IrConst(target, IrOperators.Bits(target, number));
      case ulong bits:
        return new IrConst(target, unchecked((long)bits));
      case sbyte or byte or short or ushort or int or uint or long:
        return target.IsFloat ? new IrConst(target, IrOperators.Bits(target, System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)))
          : new IrConst(target, System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
      default:
        this.Refuse(syntax, $"the constant {value} of the type {type.ToDisplayString()}");
        return Default(target);
    }
  }

  /// <summary>A module variable, or <see langword="null"/> after refusing one an initializer reads before it is initialized.</summary>
  private IrGlobal? Global(IFieldReferenceOperation field) {
    if (this._globals.TryGetValue(field.Field, out var global))
      return global;

    this.Refuse(field.Syntax, $"the field '{field.Field.Name}', which is no module variable initialized before");
    return null;
  }

  private IrExpr? Property(IPropertyReferenceOperation property) {
    var target = property.Instance is null ? null : this.Value(property.Instance);
    return (property.Property.Name, target?.Type) switch {
      ("Length", { IsArray: true }) => new IrArrayLength(target!),
      ("Length", { IsString: true }) => new IrIntrinsic(IrIntrinsicOp.StrLength, IrType.I32, [target!]),
      ("Chars", { IsString: true }) when property.Arguments is [var index] => new IrIntrinsic(IrIntrinsicOp.StrCodeUnitAt, IrType.U16, [target!, this.Value(index.Value)]),
      ("Message", { IsObject: true }) => new IrFieldLoad(target!, IrExceptionClass.Message),
      ("InnerException", { IsObject: true }) => new IrFieldLoad(target!, IrExceptionClass.InnerException),
      _ => null
    };
  }

  private static IrExpr Minus1(IrType type) => type.IsFloat ? new IrConst(type, IrOperators.Bits(type, -1.0)) : new IrConst(type, IrOperators.Wrap(type, -1));

  /// <summary>
  /// A conversion as Visual Basic makes it: <c>True</c> is -1 of the target type (255 of a <c>Byte</c>),
  /// a number is <c>True</c> when it is not 0; a floating-point number becomes an integer rounded half
  /// to even, then converted; a narrowing conversion of integers throws an <c>OverflowException</c>
  /// unless integer checks are removed; a number becomes text in the invariant culture.
  /// </summary>
  private IrExpr Convert(IrExpr value, ITypeSymbol from, ITypeSymbol to, SyntaxNode syntax) {
    var target = this.Type(to);
    var source = value.Type;
    if (source == target && !(from.SpecialType == SpecialType.System_Char ^ to.SpecialType == SpecialType.System_Char))
      return value;

    if (target.IsString) {
      if (from.SpecialType == SpecialType.System_Char)
        return new IrIntrinsic(IrIntrinsicOp.StrFromCodeUnit, IrType.String, [value]);
      if (source == IrType.Bool)
        return new IrIntrinsic(IrIntrinsicOp.FormatBoolDotNet, IrType.String, [value]);
      if (source.IsNumeric) {
        this._caveats.Add(VbNetFrontend.InvariantCulture);
        return new IrIntrinsic(IrIntrinsicOp.FormatDotNet, IrType.String, [value]);
      }
    }

    if (from.SpecialType == SpecialType.System_Char || to.SpecialType == SpecialType.System_Char) {
      this.Refuse(syntax, $"the conversion from {from.ToDisplayString()} to {to.ToDisplayString()}");
      return Default(target);
    }

    if (source == IrType.Bool && target.IsNumeric)
      return new IrConditional(value, Minus1(target), Default(target));
    if (source.IsNumeric && target == IrType.Bool)
      return new IrBinary(IrBinaryOp.NotEqual, value, Default(source));
    if (source.IsNumeric && target.IsNumeric) {
      if (target.IsFloat)
        return new IrConvert(target, value);
      if (source.IsFloat)
        return new IrConvert(target, new IrIntrinsic(IrIntrinsicOp.RoundEven, source, [value]), checks ? IrConversion.Trap : IrConversion.Saturate);
      return IrOperators.Contains(target, source) ? new IrConvert(target, value) : new IrConvert(target, value, checks ? IrConversion.Trap : IrConversion.Wrap);
    }

    if (source.IsObject && target.IsObject && source.IsAssignableTo(target))
      return value;

    this.Refuse(syntax, $"the conversion from {from.ToDisplayString()} to {to.ToDisplayString()}");
    return Default(target);
  }

  /// <summary>An operation of integers of a type, the narrow ones computed in 32 bits and converted back, which traps where Visual Basic checks.</summary>
  private IrExpr Arithmetic(IrBinaryOp op, IrExpr left, IrExpr right, IrType type, bool isChecked) {
    if (type.IsFloat)
      return new IrBinary(op, left, right);

    var checkedOp = op switch {
      IrBinaryOp.Add => IrBinaryOp.AddChecked,
      IrBinaryOp.Subtract => IrBinaryOp.SubtractChecked,
      IrBinaryOp.Multiply => IrBinaryOp.MultiplyChecked,
      _ => op
    };
    if (type.IsWideInteger)
      return new IrBinary(isChecked ? checkedOp : op, left, right);

    var wide = type.IsSigned ? IrType.I32 : IrType.U32;
    var result = new IrBinary(op, new IrConvert(wide, left), new IrConvert(wide, right));
    return new IrConvert(type, result, isChecked ? IrConversion.Trap : IrConversion.Wrap);
  }

  private IrExpr Binary(IBinaryOperation binary) {
    // r Is Nothing, a reference compared through Object.
    if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && binary.LeftOperand.Type?.SpecialType == SpecialType.System_Object) {
      var (left, right) = (Unwrapped(binary.LeftOperand), Unwrapped(binary.RightOperand));
      var other = left.ConstantValue is { HasValue: true, Value: null } ? right : right.ConstantValue is { HasValue: true, Value: null } ? left : null;
      if (other is { Type: IArrayTypeSymbol } || other?.Type is { } type && ExceptionClass(type) is not null) {
        IrExpr test = new IrIsNull(this.Value(other));
        return binary.OperatorKind == BinaryOperatorKind.Equals ? test : new IrUnary(IrUnaryOp.LogicalNot, test);
      }

      this.Refuse(binary.Syntax, $"the comparison of references '{binary.Syntax}'");
      return new IrConst(IrType.Bool, 0);
    }

    if (binary.OperatorKind == BinaryOperatorKind.Concatenate)
      return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [.. this.Concatenated(binary)]);

    return this.Binary(binary.OperatorKind, this.Value(binary.LeftOperand), this.Value(binary.RightOperand), binary.LeftOperand.Type!, binary.RightOperand.Type!, binary.IsChecked && checks, binary.Syntax);
  }

  private static IOperation Unwrapped(IOperation operation) => operation is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : operation;

  /// <summary>The parts of a chain of <c>&amp;</c>, in order.</summary>
  private IEnumerable<IrExpr> Concatenated(IOperation operation)
    => operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.Concatenate, OperatorMethod: null } binary && !binary.ConstantValue.HasValue
      ? this.Concatenated(binary.LeftOperand).Concat(this.Concatenated(binary.RightOperand))
      : [this.Value(operation)];

  private IrExpr Binary(BinaryOperatorKind kind, IrExpr left, IrExpr right, ITypeSymbol leftType, ITypeSymbol rightType, bool isChecked, SyntaxNode syntax) {
    var type = left.Type;
    switch (kind) {
      case BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply when type.IsNumeric:
        return this.Arithmetic(kind == BinaryOperatorKind.Add ? IrBinaryOp.Add : kind == BinaryOperatorKind.Subtract ? IrBinaryOp.Subtract : IrBinaryOp.Multiply, left, right, type, isChecked);
      case BinaryOperatorKind.Divide when type.IsFloat:
        return new IrBinary(IrBinaryOp.Divide, left, right);
      case BinaryOperatorKind.IntegerDivide when type.IsInteger:
        return this.Arithmetic(IrBinaryOp.Divide, left, right, type, checks);
      case BinaryOperatorKind.Remainder when type.IsNumeric:
        return this.Arithmetic(IrBinaryOp.Remainder, left, right, type, false);
      case BinaryOperatorKind.Concatenate or BinaryOperatorKind.Add when type.IsString && right.Type.IsString:
        return new IrIntrinsic(IrIntrinsicOp.StrConcat, IrType.String, [left, right]);
      case BinaryOperatorKind.Power when type == IrType.F64:
        return new IrIntrinsic(IrIntrinsicOp.Pow, IrType.F64, [left, right]);
      case BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr when type == IrType.Bool:
        return new IrBinary(kind == BinaryOperatorKind.ConditionalAnd ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr, left, right);
      case BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when type == IrType.Bool:
        // Both operands are evaluated, as Visual Basic's And and Or evaluate them: as integers where the right one has an effect.
        if (kind == BinaryOperatorKind.ExclusiveOr)
          return new IrBinary(IrBinaryOp.NotEqual, left, right);
        if (Pure(right))
          return new IrBinary(kind == BinaryOperatorKind.And ? IrBinaryOp.LogicalAnd : IrBinaryOp.LogicalOr, left, right);
        IrExpr Bit(IrExpr flag) => new IrConditional(flag, new IrConst(IrType.I32, 1), new IrConst(IrType.I32, 0));
        return new IrBinary(IrBinaryOp.NotEqual, new IrBinary(kind == BinaryOperatorKind.And ? IrBinaryOp.And : IrBinaryOp.Or, Bit(left), Bit(right)), new IrConst(IrType.I32, 0));
      case BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when type.IsInteger:
        var bitwise = kind == BinaryOperatorKind.And ? IrBinaryOp.And : kind == BinaryOperatorKind.Or ? IrBinaryOp.Or : IrBinaryOp.Xor;
        if (type.IsWideInteger)
          return new IrBinary(bitwise, left, right);
        var wide = type.IsSigned ? IrType.I32 : IrType.U32;
        return new IrConvert(type, new IrBinary(bitwise, new IrConvert(wide, left), new IrConvert(wide, right)), IrConversion.Wrap);
      case BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift when type.IsInteger && right.Type == IrType.I32:
        var shift = kind == BinaryOperatorKind.LeftShift ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight;
        if (type.IsWideInteger)
          return new IrBinary(shift, left, right);
        // A narrow value shifts by its count masked to its width, as Visual Basic masks it.
        var masked = new IrBinary(IrBinaryOp.And, right, new IrConst(IrType.I32, type.Bits - 1));
        return new IrConvert(type, new IrBinary(shift, new IrConvert(type.IsSigned ? IrType.I32 : IrType.U32, left), masked), IrConversion.Wrap);
      case BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
        or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual:
        return this.Comparison(kind, left, right, leftType, syntax);
      default:
        this.Refuse(syntax, $"the operator of '{syntax}'");
        return Default(type);
    }
  }

  /// <summary>Whether an operand can neither trap nor change anything, so that the IR may skip it where Visual Basic evaluates it.</summary>
  private static bool Pure(IrExpr expression) => expression switch {
    IrConst or IrLocalRef or IrGlobalRef or IrStrConst => true,
    IrUnary { Op: IrUnaryOp.LogicalNot, Operand: var operand } => Pure(operand),
    IrBinary { Op: >= IrBinaryOp.Equal } binary => Pure(binary.Left) && Pure(binary.Right),
    _ => false
  };

  /// <summary>A comparison: of numbers as the IR compares them, of strings by code unit, of Booleans with <c>True</c> as -1.</summary>
  private IrExpr Comparison(BinaryOperatorKind kind, IrExpr left, IrExpr right, ITypeSymbol type, SyntaxNode syntax) {
    var op = kind switch {
      BinaryOperatorKind.Equals => IrBinaryOp.Equal,
      BinaryOperatorKind.NotEquals => IrBinaryOp.NotEqual,
      BinaryOperatorKind.LessThan => IrBinaryOp.Less,
      BinaryOperatorKind.LessThanOrEqual => IrBinaryOp.LessOrEqual,
      BinaryOperatorKind.GreaterThan => IrBinaryOp.Greater,
      _ => IrBinaryOp.GreaterOrEqual
    };
    if (left.Type.IsString) {
      if (this.CompareText) {
        this.Refuse(syntax, "a comparison of strings under Option Compare Text");
        return new IrConst(IrType.Bool, 0);
      }

      if (op is IrBinaryOp.Equal or IrBinaryOp.NotEqual) {
        IrExpr same = new IrIntrinsic(IrIntrinsicOp.StrEquals, IrType.Bool, [left, right]);
        return op == IrBinaryOp.Equal ? same : new IrUnary(IrUnaryOp.LogicalNot, same);
      }

      return new IrBinary(op, new IrIntrinsic(IrIntrinsicOp.StrCompareOrdinal, IrType.I32, [left, right]), new IrConst(IrType.I32, 0));
    }

    if (left.Type == IrType.Bool && op is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual)) {
      IrExpr Number(IrExpr flag) => new IrConditional(flag, new IrConst(IrType.I32, -1), new IrConst(IrType.I32, 0));
      return new IrBinary(op, Number(left), Number(right));
    }

    // A narrow integer is compared as the Integer it widens to exactly; the IR compares the wide types.
    if (left.Type.IsInteger && !left.Type.IsWideInteger)
      return new IrBinary(op, new IrConvert(IrType.I32, left), new IrConvert(IrType.I32, right));
    if (left.Type.IsNumeric || left.Type == IrType.Bool)
      return new IrBinary(op, left, right);

    this.Refuse(syntax, $"the comparison of {type.ToDisplayString()}");
    return new IrConst(IrType.Bool, 0);
  }

  private IrExpr Unary(IUnaryOperation unary) {
    var operand = this.Value(unary.Operand);
    var type = operand.Type;
    switch (unary.OperatorKind) {
      case UnaryOperatorKind.Plus:
        return operand;
      case UnaryOperatorKind.Minus when type.IsFloat:
        return new IrUnary(IrUnaryOp.Negate, operand);
      case UnaryOperatorKind.Minus when type == IrType.I32 || type == IrType.I64:
        return new IrUnary(unary.IsChecked && checks ? IrUnaryOp.NegateChecked : IrUnaryOp.Negate, operand);
      case UnaryOperatorKind.Minus when type.IsSigned:
        return new IrConvert(type, new IrUnary(IrUnaryOp.Negate, new IrConvert(IrType.I32, operand)), unary.IsChecked && checks ? IrConversion.Trap : IrConversion.Wrap);
      case UnaryOperatorKind.Not when type == IrType.Bool:
        return new IrUnary(IrUnaryOp.LogicalNot, operand);
      case UnaryOperatorKind.Not when type.IsWideInteger:
        return new IrUnary(IrUnaryOp.BitNot, operand);
      case UnaryOperatorKind.Not when type.IsInteger:
        return new IrConvert(type, new IrUnary(IrUnaryOp.BitNot, new IrConvert(type.IsSigned ? IrType.I32 : IrType.U32, operand)), IrConversion.Wrap);
      default:
        this.Refuse(unary.Syntax, $"the operator of '{unary.Syntax}'");
        return operand;
    }
  }

  /// <summary>
  /// The type of the operands of an operator of Visual Basic, from the result-type tables of the
  /// language reference: the wider type; a signed and an unsigned integer the signed type twice as
  /// wide as the unsigned one, at least as wide as the signed one; <c>Single</c> with an integer
  /// <c>Single</c>, <c>Double</c> with anything <c>Double</c>; <c>/</c> and <c>^</c> in floating point,
  /// <c>\</c> in integers, <c>&amp;</c> in strings, the shifts in the type of their left operand.
  /// <see langword="null"/> where the result would be a <c>Decimal</c> or the operands are no numbers.
  /// </summary>
  private ITypeSymbol? OperatorType(BinaryOperatorKind kind, ITypeSymbol left, ITypeSymbol right) {
    var (a, b) = (this.Type(left), this.Type(right));
    if (kind == BinaryOperatorKind.Concatenate)
      return compilation.GetSpecialType(SpecialType.System_String);
    if (kind is BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift)
      return a.IsInteger ? left : null;
    if (a.IsString && b.IsString && kind == BinaryOperatorKind.Add)
      return left;
    if (!a.IsNumeric || !b.IsNumeric || left.SpecialType == SpecialType.System_Char || right.SpecialType == SpecialType.System_Char)
      return null;
    if (kind == BinaryOperatorKind.Power)
      return compilation.GetSpecialType(SpecialType.System_Double);

    IrType result;
    if (a == IrType.F64 || b == IrType.F64)
      result = IrType.F64;
    else if (a == IrType.F32 || b == IrType.F32)
      result = IrType.F32;
    else if (a == b)
      result = a;
    else if (a.IsSigned == b.IsSigned)
      result = a.Bits >= b.Bits ? a : b;
    else {
      var (signed, unsigned) = a.IsSigned ? (a, b) : (b, a);
      var bits = Math.Max(signed.Bits, unsigned.Bits * 2);
      if (bits > 64)
        return null;
      result = bits switch { 16 => IrType.I16, 32 => IrType.I32, _ => IrType.I64 };
    }

    if (kind == BinaryOperatorKind.Divide && !result.IsFloat)
      result = IrType.F64;
    if (kind is BinaryOperatorKind.IntegerDivide or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr && result.IsFloat)
      result = IrType.I64;
    return Symbol(result);
  }

  private ITypeSymbol Symbol(IrType type) => compilation.GetSpecialType(type.Kind switch {
    IrTypeKind.I8 => SpecialType.System_SByte,
    IrTypeKind.U8 => SpecialType.System_Byte,
    IrTypeKind.I16 => SpecialType.System_Int16,
    IrTypeKind.U16 => SpecialType.System_UInt16,
    IrTypeKind.I32 => SpecialType.System_Int32,
    IrTypeKind.U32 => SpecialType.System_UInt32,
    IrTypeKind.I64 => SpecialType.System_Int64,
    IrTypeKind.U64 => SpecialType.System_UInt64,
    IrTypeKind.F32 => SpecialType.System_Single,
    _ => SpecialType.System_Double
  });

}
