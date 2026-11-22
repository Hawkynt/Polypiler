using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Parsing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private static readonly HashSet<string> _unaryScalarOps = new(StringComparer.Ordinal) {
    "negate", "i_negate", "complement", "ncomplement", "int", "abs", "sqrt"
  };

  private static readonly HashSet<string> _incDecOps = new(StringComparer.Ordinal) {
    "preinc", "postinc", "predec", "postdec", "i_preinc", "i_postinc", "i_predec", "i_postdec"
  };

  private static readonly HashSet<string> _stringCmpOps = new(StringComparer.Ordinal) {
    "seq", "sne", "slt", "sgt", "sle", "sge", "scmp"
  };

  private static readonly HashSet<string> _listMutOps = new(StringComparer.Ordinal) {
    "push", "pop", "shift", "unshift"
  };

  private static readonly HashSet<string> _unsupportedStringOps = new(StringComparer.Ordinal) {
    "match", "subst", "trans", "qr", "sprintf", "prtf", "split", "lc", "uc", "index", "rindex"
  };

  private static readonly HashSet<string> _unsupportedScalarOps = new(StringComparer.Ordinal) {
    "pow", "sin", "cos", "atan2", "exp", "log", "hex", "oct", "rand", "srand"
  };

  private static readonly Dictionary<string, IrBinaryOp> _binaryOps = new(StringComparer.Ordinal) {
    ["add"] = IrBinaryOp.Add, ["subtract"] = IrBinaryOp.Subtract, ["multiply"] = IrBinaryOp.Multiply,
    ["divide"] = IrBinaryOp.Divide, ["modulo"] = IrBinaryOp.Remainder,
    ["bit_and"] = IrBinaryOp.And, ["nbit_and"] = IrBinaryOp.And,
    ["bit_or"] = IrBinaryOp.Or, ["nbit_or"] = IrBinaryOp.Or,
    ["bit_xor"] = IrBinaryOp.Xor, ["nbit_xor"] = IrBinaryOp.Xor,
    ["lt"] = IrBinaryOp.Less, ["le"] = IrBinaryOp.LessOrEqual,
    ["gt"] = IrBinaryOp.Greater, ["ge"] = IrBinaryOp.GreaterOrEqual,
    ["eq"] = IrBinaryOp.Equal, ["ne"] = IrBinaryOp.NotEqual
  };

  private static readonly Dictionary<string, Func<PerlLowerer, PerlOp, IrExpr>> _exprHandlers = new(StringComparer.Ordinal) {
    ["scalar"] = (l, n) => l.LowerScalarContext(n.Children[0]),
    ["const"] = (l, n) => l.LowerConstant(n.Constant!),
    ["undef"] = (_, _) => new IrNone(IrType.Optional(IrType.I64)),
    ["padsv"] = (l, n) => l.LowerPadSvRead(n.Target),
    ["padav"] = (l, n) => l.LowerPadAvRead(n.Target),
    ["padhv"] = (l, n) => l.LowerPadHvRead(n.Target),
    ["gvsv"] = (l, n) => l.LowerGlobalScalarRead(n.Symbol),
    ["not"] = (l, n) => new IrUnary(IrUnaryOp.LogicalNot, l.Truth(l.LiftExpr(n.Children[0]))),
    ["cond_expr"] = (l, n) => l.LowerConditional(n.Children[0], n.Children[1], n.Children[2]),
    ["and"] = (l, n) => l.LowerLogical("and", n.Children[0], n.Children[1]),
    ["or"] = (l, n) => l.LowerLogical("or", n.Children[0], n.Children[1]),
    ["dor"] = (l, n) => l.LowerLogical("dor", n.Children[0], n.Children[1]),
    ["xor"] = (l, n) => l.LowerLogical("xor", n.Children[0], n.Children[1]),
    ["defined"] = (l, n) => l.LowerDefined(n),
    ["exists"] = (l, n) => l.LowerExists(n),
    ["delete"] = (l, n) => l.LowerDelete(n),
    ["multideref"] = (l, n) => n.DerefOp == "exists" ? l.LowerExists(n) : n.DerefOp == "delete" ? l.LowerDelete(n) : l.LowerMultiDerefRead(n),
    ["aelem"] = (l, n) => l.LowerArrayElemRead(n),
    ["aelemfast"] = (l, n) => l.LowerArrayElemRead(n),
    ["aelemfast_lex"] = (l, n) => l.LowerArrayElemRead(n),
    ["helem"] = (l, n) => l.LowerHashElemRead(n),
    ["anonlist"] = (l, n) => l.LowerAnonList(n),
    ["emptyavhv"] = (_, _) => new IrIntrinsic(IrIntrinsicOp.ArrayOf, IrType.Array(IrType.I64), []),
    ["anonhash"] = (l, n) => l.LowerAnonHash(n),
    ["srefgen"] = (l, n) => l.LowerRefGen(n),
    ["refgen"] = (l, n) => l.LowerRefGen(n),
    ["rv2sv"] = (l, n) => l.LowerScalarDeref(n),
    ["rv2av"] = (l, n) => l.LowerArrayDeref(n),
    ["rv2hv"] = (l, n) => l.LowerPadSvRead(n.Children[0].Target),
    ["av2arylen"] = (l, n) => l.LowerLastIndex(n),
    ["length"] = (l, n) => l.LowerLength(n),
    ["concat"] = (l, n) => l.LowerConcat(n),
    ["multiconcat"] = (l, n) => l.LowerConcat(n),
    ["repeat"] = (l, n) => l.LowerRepeat(n),
    ["substr"] = (l, n) => l.LowerSubstr(n),
    ["substr_left"] = (l, n) => l.LowerSubstr(n),
    ["ord"] = (l, n) => l.LowerOrd(n),
    ["chr"] = (l, n) => l.LowerChr(n),
    ["vec"] = (l, n) => l.LowerVec(n),
    ["pack"] = (l, n) => l.LowerPack(n),
    ["unpack"] = (l, n) => l.LowerUnpack(n),
    ["join"] = (l, n) => l.LowerJoin(n),
    ["reverse"] = (l, n) => l.LowerReverse(n),
    ["sort"] = (l, n) => l.LowerSort(n),
    ["entersub"] = (l, n) => l.LowerCall(n),
    ["wantarray"] = (l, _) => new IrConst(IrType.Bool, l._contracts.ContextOf(l._currentPackage, l._currentSubName) == "list" ? 1 : 0),
    ["leave"] = (l, n) => l.LowerDoBlockExpr(n),
    ["scope"] = (l, n) => l.LowerDoBlockExpr(n),
    ["lineseq"] = (l, n) => l.LowerDoBlockExpr(n),
    ["leavetry"] = (l, n) => l.LowerEvalExpr(n),
    ["anoncode"] = (l, n) => l.LowerClosureExpr(n.Code!),
    ["keys"] = (l, n) => l.LowerHashInspection(n),
    ["values"] = (l, n) => l.LowerHashInspection(n),
    ["each"] = (l, n) => l.LowerHashInspection(n),
    ["bless"] = (l, _) => throw l.Fail(PerlFrontend.ObjectSystemUnsupported, "bless", cause: "OBJ-CLASSES"),
    ["splice"] = (l, _) => throw l.Fail(PerlFrontend.ArrayUnsupported, "splice"),
    ["ref"] = (l, _) => throw l.Fail(PerlFrontend.ReferenceUnsupported, "ref()"),
    ["entereval"] = (l, _) => throw l.Fail(PerlFrontend.ErrorHandlingUnsupported, "eval STRING"),
    ["warn"] = (l, _) => throw l.Fail(PerlFrontend.ErrorHandlingUnsupported, "warn"),
    ["exit"] = (l, _) => throw l.Fail(PerlFrontend.ErrorHandlingUnsupported, "exit"),
    ["tie"] = (l, n) => throw l.Fail(PerlFrontend.MagicUnsupported, n.Name),
    ["untie"] = (l, n) => throw l.Fail(PerlFrontend.MagicUnsupported, n.Name),
    ["tied"] = (l, n) => throw l.Fail(PerlFrontend.MagicUnsupported, n.Name)
  };

  private IrExpr LiftExpr(PerlOp node) {
    if (node.Name == "null" && StripMarks(node.Children) is [var singleInner])
      return this.LiftExpr(singleInner);

    if (_exprHandlers.TryGetValue(node.Name, out var handler))
      return handler(this, node);
    if (_unaryScalarOps.Contains(node.Name))
      return this.LowerUnaryScalar(node.Name, node.Children[0]);
    if (_incDecOps.Contains(node.Name))
      return this.LowerIncDec(node.Name, node.Children[0]);
    if (_stringCmpOps.Contains(node.Name))
      return this.LowerStringCompare(node.Name, node.Children[0], node.Children[1]);
    if (_listMutOps.Contains(node.Name))
      return this.LowerListMutation(node);
    if (_unsupportedStringOps.Contains(node.Name))
      throw this.Fail(PerlFrontend.StringUnsupported, node.Name);
    if (_unsupportedScalarOps.Contains(node.Name))
      throw this.Fail(PerlFrontend.ScalarUnsupported, node.Name);
    if (IsNumericBinaryOp(node.Name))
      return this.LowerNumericBinary(node.Name, node.Children[0], node.Children[1]);

    throw this.Fail(PerlFrontend.ScalarUnsupported, $"expression op {node.Name}");
  }

  private IrExpr LowerConstant(PerlLiteral lit) {
    if (lit.Kind == "integer") {
      if (long.TryParse(lit.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        return new IrConst(s is >= int.MinValue and <= int.MaxValue ? IrType.I32 : IrType.I64, s);
      if (ulong.TryParse(lit.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var u))
        return new IrConst(IrType.U64, unchecked((long)u));
    }
    if (lit.Kind == "float" && lit.Value.Length == 16)
      return new IrConst(IrType.F64, BinaryPrimitives.ReadInt64LittleEndian(Convert.FromHexString(lit.Value)));
    if (lit.Kind == "string") {
      if (lit.Wide != 0 || lit.Value.Any(c => c > 0xFF))
        throw this.Fail(PerlFrontend.StringUnsupported, "wide character string literal");
      return new IrStrConst(lit.Value);
    }
    if (lit.Kind == "undef")
      return new IrNone(IrType.Optional(IrType.I64));

    throw this.Fail(PerlFrontend.ScalarUnsupported, $"literal kind {lit.Kind}");
  }

  private IrExpr LowerPadSvRead(int slot) {
    if (this._variables.TryGetValue(slot, out var local))
      return new IrLocalRef(local);
    throw this.Fail(PerlFrontend.ScalarUnsupported, $"uninitialized scalar lexical slot {slot}");
  }

  private IrExpr LowerGlobalScalarRead(string? symbol) {
    if (symbol == "_")
      return this._topicVar is not null
        ? new IrLocalRef(this._topicVar)
        : throw this.Fail(PerlFrontend.ControlUnsupported, "$_ outside loop");
    if (symbol == "@")
      return this._evalErrorVar is not null
        ? new IrLocalRef(this._evalErrorVar)
        : new IrStrConst("");
    if (symbol is not null && _magicGlobals.Contains(symbol))
      throw this.Fail(PerlFrontend.MagicUnsupported, $"${symbol}");
    throw this.Fail(PerlFrontend.MagicUnsupported, $"global scalar ${symbol}");
  }

  private IrExpr LowerConditional(PerlOp test, PerlOp yes, PerlOp no) {
    var cond = this.Truth(this.LiftExpr(test));
    var t = this.LiftExpr(yes);
    var f = this.LiftExpr(no);
    if (t.Type == f.Type)
      return new IrConditional(cond, t, f);
    if (t is IrNone && f.Type.IsOptionalValue)
      return new IrConditional(cond, new IrNone(IrType.Optional(f.Type)), new IrSome(f));
    if (f is IrNone && t.Type.IsOptionalValue)
      return new IrConditional(cond, new IrSome(t), new IrNone(IrType.Optional(t.Type)));
    var common = this._bounds.CommonType(t, f, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
    return new IrConditional(cond, PerlBounds.Promote(t, common), PerlBounds.Promote(f, common));
  }

  private IrExpr LowerLogical(string name, PerlOp leftOp, PerlOp rightOp) {
    var left = this.LiftExpr(leftOp);
    var right = this.LiftExpr(rightOp);
    if (name == "xor")
      return new IrBinary(IrBinaryOp.NotEqual, this.Truth(left), this.Truth(right));
    if (name == "dor") {
      if (left.Type.IsOptional)
        return new IrCoalesce(left, this.ToType(right, left.Type.Underlying!));
      return left;
    }
    var common = this._bounds.CommonType(left, right, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
    var a = PerlBounds.Promote(left, common);
    var b = PerlBounds.Promote(right, common);
    return name == "and"
      ? new IrConditional(this.Truth(left), b, a)
      : new IrConditional(this.Truth(left), a, b);
  }

  private IrExpr LowerUnaryScalar(string name, PerlOp operandOp) {
    var val = this.LiftExpr(operandOp);
    if (name is "complement" or "ncomplement") {
      if (!val.Type.IsInteger)
        throw this.Fail(PerlFrontend.ScalarUnsupported, "complement of non-integer");
      var target = this._integer ? IrType.I64 : IrType.U64;
      return new IrUnary(IrUnaryOp.BitNot, PerlBounds.Promote(val, target));
    }
    if (name is "negate" or "i_negate") {
      if (val.Type.IsFloat && name != "i_negate" && !this._integer)
        return new IrUnary(IrUnaryOp.Negate, val);
      if (val.Type.IsInteger)
        return new IrUnary(IrUnaryOp.Negate, PerlBounds.Promote(val, IrType.I64));
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"negate on {val.Type}");
    }
    if (name == "int") {
      if (val.Type.IsInteger)
        return val;
      if (val.Type.IsFloat)
        return new IrConvert(IrType.I64, val, IrConversion.Saturate);
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"int() on {val.Type}");
    }
    if (name == "abs") {
      if (val.Type.IsFloat)
        return new IrIntrinsic(IrIntrinsicOp.Abs, val.Type, [val]);
      if (val.Type.IsInteger)
        return new IrIntrinsic(IrIntrinsicOp.Abs, PerlBounds.Promote(val, IrType.I64).Type, [PerlBounds.Promote(val, IrType.I64)]);
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"abs() on {val.Type}");
    }
    var f = this.ToType(val, IrType.F64);
    var cond = new IrBinary(IrBinaryOp.Less, f, new IrConst(IrType.F64, 0));
    var trap = new IrThrowExpression(IrType.F64, new IrNew(IrType.Object(IrExceptionClass.ArithmeticException),
      IrExceptionClass.Constructor(IrExceptionClass.ArithmeticException, 1), [new IrStrConst("Can't take sqrt of negative number")]));
    return new IrConditional(cond, trap, new IrIntrinsic(IrIntrinsicOp.Sqrt, IrType.F64, [f]));
  }

  private IrExpr LowerIncDec(string name, PerlOp targetOp) {
    var target = this.LiftExpr(targetOp);
    if (!target.Type.IsNumeric)
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"string {name}");
    var inc = name.EndsWith("inc", StringComparison.Ordinal);
    var post = name.Contains("post", StringComparison.Ordinal);
    return new IrStep(target, inc, post, Trap: false);
  }

  private IrExpr LowerNumericBinary(string name, PerlOp leftOp, PerlOp rightOp) {
    var left = this.LiftExpr(leftOp);
    var right = this.LiftExpr(rightOp);
    if (left.Type.IsOptional)
      left = new IrCoalesce(left, DefaultExpr(left.Type.Underlying!));
    if (right.Type.IsOptional)
      right = new IrCoalesce(right, DefaultExpr(right.Type.Underlying!));

    var isIntOp = name.StartsWith("i_", StringComparison.Ordinal);
    var spelling = isIntOp ? name[2..] : name;
    if (spelling is "left_shift" or "right_shift")
      return this.LowerShift(spelling == "left_shift", left, right);
    if (spelling == "ncmp")
      return this.LowerThreeWay(left, right, isIntOp);

    var op = _binaryOps[spelling];

    // Reference identity comparison `$r1 == $r2` (#437, #439)
    if (op is IrBinaryOp.Equal or IrBinaryOp.NotEqual && (left.Type.IsObject || right.Type.IsObject)) {
      var eq = new IrReferenceEquals(left, right);
      return op == IrBinaryOp.Equal ? eq : new IrUnary(IrUnaryOp.LogicalNot, eq);
    }

    if (isIntOp || (this._integer && op is IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor)) {
      var a = this.ToSigned64(left);
      var b = this.ToSigned64(right);
      return op is IrBinaryOp.Divide or IrBinaryOp.Remainder ? SignedDivision(op, a, b) : new IrBinary(op, a, b);
    }

    if (op is IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor) {
      if (left.Type.IsInteger && right.Type.IsInteger)
        return new IrBinary(op, PerlBounds.Promote(left, IrType.U64), PerlBounds.Promote(right, IrType.U64));
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"bitwise {name} on {left.Type} and {right.Type}");
    }

    if (op == IrBinaryOp.Divide) {
      // Outside `use integer`, Perl `/` is binary64 division trapping on zero divisor.
      var a = this.ToType(left, IrType.F64);
      var b = this.ToType(right, IrType.F64);
      var zeroCheck = new IrBinary(IrBinaryOp.Equal, b, new IrConst(IrType.F64, 0));
      var trap = new IrThrowExpression(IrType.F64, new IrNew(IrType.Object(IrExceptionClass.DivideByZeroException),
        IrExceptionClass.Constructor(IrExceptionClass.DivideByZeroException, 1), [new IrStrConst("Illegal division by zero")]));
      return new IrConditional(zeroCheck, trap, new IrBinary(IrBinaryOp.Divide, a, b));
    }

    if (op == IrBinaryOp.Remainder) {
      // Outside `use integer`, Perl `%` uses floor-modulus semantics (sign of right operand).
      var a = this.ToSigned64(left);
      var b = this.ToSigned64(right);
      return new IrIntrinsic(IrIntrinsicOp.ModFloor, IrType.I64, [a, b]);
    }

    if (op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply) {
      if (left.Type.IsFloat || right.Type.IsFloat)
        return new IrBinary(op, this.ToType(left, IrType.F64), this.ToType(right, IrType.F64));
      if (left.Type.IsInteger && right.Type.IsInteger)
        return this._bounds.ExactArithmetic(op, left, right, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
    }

    if (op >= IrBinaryOp.Equal && left.Type.IsNumeric && right.Type.IsNumeric) {
      var common = this._bounds.CommonType(left, right, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
      return new IrBinary(op, PerlBounds.Promote(left, common), PerlBounds.Promote(right, common));
    }

    throw this.Fail(PerlFrontend.ScalarUnsupported, $"operation {name} on {left.Type} and {right.Type}");
  }

  private IrExpr LowerThreeWay(IrExpr left, IrExpr right, bool integer) {
    if (!left.Type.IsNumeric || !right.Type.IsNumeric)
      throw this.Fail(PerlFrontend.ScalarUnsupported, "three-way comparison on non-numeric operands");
    var type = integer ? IrType.I64 : this._bounds.CommonType(left, right, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
    var a = PerlBounds.Promote(left, type);
    var b = PerlBounds.Promote(right, type);
    return new IrConditional(new IrBinary(IrBinaryOp.Less, a, b), new IrConst(IrType.I64, -1),
      new IrConditional(new IrBinary(IrBinaryOp.Greater, a, b), new IrConst(IrType.I64, 1), new IrConst(IrType.I64, 0)));
  }

  private IrExpr LowerShift(bool leftwards, IrExpr value, IrExpr count) {
    if (!value.Type.IsInteger || !count.Type.IsInteger)
      throw this.Fail(PerlFrontend.ScalarUnsupported, "shift on non-integer operands");

    var type = this._integer ? IrType.I64 : IrType.U64;
    var countType = count.Type.IsSigned ? IrType.I64 : IrType.U64;
    var v = PerlBounds.Promote(value, type);
    var c = PerlBounds.Promote(count, countType);

    IrExpr Cmp(IrBinaryOp op, long bound) => new IrBinary(op, c, new IrConst(countType, bound));
    IrExpr Apply(bool isLeft, IrExpr amount)
      => new IrBinary(isLeft ? IrBinaryOp.ShiftLeft : IrBinaryOp.ShiftRight, v, PerlBounds.Promote(amount, IrType.I32));
    IrExpr Overshift(bool isLeft) {
      var zero = new IrConst(type, 0);
      return isLeft || !type.IsSigned
        ? zero
        : new IrConditional(new IrBinary(IrBinaryOp.Less, v, zero), new IrConst(type, -1), zero);
    }

    var forward = new IrConditional(Cmp(IrBinaryOp.GreaterOrEqual, 64), Overshift(leftwards), Apply(leftwards, c));
    if (!countType.IsSigned)
      return forward;

    var backward = new IrConditional(Cmp(IrBinaryOp.LessOrEqual, -64), Overshift(!leftwards),
      Apply(!leftwards, new IrUnary(IrUnaryOp.Negate, c)));
    return new IrConditional(Cmp(IrBinaryOp.Less, 0), backward, forward);
  }

  private IrExpr ToSigned64(IrExpr value)
    => value.Type.IsInteger ? PerlBounds.Promote(value, IrType.I64)
      : value.Type.IsFloat ? new IrConvert(IrType.I64, value, IrConversion.Saturate)
      : throw this.Fail(PerlFrontend.ScalarUnsupported, $"operand of type {value.Type} under use integer");

  private static IrExpr SignedDivision(IrBinaryOp op, IrExpr left, IrExpr right) {
    var min = new IrConst(IrType.I64, long.MinValue);
    var isMin = new IrBinary(IrBinaryOp.Equal, left, min);
    var isNegOne = new IrBinary(IrBinaryOp.Equal, right, new IrConst(IrType.I64, -1));
    return new IrConditional(new IrBinary(IrBinaryOp.LogicalAnd, isMin, isNegOne),
      op == IrBinaryOp.Divide ? min : new IrConst(IrType.I64, 0), new IrBinary(op, left, right));
  }

  private static bool IsNumericBinaryOp(string name) {
    var spelling = name.StartsWith("i_", StringComparison.Ordinal) ? name[2..] : name;
    return spelling is "left_shift" or "right_shift" or "ncmp" || _binaryOps.ContainsKey(spelling);
  }

}
