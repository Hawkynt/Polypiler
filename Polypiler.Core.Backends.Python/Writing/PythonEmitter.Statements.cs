using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  private sealed partial class Writer {

    /// <summary>A loop or switch the statements being written are inside, innermost last.</summary>
    private abstract record Frame;

    /// <summary>A loop: what a <c>continue</c> of it has to do first.</summary>
    /// <param name="Test">The test of a post-test loop, which a <c>continue</c> evaluates before it goes on.</param>
    /// <param name="Step">The step of a <c>for</c> written as <c>while</c>, which a <c>continue</c> runs before it goes on.</param>
    private sealed record LoopFrame(IrExpr? Test, IrBlock? Step) : Frame;

    /// <summary>A switch; <paramref name="Wrapped"/> when it stands in a loop of one pass, which a <c>break</c> of the switch leaves.</summary>
    private sealed record SwitchFrame(bool Wrapped) : Frame;

    private readonly Dictionary<IrLocal, string> _locals = [];
    private readonly HashSet<string> _localNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _annotated = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _assignedGlobals = new(StringComparer.Ordinal);
    private readonly HashSet<IrLocal> _nonNegative = [];
    private readonly List<Frame> _frames = [];
    private int _temporaries;

    /// <summary>Forgets the names of the function written before.</summary>
    private void BeginFunction(string name) {
      this._function = name;
      this._locals.Clear();
      this._localNames.Clear();
      this._annotated.Clear();
      this._assignedGlobals.Clear();
      this._nonNegative.Clear();
      this._frames.Clear();
      this._temporaries = 0;
    }

    /// <summary>The Python name of a parameter or local: one per IR variable, none of them a name of the module.</summary>
    private string LocalName(IrLocal local) {
      if (this._locals.TryGetValue(local, out var name))
        return name;

      this.Annotation(local.Type);
      name = PythonNaming.Free(PythonNaming.Local(local.Name), candidate => this._localNames.Contains(candidate) || this._moduleNames.Contains(candidate));
      this._localNames.Add(name);
      this._locals[local] = name;
      return name;
    }

    /// <summary>A variable of the function's own for a value computed once.</summary>
    private string Temporary() {
      var name = PythonNaming.Free("_t" + (++this._temporaries).ToString(System.Globalization.CultureInfo.InvariantCulture), candidate => this._localNames.Contains(candidate) || this._moduleNames.Contains(candidate));
      this._localNames.Add(name);
      return name;
    }

    /// <summary>The statements of nested blocks in one list: Python's locals belong to the function, not to a block.</summary>
    private static List<IrStmt> Flattened(IEnumerable<IrStmt> statements) {
      var flat = new List<IrStmt>();
      foreach (var statement in statements)
        if (statement is IrBlock block)
          flat.AddRange(Flattened(block.Statements));
        else
          flat.Add(statement);

      return flat;
    }

    private List<PyStatement> Statements(IEnumerable<IrStmt> statements) {
      var flat = Flattened(statements);
      var written = new List<PyStatement>();
      for (var index = 0; index < flat.Count; ++index) {
        if (flat[index] is IrDeclare { Initializer: not null } declaration && index + 1 < flat.Count && Counted(flat[index + 1]) is { } loop
          && this.RangeLoop(declaration, loop.Condition, loop.Body, loop.Step, flat.Skip(index + 2).ToList()) is { } counted) {
          written.Add(counted);
          ++index;
          continue;
        }

        this.Statement(flat[index], written);
      }

      return written;
    }

    private PyAssign Assign(string name, PyExpression value, IrType type) {
      var annotation = this._annotated.Add(name) ? this.Annotation(type) : null;
      return new PyAssign(Name(name), value) { Annotation = annotation };
    }

    private void Statement(IrStmt statement, List<PyStatement> written) {
      switch (statement) {
        case IrDeclare { Initializer: null } declaration:
          var declared = this.LocalName(declaration.Local);
          if (this._annotated.Add(declared))
            written.Add(new PyAnnotation(declared, this.Annotation(declaration.Local.Type)));

          break;
        case IrDeclare declaration:
          written.Add(this.Assign(this.LocalName(declaration.Local), this.Expression(declaration.Initializer!), declaration.Local.Type));
          break;
        case IrAssign assignment:
          written.Add(this.Assign(this.LocalName(assignment.Local), this.Expression(assignment.Value), assignment.Local.Type));
          break;
        case IrGlobalAssign assignment:
          var global = this._globals[assignment.Global.Name];
          this._assignedGlobals.Add(global);
          written.Add(new PyAssign(Name(global), this.Expression(assignment.Value)));
          break;
        case IrStore store:
          this.Store(store, written);
          break;
        case IrExprStmt { Expression: IrStep step }:
          this.Step(step, written);
          break;
        case IrExprStmt expression:
          written.Add(new PyExpressionStatement(this.Expression(expression.Expression)));
          break;
        case IrIf conditional:
          written.Add(this.If(conditional));
          break;
        case IrWhile loop:
          written.Add(new PyWhile(this.Expression(loop.Condition), this.Inside(new LoopFrame(null, null), loop.Body.Statements)));
          break;
        case IrDoWhile loop:
          // A post-test loop: the body first, then the test, which a continue evaluates as well.
          var body = this.Inside(new LoopFrame(loop.Condition, null), loop.Body.Statements);
          if (loop.Condition is not IrConst { Value: not 0 })
            body.Add(new PyIf(Not(this.Expression(loop.Condition)), [new PyBreak()], []));

          written.Add(new PyWhile(new PyBool(true), body));
          break;
        case IrFor loop:
          // A for whose counter is no plain range: a while whose every pass ends with the step, which a continue runs as well.
          var passes = this.Inside(new LoopFrame(null, loop.Step), loop.Body.Statements);
          passes.AddRange(this.Statements(loop.Step.Statements));
          written.Add(new PyWhile(this.Expression(loop.Condition), passes));
          break;
        case IrSwitch selection:
          this.Switch(selection, written);
          break;
        case IrBreak:
          // A break of a loop, or of a switch in a loop of one pass: Eliminated took every other one out.
          written.Add(new PyBreak());
          break;
        case IrContinue:
          this.Continue(written);
          break;
        case IrReturn @return:
          written.Add(new PyReturn(@return.Value is null ? null : this.Expression(@return.Value)));
          break;
        case IrThrow { Exception: IrNew { Type.ClassType: { IsLibrary: true } type, Arguments: var arguments } } when arguments is [] or [IrStrConst]:
          // An exception of the library is the built-in exception of the trap table (docs/reference/exceptions.md).
          var message = arguments is [IrStrConst text] ? text.Value : IrExceptionClass.DefaultMessage(type);
          written.Add(new PyRaise(new PyCall(Name(BuiltinException(type)), [new PyString(message)])));
          break;
        default:
          this.Inexpressible(KindOf(statement));
          break;
      }
    }

    /// <summary>The built-in exception of Python a class of the IR's library is raised as.</summary>
    private static string BuiltinException(IrClassType type) => type.Name switch {
      "ArithmeticException" => "ArithmeticError",
      "DivideByZeroException" => "ZeroDivisionError",
      "OverflowException" => "OverflowError",
      "IndexOutOfRangeException" => "IndexError",
      "NullReferenceException" => "AttributeError",
      "InvalidCastException" => "TypeError",
      "InvalidOperationException" => "RuntimeError",
      "AssertionException" => "AssertionError",
      "KeyNotFoundException" => "KeyError",
      "FormatException" or "ArgumentException" or "ArgumentOutOfRangeException" => "ValueError",
      _ => "Exception"
    };

    /// <summary>The statements of a loop body or a switch section, written inside its frame.</summary>
    private List<PyStatement> Inside(Frame frame, IEnumerable<IrStmt> statements) {
      this._frames.Add(frame);
      try {
        return this.Statements(statements);
      } finally {
        this._frames.RemoveAt(this._frames.Count - 1);
      }
    }

    /// <summary>
    /// <c>continue</c> of the innermost loop, after the step of a <c>for</c> written as <c>while</c>,
    /// or after the test of a post-test loop, which leaves the loop when it fails.
    /// </summary>
    private void Continue(List<PyStatement> written) {
      // The verifier admits a continue only inside a loop.
      var index = this._frames.FindLastIndex(frame => frame is LoopFrame);
      if (this._frames.Skip(index + 1).Any(frame => frame is SwitchFrame { Wrapped: true }))
        this.Refuse(ControlUnsupportedDescriptor, "control construct 'a continue inside a switch that is left early' has no Python form");

      var loop = (LoopFrame)this._frames[index];
      if (loop.Step is { } step)
        written.AddRange(this.Statements(step.Statements));

      if (loop.Test is { } test && test is not IrConst { Value: not 0 })
        written.Add(new PyIf(Not(this.Expression(test)), [new PyBreak()], []));

      written.Add(new PyContinue());
    }

    /// <summary><c>if</c>, with an <c>else</c> that holds one <c>if</c> printed as <c>elif</c>; an empty branch disappears.</summary>
    private PyStatement If(IrIf conditional) {
      var test = this.Expression(conditional.Condition);
      var then = this.Statements(conditional.Then.Statements);
      var otherwise = conditional.Else is { } branch ? this.Statements(branch.Statements) : [];
      if (then.Count == 0 && otherwise.Count == 0)
        return new PyExpressionStatement(test);

      return then.Count == 0 ? new PyIf(Not(test), otherwise, []) : new PyIf(test, then, otherwise);
    }

    /// <summary>
    /// A write to an array element. Python evaluates the value before the place, the IR the array and
    /// the index first: where the value can have an effect or trap and the place is no variable or
    /// constant, the array and the index are computed into variables of their own first.
    /// </summary>
    private void Store(IrStore store, List<PyStatement> written) {
      var array = this.Expression(store.Array);
      var index = this.Expression(store.Index);
      if ((!IsPlain(store.Array) || !IsPlain(store.Index)) && !IsQuiet(store.Value)) {
        var (arrayName, indexName) = (this.Temporary(), this.Temporary());
        written.Add(new PyAssign(Name(arrayName), array));
        written.Add(new PyAssign(Name(indexName), index));
        (array, index) = (Name(arrayName), Name(indexName));
      }

      // The index is checked after the value is computed, as the IR checks it.
      var place = new PySubscript(array, this.NonNegative(store.Index) ? index : this.Helper(PythonHelpers.Index, index));
      written.Add(new PyAssign(place, this.Expression(store.Value)));
    }

    /// <summary>A variable or a constant: reading it has no effect and cannot trap.</summary>
    private static bool IsPlain(IrExpr expression) => expression is IrLocalRef or IrConst or IrConstRef;

    /// <summary>
    /// Whether an expression can neither trap nor have an effect, so that Python may evaluate it at
    /// any point: variables, constants, comparisons, and the operators that wrap or are exact on such
    /// operands.
    /// </summary>
    private static bool IsQuiet(IrExpr expression) => expression switch {
      IrLocalRef or IrConst or IrConstRef or IrGlobalRef => true,
      IrUnary unary => unary.Op != IrUnaryOp.NegateChecked && IsQuiet(unary.Operand),
      IrBinary binary => !IrOperators.Traps(binary.Op) && (binary.Left.Type.IsFloat || binary.Op is not (IrBinaryOp.Divide or IrBinaryOp.Remainder))
        && IsQuiet(binary.Left) && IsQuiet(binary.Right),
      IrConvert conversion => (conversion.Contract == IrConversion.Wrap || !conversion.Operand.Type.IsFloat && conversion.Contract != IrConversion.Trap) && IsQuiet(conversion.Operand),
      IrConditional choice => IsQuiet(choice.Condition) && IsQuiet(choice.WhenTrue) && IsQuiet(choice.WhenFalse),
      _ => false
    };

    /// <summary>A step as a statement: an assignment of the stepped value to the local or the array element, the only targets of a step.</summary>
    private void Step(IrStep step, List<PyStatement> written) {
      if (step.Target is IrLocalRef reference) {
        var name = this.LocalName(reference.Local);
        written.Add(new PyAssign(Name(name), this.Stepped(Name(name), step)));
        return;
      }

      var load = (IrArrayLoad)step.Target;
      var array = this.Expression(load.Array);
      var index = this.Expression(load.Index);
      if (!IsPlain(load.Array) || !IsPlain(load.Index)) {
        var (arrayName, indexName) = (this.Temporary(), this.Temporary());
        written.Add(new PyAssign(Name(arrayName), array));
        written.Add(new PyAssign(Name(indexName), index));
        (array, index) = (Name(arrayName), Name(indexName));
      }

      // The element is read, and its index checked, before the new value is stored.
      var read = new PySubscript(array, this.NonNegative(load.Index) ? index : this.Helper(PythonHelpers.Index, index));
      written.Add(new PyAssign(new PySubscript(array, index), this.Stepped(read, step)));
    }

  }

}
