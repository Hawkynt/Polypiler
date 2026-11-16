using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// Which arrays of a module may be <c>null</c>: an array variable, parameter or result that may is an
/// <c>Option</c> in Rust, every other one the slice or vector it always was. An array may be null where
/// the module tests it for null (the source's <c>int[]?</c>, its <c>?.</c> and <c>??</c>), where it is
/// given <c>null</c>, or a value that may be null; the facts are computed to their fixed point. Unlike
/// a reference to an object, an exported array parameter is not taken to be nullable unless the module
/// tests it: an array a caller passes is one.
/// </summary>
internal sealed class ArrayNullness {

  private readonly HashSet<(string Function, IrLocal Local)> _variables = [];
  private readonly HashSet<string> _results = new(StringComparer.Ordinal);

  /// <summary>Whether an array variable or parameter of a function may hold null.</summary>
  public bool MayBeNull(string function, IrLocal local) => this._variables.Contains((function, local));

  /// <summary>Whether a function may return a null array.</summary>
  public bool ReturnsNull(string function) => this._results.Contains(function);

  /// <summary>Whether an array value in a function may be null.</summary>
  public bool MayBeNull(string function, IrExpr value) => value switch {
    IrNull => true,
    IrLocalRef reference => this._variables.Contains((function, reference.Local)),
    IrBind bind => this._variables.Contains((function, bind.Local)),
    IrCall call => this._results.Contains(call.Function),
    IrConditional choice => this.MayBeNull(function, choice.WhenTrue) || this.MayBeNull(function, choice.WhenFalse),
    _ => false
  };

  /// <summary>Whether any array of the module may be null.</summary>
  public bool Any => this._variables.Count > 0 || this._results.Count > 0;

  /// <summary>The nullness of every array of a module.</summary>
  /// <param name="module">The module, its objects lowered to calls.</param>
  public static ArrayNullness Of(IrModule module) {
    var facts = new ArrayNullness();
    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        bool Add(IrLocal local) => facts._variables.Add((function.Name, local));
        foreach (var statement in ArrayOwnership.Statements(function.Body)) {
          switch (statement) {
            case IrDeclare { Local.Type.IsArray: true, Initializer: { } value } declare when facts.MayBeNull(function.Name, value):
              changed |= Add(declare.Local);
              break;
            case IrAssign { Local.Type.IsArray: true } assign when facts.MayBeNull(function.Name, assign.Value):
              changed |= Add(assign.Local);
              break;
            case IrReturn { Value: { Type.IsArray: true } value } when facts.MayBeNull(function.Name, value):
              changed |= facts._results.Add(function.Name);
              break;
          }

          foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
            switch (node) {
              case IrIsNull { Value: IrLocalRef { Type.IsArray: true } tested }:
                changed |= Add(tested.Local);
                break;
              case IrIsNull { Value: IrBind { Local.Type.IsArray: true } bound }:
                changed |= Add(bound.Local);
                break;
              case IrBind { Local.Type.IsArray: true } bind when facts.MayBeNull(function.Name, bind.Value):
                changed |= Add(bind.Local);
                break;
              case IrCall call when byName.TryGetValue(call.Function, out var target):
                for (var index = 0; index < call.Arguments.Count && index < target.Parameters.Count; ++index)
                  if (target.Parameters[index].Type.IsArray && facts.MayBeNull(function.Name, call.Arguments[index]))
                    changed |= facts._variables.Add((target.Name, target.Parameters[index]));
                break;
            }
          }
        }
      }
    }

    return facts;
  }

}
