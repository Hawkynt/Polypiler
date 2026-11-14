using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

/// <summary>
/// The views of the IR (#1325) in C#. From .NET Standard 2.1 on a view is a <c>Span&lt;T&gt;</c> or a
/// <c>ReadOnlySpan&lt;T&gt;</c> and every operation the member of the span, of <c>MemoryExtensions</c>, of
/// <c>MemoryMarshal</c> or of <c>BinaryPrimitives</c>, which throw what the IR traps with. Before it a view is
/// an <c>ArraySegment&lt;T&gt;</c> and every operation a helper of <see cref="CSharpViewHelpers"/> that checks
/// and throws the same. <c>docs/reference/views.md</c> lists the forms.
/// </summary>
public static partial class CSharpEmitter {

  private sealed partial class Writer {

    private const string Primitives = "global::System.Buffers.Binary.BinaryPrimitives.";

    /// <summary>The largest number of bytes a <c>stackalloc</c> takes: a larger run, or one of a length not known, is an array.</summary>
    private const int StackLimit = 1024;

    /// <summary>Whether the profile has spans; without them a view is an <c>ArraySegment&lt;T&gt;</c>.</summary>
    private bool Spans => this._table.HasApi("Span<T>", this._profile);

    /// <summary>Whether an expression is a view the profile writes as an <c>ArraySegment&lt;T&gt;</c>.</summary>
    private bool Segmented(IrExpr expression) => expression.Type.IsView && !this.Spans;

    /// <summary>The C# type of a view in the profile.</summary>
    private string ViewTypeName(IrType type) {
      var element = this.TypeName(type.Element!);
      return !this.Spans ? $"global::System.ArraySegment<{element}>"
        : type.Kind == IrTypeKind.Span ? $"global::System.Span<{element}>"
        : $"global::System.ReadOnlySpan<{element}>";
    }

    /// <summary>A call of a helper of the views, with the arguments as they stand.</summary>
    private CsInvocation ViewHelper(CSharpHelper helper, params CsExpression[] arguments) => new(new CsName(this.Helper(helper)), arguments);

    /// <summary>A call of a member of a value: <c>value.Member(arguments)</c>.</summary>
    private CsInvocation Member(IrExpr target, string member, params CsExpression[] arguments)
      => new(new CsMemberAccess(this.Operand(target, Primary), member), arguments);

    /// <summary>A call of a static method by its full name.</summary>
    private static CsInvocation Static(string method, params CsExpression[] arguments) => new(CSharpFragments.Expression(method), arguments);

    /// <summary>An element of a view of a profile without spans, read through its helper.</summary>
    private CsExpression SegmentElement(IrArrayLoad load)
      => this.ViewHelper(CSharpViewHelpers.ViewGet, this.Text(load.Array), this.Text(Promoted(load.Index)));

    /// <summary>
    /// A generic helper of the views called with its element type stated: a value written as a constant or
    /// as an <c>int</c> C# made of a narrow integer would not tell the compiler what <c>T</c> is.
    /// </summary>
    private CsInvocation Typed(CSharpHelper helper, IrType element, params CsExpression[] arguments)
      => new(new CsName($"{this.Helper(helper)}<{this.TypeName(element)}>"), arguments);

    /// <summary>An element of a view of a profile without spans, written through its helper.</summary>
    private CsExpression SegmentStore(IrStore store)
      => this.Typed(CSharpViewHelpers.ViewSet, store.Array.Type.Element!, this.Text(store.Array), this.Text(Promoted(store.Index)), this.Stored(store.Value));

    /// <summary>
    /// An element of a view of a profile without spans as a place a step changes: the element of the
    /// segment's array at the place the helper checked. The view is written twice; it is a variable, or
    /// an expression without an effect but the trap it would raise the first time.
    /// </summary>
    private CsExpression SegmentPlace(IrArrayLoad load) {
      var array = new CsMemberAccess(this.Operand(load.Array, Primary), "Array");
      return new CsElementAccess(array, this.ViewHelper(CSharpViewHelpers.ViewIndex, this.Text(load.Array), this.Text(Promoted(load.Index)))) { Type = load.Type };
    }

    /// <summary>An operation of the views, or an endian load or store on a view; <see langword="null"/> for any other intrinsic.</summary>
    private (CsExpression Node, int Binding)? View(IrIntrinsic intrinsic) {
      if (IrViews.IsEndianOnView(intrinsic))
        return (this.Endian(intrinsic), Cast);

      if (!IrViews.IsViewOperation(intrinsic.Op))
        return null;

      var node = this.Spans ? this.OnSpan(intrinsic) : this.OnSegment(intrinsic);
      return (node, node is CsMemberAccess or CsInvocation or CsName ? Primary : Unary);
    }

    /// <summary>An operation of the views in a profile with spans.</summary>
    private CsExpression OnSpan(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      var first = arguments[0];
      var rest = arguments.Skip(1).Select(argument => this.Text(Promoted(argument))).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ViewOf when first.Type.IsArray:
          return Static("global::System.MemoryExtensions.AsSpan", [this.Text(first), .. rest]);
        case IrIntrinsicOp.ViewOf when arguments.Count == 1:
          // A writable view is a read-only one by the conversion C# applies itself.
          return this.Text(first);
        case IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice:
          return this.Member(first, "Slice", rest);
        case IrIntrinsicOp.ViewLength:
          return new CsMemberAccess(this.Operand(first, Primary), "Length");
        case IrIntrinsicOp.StackAlloc:
          return this.Allocated(intrinsic);
        case IrIntrinsicOp.ViewCopy:
          return this.Member(first, "CopyTo", this.Text(arguments[1]));
        case IrIntrinsicOp.ViewTryCopy:
          return this.Member(first, "TryCopyTo", this.Text(arguments[1]));
        case IrIntrinsicOp.ViewFill:
          return this.Member(first, "Fill", this.Stored(arguments[1]));
        case IrIntrinsicOp.ViewEqual:
          return Static("global::System.MemoryExtensions.SequenceEqual", this.Text(first), this.Text(arguments[1]));
        case IrIntrinsicOp.ViewToArray:
          return this.Member(first, "ToArray");
        default:
          var cast = $"global::System.Runtime.InteropServices.MemoryMarshal.Cast<{this.TypeName(first.Type.Element!)}, {this.TypeName(intrinsic.Type.Element!)}>";
          return Static(cast, this.Text(first));
      }
    }

    /// <summary>
    /// <c>stackalloc T[n]</c> where the length is a constant whose
    /// elements take fewer than <see cref="StackLimit"/> bytes and no loop repeats it; an array otherwise,
    /// which C# converts to the span. A negative or unknown length is never put on the stack: .NET 10
    /// ends the process on <c>stackalloc int[-1]</c> instead of throwing.
    /// </summary>
    private CsExpression Allocated(IrIntrinsic intrinsic) {
      var element = new CsType(this.TypeName(intrinsic.Type.Element!));
      var length = intrinsic.Arguments[0];
      // Every profile with spans has the language of C# 8, which has stackalloc in an expression (CSharpFeature.StackAllocExpression).
      var onStack = this._steps.Count == 0 && length is IrConst { Value: >= 0 } count && count.Value * intrinsic.Type.Element!.Size < StackLimit;
      return new CsArrayCreation(element, this.AtRunTime(Promoted(length), length is IrConst { Value: >= 0 }, Loose), null) { OnStack = onStack };
    }

    /// <summary>An operation of the views in a profile without spans: a form of <c>ArraySegment&lt;T&gt;</c> or a helper.</summary>
    private CsExpression OnSegment(IrIntrinsic intrinsic) {
      var arguments = intrinsic.Arguments;
      var first = arguments[0];
      var all = arguments.Select(argument => this.Text(Promoted(argument))).ToArray();
      switch (intrinsic.Op) {
        case IrIntrinsicOp.ViewOf when first.Type.IsArray:
          return arguments.Count switch {
            1 => new CsObjectCreation(new CsType(this.ViewTypeName(intrinsic.Type)), all),
            2 => this.ViewHelper(CSharpViewHelpers.ViewFrom, all),
            _ => this.ViewHelper(CSharpViewHelpers.ViewOf, all)
          };
        case IrIntrinsicOp.ViewOf when arguments.Count == 1:
          return this.Text(first);
        case IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice:
          return this.ViewHelper(arguments.Count == 2 ? CSharpViewHelpers.SliceFrom : CSharpViewHelpers.Slice, all);
        case IrIntrinsicOp.ViewLength:
          return new CsMemberAccess(this.Operand(first, Primary), "Count");
        case IrIntrinsicOp.StackAlloc:
          var element = new CsType(this.TypeName(intrinsic.Type.Element!));
          var length = this.AtRunTime(Promoted(first), first is IrConst { Value: >= 0 }, Loose);
          return new CsObjectCreation(new CsType(this.ViewTypeName(intrinsic.Type)), [new CsArrayCreation(element, length, null)]);
        case IrIntrinsicOp.ViewCopy:
          return this.ViewHelper(CSharpViewHelpers.ViewCopy, all);
        case IrIntrinsicOp.ViewTryCopy:
          return this.ViewHelper(CSharpViewHelpers.ViewTryCopy, all);
        case IrIntrinsicOp.ViewFill:
          return this.Typed(CSharpViewHelpers.ViewFill, first.Type.Element!, all[0], this.Stored(arguments[1]));
        case IrIntrinsicOp.ViewEqual:
          return this.ViewHelper(CSharpViewHelpers.ViewEqual, all);
        case IrIntrinsicOp.ViewToArray:
          return this.ViewHelper(CSharpViewHelpers.ViewToArray, all);
        default:
          var from = first.Type.Element!;
          var to = intrinsic.Type.Element!;
          var name = $"{this.Helper(CSharpViewHelpers.ViewCast)}<{this.TypeName(from)}, {this.TypeName(to)}>";
          return new CsInvocation(new CsName(name), [all[0], Integer(from.Size), Integer(to.Size)]);
      }
    }

    /// <summary>An <c>int</c> constant.</summary>
    private static CsIntegerLiteral Integer(int value) => new(value) { Type = IrType.I32 };

    /// <summary>
    /// An endian load or store on a view of bytes: <c>BinaryPrimitives</c> on the span from the index on,
    /// which throws for a value outside it, and for a store the helper that evaluates the value first; a
    /// helper on the segment where the profile has no spans. A floating-point number has been made the
    /// integer of its bits before, where the profile reads none directly.
    /// </summary>
    private CsExpression Endian(IrIntrinsic intrinsic) {
      var store = intrinsic.Op is IrIntrinsicOp.StoreLittle or IrIntrinsicOp.StoreBig;
      var little = intrinsic.Op is IrIntrinsicOp.LoadLittle or IrIntrinsicOp.StoreLittle;
      var type = store ? intrinsic.Arguments[2].Type : intrinsic.Type;
      var view = intrinsic.Arguments[0];
      var index = intrinsic.Arguments[1];
      var size = Integer(type.Size);
      if (store) {
        var value = new CsCast(new CsType("ulong"), this.Operand(intrinsic.Arguments[2], Cast));
        var helper = this.Spans ? CSharpViewHelpers.StoreSpan : CSharpViewHelpers.StoreView;
        return this.ViewHelper(helper, this.Text(view), this.Text(Promoted(index)), value, size, new CsBooleanLiteral(little));
      }

      if (!this.Spans) {
        var loaded = this.ViewHelper(CSharpViewHelpers.LoadView, this.Text(view), this.Text(Promoted(index)), size, new CsBooleanLiteral(little));
        return new CsCast(new CsType(this.TypeName(type)), loaded);
      }

      var bytes = index is IrConst { Value: 0 } ? this.Text(view) : this.Member(view, "Slice", this.Text(Promoted(index)));
      return Static($"{Primitives}Read{ApiName(type)}{(little ? "LittleEndian" : "BigEndian")}", bytes);
    }

    /// <summary>The name of a number type in the API of <c>BinaryPrimitives</c>.</summary>
    private static string ApiName(IrType type) => type.Kind switch {
      IrTypeKind.I16 => "Int16",
      IrTypeKind.U16 => "UInt16",
      IrTypeKind.I32 => "Int32",
      IrTypeKind.U32 => "UInt32",
      IrTypeKind.I64 => "Int64",
      IrTypeKind.U64 => "UInt64",
      IrTypeKind.F32 => "Single",
      _ => "Double"
    };

  }

}
