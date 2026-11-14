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
    Program program, IReadOnlyList<Variable> globals, IReadOnlyList<FunctionModel> models, FunctionModel initializers, Lowering lowering, List<Diagnostic> diagnostics) {
    var used = models.SelectMany(model => model.Bindings.Values).OfType<Variable>().Where(variable => variable.IsGlobal).ToHashSet();
    // An initializer reads only variables declared before it, so one pass from the last to the first finds them all.
    for (var index = globals.Count - 1; index >= 0; --index)
      if (used.Contains(globals[index]) && globals[index].Initializer is { } initializer)
        foreach (var name in Identifiers(initializer))
          if (initializers.Bindings.GetValueOrDefault(name) is Variable { IsGlobal: true } read)
            used.Add(read);

    var state = globals.Where(used.Contains).ToList();
    if (state.Count == 0)
      return state;

    foreach (var statement in program.Body.Where(statement => !IsDeclaration(statement)))
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

  /// <summary>Whether a statement at the top level only declares: a function, a variable, an import or an export, or the directive <c>"use strict"</c>.</summary>
  private static bool IsDeclaration(Statement statement)
    => statement is FunctionDeclaration or VariableDeclaration or ImportDeclaration or ExportNamedDeclaration or ExportDefaultDeclaration { Declaration: FunctionDeclaration }
      or Directive or EmptyStatement;

  /// <summary>Every identifier within a node.</summary>
  private static IEnumerable<Identifier> Identifiers(Node node)
    => node is Identifier identifier ? [identifier] : node.ChildNodes.SelectMany(Identifiers);

}
