using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.JavaScript.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.JavaScript;

/// <summary>
/// Native calls in JavaScript (#1332, ADR 0007): JavaScript has no foreign-function interface, so a foreign function is
/// a function of the module that calls the function of the same symbol in a binding module the user names with
/// <c>--bind &lt;library&gt;=&lt;module or global&gt;</c> - imported by an ES module, required by CommonJS, read from a
/// global by a script. Without a binding for its library a foreign function is refused with <see cref="Inexpressible"/>.
/// The value contract is that of <c>docs/reference/foreign-calls.md</c>: the arguments as the module holds them - an
/// integer of up to 32 bits and a floating-point number as a <c>Number</c>, one of 64 bits as a <c>BigInt</c> or, where
/// the level has none, as its halves <c>{ hi, lo }</c>, a string as a string, an array, a span and an <c>out</c> or
/// <c>ref</c> argument (a one-element array) by reference, a struct as a plain object of its fields - and a result the
/// same way, where a 64-bit integer may also come back as a <c>Number</c>.
/// </summary>
public static partial class JavaScriptEmitter {

  private sealed partial class Writer {

    /// <summary>The variable a binding module is held in: <c>$</c> and the library's name with every character but a letter or a digit as <c>_</c>.</summary>
    private static string BindingName(string library)
      => "$" + new string([.. library.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')]);

    /// <summary>
    /// What a module with foreign functions cannot be written with: a library without a binding, a pointer argument,
    /// whose region no binding can read, and the last error of a native call, which no binding keeps.
    /// </summary>
    private void ForeignProblems(IrModule module) {
      // Two declarations of one symbol, such as an ANSI and a UTF-8 strlen, are one native function to refuse.
      foreach (var function in module.ForeignFunctions.DistinctBy(function => (function.Symbol, function.Library))) {
        if (!this._profile.Bindings.ContainsKey(function.Library))
          this._problems.Add(InexpressibleDescriptor.At(
            $"'{function.Symbol}' ({function.Library}) is a native function, which JavaScript profile '{this._profile.LevelName}' cannot call; --bind {function.Library}=<module or global> supplies a binding"));

        if (function.Parameters.FirstOrDefault(parameter => parameter.Type.IsPointer) is { } pointer)
          this._problems.Add(InexpressibleDescriptor.At($"'the pointer {pointer.Name} passed to the native function {function.Symbol}' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));
      }

      if (module.Functions.Any(function => IrTree.Nodes(function.Body).OfType<IrLastError>().Any()))
        this._problems.Add(InexpressibleDescriptor.At($"'the last error of a native call (Marshal.GetLastWin32Error)' cannot be expressed in JavaScript profile '{this._profile.LevelName}'"));
    }

    /// <summary>
    /// The statements that reach the binding modules the foreign functions use, one per library: <c>import * as</c> in an
    /// ES module, <c>require</c> in CommonJS, the global in a script.
    /// </summary>
    private List<JsStatement> Bindings(IrModule module) {
      var statements = new List<JsStatement>();
      foreach (var library in module.ForeignFunctions.Select(function => function.Library).Distinct().Where(this._profile.Bindings.ContainsKey)) {
        var name = BindingName(library);
        var target = this._profile.Bindings[library];
        JsStatement statement = this._profile.Module switch {
          JavaScriptModuleFormat.Esm => new JsImport(name, target),
          JavaScriptModuleFormat.Cjs => new JsVariableDeclaration("var", [new JsDeclarator(name, JavaScriptEmitter.Invoke("require", new JsString(target)))]),
          _ => new JsVariableDeclaration("var", [new JsDeclarator(name, Dotted(target))])
        };
        statements.Add(statement with { Leading = new JsTrivia(statements.Count == 0 ? 1 : 0, statements.Count == 0 ? [$"The native libraries, each through the binding the command line named (--bind): unverified externals."] : []) });
      }

      return statements;
    }

    /// <summary>
    /// A foreign function as a function of the module that calls the function of its symbol in the binding of its
    /// library and hands the result back in the module's own form: a 64-bit integer the binding gave as a <c>Number</c>
    /// becomes a <c>BigInt</c>, or its halves.
    /// </summary>
    private JsStatement ForeignFunction(IrForeignFunction function) {
      var name = this.Name(function.Name);
      var parameters = function.Parameters.Select(parameter => this.Name(parameter.Name)).ToList();
      var signature = string.Join(", ", function.Parameters.Select((parameter, index) => $"{parameters[index]}: {this.Describe(parameter.Type)}"));
      var comment = $"{name}({signature}): {this.Describe(function.ReturnType)} - '{function.Symbol}' of {function.Library}, through its binding";
      var exported = function.IsPublic && this._profile.Module == JavaScriptModuleFormat.Esm;
      if (function.IsPublic && !exported)
        this._exported.Add(name);

      JsExpression binding = Id(BindingName(function.Library));
      var callee = JavaScriptWords.IsIdentifier(function.Symbol) ? new JsMember(binding, function.Symbol) : (JsExpression)new JsIndex(binding, new JsString(function.Symbol));
      var call = new JsCall(callee, [.. parameters.Select(parameter => (JsExpression)Id(parameter))]);
      List<JsStatement> body;
      if (function.ReturnType == IrType.Void) {
        body = [new JsExpressionStatement(call)];
      } else if (function.ReturnType.IsLong) {
        var result = Id("$r");
        var number = Infix(new JsUnary("typeof", result), "===", new JsString("number"));
        var converted = this.Halves
          ? (JsExpression)JavaScriptEmitter.Halves(Infix(JavaScriptEmitter.Invoke("Math.floor", Infix(result, "/", JavaScriptEmitter.Number("4294967296"))), ">>>", JavaScriptEmitter.Number("0")), Infix(result, ">>>", JavaScriptEmitter.Number("0")))
          : JavaScriptEmitter.Invoke("BigInt", result);
        body = [
          new JsVariableDeclaration("var", [new JsDeclarator("$r", call)]),
          new JsReturn(new JsConditional(number, converted, result))
        ];
      } else {
        body = [new JsReturn(call)];
      }

      JsStatement declaration = new JsFunctionDeclaration(name, parameters, new JsBlock(body));
      return (exported ? new JsExport(declaration) : declaration) with { Leading = new JsTrivia(1, [comment]) };
    }

    /// <summary>
    /// A call of a foreign function - a call of the module's function of its name, its arguments as the module holds them -
    /// or the last error, which <see cref="ForeignProblems"/> refused and which stands as 0 for the rest of the check.
    /// </summary>
    private (JsExpression Node, bool Atomic)? ForeignWrite(IrExpr expression) => expression switch {
      IrForeignCall call => (new JsCall(Id(this.Name(call.Function)), [.. call.Arguments.Select(this.Value)]), true),
      IrLastError => (JavaScriptEmitter.Number("0"), true),
      _ => null
    };

  }

}
