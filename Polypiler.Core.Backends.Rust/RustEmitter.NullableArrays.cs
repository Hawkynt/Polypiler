using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>Which arrays of the module may be null.</summary>
    private ArrayNullness _arrayNulls = ArrayNullness.Of(new IrModule("module", []));

    /// <summary>Whether an array variable of the function being written may hold null: an <c>Option</c>.</summary>
    private bool IsNullableArray(IrLocal local) => local.Type.IsArray && this._arrayNulls.MayBeNull(this._function.Name, local);

    /// <summary>Whether an array value of the function being written may be null.</summary>
    private bool IsNullableArray(IrExpr value) => value.Type.IsArray && this._arrayNulls.MayBeNull(this._function.Name, value);

    /// <summary><c>Option&lt;&amp;[T]&gt;</c> of a borrowed array that may be null, <c>Option&lt;Vec&lt;T&gt;&gt;</c> of an owned one.</summary>
    private string NullableArrayType(IrType array, bool borrowed)
      => borrowed ? $"Option<&[{this.ElementType(array)}]>" : $"Option<Vec<{this.ElementType(array)}>>";

    /// <summary>
    /// An array value where a place that may hold null takes it: <c>None</c> of null, <c>Some(..)</c> of
    /// an array that is one, a variable that may be null as it is - borrowed (<c>as_deref()</c> of a
    /// vector) or owned (<c>map(&lt;[T]&gt;::to_vec)</c> of a borrow, <c>clone()</c> of a vector).
    /// </summary>
    private RsExpression NullableArray(IrExpr value, bool borrowed) {
      switch (value) {
        case IrNull:
          return new RsPath("None") { Type = value.Type };
        case IrConditional choice:
          return this.Choice(choice, branch => this.NullableArray(branch, borrowed));
        case IrBind bind:
          return this.Bound(bind, _ => this.NullableArray(new IrLocalRef(bind.Local), borrowed));
        case var _ when !this.IsNullableArray(value):
          return new RsCall(new RsPath("Some"), [borrowed ? this.Borrow(value, false) : this.Vector(value, true)]) { Type = value.Type };
        case IrLocalRef { Local: var local }: {
          var path = new RsPath(this._locals[local]) { Type = value.Type };
          var held = this.IsReference(local);
          return (borrowed, held) switch {
            (true, true) => path,
            (true, false) => new RsMethodCall(path, "as_deref", []) { Type = value.Type },
            (false, true) => new RsMethodCall(path, "map", [new RsPath($"<[{this.ElementType(value.Type)}]>::to_vec")]) { Type = value.Type },
            _ => new RsMethodCall(path, "clone", []) { Type = value.Type }
          };
        }
        case IrCall when !borrowed:
          return this.Expression(value);
        default:
          return this.Refuse("a borrow of an array that may be null and is made in place");
      }
    }

    /// <summary>
    /// An array that may be null used as one, which traps with <c>NullReference</c> on null: the slice
    /// itself, <c>*ply_non_null(&amp;a)</c> of a borrow, <c>ply_non_null(&amp;a).as_slice()</c> of a
    /// vector; or, as the target of an index or of <c>len()</c>, <c>ply_non_null(&amp;a)</c>.
    /// </summary>
    private RsExpression NonNullArray(IrExpr value, bool slice) {
      switch (value) {
        case IrBind bind:
          return this.Bound(bind, _ => this.NonNullArray(new IrLocalRef(bind.Local), slice));
        case IrLocalRef { Local: var local }: {
          var nonNull = this.NonNull(new RsPath(this._locals[local]) { Type = value.Type });
          if (!slice)
            return nonNull;

          return this.IsReference(local) ? new RsUnary("*", nonNull) { Type = value.Type } : new RsMethodCall(nonNull, "as_slice", []) { Type = value.Type };
        }
        case IrCall: {
          var made = this.Checked(this.Expression(value), IrTrap.NullReference);
          return slice ? new RsReference(false, made) { Type = value.Type } : made;
        }
        default:
          return this.Refuse("an array that may be null of the form " + value.GetType().Name[2..]);
      }
    }

    /// <summary>A test of an array for null: <c>a.is_none()</c>, or <c>a.is_some()</c> negated; <c>false</c> of one that never is.</summary>
    private RsExpression ArrayIsNull(IrExpr value, bool negated) {
      if (value is IrNull)
        return new RsLiteral(negated ? "false" : "true") { Type = IrType.Bool };
      if (!this.IsNullableArray(value))
        return value is IrLocalRef ? new RsLiteral(negated ? "true" : "false") { Type = IrType.Bool } : this.Refuse("a test for null of an array that is made");

      return this.Bound(value, place => new RsMethodCall(this.Receiver(place), negated ? "is_some" : "is_none", []) { Type = IrType.Bool });
    }

  }

}
