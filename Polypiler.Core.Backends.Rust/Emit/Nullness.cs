using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// Which references of a module may be <c>null</c>: an object variable, parameter or result that
/// never holds <c>null</c> is an <c>Rc&lt;T&gt;</c> in Rust, one that may an <c>Option&lt;Rc&lt;T&gt;&gt;</c>.
/// A value may be null where it is <c>null</c>, an <c>as</c> cast, a field (which holds null until a
/// constructor writes it), a variable or result that may be, or a parameter of an exported function,
/// which a caller outside the module may pass null to; a new object never is. The facts are computed
/// to their fixed point over the module, starting from none.
/// </summary>
internal sealed class Nullness {

  private readonly HashSet<(string Function, IrLocal Local)> _variables = [];
  private readonly HashSet<string> _results = new(StringComparer.Ordinal);

  /// <summary>Whether a variable or parameter of a function may hold null.</summary>
  public bool MayBeNull(string function, IrLocal local) => this._variables.Contains((function, local));

  /// <summary>Whether a function may return null.</summary>
  public bool ReturnsNull(string function) => this._results.Contains(function);

  /// <summary>Whether a value in a function may be null.</summary>
  public bool MayBeNull(string function, IrExpr value) => value switch {
    IrNew => false,
    IrLocalRef reference => this._variables.Contains((function, reference.Local)),
    IrCall call => this._results.Contains(call.Function),
    IrConditional choice => this.MayBeNull(function, choice.WhenTrue) || this.MayBeNull(function, choice.WhenFalse),
    IrCast cast => this.MayBeNull(function, cast.Value),
    _ => true
  };

  /// <summary>The nullness of every reference of a module.</summary>
  /// <param name="module">The module, its objects lowered to calls (<see cref="ObjectLowering"/>).</param>
  /// <param name="receivers">The functions whose first parameter is the object a method runs on, which a call on null traps before it is passed.</param>
  public static Nullness Of(IrModule module, ISet<string> receivers) {
    var facts = new Nullness();
    foreach (var function in module.Functions.Where(function => function.IsPublic))
      foreach (var parameter in function.Parameters.Where(parameter => IsReference(parameter.Type)))
        facts._variables.Add((function.Name, parameter));

    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        foreach (var statement in ArrayOwnership.Statements(function.Body)) {
          switch (statement) {
            case IrDeclare { Initializer: { } value } declare when IsReference(declare.Local.Type) && facts.MayBeNull(function.Name, value):
              changed |= facts._variables.Add((function.Name, declare.Local));
              break;
            case IrDeclare { Initializer: null } declare when IsReference(declare.Local.Type):
              changed |= facts._variables.Add((function.Name, declare.Local));
              break;
            case IrAssign assign when IsReference(assign.Local.Type) && facts.MayBeNull(function.Name, assign.Value):
              changed |= facts._variables.Add((function.Name, assign.Local));
              break;
            case IrReturn { Value: { } value } when IsReference(function.ReturnType) && facts.MayBeNull(function.Name, value):
              changed |= facts._results.Add(function.Name);
              break;
          }

          foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
            if (node is not IrCall call || !byName.TryGetValue(call.Function, out var target))
              continue;

            var arguments = call.Arguments;
            var first = receivers.Contains(call.Function) ? 1 : 0;
            for (var index = first; index < arguments.Count && index < target.Parameters.Count; ++index)
              if (IsReference(target.Parameters[index].Type) && facts.MayBeNull(function.Name, arguments[index]))
                changed |= facts._variables.Add((target.Name, target.Parameters[index]));
          }
        }
      }
    }

    return facts;
  }

  /// <summary>Whether a type is a reference to an object.</summary>
  public static bool IsReference(IrType type) => type.IsObject;

}
