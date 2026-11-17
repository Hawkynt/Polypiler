using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  /// <summary>The Rust type of a string that may be null.</summary>
  public const string NullableString = "Option<Vec<u16>>";

  private sealed partial class Writer {

    /// <summary>Which strings of the module may be null.</summary>
    private StringNullness _stringNulls = StringNullness.Of(new IrModule("module", []));

    /// <summary>Whether a string variable of the function being written may hold null: an <c>Option&lt;Vec&lt;u16&gt;&gt;</c>.</summary>
    private bool IsNullableString(IrLocal local) => this._stringNulls.MayBeNull(this._function.Name, local);

    /// <summary>Whether a string value of the function being written may be null.</summary>
    private bool IsNullableString(IrExpr value) => value.Type.IsString && this._stringNulls.MayBeNull(this._function.Name, value);

    /// <summary>
    /// The place a string that may be null stands in, which it is read through without a copy: a
    /// variable, or the cell of a field borrowed, <c>o.name.borrow()</c>; <see langword="null"/> for a value made.
    /// </summary>
    private RsExpression? NullableStringPlace(IrExpr text) => text switch {
      IrLocalRef reference => new RsPath(this._locals[reference.Local]) { Type = text.Type },
      IrFieldLoad { Target.Type.IsObject: true } load when !this.IsPlain(load.Target.Type) => new RsMethodCall(this.FieldCell(load.Target, load.Field), "borrow", []) { Type = text.Type },
      _ => null
    };

    /// <summary>
    /// A string where a place that may hold null takes it, an <c>Option&lt;Vec&lt;u16&gt;&gt;</c> of its
    /// own: <c>None</c> of null, a copy of a place that may be null, <c>Some(..)</c> of a string that never is.
    /// </summary>
    private RsExpression NullableStringValue(IrExpr text) {
      switch (text) {
        case IrNull:
          return new RsPath("None") { Type = text.Type };
        case IrConditional choice:
          return this.Choice(choice, this.NullableStringValue);
        case IrBind bind:
          return this.Bound(bind, _ => this.NullableStringValue(new IrLocalRef(bind.Local)));
        case var _ when !this.IsNullableString(text):
          return new RsCall(new RsPath("Some"), [this.StringValue(text)]) { Type = text.Type };
        case var _ when this.NullableStringPlace(text) is { } place:
          return new RsMethodCall(this.Receiver(place), "clone", []) { Type = text.Type };
        default:
          return this.Expression(text);
      }
    }

    /// <summary>
    /// A string that may be null used as one, which traps with <c>NullReference</c> on null: of a place
    /// the code units it holds, <c>ply_non_null(&amp;x)</c> (a <c>&amp;Vec&lt;u16&gt;</c>), of a value made
    /// the vector, <c>f().expect("NullReference")</c>; with <paramref name="owned"/> always a vector of its own.
    /// </summary>
    private RsExpression NonNullString(IrExpr text, bool owned) {
      if (text is IrBind bind)
        return this.Bound(bind, _ => this.NonNullString(new IrLocalRef(bind.Local), owned));
      if (text is IrConditional choice)
        return this.Checked(this.Choice(choice, this.NullableStringValue), IrTrap.NullReference);
      if (this.NullableStringPlace(text) is not { } place)
        return this.Checked(this.Expression(text), IrTrap.NullReference);

      var held = this.NonNull(place);
      return owned ? new RsMethodCall(this.Receiver(held), "clone", []) { Type = text.Type } : held;
    }

    /// <summary>A string that may be null where a <c>&amp;[u16]</c> is taken: the code units of a place borrowed, a vector made borrowed for the statement.</summary>
    private RsExpression NonNullStringBorrow(IrExpr text) {
      var held = this.NonNullString(text, false);
      return this.NullableStringPlace(text) is not null && text is not (IrBind or IrConditional) ? held : new RsReference(false, this.Operand(held, 15)) { Type = IrType.String };
    }

    /// <summary>A string that may be null as an operand of an equality, <c>Option&lt;&amp;[u16]&gt;</c>: <c>x.as_deref()</c>, or <c>Some(s)</c> of one that never is.</summary>
    private RsExpression NullableStringOperand(IrExpr text) {
      if (!this.IsNullableString(text) || text is IrNull)
        return text is IrNull ? new RsPath("None") { Type = text.Type } : new RsCall(new RsPath("Some"), [this.StringSlice(text)]) { Type = text.Type };
      if (this.NullableStringPlace(text) is { } place)
        return new RsMethodCall(this.Receiver(place), "as_deref", []) { Type = text.Type };

      return new RsMethodCall(this.Operand(this.NullableStringValue(text), 15), "as_deref", []) { Type = text.Type };
    }

    /// <summary>A test of a string for null: <c>x.is_none()</c>, or <c>false</c> of one that never is.</summary>
    private RsExpression StringIsNull(IrExpr text, bool negated) {
      if (text is IrNull)
        return new RsLiteral(negated ? "false" : "true") { Type = IrType.Bool };
      if (!this.IsNullableString(text))
        return new RsLiteral(negated ? "true" : "false") { Type = IrType.Bool };
      if (text is IrBind bind)
        return this.Bound(bind, _ => this.StringIsNull(new IrLocalRef(bind.Local), negated));

      var held = this.NullableStringPlace(text) ?? this.Operand(this.Expression(text), 15);
      return new RsMethodCall(this.Receiver(held), negated ? "is_some" : "is_none", []) { Type = IrType.Bool };
    }

  }

}
