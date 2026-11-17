using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;
using Polypiler.Core.Optimize.Passes;

namespace Polypiler.Core.Optimize.Interprocedural;

/// <summary>A direct call of a function of the module, where it stands.</summary>
/// <param name="Caller">The function the call stands in, or <see langword="null"/> for the initializer of a global.</param>
/// <param name="Call">The call.</param>
/// <param name="InLambda">Whether it stands in the body of a lambda, whose parameters are not those of its function.</param>
public sealed record IrCallSite(string? Caller, IrCall Call, bool InLambda);

/// <summary>
/// What the module knows of the calls of its own functions, for the passes that look across them: whether
/// a function is owned - every call of it is a direct call the module contains - and which calls those
/// are; and whether a function can be entered again while it runs. It is computed from the module as it
/// is when asked, never cached across passes, so a call another pass adds or removes is seen.
/// </summary>
public sealed class ModuleOwnership {

  /// <summary>The reason a public function is not owned: code outside the module calls it.</summary>
  public const string Exported = "exported";

  /// <summary>The reason a function a reference names is not owned: whoever holds the reference calls it.</summary>
  public const string AddressTaken = "address taken";

  /// <summary>The reason the initializer of the module is not owned: the loader calls it.</summary>
  public const string ModuleInitializer = "module initializer";

  /// <summary>The reason a constructor, method or accessor of a type is not owned: dispatch and the type's users call it.</summary>
  public const string Member = "member of a type";

  /// <summary>The reason an instantiation of a generic definition is not owned: a backend may write the definition once for all of them.</summary>
  public const string Generic = "generic instantiation";

  /// <summary>The reason a name the module has no function of is not owned.</summary>
  public const string Unknown = "no function of the module";

  private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);
  private readonly Dictionary<string, List<IrCallSite>> _sites = new(StringComparer.Ordinal);
  private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
  private readonly HashSet<string> _members = new(StringComparer.Ordinal);
  private readonly IrModule _module;

  private ModuleOwnership(IrModule module) {
    this._module = module;
    foreach (var function in module.Functions)
      this._functions[function.Name] = function;

    foreach (var function in module.Functions)
      foreach (var statement in function.Body.Statements)
        this.Census(function.Name, statement, false);

    foreach (var initializer in module.Globals.Select(global => global.Initializer).OfType<IrExpr>())
      this.Census(null, initializer, false);

    foreach (var declaration in module.Types)
      this._members.UnionWith(declaration switch {
        IrClassType type => type.Functions.Concat(Accessors(type.Properties)),
        IrInterfaceType type => type.Functions.Concat(Accessors(type.Properties)),
        _ => []
      });
  }

  /// <summary>The census of a module as it is now.</summary>
  /// <param name="module">The module.</param>
  /// <returns>The census.</returns>
  public static ModuleOwnership Of(IrModule module) {
    ArgumentNullException.ThrowIfNull(module);
    return new(module);
  }

  /// <summary>Whether every call of a function is a direct call this module contains (<see cref="WhyNotOwned"/> is <see langword="null"/>).</summary>
  /// <param name="function">The name of the function.</param>
  /// <returns><see langword="true"/> when the module sees every call.</returns>
  public bool IsOwned(string function) => this.WhyNotOwned(function) is null;

  /// <summary>
  /// Why the module may not see every call of a function: <see cref="Exported"/>, <see cref="ModuleInitializer"/>,
  /// <see cref="Member"/>, <see cref="Generic"/>, <see cref="AddressTaken"/> or <see cref="Unknown"/>, in that order;
  /// <see langword="null"/> for an owned function.
  /// </summary>
  /// <param name="function">The name of the function.</param>
  /// <returns>The reason, or <see langword="null"/>.</returns>
  public string? WhyNotOwned(string function) {
    ArgumentNullException.ThrowIfNull(function);
    if (!this._functions.TryGetValue(function, out var known))
      return Unknown;

    if (known.IsPublic)
      return Exported;

    if (this._module.Initializer == function)
      return ModuleInitializer;

    if (this._members.Contains(function))
      return Member;

    if (known.Origin is not null)
      return Generic;

    return this.IsAddressTaken(function) ? AddressTaken : null;
  }

  /// <summary>Whether a reference to the function (<see cref="IrFunctionRef"/>) stands anywhere in the module: in a function, a lambda or the initializer of a global.</summary>
  /// <param name="function">The name of the function.</param>
  /// <returns><see langword="true"/> when its address is taken.</returns>
  public bool IsAddressTaken(string function) => this._referenced.Contains(function);

  /// <summary>Every direct call of a function in the module, in the order of the functions and their statements, those in lambdas and in the initializers of globals included.</summary>
  /// <param name="function">The name of the function.</param>
  /// <returns>The calls; empty for a function nothing calls.</returns>
  public IReadOnlyList<IrCallSite> CallSitesOf(string function) => this._sites.TryGetValue(function, out var sites) ? sites : [];

  /// <summary>Whether a function can call itself, directly or through other functions of the module.</summary>
  /// <param name="function">The name of a function of the module.</param>
  /// <returns><see langword="true"/> when it lies on a cycle of the call graph.</returns>
  public bool IsRecursive(string function) => this.Reachable(function).Callees.Contains(function);

  /// <summary>
  /// What, among the code that runs while a function runs - its own and that of every function of the
  /// module it reaches by direct calls and constructions - could run code the module cannot name: a
  /// call through a value, a call of a method by dispatch, of a native function or of a name the module
  /// has no function of, a statement that runs code of a sequence or a resource, anything whose effect
  /// summary (<see cref="IrEffects"/>) is the worst case. Such code may enter the function again.
  /// </summary>
  /// <param name="function">The name of a function of the module.</param>
  /// <returns>The first such node as <c>Invoke in @F</c>, or <see langword="null"/> when there is none.</returns>
  public string? UnknownCodeReachedFrom(string function) => this.Reachable(function).Unknown;

  private (HashSet<string> Callees, string? Unknown) Reachable(string function) {
    var effects = IrEffects.Of(this._module);
    var callees = new HashSet<string>(StringComparer.Ordinal);
    var pending = new Stack<string>([function]);
    var seen = new HashSet<string>(StringComparer.Ordinal) { function };
    string? unknown = null;
    while (pending.Count > 0) {
      var current = pending.Pop();
      if (!this._functions.TryGetValue(current, out var body)) {
        unknown ??= $"call of @{current}";
        continue;
      }

      foreach (var statement in body.Body.Statements.SelectMany(All)) {
        if (statement is IrForEachCollection or IrForEachSequence or IrUsing or IrYield or IrYieldFrom)
          unknown ??= $"{Kind(statement)} in @{current}";

        foreach (var node in IrTree.Expressions(statement).SelectMany(Proper)) {
          var callee = node switch {
            IrCall call => call.Function,
            IrNew { Type.ClassType.IsLibrary: false } creation => creation.Constructor.Function,
            _ => null
          };
          if (callee is not null) {
            callees.Add(callee);
            if (seen.Add(callee))
              pending.Push(callee);
          } else if (effects.Node(node) is { ReadsEverything: true } or { WritesEverything: true }) {
            unknown ??= $"{Kind(node)} in @{current}";
          }
        }
      }
    }

    return (callees, unknown);
  }

  private static string Kind(object node) => node.GetType().Name.StartsWith("Ir", StringComparison.Ordinal) ? node.GetType().Name[2..] : node.GetType().Name;

  private static IEnumerable<string> Accessors(IEnumerable<IrProperty> properties)
    => properties.SelectMany(property => new[] { property.Getter?.Function, property.Setter?.Function }).OfType<string>();

  /// <summary>Every statement within a statement, the statement first; the bodies of lambdas are expressions and not among them.</summary>
  private static IEnumerable<IrStmt> All(IrStmt statement) {
    yield return statement;
    var inner = statement is IrBlock block ? block.Statements : IrTree.Blocks(statement).SelectMany(nested => nested.Statements);
    foreach (var nested in inner.SelectMany(All))
      yield return nested;
  }

  /// <summary>The nodes of an expression outside the bodies of lambdas, which do not run where they are made, in pre-order, operands left to right.</summary>
  private static IEnumerable<IrExpr> Proper(IrExpr expression) {
    var pending = new Stack<IrExpr>([expression]);
    while (pending.Count > 0) {
      var node = pending.Pop();
      yield return node;
      if (node is IrLambda)
        continue;

      var operands = IrTree.Operands(node);
      for (var index = operands.Count - 1; index >= 0; --index)
        pending.Push(operands[index]);
    }
  }

  private void Census(string? caller, IrStmt statement, bool inLambda) {
    foreach (var inner in All(statement))
      foreach (var expression in IrTree.Expressions(inner))
        this.Census(caller, expression, inLambda);
  }

  private void Census(string? caller, IrExpr expression, bool inLambda) {
    foreach (var node in Proper(expression))
      switch (node) {
        case IrCall call:
          if (!this._sites.TryGetValue(call.Function, out var sites))
            this._sites[call.Function] = sites = [];

          sites.Add(new(caller, call, inLambda));
          break;
        case IrFunctionRef reference:
          this._referenced.Add(reference.Function);
          break;
        case IrLambda lambda:
          foreach (var statement in lambda.Body.Statements)
            this.Census(caller, statement, true);

          break;
      }
  }

}
