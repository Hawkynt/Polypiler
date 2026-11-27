using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The nodes of the dynamic type, a pair <c>[tag, payload]</c> (<c>[0]</c> for <c>Empty</c>,
    /// <c>[1]</c> for <c>Null</c>): JavaScript's own values cannot tell an <c>i16</c> from an
    /// <c>f64</c>, which the tag does. The tag is the first element, a tested payload the second, an
    /// untested one the helper <c>$variant</c>, which traps on another tag. A tag whose payload is 64
    /// bits wide, a currency or a date has no form here.
    /// </summary>
    private (JsExpression Node, bool Atomic) Variant(IrExpr expression) {
      var tag = expression switch { IrVariantOf made => made.Tag, IrVariantValue value => value.Tag, _ => IrVariantTag.Empty };
      if (tag is IrVariantTag.Currency or IrVariantTag.Date or IrVariantTag.I64) {
        var what = $"a variant of the tag {tag}";
        if (this._refused.Add(what))
          this._problems.Add(InexpressibleDescriptor.At($"'{what}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));
        return (new JsNull(), true);
      }

      switch (expression) {
        case IrVariantOf made:
          var number = Number((int)made.Tag);
          return (new JsArray(made.Value is null ? [number] : [number, this.Value(made.Value)]), true);
        case IrVariantTagOf read:
          return (new JsIndex(this.Operand(read.Value), Number(0)), true);
        case IrVariantValue { IsTested: true } value:
          return (new JsIndex(this.Operand(value.Value), Number(1)), true);
        default:
          var untested = (IrVariantValue)expression;
          return (this.Helper("$variant", [this.Text(untested.Value), Number((int)untested.Tag)]), true);
      }
    }

  }

}
