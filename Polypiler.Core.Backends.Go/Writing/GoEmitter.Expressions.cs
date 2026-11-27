using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Polypiler.Core.Backends.Go.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Go;

public static partial class GoEmitter {

  private sealed partial class Writer {

    /// <summary>The statements an expression needs before it: Go has no conditional expression and no step as a value.</summary>
    private List<GoStmt> _prelude = [];

    /// <summary>Writes an expression with a prelude of its own: the value and the statements it needs before it.</summary>
    private (GoExpr Value, List<GoStmt> Prelude) Capture(IrExpr expression) {
      var saved = this._prelude;
      this._prelude = [];
      try {
        var value = this.Expression(expression);
        return (value, this._prelude);
      } finally {
        this._prelude = saved;
      }
    }

    private static GoIdent Name(string name) => new(name);

    private static GoSelector Member(string package, string name) => new(new GoIdent(package), name);

    private static GoCall Call(GoExpr callee, params GoExpr[] arguments) => new(callee, arguments);

    /// <summary>A conversion to a Go type.</summary>
    private GoCall Conversion(IrType type, GoExpr value) => new(this.TypeOf(type), [value]) { Type = type };

    /// <summary>Whether an expression of the tree is an untyped constant of Go: a literal, or a unary minus in front of one.</summary>
    private static bool IsUntyped(GoExpr expression) => expression is GoInt or GoFloat or GoUnary { Operand: GoInt or GoFloat };

    /// <summary>An expression with the Go type of its IR type also where Go would give a constant another: an untyped literal is converted.</summary>
    private GoExpr Typed(GoExpr expression, IrType type) => IsUntyped(expression) ? this.Conversion(type, expression) : expression;

    /// <summary>A temporary of the function, declared with its value in the prelude.</summary>
    private GoIdent Hoist(GoExpr value, IrType type) {
      var name = this.Temporary();
      this._prelude.Add(IsUntyped(value)
        ? new GoVarStmt(new GoValueSpec([name], this.TypeOf(type), [value]))
        : new GoAssign([Name(name)], ":=", [value]));
      return Name(name);
    }

    /// <summary>
    /// Writes an expression. One whose operands are all constants is folded first, with the IR's own
    /// semantics, since Go evaluates a constant expression exactly at compile time and refuses one that
    /// overflows its type or divides by zero.
    /// </summary>
    private GoExpr Expression(IrExpr expression) {
      if (expression is IrUnary or IrBinary or IrConvert && IsConstant(expression) && Fold(this._module, expression) is { } folded)
        expression = folded;

      var written = this.Written(expression);
      return written.Type is null ? written with { Type = expression.Type } : written;
    }

    private GoExpr Written(IrExpr expression) {
      return expression switch {
        IrConst constant => this.Literal(constant.Type, constant.Value),
        IrStrConst text => this.Text(text.Value),
        IrConstRef reference => Name(this._constants[reference.Constant.Name]),
        IrLocalRef local => Name(this.LocalName(local.Local)),
        IrGlobalRef global => Name(this._globals[global.Global]),
        IrUnary unary => this.Unary(unary),
        IrBinary binary => this.Binary(binary),
        IrConvert conversion => this.Convert(conversion),
        IrConditional choice => this.Conditional(choice),
        IrArrayLoad load => this.Load(load),
        IrArrayLength length => this.Conversion(IrType.I32, Call(Name("len"), this.Expression(length.Array))),
        IrNewArray creation => new GoCall(Name("make"), [this.TypeOf(creation.Type), this.Expression(creation.Length)]) { Type = creation.Type },
        IrCall call => this.CallOf(call),
        IrStep step => this.Step(step),
        IrStructNew creation => this.StructNew(creation),
        IrStructDefault value => new GoComposite(this.TypeOf(value.Type), []) { Type = value.Type },
        IrFieldLoad load => new GoSelector(this.Expression(load.Target), this.FieldName(load.Field)) { Type = load.Type },
        IrStructEquals equality => this.StructEquals(equality),
        IrIntrinsic intrinsic => this.Intrinsic(intrinsic),
        IrLambda lambda => this.Lambda(lambda),
        IrInvoke invoke => this.Invoke(invoke),
        IrFunctionRef reference when this._functions.TryGetValue(reference.Function, out var function) => Name(function),
        IrBind bind => this.Bind(bind),
        IrNull { Type.IsFunction: true } => Name("nil"),
        IrThrowExpression thrown => new GoCall(new GoFuncLit(new GoFuncType([], [new GoField([], this.TypeOf(thrown.Type))]), new GoBlock([this.Panic(thrown.Exception)])), []),
        _ => this.Inexpressible(KindOf(expression))
      };
    }

    /// <summary>Whether an expression is made of constants and the operators Go folds at compile time.</summary>
    private static bool IsConstant(IrExpr expression) => expression switch {
      IrConst or IrStrConst => true,
      IrConstRef reference => !reference.Constant.Type.IsArray,
      IrUnary unary => IsConstant(unary.Operand),
      IrBinary binary => IsConstant(binary.Left) && IsConstant(binary.Right),
      IrConvert conversion => (conversion.Type.IsNumeric || conversion.Type == IrType.Bool) && IsConstant(conversion.Operand),
      _ => false
    };

    /// <summary>The value of a constant expression as the interpreter computes it, or <see langword="null"/> where it traps or is no number, truth value or string.</summary>
    private static IrExpr? Fold(IrModule module, IrExpr expression) {
      var function = new IrFunction("fold", true, expression.Type, [], [], new IrBlock([new IrReturn(expression)]));
      object? value;
      try {
        value = IrInterpreter.Call(module with { Functions = [function], Globals = [], Implementations = [], Initializer = null }, "fold", []);
      } catch (IrTrapException) {
        return null;
      }

      var type = expression.Type;
      return value switch {
        bool truth => new IrConst(IrType.Bool, truth ? 1 : 0),
        string text => new IrStrConst(text),
        float single => new IrConst(type, IrOperators.Bits(type, single)),
        double number => new IrConst(type, IrOperators.Bits(type, number)),
        sbyte or byte or short or ushort or int or uint or long or char => new IrConst(type, IrOperators.Wrap(type, System.Convert.ToInt64(value, CultureInfo.InvariantCulture))),
        ulong word => new IrConst(type, unchecked((long)word)),
        _ => null
      };
    }

    /// <summary>A constant of a type as Go writes it: a literal, or the call of <c>math</c> that makes an infinity, a NaN or the negative zero.</summary>
    private GoExpr Literal(IrType type, long value) {
      if (type == IrType.Bool)
        return new GoIdent(value != 0 ? "true" : "false") { Type = type };

      if (type.IsFloat)
        return this.FloatLiteral(type, value);

      if (!type.IsInteger) {
        this.Refuse(TypeUnsupportedDescriptor, $"type '{type}' has no Go representation in profile '{profile.Name}'");
        return Name("nil");
      }

      if (type.IsSigned)
        return value < 0
          ? new GoUnary("-", new GoInt(-(BigInteger)value)) { Type = type }
          : new GoInt(value) { Type = type };

      var unsigned = type == IrType.U64 ? (ulong)value : (ulong)value & (ulong)IrOperators.Maximum(type);
      return new GoInt(unsigned, unsigned >= 0x10000) { Type = type };
    }

    private GoExpr FloatLiteral(IrType type, long bits) {
      var number = IrOperators.Float(type, bits);
      GoExpr Single(GoExpr value) => type == IrType.F32 ? this.Conversion(IrType.F32, value) : value;
      if (double.IsNaN(number))
        return type == IrType.F32
          ? new GoCall(Member("math", "Float32frombits"), [new GoInt((uint)bits, true)]) { Type = type }
          : new GoCall(Member("math", "Float64frombits"), [new GoInt((ulong)bits, true)]) { Type = type };

      if (double.IsInfinity(number))
        return Single(Call(Member("math", "Inf"), number > 0 ? new GoInt(1) : new GoUnary("-", new GoInt(1))));

      if (number == 0 && double.IsNegative(number))
        return Single(Call(Member("math", "Copysign"), new GoInt(0), new GoUnary("-", new GoInt(1))));

      var magnitude = Math.Abs(number);
      var text = (type == IrType.F32 ? ((float)magnitude).ToString("R", CultureInfo.InvariantCulture) : magnitude.ToString("R", CultureInfo.InvariantCulture))
        .Replace("E", "e", StringComparison.Ordinal);
      // An integral number keeps a point, so that the literal reads as a floating-point one.
      if (!text.Contains('.') && !text.Contains('e'))
        text += ".0";

      GoExpr literal = new GoFloat(text) { Type = type };
      return number < 0 ? new GoUnary("-", literal) { Type = type } : literal;
    }

    /// <summary>A string literal; a lone surrogate has no form in a Go string, which holds UTF-8.</summary>
    private GoExpr Text(string value) {
      for (var index = 0; index < value.Length; ++index)
        if (char.IsSurrogate(value[index])) {
          if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) {
            ++index;
            continue;
          }

          this.Refuse(InexpressibleDescriptor, "a string with a lone surrogate cannot be written as a Go string");
          break;
        }

      return new GoString(value) { Type = IrType.String };
    }

    /// <summary>
    /// The operands of a node, left to right, in the order the IR evaluates them: Go evaluates the
    /// calls of an expression in lexical order, but not the reads of an element, a field or a global
    /// around them, nor the statements an operand needs. Where a later operand calls or needs
    /// statements, an earlier one that reads what that could change is taken into a temporary first.
    /// </summary>
    private GoExpr[] Operands(params IReadOnlyList<IrExpr> operands) {
      var written = operands.Select(this.Capture).ToArray();
      var results = new GoExpr[operands.Count];
      for (var index = 0; index < operands.Count; ++index) {
        this._prelude.AddRange(written[index].Prelude);
        var value = written[index].Value;
        var later = Enumerable.Range(index + 1, operands.Count - index - 1);
        var hoist = !IsUntyped(value) && operands[index] is not IrConst and not IrConstRef
          && (later.Any(next => written[next].Prelude.Count > 0)
            || (later.Any(next => Calls(operands[next])) && ReadsState(operands[index])));
        results[index] = hoist ? this.Hoist(value, operands[index].Type) : value;
      }

      return results;
    }

    /// <summary>Whether an expression calls a function of the module or a helper that may trap: what could change state or trap before an earlier read.</summary>
    private static bool Calls(IrExpr expression) => IrTree.Nodes(expression).Any(node => node switch {
      IrCall or IrStep or IrInvoke or IrBind => true,
      IrBinary { Op: IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked or IrBinaryOp.Divide or IrBinaryOp.Remainder } binary => binary.Left.Type.IsInteger,
      IrUnary { Op: IrUnaryOp.NegateChecked } => true,
      IrConvert { Contract: not IrConversion.Wrap } => true,
      _ => false
    });

    /// <summary>Whether an expression reads something a call could change, or traps where it reads: an element, a field, a global.</summary>
    private static bool ReadsState(IrExpr expression) => IrTree.Nodes(expression).Any(node => node is IrArrayLoad or IrFieldLoad or IrGlobalRef or IrArrayLength);

    private GoExpr Unary(IrUnary unary) {
      switch (unary.Op) {
        case IrUnaryOp.NegateChecked when unary.Type.IsInteger:
          return this.CallHelper(GoHelpers.Negated(unary.Type), unary.Type, this.Expression(unary.Operand));
        case IrUnaryOp.Negate or IrUnaryOp.NegateChecked:
          return new GoUnary("-", this.Expression(unary.Operand)) { Type = unary.Type };
        case IrUnaryOp.BitNot:
          return new GoUnary("^", this.Expression(unary.Operand)) { Type = unary.Type };
        default:
          return Not(this.Expression(unary.Operand));
      }
    }

    /// <summary>The negation of a truth value: a comparison of integers or truth values reversed, a negation removed, otherwise <c>!</c>.</summary>
    private static GoExpr Not(GoExpr value) {
      if (value is GoUnary { Op: "!" } negation)
        return negation.Operand;

      if (value is GoBinary { Left.Type: { IsFloat: false } } comparison && comparison.Left.Type is not null
        && Reversed(comparison.Op) is { } reversed)
        return comparison with { Op = reversed };

      return new GoUnary("!", value) { Type = IrType.Bool };
    }

    private static string? Reversed(string op) => op switch {
      "==" => "!=",
      "!=" => "==",
      "<" => ">=",
      ">=" => "<",
      ">" => "<=",
      "<=" => ">",
      _ => null
    };

    private static string? OperatorOf(IrBinaryOp op) => op switch {
      IrBinaryOp.Add => "+",
      IrBinaryOp.Subtract => "-",
      IrBinaryOp.Multiply => "*",
      IrBinaryOp.Divide => "/",
      IrBinaryOp.Remainder => "%",
      IrBinaryOp.And => "&",
      IrBinaryOp.Or => "|",
      IrBinaryOp.Xor => "^",
      IrBinaryOp.Equal => "==",
      IrBinaryOp.NotEqual => "!=",
      IrBinaryOp.Less => "<",
      IrBinaryOp.LessOrEqual => "<=",
      IrBinaryOp.Greater => ">",
      IrBinaryOp.GreaterOrEqual => ">=",
      IrBinaryOp.LogicalAnd => "&&",
      IrBinaryOp.LogicalOr => "||",
      _ => null
    };

    private GoExpr Binary(IrBinary binary) {
      var type = binary.Left.Type;
      if (binary.Op is IrBinaryOp.LogicalAnd or IrBinaryOp.LogicalOr)
        return this.ShortCircuit(binary);

      if (type.IsBigInt || !(type.IsNumeric || type == IrType.Bool || (type.IsString && binary.Op is IrBinaryOp.Equal or IrBinaryOp.NotEqual)))
        return this.Inexpressible($"{binary.Op} of {type}");

      switch (binary.Op) {
        case IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked or IrBinaryOp.MultiplyChecked when type.IsInteger: {
          var operands = this.Operands(binary.Left, binary.Right);
          return this.CallHelper(GoHelpers.Checked(binary.Op, type), type, operands);
        }
        case IrBinaryOp.Divide or IrBinaryOp.Remainder when type.IsInteger:
          return this.Division(binary);
        case IrBinaryOp.Remainder: {
          var operands = this.Operands(binary.Left, binary.Right);
          return this.Wide(type, wide => Call(Member("math", "Mod"), wide(operands[0]), wide(operands[1])));
        }
        case IrBinaryOp.ShiftLeft or IrBinaryOp.ShiftRight or IrBinaryOp.ShiftRightLogical:
          return this.Shift(binary);
        case IrBinaryOp.And when binary.Right is IrUnary { Op: IrUnaryOp.BitNot } complement: {
          var operands = this.Operands(binary.Left, complement.Operand);
          return new GoBinary(operands[0], "&^", operands[1]) { Type = type };
        }
      }

      // A comparison of two values widened from one type compares the values themselves.
      if (binary.Op >= IrBinaryOp.Equal && binary.Left is IrConvert { Contract: IrConversion.Wrap } wideLeft && binary.Right is IrConvert { Contract: IrConversion.Wrap } wideRight
        && wideLeft.Operand.Type == wideRight.Operand.Type && wideLeft.Operand.Type.IsInteger && type.IsInteger && IrOperators.Contains(type, wideLeft.Operand.Type))
        return this.Binary(binary with { Left = wideLeft.Operand, Right = wideRight.Operand });

      var op = OperatorOf(binary.Op) ?? (binary.Op switch {
        IrBinaryOp.AddChecked => "+",
        IrBinaryOp.SubtractChecked => "-",
        _ => "*"
      });
      var (left, right) = this.Operands(binary.Left, binary.Right) is var pair ? (pair[0], pair[1]) : default;
      // Go may fuse x*y + z into one rounding; the explicit conversion of the product forbids it.
      if (type.IsFloat && binary.Op is IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.AddChecked or IrBinaryOp.SubtractChecked) {
        left = this.Barrier(binary.Left, left);
        right = this.Barrier(binary.Right, right);
      }

      return new GoBinary(left, op, right) { Type = binary.Type };
    }

    /// <summary>A product as the operand of a sum or difference of floating-point numbers, converted to its own type so that Go rounds it before the sum.</summary>
    private GoExpr Barrier(IrExpr operand, GoExpr written)
      => operand is IrBinary { Op: IrBinaryOp.Multiply or IrBinaryOp.MultiplyChecked } && written is GoBinary { Op: "*" } ? this.Conversion(operand.Type, written) : written;

    /// <summary>
    /// A floating-point operation Go has only on <c>float64</c>: on an <c>f32</c> the operands are
    /// widened and the result narrowed, which is exact for the operations the backend writes this way.
    /// </summary>
    private GoExpr Wide(IrType type, Func<Func<GoExpr, GoExpr>, GoExpr> operation) {
      if (type == IrType.F64)
        return operation(value => value) with { Type = type };

      return this.Conversion(IrType.F32, operation(value => IsUntyped(value) ? value : this.Conversion(IrType.F64, value)));
    }

    /// <summary>
    /// The integer division and remainder: Go's own where it does what the IR does - an unsigned one,
    /// or a signed one by a constant that is neither 0 nor -1 - and otherwise the helper, which traps on
    /// the quotient of the smallest value by -1 that Go wraps, and whose divisor is no constant, which
    /// Go would refuse to divide by when it is 0.
    /// </summary>
    private GoExpr Division(IrBinary binary) {
      var type = binary.Left.Type;
      var operands = this.Operands(binary.Left, binary.Right);
      var native = binary.Right is IrConst { Value: var divisor } && divisor != 0 && (!type.IsSigned || divisor != -1)
        || (!type.IsSigned && binary.Right is not IrConst);
      if (native)
        return new GoBinary(operands[0], binary.Op == IrBinaryOp.Divide ? "/" : "%", operands[1]) { Type = type };

      return this.CallHelper(GoHelpers.Divided(binary.Op, type), type, operands);
    }

    /// <summary>
    /// A shift by the IR's masked count: a constant count is masked here, any other by <c>&amp; 31</c>
    /// or <c>&amp; 63</c>, so that Go's shift, which takes the count as it is, never sees one of the width
    /// or more, nor a negative one, which would panic. A logical shift of a signed value goes through
    /// the unsigned type of its width; a constant shifted is given its type first.
    /// </summary>
    private GoExpr Shift(IrBinary binary) {
      var type = binary.Left.Type;
      var mask = type.IsLong ? 63 : 31;
      var operands = this.Operands(binary.Left, binary.Right);
      var count = binary.Right is IrConst constant ? new GoInt(constant.Value & mask) : (GoExpr)new GoBinary(operands[1], "&", new GoInt(mask)) { Type = binary.Right.Type };
      var value = this.Typed(operands[0], type);
      if (binary.Op == IrBinaryOp.ShiftLeft)
        return new GoBinary(value, "<<", count) { Type = type };

      if (binary.Op == IrBinaryOp.ShiftRight || !type.IsSigned)
        return new GoBinary(value, ">>", count) { Type = type };

      var unsigned = type.IsLong ? IrType.U64 : IrType.U32;
      return this.Conversion(type, new GoBinary(this.Conversion(unsigned, value), ">>", count) { Type = unsigned });
    }

    /// <summary>
    /// <c>&amp;&amp;</c> and <c>||</c>; where the right operand needs statements, they run only when it
    /// is evaluated: the left operand in a temporary, the right one assigned under an <c>if</c>.
    /// </summary>
    private GoExpr ShortCircuit(IrBinary binary) {
      var left = this.Expression(binary.Left);
      var (right, prelude) = this.Capture(binary.Right);
      var op = binary.Op == IrBinaryOp.LogicalAnd ? "&&" : "||";
      if (prelude.Count == 0)
        return new GoBinary(left, op, right) { Type = IrType.Bool };

      var result = this.Hoist(left, IrType.Bool);
      var condition = binary.Op == IrBinaryOp.LogicalAnd ? (GoExpr)result : new GoUnary("!", result);
      this._prelude.Add(new GoIf(null, condition, new GoBlock([.. prelude, new GoAssign([result], "=", [right])]), null));
      return result;
    }

    /// <summary>The conditional: a variable assigned in the arm that runs, since Go has no conditional expression.</summary>
    private GoExpr Conditional(IrConditional choice) {
      var condition = this.Expression(choice.Condition);
      var (whenTrue, truePrelude) = this.Capture(choice.WhenTrue);
      var (whenFalse, falsePrelude) = this.Capture(choice.WhenFalse);
      var name = Name(this.Temporary());
      if (choice.WhenFalse is IrConst or IrConstRef or IrStrConst && falsePrelude.Count == 0) {
        this._prelude.Add(new GoVarStmt(new GoValueSpec([name.Name], this.TypeOf(choice.Type), [whenFalse])));
        this._prelude.Add(new GoIf(null, condition, new GoBlock([.. truePrelude, new GoAssign([name], "=", [whenTrue])]), null));
        return name;
      }

      this._prelude.Add(new GoVarStmt(new GoValueSpec([name.Name], this.TypeOf(choice.Type), [])));
      this._prelude.Add(new GoIf(null, condition,
        new GoBlock([.. truePrelude, new GoAssign([name], "=", [whenTrue])]),
        new GoBlockStmt(new GoBlock([.. falsePrelude, new GoAssign([name], "=", [whenFalse])]))));
      return name;
    }

    private GoExpr Load(IrArrayLoad load) {
      if (!load.Array.Type.IsArray)
        return this.Inexpressible($"ArrayLoad of {load.Array.Type}");

      var operands = this.Operands(load.Array, Unclamped(load.Index));
      return new GoIndex(operands[0], operands[1]) { Type = load.Type };
    }

    /// <summary>
    /// An index as Go takes it: a 64-bit or unsigned index that the IR clamps into 32 bits - so that
    /// a value outside them is out of range rather than wrapped into it - is Go's index as it is, since
    /// Go indexes with any integer type and panics on every value outside the slice.
    /// </summary>
    private static IrExpr Unclamped(IrExpr index) => index switch {
      IrConvert { Type.Kind: IrTypeKind.I32, Contract: IrConversion.Wrap, Operand: IrIntrinsic { Op: IrIntrinsicOp.Clamp, Arguments: [var value, IrConst { Value: -1 }, IrConst { Value: int.MaxValue }] } } => value,
      IrConvert { Type.Kind: IrTypeKind.I32, Contract: IrConversion.Wrap, Operand: IrIntrinsic { Op: IrIntrinsicOp.Min, Arguments: [var value, IrConst { Value: int.MaxValue }] } } when !value.Type.IsSigned => value,
      _ => index
    };

    private GoExpr CallOf(IrCall call) {
      if (!this._functions.TryGetValue(call.Function, out var name))
        return this.Inexpressible($"call of '{call.Function}'");

      var arguments = this.Operands(call.Arguments);
      return new GoCall(Name(name), arguments) { Type = call.Type };
    }

    /// <summary>
    /// A step as a value: the statement in the prelude, and the value from before it (postfix) or after
    /// it (prefix); the array and index of an element are evaluated once.
    /// </summary>
    private GoExpr Step(IrStep step) {
      GoExpr target;
      switch (step.Target) {
        case IrLocalRef or IrGlobalRef:
          target = this.Expression(step.Target);
          break;
        case IrArrayLoad { Array.Type.IsArray: true } element: {
          var operands = this.Operands(element.Array, element.Index);
          var array = operands[0] is GoIdent ? operands[0] : this.Hoist(operands[0], element.Array.Type);
          var index = operands[1] is GoIdent || IsUntyped(operands[1]) ? operands[1] : this.Hoist(operands[1], element.Index.Type);
          target = new GoIndex(array, index) { Type = step.Type };
          break;
        }
        default:
          return this.Inexpressible($"step of {KindOf(step.Target)}");
      }

      GoIdent? before = step.Postfix ? this.Hoist(target, step.Type) : null;
      this._prelude.Add(this.Stepped(step, target));
      return before ?? target;
    }

    /// <summary>The statement of a step: <c>x++</c>, or the assignment of the checked sum.</summary>
    private GoStmt Stepped(IrStep step, GoExpr target) {
      if (step.Trap && step.Type.IsInteger) {
        var op = step.Increment ? IrBinaryOp.AddChecked : IrBinaryOp.SubtractChecked;
        return new GoAssign([target], "=", [this.CallHelper(GoHelpers.Checked(op, step.Type), step.Type, target, new GoInt(1))]);
      }

      return new GoIncDec(target, step.Increment);
    }

    private string FieldName(IrField field) {
      if (this._fields.TryGetValue(field, out var name))
        return name;

      this.Refuse(TypeUnsupportedDescriptor, $"the field '{field.Name}' belongs to no struct type Go has a representation of");
      return GoNaming.Mixed(field.Name, true);
    }

    private GoExpr StructNew(IrStructNew creation) {
      if (creation.Type.Declaration is not IrStructType structure)
        return this.Inexpressible($"StructNew of {creation.Type}");

      var arguments = this.Operands(creation.Arguments);
      var elements = structure.Fields.Select((field, index) => (GoExpr)new GoKeyValue(Name(this.FieldName(field)), arguments[index])).ToList();
      return new GoComposite(this.TypeOf(creation.Type), elements) { Type = creation.Type };
    }

    /// <summary>
    /// The equality of two structs: Go's <c>==</c> where it compares as the IR does, and otherwise a
    /// generated function that compares the floating-point fields so that a NaN equals a NaN, as the IR's
    /// equality does. A struct with an array field has no equality in Go.
    /// </summary>
    private GoExpr StructEquals(IrStructEquals equality) {
      var operands = this.Operands(equality.Left, equality.Right);
      if (equality.Left.Type.Declaration is not IrStructType structure)
        return this.Inexpressible($"StructEquals of {equality.Left.Type}");

      if (Fields(structure).Any(field => field.Type.IsArray || !(field.Type.IsNumeric || field.Type == IrType.Bool || field.Type.IsStruct || field.Type.IsString)))
        return this.Inexpressible($"StructEquals of {structure.Name}, which has a field Go cannot compare");

      if (!Fields(structure).Any(field => field.Type.IsFloat))
        return new GoBinary(operands[0], "==", operands[1]) { Type = IrType.Bool };

      return new GoCall(Name(this.Equality(structure)), operands) { Type = IrType.Bool };
    }

    /// <summary>Every field of a struct and of the structs it holds, depth first.</summary>
    private static IEnumerable<IrField> Fields(IrStructType structure)
      => structure.Fields.SelectMany(field => field.Type.Declaration is IrStructType inner ? Fields(inner).Prepend(field) : [field]);

    /// <summary>The name of the generated equality of a struct type, made the first time it is needed.</summary>
    private string Equality(IrStructType structure) {
      if (this._equalities.TryGetValue(structure, out var name))
        return name;

      name = this.TakeHelper("plyEqual" + this._types[structure]);
      this._equalities[structure] = name;
      GoExpr test = Name("true");
      var parts = new List<GoExpr>();
      foreach (var field in structure.Fields) {
        var field_ = this._fields[field];
        GoExpr Left() => new GoSelector(Name("a"), field_);
        GoExpr Right() => new GoSelector(Name("b"), field_);
        parts.Add(field.Type switch {
          { IsFloat: true } => new GoBinary(new GoBinary(Left(), "==", Right()), "||", new GoBinary(new GoBinary(Left(), "!=", Left()), "&&", new GoBinary(Right(), "!=", Right()))),
          { Declaration: IrStructType inner } when Fields(inner).Any(nested => nested.Type.IsFloat) => Call(Name(this.Equality(inner)), Left(), Right()),
          _ => new GoBinary(Left(), "==", Right())
        });
      }

      if (parts.Count > 0)
        test = parts.Skip(1).Aggregate(parts[0], (all, part) => new GoBinary(all, "&&", part));

      var type = Name(this._types[structure]);
      this._generated.Add(new GoFuncDecl(name, new GoFuncType([new GoField(["a", "b"], type)], [new GoField([], Name("bool"))]),
        new GoBlock([new GoReturn([test])])) { IsHelper = true });
      return name;
    }

    /// <summary>
    /// A conversion: between integers Go's own conversion, which wraps; a checked one through a helper;
    /// to floating point Go's own, which rounds to nearest even; from floating point to an integer a
    /// helper that saturates or traps, since Go leaves a number outside the range to the implementation.
    /// </summary>
    private GoExpr Convert(IrConvert conversion) {
      var (from, to) = (conversion.Operand.Type, conversion.Type);
      if (!(from.IsNumeric && to.IsNumeric) || from.IsBigInt || to.IsBigInt)
        return from == to ? this.Expression(conversion.Operand) : this.Inexpressible($"Convert from {from} to {to}");

      var value = this.Expression(conversion.Operand);
      if (from.IsFloat && to.IsInteger) {
        var wide = from == IrType.F32 ? this.Conversion(IrType.F64, value) : value;
        return this.CallHelper(GoHelpers.FromFloat(to, conversion.Contract == IrConversion.Trap), to, wide);
      }

      if (from.IsInteger && to.IsInteger && conversion.Contract == IrConversion.Trap && !IrOperators.Contains(to, from))
        return this.CallHelper(GoHelpers.Narrowed(from, to), to, value);

      if (GoHelpers.GoName(from) == GoHelpers.GoName(to))
        return value;

      // Go's arithmetic on a narrow type wraps as the IR's on the wider one wrapped back: uint8(uint32(a) + uint32(b)) is a + b.
      if (conversion.Contract == IrConversion.Wrap && to.IsInteger && to.Bits < 32 && Narrowed(conversion.Operand, to) is { } narrow)
        return this.Expression(narrow);

      return this.Conversion(to, value);
    }

    /// <summary>
    /// A computation on a wider type of values widened from a narrow one, as the same computation on the
    /// narrow type, whose Go arithmetic wraps the same way: the sum, difference, product and the bitwise
    /// operators, of widened values and of constants of the narrow type; <see langword="null"/> otherwise.
    /// </summary>
    private static IrExpr? Narrowed(IrExpr value, IrType type) {
      switch (value) {
        case IrConvert { Contract: IrConversion.Wrap } widened when widened.Operand.Type == type && IrOperators.Contains(widened.Type, type):
          return widened.Operand;
        case IrConst constant when constant.Type.IsInteger && IrOperators.Number(constant.Type, constant.Value) >= IrOperators.Minimum(type)
          && IrOperators.Number(constant.Type, constant.Value) <= IrOperators.Maximum(type):
          return new IrConst(type, constant.Value);
        case IrBinary { Op: IrBinaryOp.Add or IrBinaryOp.Subtract or IrBinaryOp.Multiply or IrBinaryOp.And or IrBinaryOp.Or or IrBinaryOp.Xor } binary: {
          var (left, right) = (Narrowed(binary.Left, type), Narrowed(binary.Right, type));
          return left is null || right is null || (left is IrConst && right is IrConst) ? null : new IrBinary(binary.Op, left, right);
        }
        case IrUnary { Op: IrUnaryOp.BitNot } complement when Narrowed(complement.Operand, type) is { } operand and not IrConst:
          return new IrUnary(IrUnaryOp.BitNot, operand);
        default:
          return null;
      }
    }

    /// <summary>
    /// A lambda as a function literal: Go's closures capture a variable, not its value, as the IR's do,
    /// so a write after the closure was made is seen by it and a write by it is seen outside.
    /// </summary>
    private GoExpr Lambda(IrLambda lambda) {
      var signature = lambda.Type.Signature!;
      var parameters = lambda.Parameters.Select(parameter => new GoField([this.LocalName(parameter)], this.TypeOf(parameter.Type))).ToList();
      IReadOnlyList<GoField> results = signature.Result == IrType.Void ? [] : [new GoField([], this.TypeOf(signature.Result))];
      this._scopes.Add([.. lambda.Parameters]);
      var saved = this._prelude;
      this._prelude = [];
      var body = this.Statements(lambda.Body.Statements);
      this._prelude = saved;
      this._scopes.RemoveAt(this._scopes.Count - 1);
      if (signature.Result == IrType.Void && body.Count > 0 && body[^1] is GoReturn { Results.Count: 0 })
        body.RemoveAt(body.Count - 1);

      if (signature.Result != IrType.Void && !Terminates(body))
        body.Add(new GoExprStmt(Call(Name("panic"), new GoString("unreachable"))));

      return new GoFuncLit(new GoFuncType(parameters, results), new GoBlock(body)) { Type = lambda.Type };
    }

    /// <summary>The call of a function value: the callee first, then the arguments; Go panics on a nil one after both, as the IR traps.</summary>
    private GoExpr Invoke(IrInvoke invoke) {
      var operands = this.Operands([invoke.Callee, .. invoke.Arguments]);
      return new GoCall(operands[0], operands[1..]) { Type = invoke.Type };
    }

    /// <summary>An assignment that is a value: the assignment before the use, the variable its value.</summary>
    private GoExpr Bind(IrBind bind) {
      var value = this.Expression(bind.Value);
      if (!this._read.Contains(bind.Local))
        return value;

      var target = Name(this.LocalName(bind.Local));
      this._prelude.Add(new GoAssign([target], "=", [value]));
      return target with { Type = bind.Type };
    }

    /// <summary>The panic of a thrown exception: the message of a library exception, or of the class, as the text of the trap.</summary>
    private GoStmt Panic(IrExpr exception) {
      var text = exception switch {
        IrNew { Arguments: [IrStrConst message, ..] } => message.Value,
        IrNew { Type.Declaration: IrClassType type } when IrExceptionClass.IsLibrary(type) => IrExceptionClass.DefaultMessage(type),
        _ => null
      };
      if (text is null) {
        this.Inexpressible("throw of " + KindOf(exception));
        text = "";
      }

      return new GoExprStmt(Call(Name("panic"), new GoString(text)));
    }

  }

}
