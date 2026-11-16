using System.Collections.Generic;
using Polypiler.Core.Backends.Rust.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>Whether a type is an optional value Rust writes as <c>Option&lt;T&gt;</c>: one whose underlying type is a value.</summary>
    private static bool IsOptionalValue(IrType type) => type.IsOptional && type.Underlying is { } underlying && IsValue(underlying);

    /// <summary>
    /// An optional value's operation: <c>Some(v)</c>, <c>None</c>, <c>x.is_none()</c>; the value of a
    /// tested one <c>x.unwrap_or_default()</c>, which cannot differ from it, of an untested one
    /// <c>ply_value(&amp;x)</c>, which panics with <c>MissingValue</c> - a function, which clippy's
    /// <c>unnecessary_unwrap</c> does not see through after a test of the variable; <c>??</c>
    /// <c>unwrap_or</c> of a fallback that is a variable or a constant, <c>unwrap_or_else(|| f)</c> of
    /// any other, which is evaluated only when needed (<c>or</c>, <c>or_else</c> where the fallback is
    /// optional itself).
    /// </summary>
    private RsExpression Optional(IrExpr expression) {
      switch (expression) {
        case IrSome some:
          return new RsCall(new RsPath("Some"), [this.Expression(some.Value)]) { Type = some.Type };
        case IrNone none:
          return new RsPath("None") { Type = none.Type };
        case IrIsNone test:
          return this.Bound(test.Value, value => new RsMethodCall(this.Receiver(value), "is_none", []) { Type = IrType.Bool });
        case IrUnwrap { IsTested: true } unwrap:
          return this.Bound(unwrap.Value, value => new RsMethodCall(this.Receiver(value), "unwrap_or_default", []) { Type = unwrap.Type });
        case IrUnwrap unwrap:
          return this.Bound(unwrap.Value, value => new RsCall(new RsPath(this.StringHelper("ply_value")), [new RsReference(false, value)]) { Type = unwrap.Type });
        case IrCoalesce coalesce: {
          var optional = coalesce.Type.IsOptional;
          var value = this.Expression(coalesce.Value);
          if (Eager(coalesce.Fallback))
            return this.Method(value, optional ? "or" : "unwrap_or", coalesce.Type, this.Expression(coalesce.Fallback));

          return this.Method(value, optional ? "or_else" : "unwrap_or_else", coalesce.Type, new RsClosure(false, [], this.Expression(coalesce.Fallback)));
        }
        case IrBind bind:
          return this.Bound(bind, value => value);
        default:
          return this.Refuse(expression.GetType().Name[2..]);
      }
    }

    /// <summary>
    /// An operation on a value that is a bind left inside an expression: the assignment, then the
    /// operation on the variable, as a block, <c>{ t = v; t.is_none() }</c>; any other value as it is.
    /// </summary>
    private RsExpression Bound(IrExpr value, System.Func<RsExpression, RsExpression> operation) {
      if (value is not IrBind bind)
        return operation(this.Expression(value));

      var place = new RsPath(this._locals[bind.Local]) { Type = bind.Local.Type };
      var assignment = new RsExpressionStatement(this.Assignment(bind.Local, bind.Value));
      return new RsBlockExpression(new RsBlock([assignment], operation(place))) { Type = value.Type };
    }

    /// <summary>Whether a fallback may be evaluated before it is needed: a variable, a constant, <c>None</c> - what clippy's <c>unnecessary_lazy_evaluations</c> wants eager.</summary>
    private bool Eager(IrExpr fallback) => fallback switch {
      IrConst or IrConstRef or IrNone => true,
      IrLocalRef reference => !this._shared.Contains(reference.Local),
      IrSome some => this.Eager(some.Value),
      _ => false
    };

  }

}
