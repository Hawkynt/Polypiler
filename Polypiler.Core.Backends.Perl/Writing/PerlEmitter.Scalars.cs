using System;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Analysis.Range;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Perl;

public static partial class PerlEmitter {

  private sealed partial class Writer {

    public string EmitExpr(IrExpr expression) {
      if (!this.CheckType(expression.Type, this._currentFunction?.Name ?? this._module.Name, false))
        return "0";

      return expression switch {
        IrConst constant => FormatScalarConstant(constant.Type, constant.Value),
        IrStrConst str => PerlNaming.StringLiteral(str.Value),
        IrConstRef constRef => "$" + this._constNames[constRef.Constant],
        IrLocalRef localRef => this.LocalName(localRef.Local),
        IrGlobalRef globalRef => "$" + this._globalNames[globalRef.Global],
        IrUnary unary => this.EmitUnary(unary),
        IrBinary binary => this.EmitBinary(binary),
        IrConvert convert => this.EmitConvert(convert),
        IrConditional cond => $"({this.EmitExpr(cond.Condition)} ? {this.EmitExpr(cond.WhenTrue)} : {this.EmitExpr(cond.WhenFalse)})",
        IrCall call => this.EmitCall(call),
        IrStep step => this.EmitStep(step),
        IrBind bind => $"do {{ {this.LocalName(bind.Local)} = {this.EmitExpr(bind.Value)}; {this.LocalName(bind.Local)} }}",
        IrArrayLoad load => this.EmitArrayLoad(load),
        IrArrayLength length => this.EmitArrayLength(length),
        IrNewArray newArray => this.EmitNewArray(newArray),
        IrIntrinsic intrinsic => this.EmitIntrinsic(intrinsic),
        IrStructNew structNew => this.EmitStructNew(structNew),
        IrStructDefault structDefault => this.DefaultValue(structDefault.Type),
        IrFieldLoad fieldLoad => this.EmitFieldLoad(fieldLoad),
        IrStructEquals structEquals => this.EmitStructEquals(structEquals),
        IrNew objNew => this.EmitObjectNew(objNew),
        IrNull => "undef",
        IrMethodCall methodCall => this.EmitMethodCall(methodCall),
        IrReferenceEquals refEq => this.EmitReferenceEquals(refEq),
        IrNone => "undef",
        IrSome some => this.EmitExpr(some.Value),
        IrIsNone isNone => $"(!defined({this.EmitExpr(isNone.Value)}) ? 1 : 0)",
        IrUnwrap unwrap => this.EmitUnwrap(unwrap),
        IrCoalesce coalesce => this.EmitCoalesce(coalesce),
        IrIsNull isNull => $"(!defined({this.EmitExpr(isNull.Value)}) ? 1 : 0)",
        IrCollection collection => this.EmitCollection(collection),
        IrLambda lambda => this.EmitLambda(lambda),
        IrFunctionRef funcRef => "\\&" + this.EmitQualifyCall(funcRef.Function),
        IrBoundMethod bound => this.EmitBoundMethod(bound),
        IrInvoke invoke => this.EmitInvoke(invoke),
        IrThrowExpression throwExpr => $"do {{ die {this.EmitExceptionValue(throwExpr.Exception)}; }}",
        IrTypeTest typeTest => this.EmitTypeTest(typeTest),
        IrCast cast => this.EmitCast(cast),
        IrAsCast asCast => this.EmitAsCast(asCast),
        IrGenericEquals genEq => this.EmitGenericEquals(genEq),
        IrGenericCompare genCmp => this.EmitGenericCompare(genCmp),
        IrAwait awaited => $"{this.HelperCall(PerlHelpers.TaskAwait)}({this.EmitExpr(awaited.Operand)})",
        IrAsync asyncExpr => this.EmitAsyncExpr(asyncExpr),
        IrArraySequence arraySeq => this.EmitArraySequence(arraySeq),
        _ => this.UnsupportedOp(expression.GetType().Name)
      };
    }

    private string EmitAsyncExpr(IrAsync asyncExpr) => asyncExpr.Op switch {
      IrAsyncOp.Completed => "{ done => 1, value => undef }",
      IrAsyncOp.FromResult => $"{this.HelperCall(PerlHelpers.TaskFromResult)}({this.CopyIfStruct(asyncExpr.Arguments[0].Type, this.EmitExpr(asyncExpr.Arguments[0]))})",
      _ => $"{this.HelperCall(PerlHelpers.TaskWhenAll)}({(asyncExpr.Type.Element == IrType.Void ? "1" : "0")}{(asyncExpr.Arguments.Count > 0 ? ", " + string.Join(", ", asyncExpr.Arguments.Select(this.EmitExpr)) : "")})"
    };

    private string EmitArraySequence(IrArraySequence arraySeq) {
      var arr = this.EmitExpr(arraySeq.Array);
      if (arraySeq.Array.Type.Element == IrType.U8 && this._profile.ByteStorage == PerlByteStorage.String)
        return $"do {{ my $_a = {arr}; sub {{ my ($_yield) = @_; for my $_i (0 .. length($$_a) - 1) {{ return unless $_yield->(ord(substr($$_a, $_i, 1))); }} }} }}";
      return $"do {{ my $_a = {arr}; sub {{ my ($_yield) = @_; for my $_el (@$_a) {{ return unless $_yield->($_el); }} }} }}";
    }

    private string EmitCoalesce(IrCoalesce coalesce) {
      var val = this.EmitExpr(coalesce.Value);
      var fallback = this.EmitExpr(coalesce.Fallback);
      return this._profile.HasDefinedOr
        ? $"({val} // {fallback})"
        : $"do {{ my $_c = {val}; defined($_c) ? $_c : {fallback} }}";
    }

    private string EmitGenericEquals(IrGenericEquals genEq) {
      var l = this.EmitExpr(genEq.Left);
      var r = this.EmitExpr(genEq.Right);
      var t = genEq.Left.Type;
      if (t.IsFloat)
        return $"do {{ my ($_l, $_r) = ({l}, {r}); (($_l != $_l && $_r != $_r) || $_l == $_r) ? 1 : 0 }}";
      if (t.IsStruct)
        return this.EmitStructEquals(new IrStructEquals(genEq.Left, genEq.Right));
      return $"(({l} == {r}) ? 1 : 0)";
    }

    private string EmitGenericCompare(IrGenericCompare genCmp) {
      var l = this.EmitExpr(genCmp.Left);
      var r = this.EmitExpr(genCmp.Right);
      if (genCmp.Left.Type.IsFloat)
        return $"do {{ my ($_l, $_r) = ({l}, {r}); $_l != $_l ? ($_r != $_r ? 0 : -1) : $_r != $_r ? 1 : ($_l <=> $_r) }}";
      return $"({l} <=> {r})";
    }

    private string EmitCall(IrCall call) {
      var target = this.EmitQualifyCall(call.Function);
      var args = string.Join(", ", call.Arguments.Select(this.EmitExpr));
      return $"{target}({args})";
    }

    private string EmitUnary(IrUnary unary) {
      var operand = this.EmitExpr(unary.Operand);
      var type = unary.Type.Integral;
      return unary.Op switch {
        IrUnaryOp.LogicalNot => $"(({operand}) ? 0 : 1)",
        IrUnaryOp.BitNot when type.IsSigned => $"(-1 - ({operand}))",
        IrUnaryOp.BitNot => $"(({operand}) ^ {Mask(type)})",
        IrUnaryOp.Negate when type.IsFloat => this.RoundFloat(type, $"-({operand})"),
        IrUnaryOp.Negate => Wrap(type, this.InInteger($"-({operand})")),
        _ => $"do {{ my $_v = {operand}; $_v == {Minimum(type)} ? die(\"Overflow: negation outside {type}\\n\") : -$_v }}"
      };
    }

    private string EmitBinary(IrBinary binary) {
      var left = this.EmitExpr(binary.Left);
      var right = this.EmitExpr(binary.Right);

      if (TryComparisonOperator(binary.Op, out var cmp))
        return $"(({left} {cmp} {right}) ? 1 : 0)";

      if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr) {
        var tok = binary.Op == IrBinaryOp.LogicalAnd ? "&&" : "||";
        return $"(({left} {tok} {right}) ? 1 : 0)";
      }

      var type = binary.Type.Integral;
      if (type.IsFloat)
        return this.EmitFloatBinary(binary.Op, type, left, right);

      if (binary.Op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply) {
        var tok = binary.Op == IrBinaryOp.Add ? "+" : binary.Op == IrBinaryOp.Subtract ? "-" : "*";
        var plain = $"({left} {tok} {right})";
        if (this.ArithmeticFits(binary))
          return plain;

        if (type == IrType.I64)
          return this._integerFunction ? plain : $"do {{ use integer; {plain} }}";
        if (type == IrType.U64)
          return $"(do {{ use integer; {left} {tok} {right} }}) & 18446744073709551615";

        var expr = this._integerFunction ? plain : this.InInteger(plain);
        return Wrap(type, expr);
      }

      return binary.Op switch {
        IrBinaryOp.Divide or IrBinaryOp.Remainder => this.EmitIntegerDivision(binary.Op, type, left, right),
        IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked => this.EmitCheckedArithmetic(binary.Op, type, left, right),
        IrBinaryOp.And => this.EmitBitwise(type, left, right, "&"),
        IrBinaryOp.Or => this.EmitBitwise(type, left, right, "|"),
        IrBinaryOp.Xor => this.EmitBitwise(type, left, right, "^"),
        IrBinaryOp.ShiftLeft => this.EmitShiftLeft(type, left, right),
        IrBinaryOp.ShiftRight when type.IsSigned => this.EmitArithmeticShiftRight(type, left, right),
        IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical => this.EmitLogicalShiftRight(type, left, right),
        _ => this.UnsupportedOp(binary.Op.ToString())
      };
    }

    private string EmitFloatBinary(IrBinaryOp op, IrType type, string left, string right) => op switch {
      IrBinaryOp.Add => this.RoundFloat(type, $"({left} + {right})"),
      IrBinaryOp.Subtract => this.RoundFloat(type, $"({left} - {right})"),
      IrBinaryOp.Multiply => this.RoundFloat(type, $"({left} * {right})"),
      IrBinaryOp.Divide => this.EmitFloatDivide(type, left, right),
      _ => this.EmitFloatRemainder(type, left, right)
    };

    private string EmitFloatDivide(IrType type, string left, string right) {
      var helper = this._profile.HasC99Posix ? PerlHelpers.FloatDivide : PerlHelpers.FloatDivide56;
      return this.RoundFloat(type, $"{this.HelperCall(helper)}({left}, {right})");
    }

    private string EmitFloatRemainder(IrType type, string left, string right) {
      var helper = this._profile.HasC99Posix ? PerlHelpers.FloatRemainder : PerlHelpers.FloatRemainder56;
      return this.RoundFloat(type, $"{this.HelperCall(helper)}({left}, {right})");
    }

    private string RoundFloat(IrType type, string expr) {
      if (type != IrType.F32)
        return expr;
      return $"{this.HelperCall(PerlHelpers.RoundF32)}({expr})";
    }

    private bool ArithmeticFits(IrBinary binary) {
      var type = binary.Type.Integral;
      if (type.IsLong || this._ranges is null)
        return false;

      var target = ValueRange.Of(type);
      var left = RangeDomain.Instance.Meet(this._ranges.At(binary.Left, this._point), target);
      var right = RangeDomain.Instance.Meet(this._ranges.At(binary.Right, this._point), target);
      if (left.IsEmpty || right.IsEmpty)
        return false;

      var result = binary.Op switch {
        IrBinaryOp.Add => new ValueRange(left.Min + right.Min, left.Max + right.Max),
        IrBinaryOp.Subtract => new ValueRange(left.Min - right.Max, left.Max - right.Min),
        _ => ProductRange(left, right)
      };
      return target.Covers(result);
    }

    private static ValueRange ProductRange(ValueRange left, ValueRange right) {
      var ll = left.Min * right.Min;
      var lh = left.Min * right.Max;
      var hl = left.Max * right.Min;
      var hh = left.Max * right.Max;
      return new(Int128.Min(Int128.Min(ll, lh), Int128.Min(hl, hh)),
        Int128.Max(Int128.Max(ll, lh), Int128.Max(hl, hh)));
    }

    private string EmitBitwise(IrType type, string left, string right, string tok) {
      if (type.IsSigned && !this._integerFunction)
        return $"do {{ use integer; ({left}) {tok} ({right}) }}";
      return $"({left} {tok} {right})";
    }

    private string EmitIntegerDivision(IrBinaryOp op, IrType type, string left, string right) {
      var isDiv = op == IrBinaryOp.Divide;
      if (type == IrType.U64)
        return BindBinary(left, right, isDiv ? "($_l - $_l % $_r) / $_r" : "$_l % $_r");

      var body = this._integerFunction
        ? (isDiv ? "($_l / $_r)" : "($_l % $_r)")
        : $"do {{ use integer; $_l {(isDiv ? "/" : "%")} $_r }}";
      if (type.IsSigned)
        body = $"($_l == {Minimum(type)} && $_r == -1 ? die(\"Overflow: quotient outside {type}\\n\") : {body})";
      return BindBinary(left, right, body);
    }

    private string EmitCheckedArithmetic(IrBinaryOp op, IrType type, string left, string right) {
      var min = Minimum(type);
      var max = Maximum(type);
      var (tok, overflows) = op switch {
        IrBinaryOp.AddChecked => ("+", type.IsSigned
          ? $"($_r > 0 && $_l > {max} - $_r) || ($_r < 0 && $_l < {min} - $_r)"
          : $"$_l > {max} - $_r"),
        IrBinaryOp.SubtractChecked => ("-", type.IsSigned
          ? $"($_r < 0 && $_l > {max} + $_r) || ($_r > 0 && $_l < {min} + $_r)"
          : "$_l < $_r"),
        _ => ("*", type.IsSigned
          ? "$_l > 0 ? ($_r > 0 ? $_l > do { use integer; " + max + " / $_r } : $_r < do { use integer; " + min + " / $_l }) : " +
            "$_l < 0 && ($_r > 0 ? $_l < do { use integer; " + min + " / $_r } : $_r < 0 && $_l < do { use integer; " + max + " / $_r })"
          : $"$_r != 0 && $_l > (({max} - {max} % $_r) / $_r)")
      };
      var compute = type == IrType.U64
        ? $"(do {{ use integer; $_l {tok} $_r }}) & 18446744073709551615"
        : Wrap(type, $"do {{ use integer; $_l {tok} $_r }}");
      return BindBinary(left, right, $"(({overflows}) ? die(\"Overflow: value outside {type}\\n\") : {compute})");
    }

    private string EmitShiftLeft(IrType type, string left, string right) {
      var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
      if (type == IrType.U64)
        return $"(({left}) << (({right}) & {mask}))";
      var shifted = this._integerFunction
        ? $"(({left}) << (({right}) & {mask}))"
        : $"do {{ use integer; ({left}) << (({right}) & {mask}) }}";
      return type == IrType.I64 ? shifted : Wrap(type, shifted);
    }

    private string EmitArithmeticShiftRight(IrType type, string left, string right) {
      var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
      return this._integerFunction
        ? $"(({left}) >> (({right}) & {mask}))"
        : $"do {{ use integer; ({left}) >> (({right}) & {mask}) }}";
    }

    private string EmitLogicalShiftRight(IrType type, string left, string right) {
      var mask = (type.Bits - 1).ToString(CultureInfo.InvariantCulture);
      if (!type.IsSigned && !this._integerFunction)
        return $"(({left}) >> (({right}) & {mask}))";

      var unsigned = type.IsLong
        ? (type.IsSigned ? $"unpack('Q', pack('q', {left}))" : left)
        : $"({left} & {Mask(type)})";
      var shift = this._integerFunction
        ? $"do {{ no integer; ({unsigned}) >> (({right}) & {mask}) }}"
        : $"(({unsigned}) >> (({right}) & {mask}))";
      return type == IrType.U64 ? shift : Wrap(type, shift);
    }

    private string EmitConvert(IrConvert convert) {
      var operand = this.EmitExpr(convert.Operand);
      var target = convert.Type.Integral;
      var source = convert.Operand.Type.Integral;
      if (target == source)
        return operand;

      if (target == IrType.F32 && source.IsLong)
        return $"{this.HelperCall(PerlHelpers.IntToF32)}({operand})";
      if (target == IrType.F32)
        return this.RoundFloat(IrType.F32, $"({operand} + 0.0)");
      if (target == IrType.F64)
        return $"({operand} + 0.0)";

      if (source.IsFloat) {
        var upper = target.IsSigned
          ? (target.IsLong ? "9223372036854775808.0" : (1UL << (target.Bits - 1)).ToString(CultureInfo.InvariantCulture) + ".0")
          : target.IsLong ? "18446744073709551616.0" : (1UL << target.Bits).ToString(CultureInfo.InvariantCulture) + ".0";
        var trap = convert.Contract == IrConversion.Trap;
        var fail = $"die(\"Overflow: conversion outside {target}\\n\")";
        var nanVal = trap ? fail : "0";
        var lowVal = trap ? fail : Minimum(target);
        var highVal = trap ? fail : Maximum(target);
        var norm = target == IrType.U64 ? "unpack('Q', pack('Q', $_v))" : Wrap(target, "$_v");
        return $"do {{ no integer; my $_v = int({operand}); $_v != $_v ? {nanVal} : $_v < {Minimum(target)} ? {lowVal} : $_v >= {upper} ? {highVal} : {norm} }}";
      }

      if (ValueRange.Of(target).Covers(ValueRange.Of(source)))
        return operand;

      if (convert.Contract == IrConversion.Wrap) {
        if (target == IrType.I64 && source == IrType.U64)
          return $"unpack('q', pack('Q', {operand}))";
        if (target == IrType.U64 && source == IrType.I64)
          return $"unpack('Q', pack('q', {operand}))";
        return Wrap(target, operand);
      }

      return $"do {{ my $_v = {operand}; $_v < {Minimum(target)} || $_v > {Maximum(target)} ? die(\"Overflow: conversion outside {target}\\n\") : $_v }}";
    }

    private string EmitStep(IrStep step) {
      var type = step.Type.Integral;
      var delta = step.Increment ? "+" : "-";
      var one = type.IsFloat ? "1.0" : "1";
      string nextExpr(string cur) {
        if (type.IsFloat)
          return this.RoundFloat(type, $"({cur} {delta} {one})");
        if (step.Trap) {
          var limit = step.Increment ? Maximum(type) : Minimum(type);
          return $"({cur} == {limit} ? die(\"Overflow: step outside {type}\\n\") : {Wrap(type, this.InInteger($"({cur} {delta} 1)"))})";
        }
        return Wrap(type, this.InInteger($"({cur} {delta} 1)"));
      }

      if (step.Target is IrLocalRef localRef) {
        var name = this.LocalName(localRef.Local);
        return step.Postfix
          ? $"do {{ my $_old = {name}; {name} = {nextExpr("$_old")}; $_old }}"
          : $"do {{ {name} = {nextExpr(name)}; {name} }}";
      }
      var load = (IrArrayLoad)step.Target;
      var arr = this.EmitExpr(load.Array);
      var idx = this.EmitExpr(load.Index);
      return step.Postfix
        ? $"do {{ my $_a = {arr}; my $_i = {idx}; die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i > $#$_a; my $_old = $_a->[$_i]; $_a->[$_i] = {nextExpr("$_old")}; $_old }}"
        : $"do {{ my $_a = {arr}; my $_i = {idx}; die \"IndexOutOfRange: index $_i\\n\" if $_i < 0 || $_i > $#$_a; $_a->[$_i] = {nextExpr("$_a->[$_i]")}; $_a->[$_i] }}";
    }

    private string EmitScalarIntrinsic(IrIntrinsic intrinsic) {
      var args = intrinsic.Arguments.Select(this.EmitExpr).ToArray();
      var type = intrinsic.Type.Integral;
      switch (intrinsic.Op) {
        case IrIntrinsicOp.Abs when type.IsFloat:
          return this.RoundFloat(type, $"abs({args[0]})");
        case IrIntrinsicOp.Abs when !type.IsSigned:
          return args[0];
        case IrIntrinsicOp.Abs:
          return $"do {{ my $_v = {args[0]}; $_v == {Minimum(type)} ? die(\"Overflow: abs outside {type}\\n\") : abs($_v) }}";
        case IrIntrinsicOp.Min when !type.IsFloat:
          return BindBinary(args[0], args[1], "($_l < $_r ? $_l : $_r)");
        case IrIntrinsicOp.Max when !type.IsFloat:
          return BindBinary(args[0], args[1], "($_l > $_r ? $_l : $_r)");
        case IrIntrinsicOp.Min:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, BindBinary(args[0], args[1],
              "(POSIX::isnan($_l) ? $_l : POSIX::isnan($_r) ? $_r : $_l < $_r ? $_l : $_r < $_l ? $_r : POSIX::signbit($_l) ? $_l : $_r)"));
          }
          return this.RoundFloat(type, BindBinary(args[0], args[1],
            $"($_l != $_l ? $_l : $_r != $_r ? $_r : $_l < $_r ? $_l : $_r < $_l ? $_r : {this.HelperCall(PerlHelpers.FloatSignBit)}($_l) ? $_l : $_r)"));
        case IrIntrinsicOp.Max:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, BindBinary(args[0], args[1],
              "(POSIX::isnan($_l) ? $_l : POSIX::isnan($_r) ? $_r : $_l > $_r ? $_l : $_r > $_l ? $_r : POSIX::signbit($_r) ? $_l : $_r)"));
          }
          return this.RoundFloat(type, BindBinary(args[0], args[1],
            $"($_l != $_l ? $_l : $_r != $_r ? $_r : $_l > $_r ? $_l : $_r > $_l ? $_r : {this.HelperCall(PerlHelpers.FloatSignBit)}($_r) ? $_l : $_r)"));
        case IrIntrinsicOp.Sign when intrinsic.Arguments[0].Type.IsFloat:
          return $"do {{ my $_v = {args[0]}; die \"ArithmeticException: NaN has no sign\\n\" if $_v != $_v; $_v < 0 ? -1 : $_v > 0 ? 1 : 0 }}";
        case IrIntrinsicOp.Sign:
          return $"do {{ my $_v = {args[0]}; $_v < 0 ? -1 : $_v > 0 ? 1 : 0 }}";
        case IrIntrinsicOp.Sqrt:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, $"do {{ my $_v = {args[0]}; POSIX::isnan($_v) || $_v < 0 ? POSIX::NAN() : sqrt($_v) }}");
          }
          return this.RoundFloat(type, $"do {{ my $_v = {args[0]}; $_v != $_v || $_v < 0 ? unpack('d', pack('Q', 9221120237041090560)) : sqrt($_v) }}");
        case IrIntrinsicOp.Floor:
          this._usesPosix = true;
          return this.RoundFloat(type, $"POSIX::floor({args[0]})");
        case IrIntrinsicOp.Ceiling:
          this._usesPosix = true;
          return this.RoundFloat(type, $"POSIX::ceil({args[0]})");
        case IrIntrinsicOp.Truncate:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, $"POSIX::trunc({args[0]})");
          }
          return this.RoundFloat(type, $"{this.HelperCall(PerlHelpers.FloatTrunc)}({args[0]})");
        case IrIntrinsicOp.RoundEven:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, $"POSIX::rint({args[0]})");
          }
          return this.RoundFloat(type, $"{this.HelperCall(PerlHelpers.FloatRoundEven)}({args[0]})");
        case IrIntrinsicOp.RoundAway:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, $"POSIX::round({args[0]})");
          }
          return this.RoundFloat(type, $"{this.HelperCall(PerlHelpers.FloatRoundAway)}({args[0]})");
        case IrIntrinsicOp.FloatBits:
          return intrinsic.Arguments[0].Type == IrType.F32
            ? $"unpack('l', pack('f', {args[0]}))"
            : $"unpack('q', pack('d', {args[0]}))";
        case IrIntrinsicOp.BitsFloat:
          return intrinsic.Arguments[0].Type == IrType.I32
            ? $"unpack('f', pack('l', {args[0]}))"
            : $"unpack('d', pack('q', {args[0]}))";
        case IrIntrinsicOp.IsNaN:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"(POSIX::isnan({args[0]}) ? 1 : 0)";
          }
          return $"do {{ my $_v = {args[0]}; ($_v != $_v) ? 1 : 0 }}";
        case IrIntrinsicOp.IsInfinity:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"(POSIX::isinf({args[0]}) ? 1 : 0)";
          }
          return $"do {{ my $_v = {args[0]}; ($_v == $_v && ($_v == 9**9**9 || $_v == -9**9**9)) ? 1 : 0 }}";
        case IrIntrinsicOp.IsFinite:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"(POSIX::isfinite({args[0]}) ? 1 : 0)";
          }
          return $"do {{ my $_v = {args[0]}; ($_v == $_v && $_v != 9**9**9 && $_v != -9**9**9) ? 1 : 0 }}";
        case IrIntrinsicOp.IsNegative:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"(POSIX::signbit({args[0]}) ? 1 : 0)";
          }
          return $"{this.HelperCall(PerlHelpers.FloatSignBit)}({args[0]})";
        case IrIntrinsicOp.IsNormal:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"(POSIX::isnormal({args[0]}) ? 1 : 0)";
          }
          return $"{this.HelperCall(PerlHelpers.FloatIsNormal)}({args[0]})";
        case IrIntrinsicOp.IsSubnormal:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"do {{ my $_v = {args[0]}; (POSIX::isfinite($_v) && $_v != 0 && !POSIX::isnormal($_v)) ? 1 : 0 }}";
          }
          return $"do {{ my $_v = {args[0]}; ($_v == $_v && $_v != 9**9**9 && $_v != -9**9**9 && $_v != 0 && !{this.HelperCall(PerlHelpers.FloatIsNormal)}($_v)) ? 1 : 0 }}";
        case IrIntrinsicOp.IsInteger:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return $"do {{ my $_v = {args[0]}; (POSIX::isfinite($_v) && POSIX::trunc($_v) == $_v) ? 1 : 0 }}";
          }
          return $"do {{ my $_v = {args[0]}; ($_v == $_v && $_v != 9**9**9 && $_v != -9**9**9 && {this.HelperCall(PerlHelpers.FloatTrunc)}($_v) == $_v) ? 1 : 0 }}";
        case IrIntrinsicOp.CopySign:
          if (this._profile.HasC99Posix) {
            this._usesPosix = true;
            return this.RoundFloat(type, $"POSIX::copysign({args[0]}, {args[1]})");
          }
          return this.RoundFloat(type, $"{this.HelperCall(PerlHelpers.FloatCopySign)}({args[0]}, {args[1]})");
        case IrIntrinsicOp.HighWord:
          return $"((unpack('Q', pack('q', {args[0]})) >> 32) & 0xFFFFFFFF)";
        case IrIntrinsicOp.JoinWords:
          return $"unpack('q', pack('Q', ((({args[0]}) & 0xFFFFFFFF) << 32) | (({args[1]}) & 0xFFFFFFFF)))";
        case IrIntrinsicOp.FormatDotNet or IrIntrinsicOp.FormatEcma: {
          var argType = intrinsic.Arguments[0].Type;
          if (argType.IsFloat) {
            var dotnet = intrinsic.Op == IrIntrinsicOp.FormatDotNet ? "1" : "0";
            var isF32 = argType == IrType.F32 ? "1" : "0";
            return $"{this.HelperCall(PerlHelpers.FormatFloat)}({args[0]}, {dotnet}, {isF32})";
          }
          return argType == IrType.U64 ? $"sprintf('%u', {args[0]})" : $"sprintf('%d', {args[0]})";
        }
        case IrIntrinsicOp.FormatBoolDotNet:
          return $"(({args[0]}) ? 'True' : 'False')";
        case IrIntrinsicOp.FormatBoolEcma:
          return $"(({args[0]}) ? 'true' : 'false')";
        default:
          return this.UnsupportedOp(intrinsic.Op.ToString());
      }
    }

    private static bool TryComparisonOperator(IrBinaryOp op, out string symbol) {
      symbol = op switch {
        IrBinaryOp.Equal => "==",
        IrBinaryOp.NotEqual => "!=",
        IrBinaryOp.Less => "<",
        IrBinaryOp.LessOrEqual => "<=",
        IrBinaryOp.Greater => ">",
        IrBinaryOp.GreaterOrEqual => ">=",
        _ => ""
      };
      return symbol.Length > 0;
    }

    private string InInteger(string expr)
      => this._integerFunction ? expr : $"do {{ use integer; {expr} }}";

    private static string Wrap(IrType type, string expr) {
      if (type == IrType.I64)
        return expr;
      if (type == IrType.U64)
        return $"unpack('Q', pack('Q', {expr}))";
      var masked = $"(({expr}) & {Mask(type)})";
      if (!type.IsSigned)
        return masked;
      var sign = $"0x{1UL << (type.Bits - 1):X}";
      return $"(({masked} ^ {sign}) - {sign})";
    }

    private static string FormatScalarConstant(IrType type, long value) {
      type = type.Integral;
      if (type == IrType.Bool)
        return value != 0 ? "1" : "0";
      if (!type.IsFloat)
        return FormatIntConstant(type, value);

      if (type == IrType.F32) {
        var bits = unchecked((uint)value);
        var f = BitConverter.UInt32BitsToSingle(bits);
        if (float.IsPositiveInfinity(f)) return "9**9**9";
        if (float.IsNegativeInfinity(f)) return "-9**9**9";
        if (float.IsNaN(f) || (f == 0f && (bits & 0x80000000u) != 0))
          return $"unpack('f', pack('L', {bits.ToString(CultureInfo.InvariantCulture)}))";
        return $"unpack('f', pack('L', {bits.ToString(CultureInfo.InvariantCulture)}))";
      }

      var dbits = unchecked((ulong)value);
      var d = BitConverter.UInt64BitsToDouble(dbits);
      if (double.IsPositiveInfinity(d)) return "9**9**9";
      if (double.IsNegativeInfinity(d)) return "-9**9**9";
      if (double.IsNaN(d) || (d == 0d && (dbits & 0x8000000000000000UL) != 0))
        return $"unpack('d', pack('Q', {dbits.ToString(CultureInfo.InvariantCulture)}))";
      var text = d.ToString("R", CultureInfo.InvariantCulture);
      if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e'))
        text += ".0";
      return text;
    }

    private static string FormatIntConstant(IrType type, long value)
      => type == IrType.U64
        ? unchecked((ulong)value).ToString(CultureInfo.InvariantCulture)
        : value.ToString(CultureInfo.InvariantCulture);

    private static string Mask(IrType type)
      => type.IsLong ? "18446744073709551615" : $"0x{(1UL << type.Bits) - 1:X}";

    private static string Minimum(IrType type)
      => type.IsSigned
        ? (type.IsLong ? long.MinValue : -(1L << (type.Bits - 1))).ToString(CultureInfo.InvariantCulture)
        : "0";

    private static string Maximum(IrType type)
      => type.IsSigned
        ? (type.IsLong ? long.MaxValue : (1L << (type.Bits - 1)) - 1).ToString(CultureInfo.InvariantCulture)
        : type.IsLong ? "18446744073709551615" : ((1UL << type.Bits) - 1).ToString(CultureInfo.InvariantCulture);

    private static string BindBinary(string left, string right, string body)
      => $"do {{ my ($_l, $_r) = ({left}, {right}); {body} }}";

  }

}
