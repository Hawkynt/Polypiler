using System.Linq;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The Rust type of a view: a slice, <c>&amp;[T]</c> where its elements are only read,
    /// <c>&amp;mut [T]</c> where the function writes them.
    /// </summary>
    private string ViewType(IrType type, bool written) => $"&{(written ? "mut " : "")}[{this.ElementType(type)}]";

    /// <summary>Whether the function being written stores into the elements of a view variable.</summary>
    private bool WritesView(IrLocal local) => Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.WritesView(this._function, local);

    /// <summary>
    /// An operation on a view: a view of an array or of another view as a slice of it, the whole one
    /// borrowed, a part through <c>ply_view</c>, which traps on a range outside it as .NET does; its
    /// length; a copy into another view (<c>copy_from_slice</c> of the same length, so that the source
    /// may overlap the target only where it is the same slice), a fill, the equality of two, a new array of one.
    /// </summary>
    private RsExpression ViewOperation(IrIntrinsic operation) {
      var arguments = operation.Arguments;
      switch (operation.Op) {
        case IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice:
          return this.View(operation, operation.Type.Kind == IrTypeKind.Span);
        case IrIntrinsicOp.ViewLength:
          return this.Cast(new RsMethodCall(this.ViewSource(arguments[0], false), "len", []) { Type = IrType.U64 }, IrType.I32);
        case IrIntrinsicOp.ViewToArray:
          return new RsMethodCall(this.ViewSource(arguments[0], false), "to_vec", []) { Type = operation.Type };
        case IrIntrinsicOp.ViewEqual when !arguments[0].Type.Element!.IsFloat:
          return this.Binary(this.ViewSource(arguments[0], false), "==", this.ViewSource(arguments[1], false), IrType.Bool);
        case IrIntrinsicOp.ViewFill:
          return new RsMethodCall(this.ViewSource(arguments[0], true), "fill", [this.ElementValue(arguments[1])]);
        default:
          return this.Refuse(operation.Op.ToString());
      }
    }

    /// <summary>Whether a function stores into the elements of its view parameter at a position.</summary>
    private bool ViewParameterWritten(string callee, int index) {
      if (this._module.Functions.FirstOrDefault(function => function.Name == callee) is not { } function || index >= function.Parameters.Count)
        return false;

      var saved = this._function;
      this._function = function;
      var written = this.WritesView(function.Parameters[index]);
      this._function = saved;
      return written;
    }

    /// <summary>A view as a slice: a variable as it is, any other view or array borrowed.</summary>
    private RsExpression ViewSource(IrExpr view, bool mutable) => view switch {
      IrLocalRef { Type.IsView: true } reference => new RsPath(this._locals[reference.Local]) { Type = view.Type },
      IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice } operation => this.View(operation, mutable),
      { Type.IsArray: true } => this.Borrow(view, mutable),
      _ => this.Refuse("a view of " + view.GetType().Name[2..])
    };

    /// <summary><c>ViewOf(source)</c>, <c>ViewOf(source, start)</c>, <c>ViewOf(source, start, length)</c> and the same of <c>Slice</c>.</summary>
    private RsExpression View(IrIntrinsic operation, bool mutable) {
      var source = this.ViewSource(operation.Arguments[0], mutable);
      if (operation.Arguments.Count == 1)
        return source;

      var helper = (operation.Arguments.Count == 2 ? "ply_view_from" : "ply_view") + (mutable ? "_mut" : "");
      return new RsCall(new RsPath(this.StringHelper(helper)), [source, .. operation.Arguments.Skip(1).Select(this.Expression)]) { Type = operation.Type };
    }

  }

}
