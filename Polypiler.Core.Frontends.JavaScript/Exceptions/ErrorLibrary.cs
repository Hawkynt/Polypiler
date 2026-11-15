using System.Collections.Generic;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

/// <summary>
/// The native errors of JavaScript as classes of the importer, each the exception class of the IR's
/// library it stands for: <c>Error</c> is <c>Exception</c>, every exception; <c>RangeError</c> is
/// <c>ArithmeticException</c>, since in a JavaScript source only a <c>BigInt</c> division by zero and
/// an invalid array length raise one; <c>TypeError</c> is <c>NullReferenceException</c>, since in a
/// typed import only a member access on <c>null</c> or <c>undefined</c> raises one. An error has
/// <c>message</c> and <c>cause</c>, the message and the inner exception of the IR.
/// </summary>
internal sealed class ErrorLibrary {

  /// <summary>Makes the classes for one file.</summary>
  /// <param name="anchor">The node a diagnostic about a native error points at: the program.</param>
  public ErrorLibrary(Node anchor) {
    this.Error = new ClassModel("Error", anchor, IrExceptionClass.Exception);
    var message = new Variable("message", anchor, false) { Type = JsType.String };
    var cause = new Variable("cause", anchor, false) { Type = JsType.Object(this.Error) };
    this.Error.Fields.Add(new ClassField("message", message, null) { Ir = IrExceptionClass.Message });
    this.Error.Fields.Add(new ClassField("cause", cause, null) { Ir = IrExceptionClass.InnerException });
    this.RangeError = new ClassModel("RangeError", anchor, IrExceptionClass.ArithmeticException) { Base = this.Error };
    this.TypeError = new ClassModel("TypeError", anchor, IrExceptionClass.NullReferenceException) { Base = this.Error };
    this.ByName = new Dictionary<string, ClassModel> { ["Error"] = this.Error, ["RangeError"] = this.RangeError, ["TypeError"] = this.TypeError };
  }

  /// <summary><c>Error</c>: every exception.</summary>
  public ClassModel Error { get; }

  /// <summary><c>RangeError</c>: the arithmetic exceptions.</summary>
  public ClassModel RangeError { get; }

  /// <summary><c>TypeError</c>: a member access on <c>null</c>.</summary>
  public ClassModel TypeError { get; }

  /// <summary>The three by their names.</summary>
  public IReadOnlyDictionary<string, ClassModel> ByName { get; }

  /// <summary>The native errors of JavaScript that are no class the importer knows; <c>instanceof</c> one of them is refused.</summary>
  public static readonly IReadOnlySet<string> Others = new HashSet<string> { "EvalError", "ReferenceError", "SyntaxError", "URIError", "AggregateError" };

}
