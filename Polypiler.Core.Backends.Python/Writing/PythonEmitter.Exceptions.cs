using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Python.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Python;

public static partial class PythonEmitter {

  /// <summary>
  /// The Python class each exception class of the IR's library is (<c>docs/reference/exceptions.md</c>):
  /// a built-in exception where one has the meaning - <c>ValueError</c> for <c>ArgumentException</c>,
  /// which Python raises for an argument it does not accept - and otherwise a class the module declares
  /// that derives from the built-in exception of the nearest meaning: <c>ArgumentOutOfRangeException</c>
  /// and <c>FormatException</c> from <c>ValueError</c>, <c>InvalidCastException</c> from <c>TypeError</c>.
  /// A caller still sees the built-in exception; a catch of <c>ArgumentException</c> leaves the
  /// <c>FormatError</c> out, which is no argument exception in the IR.
  /// </summary>
  /// <param name="type">A class of <see cref="IrExceptionClass.All"/>.</param>
  /// <returns>The name of the Python class.</returns>
  public static string ExceptionClass(IrClassType type) {
    ArgumentNullException.ThrowIfNull(type);
    return type.Name switch {
      "ArithmeticException" => "ArithmeticError",
      "DivideByZeroException" => "ZeroDivisionError",
      "OverflowException" => "OverflowError",
      "IndexOutOfRangeException" => "IndexError",
      "NullReferenceException" => "AttributeError",
      "InvalidCastException" => "InvalidCastError",
      "InvalidOperationException" => "RuntimeError",
      "KeyNotFoundException" => "KeyError",
      "ArgumentException" => "ValueError",
      "ArgumentOutOfRangeException" => "ArgumentOutOfRangeError",
      "FormatException" => "FormatError",
      _ => "Exception"
    };
  }

  /// <summary>The classes the module declares for library exceptions, each with the class it derives from, in the order they are declared.</summary>
  private static readonly (string Name, string Base)[] _declaredExceptions = [
    ("ArgumentOutOfRangeError", "ValueError"),
    ("FormatError", "ValueError"),
    ("InvalidCastError", "TypeError")
  ];

  private sealed partial class Writer {

    /// <summary>The exception classes of the library the module declares, by name.</summary>
    private readonly HashSet<string> _exceptions = new(StringComparer.Ordinal);

    /// <summary>The Python class of a library exception class, which the module declares where it is no built-in one.</summary>
    private string LibraryClass(IrClassType type) {
      var name = ExceptionClass(type);
      if (_declaredExceptions.Any(declared => declared.Name == name))
        this._exceptions.Add(name);

      return name;
    }

    /// <summary>The class a trap raises, as <see cref="LibraryClass"/> names it.</summary>
    private string TrapClass(IrTrap trap) => this.LibraryClass(IrExceptionClass.Of(trap));

    /// <summary>A raise of a trap: the Python class of its exception with the trap's own text, which tells two traps of one class apart.</summary>
    private PyStatement RaiseTrap(IrTrap trap) => new PyRaise(new PyCall(Name(this.TrapClass(trap)), [new PyString(IrTrapException.Describe(trap))]));

    /// <summary>The declarations of the library exception classes the module uses, each after its base.</summary>
    private IEnumerable<PyStatement> ExceptionClasses() {
      foreach (var (name, @base) in _declaredExceptions.Where(declared => this._exceptions.Contains(declared.Name)))
        yield return new PyClassDef(name, [Name(@base)], []);
    }

    /// <summary>The statements of exception handling; <see langword="false"/> for any other.</summary>
    private bool ExceptionStatement(IrStmt statement, List<PyStatement> written) {
      switch (statement) {
        case IrThrow { Exception: IrNew { Type.ClassType: { IsLibrary: true } type, Arguments: var arguments } } when arguments is [] or [IrStrConst]:
          // An exception of the library is the Python class of the trap table (docs/reference/exceptions.md).
          var message = arguments is [IrStrConst text] ? text.Value : IrExceptionClass.DefaultMessage(type);
          written.Add(new PyRaise(new PyCall(Name(this.LibraryClass(type)), [new PyString(message)])));
          return true;
        case IrThrow thrown:
          written.Add(new PyRaise(this.Expression(thrown.Exception)));
          return true;
        case IrRethrow:
          written.Add(new PyRaise(null));
          return true;
        case IrTry attempt:
          written.Add(this.Try(attempt));
          return true;
        case IrUsing scope:
          this.Using(scope, written);
          return true;
        default:
          return false;
      }
    }

    /// <summary>
    /// A try. Its catch clauses are <c>except</c> clauses in order where Python's classes select what
    /// the IR's do and no clause has a filter; otherwise one <c>except Exception as e:</c> whose body
    /// tests the clauses in order with <c>isinstance</c> and the filters, and raises again what none
    /// selects. A filter runs after the <c>finally</c> blocks inside the try, so one that might see the
    /// difference is refused (<see cref="IrExceptionFilter.MayRunLate"/>).
    /// </summary>
    private PyTry Try(IrTry attempt) {
      var body = this.Statements(attempt.Body.Statements);
      var final = attempt.Finally is null ? [] : this.Statements(attempt.Finally.Statements);
      if (attempt.Catches.Count == 0)
        return new PyTry(body, [], final);

      foreach (var clause in attempt.Catches.Where(clause => clause.Filter is not null && !IrExceptionFilter.MayRunLate(attempt, clause)))
        this.Refuse(InexpressibleDescriptor, $"the exception filter of a catch of {clause.Class.Name} runs before finally blocks; it cannot be expressed in Python profile '{profile.Name}'");

      var chained = attempt.Catches.Any(clause => clause.Filter is not null || clause.Class == IrExceptionClass.NullReferenceException || clause.Class == IrExceptionClass.ArgumentException);
      if (!chained) {
        var handlers = attempt.Catches.Select(clause => {
          var name = clause.Local is { } local ? this.LocalName(local) : null;
          if (name is not null)
            this._annotated.Add(name);

          var statements = this.Statements(clause.Body.Statements);
          if (clause.Local is { } bound && this.IsCell(bound))
            statements.Insert(0, new PyAssign(Name(name!), new PyList([Name(name!)])));

          return new PyExceptHandler(this.CaughtClasses(clause.Class), name, statements);
        }).ToList();
        return new PyTry(body, handlers, final);
      }

      var caught = attempt.Catches.Select(clause => clause.Local).FirstOrDefault(local => local is not null) is { } first && !this.IsCell(first)
        ? this.LocalName(first)
        : this.Temporary("_error");
      this._annotated.Add(caught);
      IReadOnlyList<PyStatement> chain = [new PyRaise(null)];
      for (var index = attempt.Catches.Count - 1; index >= 0; --index) {
        var clause = attempt.Catches[index];
        var statements = new List<PyStatement>();
        if (clause.Local is { } local && (this.LocalName(local) != caught || this.IsCell(local)))
          statements.Add(this.Declare(local, Name(caught)));

        statements.AddRange(this.Statements(clause.Body.Statements));
        PyExpression? test = clause.Class == IrExceptionClass.Exception ? null : this.Test(clause.Class, Name(caught));
        if (clause.Filter is { } filter) {
          // The filter reads the clause's variable, which is given the exception first.
          var condition = this.Expression(filter);
          if (clause.Local is { } named && (this.LocalName(named) != caught || this.IsCell(named))) {
            var bound = new PyNamedExpression(this.LocalName(named), this.IsCell(named) ? new PyList([Name(caught)]) : Name(caught));
            condition = new PyBoolOp(Compare(bound, "is not", new PyNone()), "and", condition);
          }

          test = test is null ? condition : new PyBoolOp(test, "and", condition);
        }

        chain = test is null ? statements : [new PyIf(test, statements.Count == 0 ? [new PyPass()] : statements, chain)];
      }

      return new PyTry(body, [new PyExceptHandler(Name("Exception"), caught, [.. chain])], final);
    }

    /// <summary>The Python classes an <c>except</c> clause names for a class of the IR.</summary>
    private PyExpression CaughtClasses(IrClassType type)
      => type.IsLibrary ? Name(this.LibraryClass(type)) : Name(this.TypeNameOf(type));

    /// <summary>
    /// The test that selects an exception of a class: <c>isinstance</c>; a <c>NullReferenceException</c>
    /// is Python's <c>AttributeError</c> or <c>TypeError</c> of a member of <c>None</c>, but no
    /// <c>InvalidCastError</c>.
    /// </summary>
    private PyExpression Test(IrClassType type, PyExpression caught) {
      if (type == IrExceptionClass.NullReferenceException)
        return new PyBoolOp(new PyCall(Name("isinstance"), [caught, new PyTuple([Name("AttributeError"), Name("TypeError")])]), "and",
          new PyUnary("not", new PyCall(Name("isinstance"), [caught, Name(this.LibraryClass(IrExceptionClass.InvalidCastException))])));

      if (type == IrExceptionClass.ArgumentException)
        return new PyBoolOp(new PyCall(Name("isinstance"), [caught, Name("ValueError")]), "and",
          new PyUnary("not", new PyCall(Name("isinstance"), [caught, Name(this.LibraryClass(IrExceptionClass.FormatException))])));

      return new PyCall(Name("isinstance"), [caught, this.CaughtClasses(type)]);
    }

    /// <summary>
    /// A resource scope as its lowering: the resource bound, then <c>try:</c> the body <c>finally:</c>
    /// <c>Dispose()</c> unless the resource is <c>None</c>.
    /// </summary>
    private void Using(IrUsing scope, List<PyStatement> written) {
      written.Add(this.Declare(scope.Resource, this.Expression(scope.Initializer)));
      var resource = this.Read(scope.Resource);
      var dispose = new PyExpressionStatement(new PyCall(new PyAttribute(resource, this.MethodName(scope.Dispose)), []));
      written.Add(new PyTry(this.Statements(scope.Body.Statements), [], [new PyIf(Compare(resource, "is not", new PyNone()), [dispose], [])]));
    }

  }

}
