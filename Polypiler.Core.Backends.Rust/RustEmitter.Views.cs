using System.Collections.Generic;
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
        case IrIntrinsicOp.ViewCopy:
          return this.ViewCopy(arguments[0], arguments[1]);
        default:
          return this.Refuse(operation.Op.ToString());
      }
    }

    /// <summary>
    /// A copy of one view into another: <c>ply_view_mut(target, 0, source.len() as i32).copy_from_slice(source)</c>,
    /// which panics with <c>ArgumentOutOfRange</c> for a shorter target before anything is copied, as the IR does;
    /// a source that is no variable is bound first, so that it is evaluated before the target. Two views of one
    /// array - which the IR copies as <c>memmove</c> and Rust cannot borrow shared and exclusive at once - are refused.
    /// </summary>
    private RsExpression ViewCopy(IrExpr source, IrExpr target) {
      if (this.ViewRootOf(source) is { } from && from == this.ViewRootOf(target))
        return this.CopyWithin(source, target) ?? this.Refuse("a view copied onto a view of the same array");

      var value = this.ViewSource(source, false);
      if (value is RsPath) {
        var length = this.Cast(new RsMethodCall(value, "len", []) { Type = IrType.U64 }, IrType.I32);
        var window = this.HelperCall("ply_view_mut", target.Type, [this.ViewSource(target, true), new RsLiteral("0"), length]);
        return new RsMethodCall(window, "copy_from_slice", [value]);
      }

      var name = this.Temporary("source");
      var bound = new RsPath(name) { Type = source.Type };
      var measured = this.Cast(new RsMethodCall(bound, "len", []) { Type = IrType.U64 }, IrType.I32);
      var into = this.HelperCall("ply_view_mut", target.Type, [this.ViewSource(target, true), new RsLiteral("0"), measured]);
      return new RsBlockExpression(new RsBlock([new RsLet(name, false, null, value)], new RsMethodCall(into, "copy_from_slice", [bound])));
    }

    /// <summary>A part of one variable a view expression names directly: the variable, and the start and length of the part where it has them.</summary>
    private static (IrLocal Root, IrExpr? Start, IrExpr? Length)? Part(IrExpr view) => view switch {
      IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var whole, var start] } when Whole(whole) is { } root => (root.Local, start, null),
      IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var whole, var start, var length] } when Whole(whole) is { } root => (root.Local, start, length),
      _ when Whole(view) is { } root => (root.Local, null, null),
      _ => null
    };

    /// <summary>The variable a view expression names whole: the variable, or a view of all of it.</summary>
    private static IrLocalRef? Whole(IrExpr view) => view switch {
      IrLocalRef reference => reference,
      IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var inner] } => Whole(inner),
      _ => null
    };

    /// <summary>
    /// A copy between two parts of one variable, which the IR does as <c>memmove</c>: the source's part checked
    /// and measured, then the target's, each through <c>ply_view</c> with its <c>ArgumentOutOfRange</c>; a
    /// longer source panics with the same trap; then <c>copy_within</c>. Every bound that is no variable or
    /// constant is bound first, in the IR's order. <see langword="null"/> where a part is no direct view of the variable.
    /// </summary>
    private RsExpression? CopyWithin(IrExpr source, IrExpr target) {
      if (Part(source) is not { } from || Part(target) is not { } to || from.Root != to.Root)
        return null;

      var statements = new List<RsStatement>();
      var root = new IrLocalRef(from.Root);
      RsExpression Bound(IrExpr value, string stem) {
        var expression = this.Expression(value);
        if (expression is RsPath or RsLiteral)
          return expression;

        var name = this.Temporary(stem);
        statements.Add(new RsLet(name, false, null, expression));
        return new RsPath(name) { Type = value.Type };
      }

      RsExpression Measured((IrLocal Root, IrExpr? Start, IrExpr? Length) part, string stem, out RsExpression start) {
        start = part.Start is null ? new RsLiteral("0") { Type = IrType.I32 } : Bound(part.Start, stem + "_start");
        var whole = this.ViewSource(root, false);
        // A method borrows its receiver itself: `a.len()`, never `&a.len()`.
        var view = part.Start is null ? (whole is RsReference { Operand: var owner } ? owner : whole)
          : part.Length is null ? this.HelperCall("ply_view_from", root.Type, [whole, start])
          : this.HelperCall("ply_view", root.Type, [whole, start, Bound(part.Length, stem + "_length")]);
        var name = this.Temporary(stem);
        statements.Add(new RsLet(name, false, null, new RsMethodCall(view, "len", []) { Type = IrType.U64 }));
        return new RsPath(name) { Type = IrType.U64 };
      }

      var count = Measured(from, "source", out var sourceStart);
      var room = Measured(to, "target", out var targetStart);
      statements.Add(new RsExpressionStatement(new RsIf(this.Binary(count, ">", room, IrType.Bool), new RsBlock([new RsExpressionStatement(Panic(IrTrap.ArgumentOutOfRange))], null), null)));
      var range = sourceStart is RsLiteral { Text: "0" } ? new RsRange(null, count, false) : new RsRange(Size(sourceStart), this.Binary(Size(sourceStart), "+", count, IrType.U64), false);
      var place = this.ViewSource(root, true) is RsReference { Operand: var vector } ? vector : this.ViewSource(root, true);
      statements.Add(new RsExpressionStatement(new RsMethodCall(place, "copy_within", [range, Size(targetStart)])));
      return new RsBlockExpression(new RsBlock(statements, null));
    }

    /// <summary>
    /// Two views that split one variable at a position - <c>l = a[0..m]</c>, then <c>r = a[m..]</c>, at least one
    /// of them written - as Rust's <c>split_at_mut</c>, which borrows both halves exclusively at once:
    /// <c>let (l, r) = a.split_at_mut(ply_view(a, 0, m).len());</c>, the position checked by <c>ply_view</c>
    /// with the IR's <c>ArgumentOutOfRange</c>. <see langword="null"/> for any other pair of statements.
    /// </summary>
    private RsStatement? SplitViews(IReadOnlyList<IrStmt> statements, int index) {
      if (index + 1 >= statements.Count
          || statements[index] is not IrDeclare { Local: var left, Initializer: IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var first, IrConst { Value: 0 }, var middle] } }
          || statements[index + 1] is not IrDeclare { Local: var right, Initializer: IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice, Arguments: [var second, var start] } }
          || Whole(first) is not { } root || Whole(second) is not { } other || other.Local != root.Local || left.Type.Kind != IrTypeKind.Span || right.Type.Kind != IrTypeKind.Span
          || !(this.WritesView(left) || this.WritesView(right)) || !SamePosition(middle, start)
          || Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Statements(this._function.Body).Any(statement => statement is IrAssign assign && (assign.Local == left || assign.Local == right)))
        return null;

      var measured = new RsMethodCall(this.HelperCall("ply_view", root.Type, [this.ViewSource(root, false), new RsLiteral("0"), this.Expression(middle)]), "len", []) { Type = IrType.U64 };
      var receiver = this.ViewSource(root, true) is RsReference { Operand: var vector } ? vector : this.ViewSource(root, true);
      return new RsLet($"({this._locals[left]}, {this._locals[right]})", false, null, new RsMethodCall(receiver, "split_at_mut", [measured]));
    }

    /// <summary>Whether two positions are the same value: one variable or constant, converted the same way.</summary>
    private static bool SamePosition(IrExpr first, IrExpr second) => (first, second) switch {
      (IrLocalRef a, IrLocalRef b) => a.Local == b.Local,
      (IrConst a, IrConst b) => a == b,
      (IrConvert a, IrConvert b) => a.Type == b.Type && a.Contract == b.Contract && SamePosition(a.Operand, b.Operand),
      _ => false
    };

    /// <summary>An <c>i32</c> position or length that is not negative as a <c>usize</c>.</summary>
    private RsExpression Size(RsExpression value) => value is RsLiteral literal ? literal with { Type = IrType.U64 } : new RsCast(this.Operand(value, 12), "usize") { Type = IrType.U64 };

    /// <summary>
    /// The variable whose memory a view expression reaches: the array of a view made of it, a view variable's
    /// own source where one declaration gives it its only value, else the variable itself; <see langword="null"/>
    /// for anything else.
    /// </summary>
    private IrLocal? ViewRootOf(IrExpr view) {
      switch (view) {
        case IrIntrinsic { Op: IrIntrinsicOp.ViewOf or IrIntrinsicOp.Slice } operation:
          return this.ViewRootOf(operation.Arguments[0]);
        case IrLocalRef { Local: var local } when local.Type.IsView:
          var declarations = Polypiler.Core.Backends.Rust.Emit.ArrayOwnership.Statements(this._function.Body).Where(statement => statement is IrDeclare declare && declare.Local == local
            || statement is IrAssign assign && assign.Local == local).ToList();
          return declarations is [IrDeclare { Initializer: { } initializer }] && this.ViewRootOf(initializer) is { } root ? root : local;
        case IrLocalRef { Local: var array }:
          return array;
        default:
          return null;
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
      IrIntrinsic { Op: IrIntrinsicOp.Reinterpret } overlay => this.Overlay(overlay, mutable),
      // A view of a new array of a known length borrows an array, not a vector (clippy's useless_vec).
      IrNewArray { Length: IrConst { Value: >= 0 and < 4096 } count } made => new RsReference(mutable, this.Zeros(made.Type.Element!, count.Value)) { Type = view.Type },
      { Type.IsArray: true } => this.Borrow(view, mutable),
      _ => this.Refuse("a view of " + view.GetType().Name[2..])
    };

    /// <summary><c>ViewOf(source)</c>, <c>ViewOf(source, start)</c>, <c>ViewOf(source, start, length)</c> and the same of <c>Slice</c>.</summary>
    private RsExpression View(IrIntrinsic operation, bool mutable) {
      var source = this.ViewSource(operation.Arguments[0], mutable);
      if (operation.Arguments.Count == 1)
        return source;

      var helper = (operation.Arguments.Count == 2 ? "ply_view_from" : "ply_view") + (mutable ? "_mut" : "");
      return this.HelperCall(helper, operation.Type, [source, .. operation.Arguments.Skip(1).Select(this.Expression)]);
    }

  }

}
