using System.Collections.Generic;
using Acornima.Ast;
using Polypiler.Core.Frontends.JavaScript.Inference;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.JavaScript;

internal sealed partial class Lowering {

  /// <summary>
  /// A variable as it is read: a local or parameter, or a global of the module. A variable of the
  /// module that a function uses was lowered before the function, or refused, and then no function is.
  /// </summary>
  private static IrExpr Read(Variable variable) => variable.IsGlobal ? new IrGlobalRef(variable.Global!) : new IrLocalRef(variable.Local!);

  /// <summary>An assignment to a variable, a local or a global of the module, that <see cref="Read"/> accepted.</summary>
  private static IrStmt Write(Variable variable, IrExpr value)
    => variable.IsGlobal ? new IrGlobalAssign(variable.Global!, value) : new IrAssign(variable.Local!, value);

  /// <summary>
  /// Lowers a variable of the module to a global, or adds the reason it is refused to the
  /// diagnostics. It has the type the inference found for it from every value that reaches it, and
  /// the value of its initializer in that type; a variable declared without one starts as zero,
  /// which no function can see: a read that an assignment of the same function does not precede is
  /// refused, as for a local. The initializer is an expression, which declares no temporary; a
  /// variable of the module holds no object.
  /// </summary>
  public void Global(Variable global, FunctionModel initializers, List<Diagnostic> diagnostics) {
    this._model = initializers;
    this._locals.Clear();
    this._assigned = [];
    types.Enter(initializers);
    try {
      if (global.Problem is { } problem)
        throw this.Unsupported(global.Declaration, problem);

      this.CheckType(global.Type, global.Name, global.Evidence ?? global.Declaration);
      var type = this.Storable(global.Type, global.Initializer ?? global.Declaration);
      if ((type.Element ?? type).IsStruct)
        throw this.Unsupported(global.Declaration, $"an object in the variable {global.Name} of the module");

      var initializer = global.Initializer is null ? null : this.Value(global.Initializer, type);
      global.Global = new IrGlobal(global.Name, type, initializer);
    } catch (Refusal refusal) {
      diagnostics.Add(refusal.Diagnostic);
    }
  }

  /// <summary>Refuses a statement at the top level of a module that has variables: it would run, and change them, when the module is loaded.</summary>
  public Diagnostic TopLevel(Node statement) => this.Unsupported(statement, "a statement at the top level of the module").Diagnostic;

  /// <summary>Refuses an initializer that calls a function that reads or writes a variable of the module.</summary>
  public Diagnostic Impure(Node initializer, string function)
    => this.Unsupported(initializer, $"an initializer that calls {function}, which reads or writes a variable of the module").Diagnostic;

}
