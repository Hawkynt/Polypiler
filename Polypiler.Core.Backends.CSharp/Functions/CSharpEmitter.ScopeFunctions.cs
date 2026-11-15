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
    /// local functions (C# 7) and only that function calls the lifted one, neither is a method, generic or
    /// unsafe, and - where a local function may not reuse a name of its function, before C# 8 - no name
    /// of the one is a name of the other. Every other lifted function stays a private method of the class.
    /// </summary>
    private Dictionary<string, List<IrFunction>> ScopeFunctions() {
      var nested = new Dictionary<string, List<IrFunction>>(StringComparer.Ordinal);
      if (!this.Has(CSharpFeature.LocalFunction))
        return nested;

      var functions = this._module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
      var values = new HashSet<string>(StringComparer.Ordinal);
      foreach (var function in this._module.Functions)
        foreach (var node in IrTree.Nodes(function.Body))
          if (node is IrCall call) {
            if (!callers.TryGetValue(call.Function, out var calling))
              callers[call.Function] = calling = new(StringComparer.Ordinal);

            calling.Add(function.Name);
          } else if (node is IrFunctionRef reference) {
            values.Add(reference.Function);
          }

      values.UnionWith(this._module.Globals.Select(global => global.Initializer).OfType<IrExpr>().SelectMany(IrTree.Nodes).OfType<IrCall>().Select(call => call.Function));
      var candidates = new List<(IrFunction Scope, IrFunction Lifted)>();
      foreach (var known in this._module.ScopeFunctions.Where(known => known.IsLifted))
        if (functions.TryGetValue(known.Name, out var lifted) && functions.TryGetValue(known.Scope, out var scope) && !lifted.IsPublic && lifted != scope
            && !values.Contains(lifted.Name) && callers.TryGetValue(lifted.Name, out var calling) && calling.SetEquals([scope.Name])
            && this.Local(scope) && this.Local(lifted) && (this.Has(CSharpFeature.StaticLocalFunction) || !VariableNames(scope).Overlaps(VariableNames(lifted).Append(lifted.Name))))
          candidates.Add((scope, lifted));

      // A lifted function whose scope is itself written inside another stays where it is.
      var inner = candidates.Select(candidate => candidate.Lifted.Name).ToHashSet(StringComparer.Ordinal);
      foreach (var (scope, lifted) in candidates.Where(candidate => !inner.Contains(candidate.Scope.Name)))
        (nested.TryGetValue(scope.Name, out var list) ? list : nested[scope.Name] = []).Add(lifted);

      return nested;
    }

    /// <summary>Whether a function may hold or be a local function: no method, no instance of a generic one, nothing unsafe.</summary>
    private bool Local(IrFunction function)
      => !this.IsMember(function) && !this._generics.Generalized.ContainsKey(function.Name) && !Unsafe([], function).Contains("unsafe");

    /// <summary>The names of the variables of a function: its parameters, its locals and those of its lambdas.</summary>
    private static HashSet<string> VariableNames(IrFunction function)
      => [.. function.Parameters.Concat(function.Locals).Concat(IrTree.Nodes(function.Body).OfType<IrLambda>().SelectMany(lambda => lambda.Parameters.Concat(lambda.Locals))).Select(local => local.Name)];

    /// <summary>
    /// A method with the lifted functions of its scope as local functions at the end of its block,
    /// <c>static</c> where the language has that (C# 8); an expression body becomes a block that returns it.
    /// </summary>
    private CsMethod WithScopeFunctions(CsMethod method, IrFunction function, Dictionary<string, List<IrFunction>> nested) {
      if (!nested.TryGetValue(function.Name, out var lifted))
        return method;

      var locals = lifted.Select(this.Function).Select(inner => (CsStatement)new CsLocalFunction(
        this.Has(CSharpFeature.StaticLocalFunction) ? ["static"] : [], inner.ReturnType, inner.Name, inner.Parameters, inner.Body, inner.ExpressionBody));
      IReadOnlyList<CsStatement> body = method.Body?.Statements ?? [function.ReturnType == IrType.Void ? new CsExpressionStatement(method.ExpressionBody!) : new CsReturn(method.ExpressionBody)];
      return method with { Body = new CsBlock([.. body, .. locals]), ExpressionBody = null };
    }

  }

}
