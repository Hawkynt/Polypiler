using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// Exceptions in JavaScript, at every level: a try whose catch clauses are one <c>catch (e)</c> that
/// tests them in order - <c>if (e instanceof A) … else if (e instanceof B) … else throw e;</c> - and
/// throws on what none selects; a filter joined to the test of its clause where
/// <see cref="IrExceptionFilter.MayRunLate"/> allows it and refused (<see cref="Inexpressible"/>)
/// otherwise; <c>throw;</c> as <c>throw e;</c>; a catch without a binding from ECMAScript 2019; the
/// inner exception as the <c>cause</c> of ECMAScript 2022, assigned below it. A class of the module
/// that derives from an exception class extends <c>Error</c> or the generated error class
/// (<see cref="ErrorClass"/>) and names itself; the message and the inner exception of the IR are
/// <c>message</c> and <c>cause</c>.
/// </summary>
public static partial class JavaScriptEmitter {

  /// <summary>
  /// The JavaScript class an exception class of the IR's library is: a generated error class such as
  /// <c>DivideByZeroError</c>, <c>Error</c> for <c>Exception</c>, or the engine's <c>TypeError</c> for
  /// <c>NullReferenceException</c> (<c>docs/reference/exceptions.md</c>).
  /// </summary>
  /// <param name="type">A class of <see cref="IrExceptionClass.All"/>.</param>
  /// <returns>The name of the JavaScript class.</returns>
  public static string ErrorClass(IrClassType type) => JavaScriptHelpers.ErrorClass(type);

  private sealed partial class Writer {

    /// <summary>The names the catch blocks around the statement being written bind their exception to, the innermost last.</summary>
    private readonly Stack<string> _caught = new();

    /// <summary>Whether the module converts with a cast, whose <c>InvalidCastError</c> a test for a <c>TypeError</c> must leave out.</summary>
    private bool _casts;

    /// <summary>
    /// The JavaScript class of an exception class of the IR's library, declared in the module as a
    /// generated error class where it is one (<see cref="JavaScriptHelpers.ErrorClass"/>).
    /// </summary>
    private string Error(IrClassType type) {
      var name = JavaScriptHelpers.ErrorClass(type);
      if (name is "Error" or "TypeError")
        this.Native(name);
      else
        this._used.Add(name);

      return name;
    }

    /// <summary>The name a class is written with: its own, or the JavaScript class of a library class.</summary>
    private string ClassName(IrClassType type) => type.IsLibrary ? this.Error(type) : this.Name(type.Name);

    /// <summary>Takes note of what the exceptions of a module need: the names of the fields of an exception, and whether it casts.</summary>
    private void NameExceptions(IrModule module) {
      this._memberNames[IrExceptionClass.Message] = "message";
      this._memberNames[IrExceptionClass.InnerException] = "cause";
      this._casts = module.Functions.Any(function => IrTree.Nodes(function.Body).OfType<IrCast>().Any());
    }

    /// <summary>The statements of exception handling, and the call of a library constructor that begins a constructor; <see langword="false"/> for any other.</summary>
    private bool ExceptionStatement(IrStmt statement, List<JsStatement> statements) {
      switch (statement) {
        case IrThrow thrown:
          statements.Add(new JsThrow(this.Value(thrown.Exception)));
          return true;
        case IrRethrow:
          statements.Add(new JsThrow(Id(this._caught.Peek())));
          return true;
        case IrTry attempt:
          statements.Add(this.Try(attempt));
          return true;
        case IrExprStmt { Expression: IrCall call } when IrExceptionClass.ConstructorOf(call.Function) is { } constructor:
          statements.AddRange(this.LibraryChain(call, constructor));
          return true;
        default:
          return false;
      }
    }

    /// <summary>A try: its catch clauses as one catch whose block tests them in order and throws on what none selects.</summary>
    private JsTry Try(IrTry attempt) {
      var block = new JsBlock(this.Statements(attempt.Body));
      var final = attempt.Finally is null ? null : new JsBlock(this.Statements(attempt.Finally));
      if (attempt.Catches.Count == 0)
        return new JsTry(block, null, null, final);

      var outer = this._taken.Keys.ToHashSet();
      // The catch binds the exception to the variable of the first clause that has one, or to a name of its own.
      var first = attempt.Catches.Select(clause => clause.Local).FirstOrDefault(local => local is not null);
      var name = first is null ? this.Fresh("$e") : this.Declare(first);
      this._caught.Push(name);
      JsStatement? chain = null;
      for (var index = attempt.Catches.Count - 1; index >= 0; --index) {
        var clause = attempt.Catches[index];
        if (clause.Filter is not null && !IrExceptionFilter.MayRunLate(attempt, clause) && this._refused.Add($"filter of {clause.Class.Name}"))
          this._problems.Add(InexpressibleDescriptor.At(
            $"the exception filter of a catch of {clause.Class.Name} runs before finally blocks; it cannot be expressed in profile '{this._profile.LevelName}'"));

        var body = new List<JsStatement>();
        if (clause.Local is { } local && local != first)
          body.Add(new JsVariableDeclaration(this.Let, [new JsDeclarator(this.Declare(local), Id(name))]));

        var test = this.CatchTest(clause.Class, Id(name));
        if (clause.Filter is { } filter) {
          var condition = this.Operand(filter);
          test = test is null ? condition : Infix(Parenthesized(test), "&&", condition);
        }

        this.Statements(clause.Body, body);
        var handler = new JsBlock(body);
        // A clause of every exception selects whatever reaches it; C# allows no clause after it.
        chain = test is null ? handler : new JsIf(test, handler, chain ?? new JsBlock([new JsThrow(Id(name))]));
      }

      this._caught.Pop();
      foreach (var taken in this._taken.Keys.Where(taken => !outer.Contains(taken)).ToList())
        this._taken.Remove(taken);

      // A clause of every exception without a variable needs no binding where the level can leave it out (ECMAScript 2019).
      var unbound = attempt.Catches is [{ Local: null, Filter: null } only] && only.Class == IrExceptionClass.Exception && !Rethrows(only.Body) && this.Native("optional catch binding");
      var statements = chain is JsBlock whole ? whole.Statements : [chain!];
      return new JsTry(block, unbound ? null : name, new JsBlock(statements), final);
    }

    /// <summary>Whether a block throws again an exception it caught, which then needs a name.</summary>
    private static bool Rethrows(IrStmt statement)
      => statement is IrRethrow || (statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(inner => inner.Statements)).Any(Rethrows);

    /// <summary>A name of its own for the exception a catch binds, which no variable of the function has.</summary>
    private string Fresh(string name) {
      var chosen = name;
      for (var number = 1; this._taken.ContainsKey(chosen); ++number)
        chosen = $"{name}{number}";

      this._taken[chosen] = new IrLocal(chosen, IrExceptionClass.Reference);
      return chosen;
    }

    /// <summary>
    /// The test that selects an exception of a class, or <see langword="null"/> for <c>Exception</c>,
    /// which selects every one: <c>instanceof</c> the class; a <c>NullReferenceException</c> is the
    /// engine's <c>TypeError</c> but not the <c>InvalidCastError</c> of a cast, an
    /// <c>ArgumentException</c> also an <c>ArgumentOutOfRangeError</c>, which extends <c>RangeError</c>.
    /// </summary>
    private JsExpression? CatchTest(IrClassType type, JsExpression caught) {
      this.Native("instanceof");
      if (type == IrExceptionClass.Exception)
        return null;

      var test = Infix(caught, "instanceof", Id(this.ClassName(type)));
      if (type == IrExceptionClass.NullReferenceException && this._casts)
        return Infix(test, "&&", new JsUnary("!", Parenthesized(Infix(caught, "instanceof", Id(this.Error(IrExceptionClass.InvalidCastException))))));

      if (type == IrExceptionClass.ArgumentException)
        return Infix(test, "||", Infix(caught, "instanceof", Id(this.Error(IrExceptionClass.ArgumentOutOfRangeException))));

      return test;
    }

    /// <summary>
    /// The call of a library constructor that begins the constructor of a class derived from the
    /// library class: <c>super(m)</c> - with <c>{ cause }</c> at ECMAScript 2022 where the base is
    /// <c>Error</c> - or, without classes, the message and the stack set as the generated error classes
    /// set them; then the name of the class, and the cause where it is not passed. A message the source
    /// does not give is the one .NET gives an object of the class.
    /// </summary>
    private List<JsStatement> LibraryChain(IrCall call, IrConstructor constructor) {
      var owner = IrExceptionClass.All.First(type => type.Constructors.Contains(constructor));
      var type = this._class!;
      var arguments = call.Arguments.Skip(1).Select(this.Value).ToList();
      var message = arguments.Count > 0 ? arguments[0] : new JsString(IrExceptionClass.DefaultMessage(owner == IrExceptionClass.Exception ? type : owner));
      var inner = arguments.Count > 1 ? arguments[1] : null;
      var statements = new List<JsStatement>();
      var self = new JsThis();
      if (this.Native("class")) {
        var native = inner is not null && owner == IrExceptionClass.Exception && this.Native("Error cause");
        statements.Add(new JsExpressionStatement(new JsCall(Id("super"), native ? [message, new JsObject([new JsProperty("cause", inner!)])] : [message])));
        if (native)
          inner = null;
      } else {
        var root = owner == IrExceptionClass.Exception ? "Error" : JavaScriptHelpers.NativeRoot(this.Error(owner));
        statements.Add(new JsExpressionStatement(new JsAssignment(new JsMember(self, "message"), "=", message)));
        statements.Add(new JsExpressionStatement(new JsAssignment(new JsMember(self, "stack"), "=", new JsMember(new JsNew(Id(root), [new JsMember(self, "message")]), "stack"))));
      }

      statements.Add(new JsExpressionStatement(new JsAssignment(new JsMember(self, "name"), "=", new JsString(type.Name))));
      if (inner is not null)
        statements.Add(new JsExpressionStatement(new JsAssignment(new JsMember(self, "cause"), "=", inner)));

      return statements;
    }

    /// <summary>
    /// A new object of a library exception class: <c>new C(m)</c>, the message .NET gives it where the
    /// source gives none, and an inner exception through the helper <c>$caused</c>.
    /// </summary>
    private (JsExpression Node, bool Atomic) NewLibraryException(IrNew creation) {
      var type = creation.Type.ClassType!;
      var arguments = creation.Arguments.Select(this.Value).ToList();
      var created = new JsNew(Id(this.Error(type)), [arguments.Count > 0 ? arguments[0] : new JsString(IrExceptionClass.DefaultMessage(type))]);
      return (arguments.Count > 1 ? this.Helper("$caused", [created, arguments[1]]) : created, true);
    }

    /// <summary>The inner exception of an exception: its <c>cause</c>, <c>null</c> where it has none.</summary>
    private (JsExpression Node, bool Atomic) InnerException(IrFieldLoad load) => (this.Helper("$cause", [this.Text(load.Target)]), true);

    /// <summary>A throw where a value stands, through the helper <c>$throw</c>: JavaScript has no throw expression.</summary>
    private (JsExpression Node, bool Atomic) ThrowExpression(IrThrowExpression thrown) => (this.Helper("$throw", [this.Value(thrown.Exception)]), true);

  }

}
