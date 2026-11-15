using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript.Inference;

internal sealed partial class TypeInference {

  /// <summary>What every refusal of a promise completed by hand says.</summary>
  private const string HandCompleted = "a task completed by hand: .NET continues inside SetResult, JavaScript after it";

  /// <summary>Whether the file declares an async function, which only then makes the promises of its calls a matter of the import.</summary>
  private bool? _async;

  /// <summary>Whether a call is <c>Promise.name(…)</c>, and the name.</summary>
  internal static bool IsPromiseCall(Node node, out string name) {
    if (node is CallExpression { Callee: MemberExpression { Computed: false, Object: Identifier { Name: "Promise" }, Property: Identifier { Name: var member } } }) {
      name = member;
      return true;
    }

    name = "";
    return false;
  }

  /// <summary>The async function of the module a call calls by its name, or <see langword="null"/>.</summary>
  internal Candidate? AsyncCallee(Node node)
    => node is CallExpression { Callee: Identifier { Name: var name } }
      ? candidates.Find(candidate => candidate is { IsProperty: false, Class: null, IsNested: false } && candidate.Name == name && candidate.Function.Async)
      : null;

  /// <summary>
  /// Why a function of a file with async functions cannot be imported, or <see langword="null"/> (#1330):
  /// an async generator, an async function nested in another or in a class, <c>for await</c>, a promise
  /// made by hand, a continuation or a timer, a member of <c>Promise</c> other than <c>resolve</c> and
  /// <c>all</c>, and a promise that is not awaited where it is created - the order of its effects would
  /// differ between JavaScript and .NET.
  /// </summary>
  private Diagnostic? AsyncProblem(Candidate candidate) {
    Diagnostic Unsupported(Node node, string what) => this.Diagnose(node, JavaScriptFrontend.UnsupportedDescriptor, $"'{what}' is not supported yet");
    var function = candidate.Function;
    if (function.Async && function.Generator)
      return Unsupported(candidate.Anchor, "an async generator");

    if (function.Async && (candidate.IsNested || candidate.Class is not null))
      return Unsupported(candidate.Anchor, candidate.Class is not null ? "an async method" : "an async lambda");

    this._async ??= candidates.Any(other => other.Function.Async);
    if (!this._async.Value)
      return null;

    Diagnostic? Search(Node node, Node? parent, Node? grandparent) {
      switch (node) {
        case IFunction nested when !ReferenceEquals(nested, function):
          // A function inside is a candidate of its own, checked by itself.
          return null;
        case ForOfStatement { Await: true } loop:
          return Unsupported(loop, "for await");
        case NewExpression { Callee: Identifier { Name: "Promise" } } made:
          return Unsupported(made, HandCompleted);
        case CallExpression { Callee: MemberExpression { Computed: false, Property: Identifier { Name: "then" or "catch" or "finally" } method } } continued:
          return Unsupported(continued, "." + method.Name);
        case CallExpression { Callee: Identifier { Name: "setTimeout" or "setInterval" or "setImmediate" or "queueMicrotask" } timer } scheduled:
          return Unsupported(scheduled, timer.Name);
        case CallExpression promised when IsPromiseCall(promised, out var member):
          if (member is not ("resolve" or "all"))
            return Unsupported(promised, "Promise." + member);

          if (parent is not AwaitExpression)
            return Unsupported(promised, IrAsyncFunctions.NotAwaited);

          if (member == "all" && promised.Arguments is not [ArrayExpression])
            return Unsupported(promised, "Promise.all over a collection of promises");

          break;
        case CallExpression call when this.AsyncCallee(call) is not null:
          var awaited = parent is AwaitExpression
            || (parent is ArrayExpression && grandparent is not null && IsPromiseCall(grandparent, out var all) && all == "all");
          if (!awaited)
            return Unsupported(call, IrAsyncFunctions.NotAwaited);

          break;
        case ArrayExpression elements when parent is not null && IsPromiseCall(parent, out var container) && container == "all":
          if (elements.Elements.FirstOrDefault(element => this.AsyncCallee(element!) is null) is { } other)
            return Unsupported(other, IrAsyncFunctions.NotAwaited);

          break;
      }

      foreach (var child in node.ChildNodes)
        if (Search(child, node, parent) is { } problem)
          return problem;

      return null;
    }

    return Search((Node)function, null, null);
  }

  /// <summary>
  /// The type of what an <c>await</c> gives: the value of <c>Promise.resolve(v)</c>, nothing for
  /// <c>Promise.resolve()</c>, the array of the results of <c>Promise.all</c>, and otherwise the type of the
  /// operand - the result of an async function of the module, or a value that is no promise.
  /// </summary>
  private JsType AwaitedType(AwaitExpression awaited) {
    var operand = awaited.Argument;
    if (IsPromiseCall(operand, out var member) && operand is CallExpression call) {
      if (member == "resolve")
        return call.Arguments.Count == 0 ? JsType.Undefined : this.Of(call.Arguments[0]);

      if (member == "all" && call.Arguments is [ArrayExpression array])
        return JsType.ArrayOf(array.Elements.Select(element => this.Of(element!)).Aggregate(JsType.Unknown, JsType.Join), true);
    }

    return this.Of(operand);
  }

}
