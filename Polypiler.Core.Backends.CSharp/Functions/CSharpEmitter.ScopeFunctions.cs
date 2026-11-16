using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.CSharp.Syntax;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.CSharp;

public static partial class CSharpEmitter {

  private sealed partial class Writer {

    /// <summary>
    /// The functions of a scope the optimizer lifted (<see cref="IrModule.ScopeFunctions"/>) that are
    /// written as local functions of their scope, by the function they stand in: where the language has
    /// local functions (C# 7), only that function calls the lifted one, it is no method of a class and -
    /// where a local function may not reuse a name of its function, before C# 8 - no name of the one is a
    /// name of the other. Every other lifted function stays a private method of the class. A lifted
    /// function is private, only ever called and never generic, and one inlined everywhere is gone.
    /// </summary>
    private Dictionary<string, List<IrFunction>> ScopeFunctions() {
      var nested = new Dictionary<string, List<IrFunction>>(StringComparer.Ordinal);
      if (!this.Has(CSharpFeature.LocalFunction))
        return nested;

      var functions = this._module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      var callers = this._module.Functions
        .SelectMany(function => IrTree.Nodes(function.Body).OfType<IrCall>().Select(call => (Callee: call.Function, Caller: function.Name)))
        .Distinct().ToLookup(pair => pair.Callee, pair => pair.Caller);
      var candidates = new List<(IrFunction Scope, IrFunction Lifted)>();
      foreach (var known in this._module.ScopeFunctions.Where(known => known.IsLifted && functions.ContainsKey(known.Name)))
        if (callers[known.Name].SequenceEqual([known.Scope]) && functions[known.Scope] is var scope && !this.IsMember(scope)
            && (this.Has(CSharpFeature.StaticLocalFunction) || !VariableNames(scope).Overlaps(VariableNames(functions[known.Name]).Append(known.Name))))
          candidates.Add((scope, functions[known.Name]));

      // A lifted function whose scope is itself written inside another stays where it is.
      var inner = candidates.Select(candidate => candidate.Lifted.Name).ToHashSet(StringComparer.Ordinal);
      foreach (var (scope, lifted) in candidates.Where(candidate => !inner.Contains(candidate.Scope.Name)))
        (nested.TryGetValue(scope.Name, out var list) ? list : nested[scope.Name] = []).Add(lifted);

      return nested;
    }

    /// <summary>The names of the variables of a function: its parameters, its locals and those of its lambdas.</summary>
    private static HashSet<string> VariableNames(IrFunction function)
      => [.. function.Parameters.Concat(function.Locals).Concat(IrTree.Nodes(function.Body).OfType<IrLambda>().SelectMany(lambda => lambda.Parameters.Concat(lambda.Locals))).Select(local => local.Name)];

    /// <summary>
    /// A method with the lifted functions of its scope as local functions at the end of its block,
    /// <c>static</c> where the language has that (C# 8) and <c>unsafe</c> where the method would be; an
    /// expression body becomes a block that returns it.
    /// </summary>
    private CsMethod WithScopeFunctions(CsMethod method, IrFunction function, Dictionary<string, List<IrFunction>> nested) {
      if (!nested.TryGetValue(function.Name, out var lifted))
        return method;

      var locals = lifted.Select(this.Function).Select(inner => (CsStatement)new CsLocalFunction(
        [.. this.Has(CSharpFeature.StaticLocalFunction) ? ["static"] : (string[])[], .. inner.Modifiers.Where(modifier => modifier == "unsafe")], inner.ReturnType, inner.Name, inner.Parameters, inner.Body, inner.ExpressionBody));
      // Only a function that returns a value has an expression body.
      IReadOnlyList<CsStatement> body = method.Body?.Statements ?? [new CsReturn(method.ExpressionBody)];
      return method with { Body = new CsBlock([.. body, .. locals]), ExpressionBody = null };
    }

  }

}
