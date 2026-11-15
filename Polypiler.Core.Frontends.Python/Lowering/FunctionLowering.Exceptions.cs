using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Model;
using Polypiler.Core.Frontends.Python.Resolution;
using Polypiler.Core.Frontends.Python.Typing;
using Polypiler.Core.IR;
using static Polypiler.Core.Frontends.Python.Lowering.Ir;

namespace Polypiler.Core.Frontends.Python.Lowering;

/// <summary>
/// Exceptions: <c>raise</c> of a class or of an exception, bare <c>raise</c> in a handler, and
/// <c>try</c> with <c>except</c> clauses tried in order - each catches its class and every class
/// derived from it - <c>else</c>, which runs only when the body ended normally and which the handlers
/// do not protect, and <c>finally</c>, which runs on every exit.
/// </summary>
internal sealed partial class FunctionLowering {

  /// <summary><c>raise E</c>, <c>raise E("m")</c>, <c>raise e</c>, and bare <c>raise</c>, which raises the exception being handled again.</summary>
  private IEnumerable<IrStmt> RaiseStatement(PyNode statement) {
    if (statement.Node("exc") is not { } raised)
      return [new IrRethrow()];

    var callee = raised.Is("Call") ? raised.Node("func")! : raised;
    if (callee.Is("Name") && this.Scope.Resolve(callee) == Binding.Class && this.Scope.Class(callee) is { IsException: true } type) {
      if (raised.Is("Call"))
        return [new IrThrow(this.Construct(raised, type).Item1)];

      // A class raised without arguments: an exception whose message is empty, as str(e) gives it.
      return [new IrThrow(this.NewException(type, new IrStrConst("")))];
    }

    var value = this.Value(raised);
    return value.Type is { Kind: PyKind.Object, Class.IsException: true }
      ? [new IrThrow(value.Value)]
      : throw this.Refuse(raised, PythonFrontend.UnsupportedExceptionDescriptor, $"exception construct 'raise of a value of the type {value.Type}, which raises TypeError,' is not supported by the Python frontend");
  }

  /// <summary>
  /// <c>try</c>: the body, a catch clause per class an <c>except</c> names - a tuple of classes is one
  /// clause per class, each with the handler's statements; <c>LookupError</c> also catches the
  /// library's index and key errors - <c>else</c> under a flag the body's last statement sets, and
  /// <c>finally</c> around all of it.
  /// </summary>
  private IEnumerable<IrStmt> Try(PyNode statement) {
    if (this.Function.IsGenerator && statement.Nodes("handlers").Count > 0 && ContainsYield(statement.Nodes("body")))
      throw this.Refuse(statement, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(this.Function.Name) + " (a yield inside a try with except: #760)");

    var body = this.Statements(statement.Nodes("body"));
    var orelse = statement.Nodes("orelse");
    var completed = orelse.Count > 0 ? this.Fresh("completed", IrType.Bool) : null;
    if (completed is not null)
      body.Add(Assign(completed, Bool(true)));

    var catches = new List<IrCatch>();
    foreach (var handler in statement.Nodes("handlers")) {
      var classes = handler.Node("type") is not { } named ? [IrExceptionClass.Exception]
        : (named.Is("Tuple") ? named.Nodes("elts") : [named]).SelectMany(name => Classes.Caught(this.HandlerClass(name))).ToList();
      foreach (var caught in classes) {
        IrLocal? local = null;
        var handled = new List<IrStmt>();
        if (handler.Text("name") is { } variableName) {
          var variable = this.Function.Find(variableName)!;
          local = this.Fresh(variableName + "Caught", IrType.Object(caught));
          handled.Add(Assign(this.Local(variable), new IrCast(PyTypes.Ir(variable.Type), Ref(local))));
        }

        handled.AddRange(this.Statements(handler.Nodes("body")));
        catches.Add(new IrCatch(caught, local, null, new(handled)));
      }
    }

    var result = new List<IrStmt>();
    if (completed is not null)
      result.Add(new IrDeclare(completed, Bool(false)));

    IrStmt attempt = catches.Count > 0 ? new IrTry(new(body), catches, null) : new IrBlock(body);
    var protectedPart = new List<IrStmt> { attempt };
    if (completed is not null)
      protectedPart.Add(new IrIf(Ref(completed), new(this.Statements(orelse)), null));

    var final = statement.Nodes("finalbody");
    if (final.Count == 0) {
      result.AddRange(protectedPart);
      return result;
    }

    result.Add(new IrTry(new(protectedPart), [], new(this.Statements(final))));
    return result;
  }

  /// <summary>The class an <c>except</c> names: of the module, or a built-in exception, its IR class declared.</summary>
  private ClassModel HandlerClass(PyNode name) {
    var type = this.Scope.Class(name)!;
    if (type.Ir is null && type.Kind == ClassKind.BuiltinException)
      ClassDeclarations.Exception(type, this._context);

    return type;
  }

  /// <summary>Whether statements hold a <c>yield</c> outside the functions nested in them.</summary>
  private static bool ContainsYield(IReadOnlyList<PyNode> statements)
    => statements.Any(statement => statement.Kind != "FunctionDef"
      && (ScopeResolver.Expressions(statement).Any(expression => ScopeResolver.Walk(expression).Any(node => node.Kind is "Yield" or "YieldFrom"))
        || ContainsYield(statement.Nodes("body")) || ContainsYield(statement.Nodes("orelse")) || ContainsYield(statement.Nodes("finalbody"))
        || statement.Nodes("handlers").Any(handler => ContainsYield(handler.Nodes("body")))));

  /// <summary>
  /// A nested <c>def</c>: the function value assigned to the local that holds it, made where the
  /// statement runs. The function is a lambda over the variables it shares with the functions it
  /// stands in - captured, never copied, so it sees every later assignment and its own assignments
  /// through <c>nonlocal</c> are seen.
  /// </summary>
  private IEnumerable<IrStmt> NestedDefinition(PyNode statement) {
    var nested = this.Function.Nested[statement.Text("name")!];
    if (nested.IsGenerator)
      throw this.Refuse(statement, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(nested.Name) + " (a nested generator: #760)");

    var lowering = new FunctionLowering(this._context, this._module, nested, this);
    var (parameters, body) = lowering.Body();
    var lambda = IrLambda.Of(parameters, PyTypes.Ir(nested.Returns), lowering._locals, body);
    return [Assign(this.NestedLocal(nested), lambda)];
  }

}
