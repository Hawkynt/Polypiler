using System.Collections.Generic;
using System.Linq;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

/// <summary>
/// The variables of a module that the imported functions use, and what a module with such state may
/// have at its top level. A module is evaluated once, in source order (ECMA-262 §16.2): its
/// declarations initialise its variables, and any other statement there would run once as well and
/// could change them, which no target language reproduces in the same order. So a module with state
/// may have nothing at its top level but declarations.
/// </summary>
internal static class ModuleState {

  /// <summary>
  /// Lowers the variables of the module that the functions use, and those their initializers read,
  /// in source order; refuses a statement at the top level of a module that has any.
  /// </summary>
  /// <returns>The variables lowered, in source order.</returns>
  public static IReadOnlyList<Variable> Lower(
    Program program, IReadOnlyList<Variable> globals, IReadOnlyList<FunctionModel> models, FunctionModel initializers, Lowering lowering, List<Diagnostic> diagnostics,
    IEnumerable<Variable> owned, IReadOnlyCollection<Node> declarations) {
    var used = models.SelectMany(model => model.Bindings.Values).OfType<Variable>().Where(variable => variable.IsGlobal).ToHashSet();
    // A static field is a member of its class, whoever uses it.
    used.UnionWith(owned);
    // An initializer reads only variables declared before it, so one pass from the last to the first finds them all.
    for (var index = globals.Count - 1; index >= 0; --index)
      if (used.Contains(globals[index]) && globals[index].Initializer is { } initializer)
        foreach (var name in Identifiers(initializer))
          if (initializers.Bindings.GetValueOrDefault(name) is Variable { IsGlobal: true } read)
            used.Add(read);

    var state = globals.Where(used.Contains).ToList();
    if (state.Count == 0)
      return state;

    foreach (var statement in program.Body.Where(statement => !IsDeclaration(statement) && !declarations.Contains(statement)))
      diagnostics.Add(lowering.TopLevel(statement));

    foreach (var global in state)
      lowering.Global(global, initializers, diagnostics);

    return state;
  }

  /// <summary>Refuses an initializer that calls a function that reads or writes a variable of the module, directly or through other calls.</summary>
  public static void CheckInitializers(IReadOnlyList<Variable> state, IReadOnlyList<IrFunction> functions, Lowering lowering, List<Diagnostic> diagnostics) {
    if (state.Count == 0)
      return;

    var impure = IrVerifier.Impure(new IrModule("", functions));
    foreach (var global in state)
      if (global.Global!.Initializer is { } value && IrTree.Nodes(value).OfType<IrCall>().FirstOrDefault(call => impure.Contains(call.Function)) is { } call)
        diagnostics.Add(lowering.Impure(global.Initializer!, call.Function));
  }

  /// <summary>
  /// Refuses a variable of the module that the module's initializer reads - itself or through the
  /// functions it calls, the methods that override those it calls and the functions it makes values of
  /// - in a statement that stands before the variable's declaration (#1410). JavaScript evaluates the
  /// module in source order, so such a read finds the variable not yet given its value (a
  /// ReferenceError for <c>let</c> and <c>const</c>, <c>undefined</c> for <c>var</c>); the IR gives every
  /// variable its value before the initializer runs.
  /// </summary>
  public static void CheckOrder(IReadOnlyList<Variable> state, IrFunction? initializer, IReadOnlyList<IrFunction> functions, IEnumerable<IrClassType> classes,
    IReadOnlyDictionary<IrStmt, Node> sites, Lowering lowering, List<Diagnostic> diagnostics) {
    if (initializer is null || state.Count == 0)
      return;

    // What a function reaches: the functions it calls or makes values of, and the overrides of the methods it calls.
    var overriders = classes.SelectMany(type => type.Methods).Where(method => method.Overrides is not null)
      .ToLookup(method => method.Overrides!.Function, method => method.Function);
    IEnumerable<string> Reached(IEnumerable<IrExpr> nodes) => nodes.SelectMany(node => node switch {
      IrCall call => [call.Function],
      IrMethodCall call => overriders[call.Method.Function].Prepend(call.Method.Function),
      IrNew creation => [creation.Constructor.Function],
      IrFunctionRef value => [value.Function],
      _ => System.Array.Empty<string>()
    });

    var bodies = functions.ToDictionary(function => function.Name, function => IrTree.Nodes(function.Body).ToList());
    foreach (var global in state.Where(global => global.Global is not null)) {
      var declared = global.Declaration.Start;
      var early = sites.Where(site => site.Value.Start < declared).ToList();
      if (early.Count == 0)
        continue;

      // The functions that read the variable, directly or through what they reach.
      var readers = bodies.Where(body => body.Value.Any(node => node is IrGlobalRef reference && reference.Global == global.Global)).Select(body => body.Key).ToHashSet();
      for (var changed = true; changed;) {
        changed = false;
        foreach (var (name, nodes) in bodies)
          if (!readers.Contains(name) && Reached(nodes).Any(readers.Contains))
            changed |= readers.Add(name);
      }

      foreach (var (statement, node) in early) {
        var nodes = IrTree.Nodes(statement).ToList();
        if (nodes.Any(part => part is IrGlobalRef reference && reference.Global == global.Global) || Reached(nodes).Any(readers.Contains)) {
          diagnostics.Add(lowering.ReadEarly(node, global.Name));
          break;
        }
      }
    }
  }

  /// <summary>Whether a statement at the top level only declares: a function, a variable, an import or an export, or the directive <c>"use strict"</c>.</summary>
  private static bool IsDeclaration(Statement statement)
    => statement is FunctionDeclaration or VariableDeclaration or ImportDeclaration or ExportNamedDeclaration or ExportDefaultDeclaration { Declaration: FunctionDeclaration }
      or Directive or EmptyStatement;

  /// <summary>Every identifier within a node.</summary>
  private static IEnumerable<Identifier> Identifiers(Node node)
    => node is Identifier identifier ? [identifier] : node.ChildNodes.SelectMany(Identifiers);

}
