using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Rust.Syntax;
using Polypiler.Core.Frontends.Rust.Typing;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Rust.Lowering;

/// <summary>Paths, operators, assignments, casts, references, fields, indices and calls.</summary>
internal sealed partial class FunctionLowering {

  private IrExpr? Path(PathExpr path) {
    if (this._typed.Bindings.TryGetValue(path, out var binding)) {
      if (binding.Constant is { } constant) {
        if (this.Table(path) is { } table)
          return this.Duplicate(new IrConstRef(table), binding.Type);

        return this.Value(constant.Value);
      }

      if (this._places.TryGetValue(binding, out var place))
        return this.Read(place);

      return this._values[binding];
    }

    if (this._typed.Callees.TryGetValue(path, out var function)) {
      if (Outputs(function).Count > 0) {
        this.Refuse(RustFrontend.Unsupported, $"function '{function.CratePath}' with &mut parameters as a value", path.At);
        return this.Zero(this.Ir(this._typed.TypeOf(path)));
      }

      return new IrFunctionRef(this.Ir(this._typed.TypeOf(path)), function.IrName);
    }

    if (this._typed.Resolutions.TryGetValue(path, out var resolution))
      return this.Resolved(resolution, this._typed.TypeOf(path));

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

  /// <summary>The value a path that is no variable names: <c>None</c>, a variant, a unit struct.</summary>
  private IrExpr Resolved(object resolution, RType type) => resolution switch {
    "None" => new IrNone(this.Ir(type)),
    (EnumInfo enumeration, string variant) => this.Variant(enumeration, variant),
    _ => new IrStructNew(IrType.Struct(this._module.Struct((StructInfo)resolution)), [])
  };

  private IrConst Variant(EnumInfo enumeration, string variant) {
    var declaration = this._module.Enum(enumeration);
    return new IrConst(declaration.Type, declaration.Member(variant)!.Value);
  }

  /// <summary>
  /// The table of the module a constant or immutable static array of literal elements is; it is read
  /// in place, and copied where it is used as a value, as Rust copies a constant at every use.
  /// </summary>
  private IrModuleConst? Table(PathExpr path) {
    if (!this._typed.Bindings.TryGetValue(path, out var binding) || binding.Constant is not { } constant || binding.Type.Resolved.Kind != RKind.Array)
      return null;

    var element = binding.Type.Resolved.Element!.Resolved;
    if (!element.IsNumber)
      return null;

    return this._module.Table(binding, () => {
      var values = TableValues(constant.Value, element, binding.Type.Resolved.Length);
      return values is null ? null : new IrModuleConst(constant.Name, IrType.Array(element.Ir), values);
    });
  }

  private List<long>? TableValues(Expr value, RType element, long length) {
    switch (value) {
      case ArrayExpr array: {
        var values = new List<long>();
        foreach (var item in array.Elements) {
          if (this.ConstantValue(item, element) is not { } constant)
            return null;

          values.Add(constant);
        }

        return values;
      }
      case ArrayRepeatExpr repeat when this.ConstantValue(repeat.Value, element) is { } single:
        return [.. Enumerable.Repeat(single, (int)length)];
      default:
        return null;
    }
  }

  /// <summary>The value of a constant element as an <see cref="IrConst"/> carries it.</summary>
  private long? ConstantValue(Expr expression, RType type) {
    if (type.Resolved.Kind == RKind.Float)
      return expression switch {
        FloatLiteral literal => Float(type, literal.Text).Value,
        UnaryExpr { Op: "-", Operand: FloatLiteral literal } => FloatConstant(type.Ir, -double.Parse(literal.Text, System.Globalization.CultureInfo.InvariantCulture)).Value,
        _ => null
      };

    return Constants.Evaluate(expression, this._typed) is { } value ? Integer(type, value).Value : null;
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
    if (binary.Op is "==" or "!=" && !operandType.Dereferenced.IsScalar && operandType.Dereferenced.Kind != RKind.Char && !operandType.IsText)
      return this.Equality(binary.Op == "==", values[0], values[1], operandType.Dereferenced);

    return this.Operator(binary.Op, values[0], values[1], operandType.Dereferenced);
  }

  /// <summary>A binary operator applied to two lowered operands of the type of the left one.</summary>
  private IrExpr Operator(string op, IrExpr left, IrExpr right, RType type) {
    if (type.IsText)
      return this.TextOperator(op, left, right);

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
    var placeType = this._typed.TypeOf(assign.Target).Dereferenced;
    // The value is evaluated before the place, also for a compound assignment on a primitive.
    var value = this.Value(assign.Value);
    if (this._diverged)
      return null;

    if (assign.Target is PathExpr path && this._typed.Bindings.TryGetValue(path, out var binding) && !this._places.ContainsKey(binding)) {
      var local = ((IrLocalRef)this._values[binding]).Local;
      if (assign.Op != "=")
        value = this.Operator(assign.Op, new IrLocalRef(local), value, placeType);
      else
        value = this.Converted(value, binding.Type, this._typed.TypeOf(assign.Value));

      this.Emit(new IrAssign(local, value));
      return null;
    }

    value = this.Stable(value, "assigned");
    var place = this.PlaceOf(assign.Target);
    if (place is null)
      return null;

    this.Write(place, assign.Op == "=" ? value : this.Operator(assign.Op, this.Read(place), value, placeType));
    return null;
  }

  private IrExpr Cast(CastExpr cast) {
    var value = this.Value(cast.Operand);
    var target = this._typed.TypeOf(cast).Ir;
    if (value.Type.IsEnum)
      value = new IrConvert(value.Type.Integral, value, IrConversion.Wrap);

    return Cast(value, target);
  }

  // ---------------------------------------------------------------- references, fields and indices

  private IrExpr? Reference(ReferenceExpr reference) {
    if (IsExclusiveValue(this._typed.TypeOf(reference))) {
      this.Refuse(RustFrontend.Unsupported, "'&mut' to a value used other than as an argument, a receiver or the initializer of a let", reference.At);
      return this.Lower(reference.Operand);
    }

    // `&a[1..]` of a writable array is a read-only view.
    var value = this.Lower(reference.Operand);
    return value is null ? null : this.Converted(value, this._typed.TypeOf(reference), null);
  }

  private IrExpr? Deref(DerefExpr deref) {
    if (deref.Operand is PathExpr path && this._typed.Bindings.TryGetValue(path, out var binding) && this._places.TryGetValue(binding, out var place))
      return this.Read(place);

    return this.Lower(deref.Operand);
  }

  private IrExpr Field(FieldExpr field) {
    if (field.Target is MethodCallExpr { Name: var name } call && name.StartsWith(OverflowingPrefix, StringComparison.Ordinal) && field.Name is "0" or "1"
        && this._typed.TypeOf(call.Receiver).IsNumber) {
      var (value, flag) = this.Overflowing(call);
      if (field.Name == "0")
        return value;

      // The flag of a division: the division is still done, and traps on a zero divisor.
      if (call.Name is "overflowing_div" or "overflowing_rem")
        this.Stable(value);

      return flag;
    }

    var target = this.Lower(field.Target) ?? this.Zero(this.Ir(this._typed.TypeOf(field.Target)));
    return new IrFieldLoad(target, this.FieldOf(this._typed.TypeOf(field.Target).Dereferenced, field.Name));
  }

  private IrExpr Index(IndexExpr index) {
    if (index.Index is RangeExpr && this._typed.TypeOf(index.Target).IsText)
      return this.TextSlice(index);

    if (index.Index is RangeExpr range)
      return this.RangeIndex(index, range);

    var container = this.Container(index.Target);
    var position = this.Position(this.Value(index.Index));
    if (container.Type.IsList)
      return new IrCollection(IrCollectionOp.ListGet, container.Type.Element!, [container, position]);

    return new IrArrayLoad(container, position);
  }

  // ---------------------------------------------------------------- calls

  private IrExpr? Call(CallExpr call) {
    if (this._typed.Resolutions.TryGetValue(call, out var resolution))
      return this.Construct(call, resolution);

    if (this._typed.Closures.TryGetValue(call, out var closure))
      return this.Apply(closure, [.. this.Operands([.. call.Arguments]).Select(value => (Item)new ValueItem(value))]);

    if (call.Callee is PathExpr path && this._typed.Bindings.TryGetValue(path, out var variable)) {
      var callee = this._values[variable];
      var signature = variable.Type.Resolved;
      var arguments = this.Operands([.. call.Arguments], [.. signature.Elements]);
      var result = signature.Element!.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : this.Ir(signature.Element);
      var invocation = new IrInvoke(result, callee, arguments);
      if (result != IrType.Void)
        return invocation;

      this.Emit(new IrExprStmt(invocation));
      return null;
    }

    var function = this._typed.Callees[call];
    IReadOnlyList<RType> parameters = function.Receiver is null ? function.Parameters : [function.Receiver, .. function.Parameters];
    return this.Invoke(function, [.. call.Arguments], parameters, null);
  }

  /// <summary>
  /// A call of a function of the crate: the arguments left to right - a <c>&amp;mut</c> to a value
  /// as the value of its place - then the call, then the places written back from the copies the
  /// function returns with its result.
  /// </summary>
  private IrExpr? Invoke(FunctionSignature function, IReadOnlyList<Expr> arguments, IReadOnlyList<RType> parameters, (Expr Receiver, ReceiverKind Kind)? receiver) {
    var values = new List<IrExpr>();
    var places = new List<Place>();
    // A method call passes its receiver first, adjusted as the method takes it; a path call passes it as written.
    var all = new List<(Expr Expression, RType Type, bool IsReceiver)>();
    if (receiver is { } self)
      all.Add((self.Receiver, function.Receiver!, true));

    all.AddRange(arguments.Select((argument, index) => (argument, parameters[index], false)));
    foreach (var (expression, parameterType, isReceiver) in all) {
      var mark = this._statements.Count;
      IrExpr value;
      var type = parameterType;
      if (IsExclusiveValue(type)) {
        var place = isReceiver ? this.ReceiverPlace(expression) : this.PlaceOfReference(expression) ?? this.ReceiverPlace(expression);
        if (place is null)
          return null;

        places.Add(place);
        value = this.Read(place);
      } else if (isReceiver) {
        value = function.Item.Receiver == ReceiverKind.Value ? this.Value(expression) : this.Lower(expression) ?? this.Zero(this.Ir(type));
      } else {
        value = this.Converted(this.Value(expression), type, this._typed.Types.GetValueOrDefault(expression));
      }

      if (this._diverged)
        return null;

      if (this._statements.Count != mark) {
        var inserted = 0;
        for (var earlier = 0; earlier < values.Count; ++earlier) {
          if (values[earlier] is IrConst)
            continue;

          var local = this.NewLocal("operand", values[earlier].Type);
          this._statements.Insert(mark + inserted++, new IrDeclare(local, values[earlier]));
          values[earlier] = new IrLocalRef(local);
        }
      }

      values.Add(value);
    }

    var outputs = Outputs(function);
    var result = function.Result.Resolved.Kind is RKind.Unit or RKind.Never ? IrType.Void : this.Ir(function.Result);
    if (outputs.Count == 0) {
      var plain = new IrCall(function.IrName, result, values);
      if (result != IrType.Void)
        return plain;

      this.Emit(new IrExprStmt(plain));
      return null;
    }

    var output = this._module.OutputOf(function);
    var returned = this.Temporary(new IrCall(function.IrName, IrType.Struct(output), values), "returned");
    var offset = result == IrType.Void ? 0 : 1;
    for (var index = 0; index < places.Count; ++index)
      this.Write(places[index], new IrFieldLoad(returned, output.Fields[offset + index]));

    return result == IrType.Void ? null : new IrFieldLoad(returned, output.Fields[0]);
  }

  /// <summary>The place a method with <c>&amp;mut self</c> works on: the receiver's place, or what a <c>&amp;mut</c> receiver reaches.</summary>
  private Place? ReceiverPlace(Expr receiver) {
    if (receiver is ReferenceExpr { Mutable: true } reference)
      return this.PlaceOf(reference.Operand);

    return this.PlaceOf(receiver);
  }

}
