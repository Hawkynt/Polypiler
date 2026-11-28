using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Perl.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Perl.Lowering;

internal sealed partial class PerlLowerer {

  private static readonly Dictionary<string, IrBinaryOp> _binaryOps = new(StringComparer.Ordinal) {
    ["+"] = IrBinaryOp.Add, ["-"] = IrBinaryOp.Subtract, ["*"] = IrBinaryOp.Multiply,
    ["/"] = IrBinaryOp.Divide, ["%"] = IrBinaryOp.Remainder,
    ["&"] = IrBinaryOp.And, ["|"] = IrBinaryOp.Or, ["^"] = IrBinaryOp.Xor,
    ["<"] = IrBinaryOp.Less, ["<="] = IrBinaryOp.LessOrEqual,
    [">"] = IrBinaryOp.Greater, [">="] = IrBinaryOp.GreaterOrEqual,
    ["=="] = IrBinaryOp.Equal, ["!="] = IrBinaryOp.NotEqual
  };

  /// <summary>The names of perl's ops for the operators, as diagnostics name them.</summary>
  private static readonly Dictionary<string, string> _opNames = new(StringComparer.Ordinal) {
    ["+"] = "add", ["-"] = "subtract", ["*"] = "multiply", ["/"] = "divide", ["%"] = "modulo", ["**"] = "pow",
    ["<<"] = "left_shift", [">>"] = "right_shift", ["&"] = "bit_and", ["|"] = "bit_or", ["^"] = "bit_xor",
    ["<"] = "lt", ["<="] = "le", [">"] = "gt", [">="] = "ge", ["=="] = "eq", ["!="] = "ne", ["<=>"] = "ncmp",
    ["eq"] = "seq", ["ne"] = "sne", ["lt"] = "slt", ["gt"] = "sgt", ["le"] = "sle", ["ge"] = "sge", ["cmp"] = "scmp"
  };

  /// <summary>The operators <c>use integer</c> compiles into an integer op of its own (<c>i_add</c>, ...).</summary>
  private static readonly HashSet<string> _integerOps = new(StringComparer.Ordinal) {
    "+", "-", "*", "/", "%", "<", "<=", ">", ">=", "==", "!=", "<=>"
  };

  private static readonly HashSet<string> _unsupportedStringOps = new(StringComparer.Ordinal) {
    "sprintf", "printf", "split", "lc", "uc", "index", "rindex"
  };

  private static readonly HashSet<string> _unsupportedScalarOps = new(StringComparer.Ordinal) {
    "sin", "cos", "atan2", "exp", "log", "hex", "oct", "rand", "srand"
  };

  /// <summary>Lowers an expression in the context perl gives it.</summary>
  private IrExpr LiftExpr(PerlExpr node, PerlWant want) {
    if (node is not (PerlNumber or PerlString) && this.Fold(node) is { } folded)
      return this.LowerFolded(folded);

    switch (node) {
      case PerlNumber number:
        return LowerNumber(number.Value);
      case PerlString s:
        return this.LowerString(s.Value);
      case PerlInterpolation interpolation:
        return this.LowerConcatParts(this.ConcatParts(interpolation));
      case PerlList { Parenthesized: true, Items: [var only] }:
        return this.LiftExpr(only, want);
      case PerlVariable variable:
        return this.LowerVariable(variable, want);
      case PerlElement element:
        return this.LowerElementRead(element);
      case PerlDeref deref:
        return this.LowerDeref(deref, want);
      case PerlLastIndex last:
        return this.LowerLastIndex(last);
      case PerlUnary unary:
        return this.LowerUnary(unary, want);
      case PerlStep step:
        return this.LowerIncDec(step, want);
      case PerlBinary binary:
        return this.LowerBinary(binary, want);
      case PerlTernary ternary:
        return this.LowerConditional(ternary, want);
      case PerlAnonArray array:
        return this.LowerAnonList(array);
      case PerlAnonHash hash:
        return this.LowerAnonHash(hash);
      case PerlAnonSub sub:
        return this.LowerClosureExpr(sub.Sub);
      case PerlCall call:
        return this.LowerCallExpr(call, want);
      case PerlCallReference reference:
        return this.LowerCallReference(reference);
      case PerlMethodCall method:
        return this.LowerMethodCall(method);
      case PerlDoBlock block:
        return this.LowerDoBlockExpr(block.Body, want);
      case PerlEvalBlock eval:
        return this.LowerEvalExpr(eval);
      case PerlComparisonChain:
        throw this.Fail(PerlFrontend.ScalarUnsupported, "chained comparison");
      case PerlPattern pattern:
        throw this.Fail(PerlFrontend.StringUnsupported, pattern.Kind switch { "m" => "match", "qr" => "qr", "s" => "subst", _ => "trans" });
      case PerlBinary or PerlAssign:
        throw this.Fail(PerlFrontend.ScalarUnsupported, "assignment as an expression");
    }
    throw this.Fail(PerlFrontend.ScalarUnsupported, $"expression {Describe(node)}");
  }

  private static string Describe(PerlExpr node) => node switch {
    PerlAssign a => $"assignment {a.Operator}",
    PerlList => "list",
    PerlWords => "qw list",
    PerlSlice => "slice",
    PerlLocal => "local",
    PerlReadLine => "readline",
    PerlCommand => "backticks",
    PerlJump j => j.Keyword,
    PerlDeclaration => "declaration",
    _ => node.GetType().Name
  };

  private static IrExpr LowerNumber(PerlNumeric value) => value.Kind switch {
    PerlNumericKind.Integer => new IrConst(value.Integer is >= int.MinValue and <= int.MaxValue ? IrType.I32 : IrType.I64, value.Integer),
    PerlNumericKind.Unsigned => new IrConst(IrType.U64, value.Bits),
    _ => new IrConst(IrType.F64, value.Bits)
  };

  private IrExpr LowerString(string value) {
    if (value.Any(c => c > 0xFF))
      throw this.Fail(PerlFrontend.StringUnsupported, "wide character string literal");
    return new IrStrConst(value);
  }

  private IrExpr LowerVariable(PerlVariable variable, PerlWant want) {
    switch (variable.Sigil) {
      case '$':
        if (variable.Lexical is { Kind: not PerlDeclarator.Our } lex) {
          if (this.TryLocal(lex, out var local))
            return new IrLocalRef(local);
          throw this.Fail(PerlFrontend.ScalarUnsupported, $"the lexical ${variable.Name} of an enclosing scope");
        }
        return this.LowerGlobalScalarRead(variable.Name);
      case '@':
        if (variable.Lexical is { Kind: not PerlDeclarator.Our } arrayLex)
          return this.LowerPadAvRead(arrayLex, variable.Name);
        if (variable.Name == "_")
          throw this.Fail(PerlFrontend.SubUnsupported, "@_ outside parameter unpacking");
        if (_magicGlobals.Contains(variable.Name))
          throw this.Fail(PerlFrontend.MagicUnsupported, $"@{variable.Name}");
        throw this.Fail(PerlFrontend.ReferenceUnsupported, "array dereference");
      case '%':
        if (variable.Lexical is { Kind: not PerlDeclarator.Our } hashLex)
          return this.LowerPadHvRead(hashLex, variable.Name);
        if (_magicGlobals.Contains(variable.Name))
          throw this.Fail(PerlFrontend.MagicUnsupported, $"%{variable.Name}/@{variable.Name}");
        throw this.Fail(PerlFrontend.MagicUnsupported, $"global hash %{variable.Name}");
      default:
        throw this.Fail(PerlFrontend.MagicUnsupported, $"{variable.Sigil}{variable.Name}");
    }
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
    var shortName = symbol is null ? "" : ShortName(symbol);
    if (_magicGlobals.Contains(shortName))
      throw this.Fail(PerlFrontend.MagicUnsupported, $"${shortName}");
    throw this.Fail(PerlFrontend.MagicUnsupported, $"global scalar ${shortName}");
  }

  private IrExpr LowerConditional(PerlTernary ternary, PerlWant want) {
    if (this.Fold(ternary.Condition) is { } folded)
      return this.LiftExpr(folded.Truth ? ternary.Then : ternary.Else, want);
    var cond = this.Truth(this.LiftExpr(ternary.Condition, PerlWant.Scalar));
    var t = this.LiftExpr(ternary.Then, want);
    var f = this.LiftExpr(ternary.Else, want);
    if (t.Type == f.Type)
      return new IrConditional(cond, t, f);
    if (t is IrNone && f.Type.IsOptionalValue)
      return new IrConditional(cond, new IrNone(IrType.Optional(f.Type)), new IrSome(f));
    if (f is IrNone && t.Type.IsOptionalValue)
      return new IrConditional(cond, new IrSome(t), new IrNone(IrType.Optional(t.Type)));
    var common = this._bounds.CommonType(t, f, m => this.Fail(PerlFrontend.ScalarUnsupported, m));
    return new IrConditional(cond, PerlBounds.Promote(t, common), PerlBounds.Promote(f, common));
  }

  private IrExpr LowerLogical(string name, PerlExpr leftOp, PerlExpr rightOp, PerlWant want) {
    if (name is "and" or "or" or "dor" && this.Fold(leftOp) is { } folded) {
      var takeRight = name == "and" ? folded.Truth : name == "or" ? !folded.Truth : folded.Undefined;
      return takeRight ? this.LiftExpr(rightOp, want) : this.LiftExpr(leftOp, want);
    }
    var left = this.LiftExpr(leftOp, PerlWant.Scalar);
    var right = this.LiftExpr(rightOp, want);
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

  private IrExpr LowerUnary(PerlUnary unary, PerlWant want) {
    switch (unary.Operator) {
      case "!" or "not":
        if (unary.Operand is null)
          return new IrConst(IrType.Bool, 1);
        return new IrUnary(IrUnaryOp.LogicalNot, this.Truth(this.LiftExpr(unary.Operand, PerlWant.Scalar)));
      case "+":
        return this.LiftExpr(unary.Operand!, want);
      case "\\":
        return this.LowerRefGen(unary.Operand!);
      case "-":
        return this.LowerUnaryScalar(this._integer ? "i_negate" : "negate", unary.Operand!);
      case "~" or "~.":
        return this.LowerUnaryScalar(unary.Operator == "~" ? "complement" : "scomplement", unary.Operand!);
      default:
        throw this.Fail(PerlFrontend.ScalarUnsupported, $"file test {unary.Operator}");
    }
  }

  private IrExpr LowerUnaryScalar(string name, PerlExpr operandOp) {
    var val = this.LiftExpr(operandOp, PerlWant.Scalar);
    if (name is "complement" or "scomplement") {
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

  private IrExpr LowerIncDec(PerlStep step, PerlWant want) {
    var target = this.LiftExpr(step.Target, PerlWant.Scalar);
    // In void context perl turns a postfix increment into the prefix one.
    var post = !step.Prefix && want != PerlWant.Void;
    var name = (this._integer ? "i_" : "") + (post ? "post" : "pre") + (step.Increment ? "inc" : "dec");
    if (!target.Type.IsNumeric)
      throw this.Fail(PerlFrontend.ScalarUnsupported, $"string {name}");
    return new IrStep(target, step.Increment, post, Trap: false);
  }

  private IrExpr LowerBinary(PerlBinary binary, PerlWant want) {
    switch (binary.Operator) {
      case "&&" or "and":
        return this.LowerLogical("and", binary.Left, binary.Right, want);
      case "||" or "or":
        return this.LowerLogical("or", binary.Left, binary.Right, want);
      case "//":
        return this.LowerLogical("dor", binary.Left, binary.Right, want);
      case "xor" or "^^":
        return this.LowerLogical("xor", binary.Left, binary.Right, want);
      case ".":
        return this.LowerConcatParts(this.ConcatParts(binary));
      case "x":
        return this.LowerRepeat(binary);
      case "eq" or "ne" or "lt" or "gt" or "le" or "ge" or "cmp":
        return this.LowerStringCompare(_opNames[binary.Operator], binary.Left, binary.Right);
      case "**":
        throw this.Fail(PerlFrontend.ScalarUnsupported, "pow");
      case "=~" or "!~":
        throw this.Fail(PerlFrontend.StringUnsupported, "match");
      case ".." or "...":
        throw this.Fail(PerlFrontend.ScalarUnsupported, "flip-flop range");
      case "<=>" or "<<" or ">>" or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<" or "<=" or ">" or ">=" or "==" or "!=":
        return this.LowerNumericBinary(this.OpName(binary.Operator), binary.Left, binary.Right);
    }
    throw this.Fail(PerlFrontend.ScalarUnsupported, $"operator {binary.Operator}");
  }

  private string OpName(string op) => (this._integer && _integerOps.Contains(op) ? "i_" : "") + _opNames[op];

  private IrExpr LowerNumericBinary(string name, PerlExpr leftOp, PerlExpr rightOp)
    => this.LowerNumericBinary(name, this.LiftExpr(leftOp, PerlWant.Scalar), this.LiftExpr(rightOp, PerlWant.Scalar));

  private static readonly Dictionary<string, IrBinaryOp> _opByName = new(StringComparer.Ordinal) {
    ["add"] = IrBinaryOp.Add, ["subtract"] = IrBinaryOp.Subtract, ["multiply"] = IrBinaryOp.Multiply,
    ["divide"] = IrBinaryOp.Divide, ["modulo"] = IrBinaryOp.Remainder,
    ["bit_and"] = IrBinaryOp.And, ["bit_or"] = IrBinaryOp.Or, ["bit_xor"] = IrBinaryOp.Xor,
    ["lt"] = IrBinaryOp.Less, ["le"] = IrBinaryOp.LessOrEqual,
    ["gt"] = IrBinaryOp.Greater, ["ge"] = IrBinaryOp.GreaterOrEqual,
    ["eq"] = IrBinaryOp.Equal, ["ne"] = IrBinaryOp.NotEqual
  };

  private IrExpr LowerNumericBinary(string name, IrExpr left, IrExpr right) {
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

    var op = _opByName[spelling];

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
      var a = this.ToType(left, IrType.F64);
      var b = this.ToType(right, IrType.F64);
      var zeroCheck = new IrBinary(IrBinaryOp.Equal, b, new IrConst(IrType.F64, 0));
      var trap = new IrThrowExpression(IrType.F64, new IrNew(IrType.Object(IrExceptionClass.DivideByZeroException),
        IrExceptionClass.Constructor(IrExceptionClass.DivideByZeroException, 1), [new IrStrConst("Illegal division by zero")]));
      return new IrConditional(zeroCheck, trap, new IrBinary(IrBinaryOp.Divide, a, b));
    }

    if (op == IrBinaryOp.Remainder) {
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

}
