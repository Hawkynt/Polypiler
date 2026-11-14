using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The expressions of optional values as <c>Nullable&lt;T&gt;</c>, which every profile has (C# 2):
    /// none is <c>null</c>, a value its own, the test <c>x == null</c>, the value <c>x.Value</c> - which
    /// throws <c>InvalidOperationException</c>, the trap - and <c>x ?? y</c>. Where a value stands
    /// without a type to convert to, as a branch of a conditional, it is cast to <c>T?</c>. A
    /// conditional of a <c>null</c> test of a variable and a member of it is <c>r?.m</c> from C# 6 on.
    /// </summary>
    private (CsExpression Node, int Binding) Optional(IrExpr expression) {
      switch (expression) {
        case IrNull:
          return (new CsNullLiteral(), Primary);
        case IrNone none:
          return (new CsCast(new CsType(this.TypeName(none.Type)), new CsNullLiteral()), Cast);
        case IrSome some:
          return (new CsCast(new CsType(this.TypeName(some.Type)), this.Operand(some.Value, Cast)), Cast);
        case IrIsNone test:
          return (new CsBinary(this.Operand(test.Value, Unary), "==", new CsNullLiteral()), Loose);
        case IrIsNull test:
          return (new CsBinary(this.Operand(test.Value, Unary), "==", new CsNullLiteral()), Loose);
        case IrUnwrap unwrap:
          return (new CsMemberAccess(this.Operand(unwrap.Value, Primary), "Value"), Primary);
        case IrCoalesce coalesce:
          var value = this.Operand(coalesce.Value, Unary);
          return (new CsBinary(value, "??", this.Operand(coalesce.Fallback, Unary)), Loose);
        case IrBind bind:
          var assigned = new CsAssignment(this.Variable(bind.Local), "=", this.Stored(bind.Value)) { Type = bind.Type };
          return (new CsParenthesized(assigned) { Type = bind.Type }, Primary);
        default:
          var choice = (IrConditional)expression;
          if (this.NullConditional(choice) is { } access)
            return (access, Primary);

          var condition = this.Operand(choice.Condition, Unary);
          var whenTrue = this.Operand(choice.WhenTrue, Unary);
          return (new CsConditional(condition, whenTrue, this.Operand(choice.WhenFalse, Unary)), Loose);
      }
    }

    /// <summary>
    /// <c>r?.Length</c>, <c>r?[i]</c> or <c>r?.f</c> where the profile has the operator and the
    /// conditional is exactly what it means: a variable tested for <c>null</c>, nothing when it is,
    /// and the member of it otherwise; <see langword="null"/> for any other conditional.
    /// </summary>
    private CsExpression? NullConditional(IrConditional choice) {
      if (choice is not { Condition: IrIsNull { Value: IrLocalRef { Local: var local } receiver }, WhenTrue: IrNone or IrNull } || local == this._receiver
          || !this.Has(CSharpFeature.NullConditional))
        return null;

      var member = choice.WhenFalse is IrSome some ? some.Value : choice.WhenFalse;
      if (choice.WhenFalse is not IrSome && choice.Type.IsOptional)
        return null;

      return member switch {
        IrArrayLength { Array: IrLocalRef { Local: var array } } when array == local
          => new CsMemberAccess(this.Variable(local), "Length") { Conditional = true, Type = choice.Type },
        IrArrayLoad { Array: IrLocalRef { Local: var array } } load when array == local && !this.Segmented(receiver)
          => this.Element(receiver, load.Index) with { Conditional = true, Type = choice.Type },
        IrFieldLoad { Target: IrLocalRef { Local: var target } } load when target == local && receiver.Type.IsObject
          => new CsMemberAccess(this.Variable(local), this.ObjectFieldName(load.Field, receiver)) { Conditional = true, Type = choice.Type },
        _ => null
      };
    }

    /// <summary>
    /// <c>x ??= v;</c> from C# 8 on, for the statement that assigns a variable only when it holds
    /// nothing; <see langword="null"/> for any other statement, or a profile below.
    /// </summary>
    private CsExpression? CoalescingAssignment(IrIf conditional) {
      if (conditional is not { Else: null, Then.Statements: [IrAssign assign] } || !this.Has(CSharpFeature.NullCoalescingAssignment))
        return null;

      var tested = conditional.Condition switch {
        IrIsNone test => test.Value,
        IrIsNull test => test.Value,
        _ => null
      };
      if (tested is not IrLocalRef { Local: var local } || local != assign.Local || local == this._receiver || local == this._value)
        return null;

      var value = assign.Value is IrSome some ? some.Value : assign.Value;
      return new CsAssignment(this.Variable(local), "??=", this.Stored(value));
    }

  }

}
