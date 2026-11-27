using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.VisualBasic.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.VisualBasic;

public static partial class VisualBasicEmitter {

  private sealed partial class Writer {

    /// <summary>The handlers in force where a statement is written, innermost last: the label of each try's catch dispatch.</summary>
    private readonly List<string> _handlers = [];

    /// <summary>The variables that hold the number of the error each enclosing catch clause runs for, innermost last.</summary>
    private readonly List<string> _caught = [];

    /// <summary>The run-time error number a library exception class is raised with.</summary>
    private static int? NumberOf(IrClassType type) {
      if (type == IrExceptionClass.DivideByZeroException)
        return 11;
      if (type == IrExceptionClass.OverflowException)
        return 6;
      if (type == IrExceptionClass.IndexOutOfRangeException)
        return 9;
      if (type == IrExceptionClass.InvalidCastException)
        return 13;
      if (type == IrExceptionClass.NullReferenceException)
        return 91;
      if (type == IrExceptionClass.ArgumentException || type == IrExceptionClass.ArgumentOutOfRangeException)
        return 5;
      return null;
    }

    /// <summary>The test whether the number of an error is one a catch clause of a library class selects.</summary>
    private VbExpression? Selects(IrClassType type, VbExpression number) {
      if (type == IrExceptionClass.Exception)
        return new VbBoolean(true);
      if (type == IrExceptionClass.ArithmeticException)
        return new VbBinary(new VbBinary(number, "=", new VbInteger(6)), "Or", new VbBinary(number, "=", new VbInteger(11)));
      return NumberOf(type) is { } value ? new VbBinary(number, "=", new VbInteger(value)) : null;
    }

    /// <summary>The handler a statement after the innermost try is written under: the next outer one, or none (<c>0</c>).</summary>
    private string Outer(int depth) => depth > 0 ? this._handlers[depth - 1] : "0";

    /// <summary>
    /// <c>throw new E()</c> of a library exception: <c>Err.Raise</c> with the number of its class. A
    /// rethrow raises the number its catch clause ran for again.
    /// </summary>
    private void Throw(IrExpr exception) {
      if (exception is IrNew { Type.Declaration: IrClassType type, Arguments.Count: 0 } && NumberOf(type) is { } number) {
        this._out.Add(new VbCallStatement(new VbMember(new VbName("Err"), "Raise"), [new VbInteger(number)]));
        return;
      }

      this.Refuse(InexpressibleDescriptor, "the throw of an exception other than a new library exception without a message");
    }

    private void Rethrow() {
      if (this._caught.Count == 0) {
        this.ControlRefused("rethrow outside a catch clause");
        return;
      }

      this._out.Add(new VbCallStatement(new VbMember(new VbName("Err"), "Raise"), [new VbName(this._caught[^1])]));
    }

    /// <summary>
    /// A try as Visual Basic's <c>On Error GoTo</c>: the body under a handler whose label takes the
    /// number of the error and resumes - which ends the handler and clears <c>Err</c> - with a dispatch
    /// that runs the first catch clause whose class the number stands for, or raises the number again.
    /// After the try, and before every jump out of it, the handler of the enclosing try is in force again.
    /// </summary>
    private void Try(IrTry attempt) {
      if (attempt.Finally is not null) {
        this.ControlRefused("try with finally");
        return;
      }

      var depth = this._handlers.Count;
      var handler = this.Label("Catch");
      var dispatch = this.Label("Dispatch");
      var after = this.Label("EndTry");
      this._out.Add(new VbOnError(handler));
      this._handlers.Add(handler);
      var body = this.Statements(attempt.Body);
      this._handlers.RemoveAt(depth);
      this._out.AddRange(body);
      this._out.Add(new VbOnError(this.Outer(depth)));
      this._out.Add(new VbGoTo(after));
      var number = this.Hold(IrType.I32, "errNumber");
      this._out.Add(new VbLabel(handler));
      this._out.Add(new VbAssign(new VbName(number), new VbMember(new VbName("Err"), "Number")));
      this._out.Add(new VbResume(dispatch));
      this._out.Add(new VbLabel(dispatch));
      this._out.Add(new VbOnError(this.Outer(depth)));
      var branches = new List<VbIfBranch>();
      List<VbStatement>? otherwise = null;
      foreach (var clause in attempt.Catches) {
        if (clause.Filter is not null || clause.Local is { } local && IrTree.Nodes(clause.Body).OfType<IrLocalRef>().Any(reference => reference.Local == local)) {
          this.ControlRefused("a catch clause with a filter or that reads the exception");
          continue;
        }

        if (this.Selects(clause.Class, new VbName(number)) is not { } test) {
          this.Refuse(InexpressibleDescriptor, $"the catch clause of '{clause.Class.Name}', which no run-time error number stands for");
          continue;
        }

        this._caught.Add(number);
        var statements = this.Statements(clause.Body);
        this._caught.RemoveAt(this._caught.Count - 1);
        if (test is VbBoolean { Value: true }) {
          otherwise = statements;
          break;
        }

        branches.Add(new(test, statements));
      }

      otherwise ??= [new VbCallStatement(new VbMember(new VbName("Err"), "Raise"), [new VbName(number)])];
      this._out.AddRange(branches.Count == 0 ? otherwise : [new VbIf(branches, otherwise)]);
      this._out.Add(new VbLabel(after));
    }

    /// <summary>Puts the handler of a loop's level in force again before a jump that leaves the tries inside the loop.</summary>
    private void LeaveTries(int depth) {
      if (this._handlers.Count > depth)
        this._out.Add(new VbOnError(this.Outer(depth)));
    }

  }

}
