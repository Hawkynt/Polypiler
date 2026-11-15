using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The async functions of JavaScript (#1330): an <c>async function</c> of the module is an async function
/// of the IR, of the type its returns are inferred to, and <c>await</c> is an <see cref="IrAwait"/> of the
/// call of one, of <c>Promise.resolve</c>, of <c>Promise.all</c> of such calls or of a value that is no
/// promise. Inference has refused everything else (<see cref="JavaScriptAsyncApi"/>).
/// </summary>
internal sealed partial class Lowering {

  /// <summary>The calls of <c>Promise.all</c> lowered, with their node, for the diagnostics of one whose order or rejection depends on timing.</summary>
  private readonly Dictionary<IrAsync, Node> _whenAll = new(ReferenceEqualityComparer.Instance);

  /// <summary>What the refusal of a thenable an async function returns or awaits says.</summary>
  private const string Thenable = "a thenable object, of a class with then, returned or awaited: JavaScript calls its then, .NET does not";

  /// <summary>The type a function declares: the result its returns give, or for an async function the async value of it.</summary>
  private IrType Declared(FunctionModel model) {
    if (!model.Candidate.Function.Async)
      return this._returnType;

    if (TypeInference.IsThenable(model.Result))
      throw this.Unsupported(model.Candidate.Anchor, Thenable);

    return IrAsyncFunctions.IsResult(this._returnType)
      ? IrType.Async(this._returnType)
      : throw this.Unsupported(model.Candidate.Anchor, $"{JsType.Spell(this._returnType)} as the result of an async function");
  }

  /// <summary><c>await</c> of the call of an async function, of <c>Promise.resolve</c>, of <c>Promise.all</c> of such calls, or of a value.</summary>
  private IrAwait Await(AwaitExpression awaited) {
    var operand = awaited.Argument;
    if (TypeInference.IsPromiseCall(operand, out var member)) {
      var call = (CallExpression)operand;
      if (member == "all")
        return new IrAwait(this.WhenAll(call, types.Of(awaited)));

      if (call.Arguments.Count == 0)
        return new IrAwait(new IrAsync(IrAsyncOp.Completed, IrType.Async(IrType.Void), []));

      return new IrAwait(this.Resolved(call.Arguments[0]));
    }

    return new IrAwait(types.AsyncCallee(operand) is not null ? this.AsyncCall(operand) : this.Resolved(operand));
  }

  /// <summary>A value as the async value that has it.</summary>
  private IrAsync Resolved(Node value) {
    if (TypeInference.IsThenable(types.Of(value)))
      throw this.Unsupported(value, Thenable);

    var lowered = this.Value(value, this.Storable(types.Of(value), value));
    return new IrAsync(IrAsyncOp.FromResult, IrType.Async(lowered.Type), [lowered]);
  }

  /// <summary>The call of an async function of the module, of the async type of its result.</summary>
  private IrCall AsyncCall(Node node) {
    var call = (IrCall)this.Expression(node);
    return call with { Type = IrType.Async(call.Type) };
  }

  /// <summary><c>Promise.all([f(…), g(…)])</c> of calls of async functions of the module.</summary>
  private IrAsync WhenAll(CallExpression call, JsType result) {
    var arguments = ((ArrayExpression)call.Arguments[0]).Elements.Select(element => (IrExpr)this.AsyncCall(element!)).ToList();
    var (type, problem) = IrAsyncFunctions.Signature(IrAsyncOp.WhenAll, [.. arguments.Select(argument => argument.Type)], this.Storable(result, call).Element);
    var operation = new IrAsync(IrAsyncOp.WhenAll, type ?? throw this.Unsupported(call, $"Promise.all: {problem}"), arguments);
    this._whenAll[operation] = call;
    return operation;
  }

  /// <summary>
  /// The diagnostics of the calls of <c>Promise.all</c> of a module whose order or rejection would show:
  /// one of a function that writes more than its own locals, and one of which two calls can fault -
  /// <c>Promise.all</c> rejects with the first fault in time, <c>Task.WhenAll</c> with the first in argument order.
  /// </summary>
  public IEnumerable<Diagnostic> WhenAllProblems(IrModule module) {
    Diagnostic At(IrAsync operation, string what)
      => types.Diagnose(this._whenAll[operation], JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");

    foreach (var (_, operation, callee) in IrAsyncFunctions.Timed(module))
      yield return At(operation, $"Promise.all of '{callee}', which writes more than its own locals, whose order depends on timing");

    var faultFree = IrAsyncFunctions.FaultFree(module);
    foreach (var operation in module.Functions.SelectMany(function => IrTree.Nodes(function.Body)).OfType<IrAsync>().Where(operation => operation.Op == IrAsyncOp.WhenAll))
      if (operation.Arguments.OfType<IrCall>().Count(call => !faultFree.Contains(call.Function)) > 1)
        yield return At(operation, "Promise.all whose rejection depends on timing");
  }

}
