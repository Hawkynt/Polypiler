using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The copies of a function that JavaScript uses as a generic: a function the file does not export,
/// that is no member of a class and that its calls pass values of several kinds - an
/// <c>Int32Array</c> here, a <c>Float64Array</c> there - gets one copy per combination of the kinds
/// its calls pass, each typed on its own, and each call calls the copy of its kind. A copy is named
/// in the IR as an instantiation is, <c>middle&lt;i32[]&gt;</c>, without an origin. An exported
/// function keeps one type per parameter, which a caller outside the file relies on.
/// </summary>
public static partial class JavaScriptFrontend {

  /// <summary>
  /// The calls that need a copy of the function they call: of each function that is not public, no
  /// member and not called from an initializer, and whose parameters take values of two kinds, every
  /// call by its callee with the number of the combination of kinds it passes. Empty when no function needs copies.
  /// </summary>
  private static Dictionary<Node, (IFunction Function, int Copy)> Wanted(TypeInference inference, List<FunctionModel> models) {
    var wanted = new Dictionary<Node, (IFunction Function, int Copy)>();
    var initialized = inference.Initializers.Flows.Where(flow => flow.Kind == FlowKind.Argument).Select(flow => flow.Callee).ToHashSet();
    foreach (var model in models.Where(model => !model.IsPublic && model.Candidate is { Class: null, IsProperty: false, IsNested: false } && !initialized.Contains(model.Candidate)
        // A parameter JSDoc states keeps one type: calls that pass an Array and a typed array to its T[] are refused, not copied (#1374).
        && model.Parameters.Exists(parameter => parameter.Entry.Kind == JsKind.Conflict && parameter.Declared is null))) {
      var calls = models
        .SelectMany(caller => caller.Flows.Where(flow => flow.Kind == FlowKind.Argument && flow.Callee == model.Candidate && flow.Source is CallExpression).Select(flow => (Caller: caller, Call: (CallExpression)flow.Source!)))
        .DistinctBy(entry => entry.Call)
        .Select(entry => {
          inference.Enter(entry.Caller);
          return (entry.Call, Kinds: string.Join(",", entry.Call.Arguments.Select(argument => inference.Of(argument).Describe())));
        })
        .ToList();
      var kinds = calls.Select(call => call.Kinds).Distinct().ToList();
      if (kinds.Count < 2)
        continue;

      foreach (var (call, kind) in calls)
        wanted[call.Callee] = (model.Candidate.Function, kinds.IndexOf(kind) + 1);
    }

    return wanted;
  }

  /// <summary>
  /// Replaces every function that calls need copies of by its copies, named after it with the number
  /// of the copy, <c>middle&lt;1&gt;</c>, and says which copy each of those calls calls.
  /// </summary>
  private static Dictionary<Node, Candidate> Copies(List<Candidate> candidates, Dictionary<Node, (IFunction Function, int Copy)> copies) {
    var redirects = new Dictionary<Node, Candidate>();
    foreach (var group in copies.GroupBy(entry => entry.Value.Function)) {
      var original = candidates.First(candidate => candidate.Function == group.Key);
      var made = group.Select(entry => entry.Value.Copy).Distinct().Order().ToDictionary(copy => copy, copy => new Candidate($"{original.Name}<{copy}>", original.Anchor, original.Function) {
        Documentation = original.Documentation, Problem = original.Problem
      });
      candidates.InsertRange(candidates.IndexOf(original), made.Values);
      candidates.Remove(original);
      foreach (var (callee, (_, copy)) in group)
        redirects[callee] = made[copy];
    }

    return redirects;
  }

  /// <summary>The module with each copy named as an instantiation, after the types of its parameters: <c>middle&lt;i32[]&gt;</c>.</summary>
  private static IrModule Named(IrModule module, IEnumerable<Candidate> copies) {
    var names = new Dictionary<string, string>();
    var taken = module.Functions.Select(function => function.Name).ToHashSet();
    foreach (var copy in copies.Select(candidate => candidate.Name).Distinct()) {
      var function = module.Functions.First(candidate => candidate.Name == copy);
      var name = IrGenerics.Mangle(IrGenerics.SourceName(copy), function.Parameters.Select(parameter => parameter.Type));
      if (taken.Add(name))
        names[copy] = name;
    }

    return names.Count == 0 ? module : IrGenerics.Rename(module, names);
  }

}
