using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// Whether a view of an element type is a typed array, the <c>subarray</c> of the array it views, as it
    /// is where the profile stores the type in a typed array; otherwise it is an object <c>{ a, o, n }</c>
    /// over a plain array. <c>docs/reference/views.md</c> lists the forms.
    /// </summary>
    private bool Typed(IrType view) => this.Native(Storage(view.Element!));

    /// <summary>Whether an expression is a view written as an object over a plain array.</summary>
    private bool Plain(IrExpr expression) => expression.Type.IsView && !this.Typed(expression.Type);

    /// <summary>An element of a view that is an object, read through its helper.</summary>
    private JsCall ViewElement(IrArrayLoad load) => this.Helper("$vget", load.Array, load.Index);

    /// <summary>An element of a view that is an object, written through its helper.</summary>
    private JsCall ViewStore(IrStore store) => this.Helper("$vset", [this.Text(store.Array), this.Text(store.Index), this.Value(store.Value)]);

    /// <summary>
    /// The array and the index a step of an element changes: those of the IR, or for a view that is an
    /// object its plain array and the place the helper checked. The view is written twice; it is a
    /// variable, or an expression without an effect but the trap it would raise the first time.
    /// </summary>
    private (JsExpression Array, JsExpression Index) Place(IrArrayLoad element)
      => this.Plain(element.Array)
        ? (new JsMember(this.Operand(element.Array), "a"), this.Helper("$vix", element.Array, element.Index))
        : (this.Text(element.Array), this.Text(element.Index));

    /// <summary>A view as a value of the type the header of a function names.</summary>
    private string DescribeView(IrType view) => this.Typed(view) ? Storage(view.Element!) : $"{{ a: Array<{this.Describe(view.Element!)}>, o, n }}";

    /// <summary>Reports an operation the profile cannot write, once per kind.</summary>
    private void Refuse(string what) {
      if (this._refused.Add(what))
        this._problems.Add(InexpressibleDescriptor.At($"'{what}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));
    }

    /// <summary>An operation of the views, or an endian load or store on a view; <see langword="null"/> for any other intrinsic.</summary>
    private (JsExpression Node, bool Atomic)? View(IrIntrinsic intrinsic) {
      if (IrViews.IsEndianOnView(intrinsic))
        return this.EndianView(intrinsic);

      if (!IrViews.IsViewOperation(intrinsic.Op))
        return null;

      var arguments = intrinsic.Arguments;
      var first = arguments[0];
      var all = arguments.Select(this.Text).ToList();
      var typed = this.Typed(intrinsic.Op == IrIntrinsicOp.StackAlloc ? intrinsic.Type : first.Type.IsView ? first.Type : intrinsic.Type);
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ViewOf when arguments.Count == 1 && (typed || first.Type.IsView):
          // A typed array is a view of the whole of itself; a view is one of any view it is made of.
          return (all[0], this.Expression(first).Atomic);
        case IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice:
          return (this.Helper(typed ? "$slice" : first.Type.IsView ? "$vsub" : "$vobj", all), true);
        case IrIntrinsicOp.ViewLength:
          return (new JsMember(this.Operand(first), typed ? "length" : "n"), true);
        case IrIntrinsicOp.StackAlloc:
          var element = intrinsic.Type.Element!;
          var count = this.Helper("$len", first);
          return typed
            ? (new JsNew(Id(Storage(element)), [count]), true)
            : (this.Helper("$vobj", [this.Helper("$new", [count, this.Zero(element)])]), true);
        case IrIntrinsicOp.ViewCopy:
          return (this.Helper(typed ? "$vcopy" : "$vcopyo", all), true);
        case IrIntrinsicOp.ViewTryCopy:
          return (this.Helper(typed ? "$vtrycopy" : "$vtrycopyo", all), true);
        case IrIntrinsicOp.ViewFill:
          return typed ? (this.Fill(first, all[1]), true) : (this.Helper("$vfillo", all), true);
        case IrIntrinsicOp.ViewEqual:
          return (this.Helper(typed ? "$vequal" : "$vequalo", all), true);
        case IrIntrinsicOp.ViewToArray:
          return (this.Helper(typed ? "$vtoarray" : "$vtoarrayo", all), true);
        default:
          return this.Reinterpreted(intrinsic, all[0]);
      }
    }

    /// <summary>
    /// A reinterpretation: a copy of the bytes of the view, the lowest byte of every element first, read
    /// as elements of the other type - through a <c>DataView</c> where both are typed arrays, by
    /// arithmetic between integers of up to 32 bits in plain arrays. Anything else, a 64-bit integer or a
    /// floating-point number in a plain array, the profile cannot write.
    /// </summary>
    private (JsExpression Node, bool Atomic) Reinterpreted(IrIntrinsic intrinsic, JsExpression view) {
      var from = intrinsic.Arguments[0].Type.Element!;
      var to = intrinsic.Type.Element!;
      JsNumber Size(IrType type) => Number(type.Size.ToString(CultureInfo.InvariantCulture));
      if (this.Typed(intrinsic.Arguments[0].Type) && this.Typed(intrinsic.Type))
        return (this.Helper("$vcast", [view, Size(from), new JsString("set" + Accessor(from)), Size(to), new JsString("get" + Accessor(to)), Id(Storage(to))]), true);

      if (from.IsInteger && to.IsInteger && !from.IsLong && !to.IsLong)
        return (this.Helper("$vcasto", [view, Size(from), Size(to), new JsBoolean(to.IsSigned)]), true);

      this.Refuse($"Reinterpret of {intrinsic.Arguments[0].Type} as {intrinsic.Type}");
      return (view, true);
    }

    /// <summary>The name of the methods of a <c>DataView</c> for a number type, after <c>get</c> or <c>set</c>.</summary>
    private static string Accessor(IrType type) => type.Kind switch {
      IrTypeKind.I8 => "Int8",
      IrTypeKind.U8 => "Uint8",
      IrTypeKind.I16 => "Int16",
      IrTypeKind.U16 => "Uint16",
      IrTypeKind.I32 => "Int32",
      IrTypeKind.U32 => "Uint32",
      IrTypeKind.I64 => "BigInt64",
      IrTypeKind.U64 => "BigUint64",
      IrTypeKind.F32 => "Float32",
      _ => "Float64"
    };

    /// <summary>
    /// An endian load or store on a view of bytes. On a typed array through a <c>DataView</c> of the
    /// bytes from the index on, made after the range is checked, with the byte order given - never a typed
    /// array of the wider type, whose offset would have to be aligned and whose order is the host's.
    /// Where the profile has no <c>DataView</c>, or for 64 bits without <c>BigInt</c>, the helpers of the
    /// arrays on the place <c>$vat</c> checked; a floating-point number is then the integer of its bits.
    /// </summary>
    private (JsExpression Node, bool Atomic) EndianView(IrIntrinsic intrinsic) {
      var store = intrinsic.Op is IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig;
      var little = new JsBoolean(intrinsic.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.StoreLittle);
      var type = store ? intrinsic.Arguments[2].Type : intrinsic.Type;
      var view = intrinsic.Arguments[0];
      var index = this.Text(intrinsic.Arguments[1]);
      var size = Number(type.Size.ToString(CultureInfo.InvariantCulture));
      var typed = this.Typed(view.Type);
      var direct = typed && (!type.IsLong || !this.Halves) && (!type.IsFloat || this.Native(type == IrType.F32 ? ".getFloat32" : ".getFloat64"));
      if (direct && store) {
        var method = new JsString("set" + Accessor(type));
        return (this.Helper("$vstore", [this.Text(view), index, size, this.Text(intrinsic.Arguments[2]), little, method]), true);
      }

      if (direct) {
        this.Native(".get" + Accessor(type));
        var data = this.Helper("$vdv", [this.Text(view), index, size]);
        return (new JsCall(new JsMember(data, "get" + Accessor(type)), [Number("0"), little]), true);
      }

      if (type.IsFloat) {
        var bits = type == IrType.F32 ? IrType.I32 : IrType.I64;
        return store
          ? this.Expression(intrinsic with { Arguments = [view, intrinsic.Arguments[1], new IrIntrinsic(IrIntrinsicOp.FloatBits, bits, [intrinsic.Arguments[2]])] })
          : this.Expression(new IrIntrinsic(IrIntrinsicOp.BitsFloat, type, [intrinsic with { Type = bits }]));
      }

      var bytes = typed ? this.Text(view) : new JsMember(this.Operand(view), "a");
      var at = this.Helper("$vat", [this.Text(view), index, size]);
      if (store)
        return type.IsLong
          ? (this.Helper("$store64", [bytes, at, this.Text(intrinsic.Arguments[2]), little]), true)
          : (this.Helper("$store", [bytes, at, this.Text(intrinsic.Arguments[2]), size, little]), true);

      return type.IsLong
        ? this.Typed(type, this.Helper("$load64", [bytes, at, little]))
        : Cut(type, this.Helper("$load", [bytes, at, size, little]));
    }

  }

}
