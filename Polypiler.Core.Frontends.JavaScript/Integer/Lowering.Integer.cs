using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

internal sealed partial class Lowering {

  /// <summary>The <c>DataView</c>s of the function by their variable: the array of bytes each one views, held in a local of its own.</summary>
  private readonly Dictionary<Variable, IrExpr> _views = [];

  /// <summary>
  /// A call that <see cref="JavaScriptIntegerApi"/> maps to an integer operation: <c>Math.abs</c>,
  /// <c>Math.min</c>, <c>Math.max</c> and <c>Math.sign</c> of proven integers, <c>BigInt.asUintN</c> and
  /// <c>BigInt.asIntN</c>, and the methods of a <c>DataView</c>. <see langword="null"/> for any other
  /// call, which is lowered as before.
  /// </summary>
  private IrExpr? IntegerCall(CallExpression call, string? builtin) {
    var arguments = call.Arguments;
    switch (builtin) {
      case "Math.abs" or "Math.sign" or "Math.min" or "Math.max" when types.Of(call).IsInteger:
        var values = arguments.Select(argument => this.Evaluate(argument)).ToList();
        if (builtin == "Math.sign")
          return new IrIntrinsic(IrIntrinsicOp.Sign, IrType.I32, [values[0].Expr]);

        var type = types.Of(call).Ir!;
        if (builtin == "Math.abs")
          return Convert(new IrIntrinsic(IrIntrinsicOp.AbsWrap, values[0].Expr.Type, [values[0].Expr]), type);

        var op = builtin == "Math.min" ? IrIntrinsicOp.Min : IrIntrinsicOp.Max;
        return values.Select(value => Convert(value.Expr, type)).Aggregate((first, second) => new IrIntrinsic(op, type, [first, second]));
      case "BigInt.asUintN" or "BigInt.asIntN":
        if (arguments.Count != 2)
          throw this.Unsupported(call, $"{builtin} with {arguments.Count} arguments");

        if (arguments[0] is not NumericLiteral { Value: >= 1 and <= 64 and var width } || width != System.Math.Floor(width))
          throw this.Unsupported(call, $"{builtin} with a width other than a literal from 1 to 64");

        var unsigned = builtin == "BigInt.asUintN";
        var big = unsigned ? this.Big(arguments[1], Coercion.Uint64, IrType.U64) : this.Big(arguments[1], Coercion.Int64, IrType.I64);
        return width == 64 ? big : new IrIntrinsic(unsigned ? IrIntrinsicOp.ZeroExtend : IrIntrinsicOp.SignExtend, big.Type, [big, new IrConst(IrType.I32, (long)width)]);
      case null when this.ViewOf(call) is var (method, bytes) && bytes is not null:
        return (IrExpr)this.ViewCall(call, method, bytes, false);
      default:
        return null;
    }
  }

  /// <summary>The method a call names and the bytes of the <c>DataView</c> it is called on, or no bytes for a call on anything else.</summary>
  private (string Method, IrExpr? Bytes) ViewOf(CallExpression call)
    => call.Callee is MemberExpression { Computed: false, Object: Identifier view, Property: Identifier { Name: var method } }
      && this._model.Bindings.GetValueOrDefault(view) is Variable variable && this._views.TryGetValue(variable, out var bytes)
        ? (method, bytes) : ("", null);

  /// <summary>
  /// A call of <c>setInt8</c> or <c>setUint8</c> of a <c>DataView</c> that stands as a statement: the
  /// store of the byte into the array. <see langword="null"/> for any other call.
  /// </summary>
  private IrStmt? ByteStore(CallExpression call)
    => this.ViewOf(call) is var (method, bytes) && bytes is not null && method is "setInt8" or "setUint8" ? (IrStmt)this.ViewCall(call, method, bytes, true) : null;

  /// <summary>
  /// A method of a <c>DataView</c>: a read or a write of 8 bits is an element of the array, one of 16,
  /// 32 or 64 bits the endian load or store in the byte order the last argument states as a literal.
  /// </summary>
  private object ViewCall(CallExpression call, string method, IrExpr bytes, bool statement) {
    var arguments = call.Arguments;
    if (JavaScriptIntegerApi.ViewType(method) is not { } type)
      throw this.Unsupported(call, $"the DataView method {method}");

    var reads = method.StartsWith("get", System.StringComparison.Ordinal);
    var count = reads ? 1 : 2;
    if (arguments.Count < count || arguments.Count > count + 1)
      throw this.Unsupported(call, $"DataView.{method} with {arguments.Count} arguments");

    var little = arguments.Count > count && type.Bits > 8
      ? arguments[count] is BooleanLiteral { Value: var order } ? order : throw this.Unsupported(arguments[count], $"DataView.{method} with a byte order that is not a literal")
      : false;
    var position = this.Evaluate(arguments[0]);
    if (!position.Type.IsWord)
      throw this.Unsupported(arguments[0], $"a DataView index that is no proven integer, which is {position.Type.Describe()}");

    var index = Convert(position.Expr, IrType.I32);
    if (reads && type.Bits == 8)
      return Convert(new IrArrayLoad(bytes, index), type);

    if (reads)
      return new IrIntrinsic(little ? IrIntrinsicOp.LoadLittle : IrIntrinsicOp.LoadBig, type, [bytes, index]);

    if (type.Bits == 8 && !statement)
      throw this.Unsupported(call, $"DataView.{method} inside an expression");

    var value = type.IsLong ? this.Big(arguments[1], type.IsSigned ? Coercion.Int64 : Coercion.Uint64, type)
      : Convert(type.IsSigned ? this.Int32(arguments[1]) : this.Uint32(arguments[1]), type);
    return type.Bits == 8
      ? new IrStore(bytes, index, Convert(value, IrType.U8))
      : new IrIntrinsic(little ? IrIntrinsicOp.StoreLittle : IrIntrinsicOp.StoreBig, IrType.Void, [bytes, index, value]);
  }

  /// <summary>
  /// A <c>const</c> declared as <c>new DataView(bytes.buffer)</c> or <c>new DataView(bytes.buffer,
  /// bytes.byteOffset, bytes.byteLength)</c> of a <c>Uint8Array</c>: no variable of the IR, but the
  /// array, held as it is at this point, which every method of the view works on.
  /// </summary>
  private bool View(VariableDeclarator declarator, Variable variable, List<IrStmt> into) {
    if (declarator.Init is not NewExpression { Callee: Identifier { Name: "DataView" } constructor } creation || this._model.Bindings.ContainsKey(constructor))
      return false;

    var array = creation.Arguments switch {
      [MemberExpression { Computed: false, Object: Identifier bytes, Property: Identifier { Name: "buffer" } }] => bytes,
      [MemberExpression { Computed: false, Object: Identifier bytes, Property: Identifier { Name: "buffer" } },
        MemberExpression { Computed: false, Object: Identifier offset, Property: Identifier { Name: "byteOffset" } },
        MemberExpression { Computed: false, Object: Identifier length, Property: Identifier { Name: "byteLength" } }]
        when offset.Name == bytes.Name && length.Name == bytes.Name => bytes,
      _ => null
    };
    if (array is null || !variable.IsConstant)
      throw this.Unsupported(creation, variable.IsConstant ? "a DataView that is not made of the buffer of a Uint8Array of the function" : "a DataView in a variable that is not const");

    var value = this.Expression(array);
    if (value.Type != IrType.Array(IrType.U8))
      throw this.Unsupported(array, $"a DataView of {value.Type}, which is no Uint8Array");

    var held = new IrLocal($"{variable.Name}$bytes", value.Type);
    this._locals.Add(held);
    into.Add(new IrDeclare(held, value));
    this._views[variable] = new IrLocalRef(held);
    return true;
  }

}
