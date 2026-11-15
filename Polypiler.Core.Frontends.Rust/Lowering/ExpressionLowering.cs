using System;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>Paths, operators, assignments, casts and calls.</summary>
internal sealed partial class FunctionLowering {

  private IrExpr Path(PathExpr path) {
    if (this._typed.Bindings.TryGetValue(path, out var binding))
      return binding.Constant is { } constant ? this.Value(constant.Value) : this._values[binding];

    var owner = RType.Named(path.Segments[0])!;
    var name = path.Segments[1];
    if (owner.Kind == RKind.Float)
      return FloatLimit(owner.Ir, name);

    return name switch {
      "MIN" => Minimum(owner.Ir),
      "MAX" => Maximum(owner.Ir),
      _ => new IrConst(IrType.U32, owner.Bits)
    };
  }

  private IrExpr Unary(UnaryExpr unary) {
    var type = this._typed.TypeOf(unary);
    if (unary is { Op: "-", Operand: IntegerLiteral literal })
      return Integer(type, -literal.Value);

    var operand = this.Value(unary.Operand);
    if (unary.Op == "-")
      return operand is IrConst { Type.IsFloat: true } constant
        ? FloatConstant(type.Ir, -(type.Ir == IrType.F32 ? BitConverter.Int32BitsToSingle((int)constant.Value) : BitConverter.Int64BitsToDouble(constant.Value)))
        : this.Negate(operand, type.Ir, this.Checked);

    return type.Kind == RKind.Bool ? Not(operand) : Bitwise(null, operand, null, type.Ir);
  }

  private IrExpr Binary(BinaryExpr binary) {
    if (binary.Op is "&&" or "||")
      return this.ShortCircuit(binary);

    var operandType = this._typed.TypeOf(binary.Left);
    var values = this.Operands(binary.Left, binary.Right);
    return this.Operator(binary.Op, values[0], values[1], operandType);
  }

  /// <summary>A binary operator applied to two lowered operands of the type of the left one.</summary>
  private IrExpr Operator(string op, IrExpr left, IrExpr right, RType type) {
    var ir = type.Ir;
    switch (op) {
      case "<<" or ">>":
        return this.Shift(op == "<<", left, right, ir, this.Checked);
      case "==" or "!=" or "<" or ">" or "<=" or ">=": {
        var comparison = op switch {
          "==" => IrBinaryOp.Equal,
          "!=" => IrBinaryOp.NotEqual,
          "<" => IrBinaryOp.Less,
          ">" => IrBinaryOp.Greater,
          "<=" => IrBinaryOp.LessOrEqual,
          _ => IrBinaryOp.GreaterOrEqual
        };
        if (ir == IrType.Bool && comparison is not (IrBinaryOp.Equal or IrBinaryOp.NotEqual))
          return Binary(comparison, Cast(left, IrType.I32), Cast(right, IrType.I32));

        return Compare(comparison, left, right);
      }
      case "&" or "|" or "^" when ir == IrType.Bool:
        // Both operands are evaluated, left first: the right one is read before the operator decides.
        if (!IsSimple(right)) {
          left = this.Stable(left);
          right = this.Stable(right);
        }

        return op switch {
          "&" => And(left, right),
          "|" => Or(left, right),
          _ => Binary(IrBinaryOp.NotEqual, left, right)
        };
      case "&" or "|" or "^":
        return Bitwise(op switch { "&" => IrBinaryOp.And, "|" => IrBinaryOp.Or, _ => IrBinaryOp.Xor }, left, right, ir);
      default: {
        var arithmetic = op switch {
          "+" => IrBinaryOp.Add,
          "-" => IrBinaryOp.Subtract,
          "*" => IrBinaryOp.Multiply,
          "/" => IrBinaryOp.Divide,
          _ => IrBinaryOp.Remainder
        };
        return this.Arithmetic(arithmetic, left, right, ir, this.Checked);
      }
    }
  }

  /// <summary><c>&&</c> and <c>||</c>: the right operand only when the left one does not decide.</summary>
  private IrExpr ShortCircuit(BinaryExpr binary) {
    var left = this.Value(binary.Left);
    var (statements, right, diverged) = this.Capture(() => this.Value(binary.Right));
    if (statements.Count == 0 && !diverged)
      return binary.Op == "&&" ? And(left, right!) : Or(left, right!);

    var result = this.NewLocal("condition", IrType.Bool);
    this.Emit(new IrDeclare(result, left));
    if (!diverged)
      statements.Add(new IrAssign(result, right!));

    var test = binary.Op == "&&" ? (IrExpr)new IrLocalRef(result) : Not(new IrLocalRef(result));
    this.Emit(new IrIf(test, new IrBlock(statements), null));
    return new IrLocalRef(result);
  }

  private IrExpr? Assign(AssignExpr assign) {
    var binding = this._typed.Bindings[assign.Target];
    var local = ((IrLocalRef)this._values[binding]).Local;
    // The value is evaluated before the place, also for a compound assignment on a primitive.
    var value = this.Value(assign.Value);
    if (assign.Op != "=")
      value = this.Operator(assign.Op, new IrLocalRef(local), value, binding.Type);

    this.Emit(new IrAssign(local, value));
    return null;
  }

  private IrExpr Cast(CastExpr cast) => Cast(this.Value(cast.Operand), this._typed.TypeOf(cast).Ir);

  private IrExpr? Call(CallExpr call) {
    var path = (PathExpr)call.Callee;
    if (path.Segments.Count == 2) {
      var type = RType.Named(path.Segments[0])!.Ir;
      var bits = this.Value(call.Arguments[0]);
      return new IrIntrinsic(IrIntrinsicOp.BitsFloat, type, [Cast(bits, type == IrType.F32 ? IrType.I32 : IrType.I64)]);
    }

    var signature = this._typed.Functions[path.Segments[0]];
    var arguments = this.Operands([.. call.Arguments]);
    var result = signature.Result.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : signature.Result.Ir;
    var invocation = new IrCall(signature.Item.Name, result, arguments);
    if (result != IrType.Void)
      return invocation;

    this.Emit(new IrExprStmt(invocation));
    return null;
  }

  private IrExpr Field(FieldExpr field) {
    var call = (MethodCallExpr)field.Target;
    var (value, flag) = this.Overflowing(call);
    if (field.Name == "0")
      return value;

    // The flag of a division: the division is still done, and traps on a zero divisor.
    if (call.Name is "overflowing_div" or "overflowing_rem")
      this.Stable(value);

    return flag;
  }

}
