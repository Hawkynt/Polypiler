using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The temporary that holds the left side of <c>??</c> below ECMAScript 2020, declared at the start of a function that needs it.</summary>
    private const string Coalesced = "$c";

    /// <summary>Whether the function being written uses <see cref="Coalesced"/>.</summary>
    private bool _coalesced;

    /// <summary>Whether the function being written is exported with an optional result whose source spelled none <c>undefined</c>.</summary>
    private bool _undefined;

    /// <summary>
    /// The expressions of optional values. Inside a module none is <c>null</c> and a value is itself;
    /// <c>x == null</c> tests both of JavaScript's absent values. <c>??</c> is the operator from
    /// ECMAScript 2020 on and a conditional on <c>!= null</c> below - never <c>||</c>, which would take
    /// 0 and false for absent. A lifted operation needs nothing here: the IR has written it out.
    /// </summary>
    private (JsExpression Node, bool Atomic) Optional(IrExpr expression) {
      switch (expression) {
        case IrNull or IrNone:
          return (new JsNull(), true);
        case IrSome some:
          return this.Expression(some.Value);
        case IrIsNone test:
          return (Infix(this.Operand(test.Value), "==", new JsNull()), false);
        case IrIsNull test:
          return (Infix(this.Operand(test.Value), "==", new JsNull()), false);
        case IrUnwrap { IsTested: true } unwrap:
          return this.Expression(unwrap.Value);
        case IrUnwrap unwrap:
          return (this.Helper("$unwrap", unwrap.Value), true);
        case IrCoalesce coalesce:
          return (this.Coalesce(coalesce), false);
        default:
          var bind = (IrBind)expression;
          return (new JsAssignment(this.Variable(bind.Local), "=", this.Value(bind.Value)), false);
      }
    }

    /// <summary>
    /// <c>a ?? b</c>, with <c>a?.length</c> or <c>a?.f</c> on the left where that is the conditional
    /// and the level has both operators: the <c>undefined</c> that <c>?.</c> gives is coalesced at once.
    /// Below ECMAScript 2020 the left side is tested and read: a variable as it is, anything else
    /// through the temporary <see cref="Coalesced"/>, which is read at once after it is assigned.
    /// </summary>
    private JsExpression Coalesce(IrCoalesce coalesce) {
      if (this.Native("??")) {
        var left = this.OptionalChain(coalesce.Value) ?? this.Operand(coalesce.Value);
        return Infix(left, "??", this.Operand(coalesce.Fallback));
      }

      var fallback = this.Operand(coalesce.Fallback);
      if (coalesce.Value is IrLocalRef or IrGlobalRef) {
        var value = this.Operand(coalesce.Value);
        return new JsConditional(Infix(value, "!=", new JsNull()), value, fallback);
      }

      this._coalesced = true;
      var assigned = Parenthesized(new JsAssignment(Id(Coalesced), "=", this.Value(coalesce.Value)));
      return new JsConditional(Infix(assigned, "!=", new JsNull()), Id(Coalesced), fallback);
    }

    /// <summary>
    /// <c>r?.length</c> or <c>r?.f</c> for the conditional that tests a variable for <c>null</c> and
    /// reads the length of the array or a field of the object it names, where the level has the operator;
    /// <see langword="null"/> for anything else.
    /// </summary>
    private JsExpression? OptionalChain(IrExpr value) {
      if (value is not IrConditional { Condition: IrIsNull { Value: IrLocalRef { Local: var local } receiver }, WhenTrue: IrNone or IrNull } choice
          || local == this._receiver || !this.Native("?."))
        return null;

      var member = choice.WhenFalse is IrSome some ? some.Value : choice.WhenFalse;
      return member switch {
        IrArrayLength { Array: IrLocalRef { Local: var array } } when array == local && !receiver.Type.Element!.IsLong
          => new JsMember(this.Variable(local), "length") { Optional = true, Type = value.Type },
        _ => null
      };
    }

    /// <summary><c>x ??= v;</c> from ECMAScript 2021 on, for the statement that assigns a variable only when it holds nothing; <see langword="null"/> otherwise.</summary>
    private JsExpression? CoalescingAssignment(IrIf conditional) {
      if (conditional is not { Else: null, Then.Statements: [IrAssign assign] } || !this.Native("??="))
        return null;

      var tested = conditional.Condition switch {
        IrIsNone test => test.Value,
        IrIsNull test => test.Value,
        _ => null
      };
      if (tested is not IrLocalRef { Local: var local } || local != assign.Local)
        return null;

      return new JsAssignment(this.Variable(local), "??=", this.Value(assign.Value));
    }

    /// <summary>
    /// The value an exported function returns whose source spelled none <c>undefined</c>: none is
    /// <c>void 0</c>, a value is itself, anything else is turned from <c>null</c> into <c>undefined</c>.
    /// </summary>
    private JsExpression Undefined(IrExpr value) => value switch {
      IrNone => new JsUnary("void", new JsNumber(0)),
      IrSome some => this.Value(some.Value),
      _ => this.Helper("$undef", value)
    };

  }

}
