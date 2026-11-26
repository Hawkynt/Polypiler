using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// Which strings of a module may be <c>null</c> (#1422): a string variable, parameter, result or field
/// that may is an <c>Option&lt;Vec&lt;u16&gt;&gt;</c> in Rust, every other one the code units it always
/// was. A string may be null where it is given <c>null</c> or a value that may be null, or where the
/// module tests it for null - a field the module tests holds null until a constructor writes it, as
/// .NET's does. The facts are computed to their fixed point over the module, starting from none. Like an
/// array, an exported string parameter is not taken to be nullable unless the module tests it.
/// </summary>
internal sealed class StringNullness {

  private readonly HashSet<(string Function, IrLocal Local)> _variables = [];
  private readonly HashSet<string> _results = new(StringComparer.Ordinal);
  private readonly HashSet<IrField> _fields = new(ReferenceEqualityComparer.Instance);

  /// <summary>Whether a string variable or parameter of a function may hold null.</summary>
  public bool MayBeNull(string function, IrLocal local) => local.Type.IsString && this._variables.Contains((function, local));

  /// <summary>Whether a function may return a null string.</summary>
  public bool ReturnsNull(string function) => this._results.Contains(function);

  /// <summary>Whether a string field may hold null.</summary>
  public bool MayBeNull(IrField field) => field.Type.IsString && this._fields.Contains(field);

  /// <summary>Whether any string of the module may be null.</summary>
  public bool Any => this._variables.Count > 0 || this._results.Count > 0 || this._fields.Count > 0;

  /// <summary>Whether a string value in a function may be null.</summary>
  public bool MayBeNull(string function, IrExpr value) => value.Type.IsString && value switch {
    IrNull => true,
    IrLocalRef reference => this._variables.Contains((function, reference.Local)),
    IrBind bind => this._variables.Contains((function, bind.Local)),
    IrFieldLoad load => this._fields.Contains(load.Field),
    IrCall call => this._results.Contains(call.Function),
    IrAwait { Operand: IrCall call } => this._results.Contains(call.Function),
    IrAwait { Operand: IrAsync { Op: IrAsyncOp.FromResult } result } => this.MayBeNull(function, result.Arguments[0]),
    IrConditional choice => this.MayBeNull(function, choice.WhenTrue) || this.MayBeNull(function, choice.WhenFalse),
    _ => false
  };

  /// <summary>The value a test for null names: of <c>x == null</c>, <c>x is null</c>, <c>ReferenceEquals(x, null)</c>.</summary>
  private static IrExpr? Tested(IrExpr node) => node switch {
    IrIsNull { Value.Type.IsString: true } test => test.Value,
    IrReferenceEquals { Left: IrNull, Right.Type.IsString: true } equality => equality.Right,
    IrReferenceEquals { Right: IrNull, Left.Type.IsString: true } equality => equality.Left,
    _ => null
  };

  /// <summary>The nullness of every string of a module.</summary>
  /// <param name="module">The module, its objects lowered to calls.</param>
  public static StringNullness Of(IrModule module) {
    var facts = new StringNullness();
    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    // A call through a slot passes its arguments to every method of the slot.
    var slots = new Dictionary<IrMethod, List<string>>(ReferenceEqualityComparer.Instance);
    foreach (var method in module.Types.OfType<IrClassType>().SelectMany(type => type.Methods).Concat(module.Types.OfType<IrInterfaceType>().SelectMany(type => type.Methods))) {
      if (!slots.TryGetValue(method.Root, out var functions))
        slots[method.Root] = functions = [];
      functions.Add(method.Function);
    }

    var groups = SlotGroups.Of(module);
    for (var changed = true; changed;) {
      changed = false;
      foreach (var function in module.Functions) {
        bool Add(IrLocal local) => facts._variables.Add((function.Name, local));
        bool Nullable(IrExpr value) => facts.MayBeNull(function.Name, value);
        foreach (var statement in ArrayOwnership.Statements(function.Body)) {
          switch (statement) {
            case IrDeclare { Local.Type.IsString: true, Initializer: { } value } declare when Nullable(value):
              changed |= Add(declare.Local);
              break;
            case IrAssign { Local.Type.IsString: true } assign when Nullable(assign.Value):
              changed |= Add(assign.Local);
              break;
            case IrReturn { Value: { Type.IsString: true } value } when Nullable(value):
              changed |= facts._results.Add(function.Name);
              break;
            case IrFieldStore { Field.Type.IsString: true } store when Nullable(store.Value):
              changed |= facts._fields.Add(store.Field);
              break;
          }

          foreach (var node in IrTree.Expressions(statement).SelectMany(IrTree.Nodes)) {
            switch (Tested(node)) {
              case IrLocalRef tested:
                changed |= Add(tested.Local);
                break;
              case IrBind bound:
                changed |= Add(bound.Local);
                break;
              case IrFieldLoad load:
                changed |= facts._fields.Add(load.Field);
                break;
            }

            switch (node) {
              case IrBind { Local.Type.IsString: true } bind when Nullable(bind.Value):
                changed |= Add(bind.Local);
                break;
              case IrCall call when byName.TryGetValue(call.Function, out var target):
                for (var index = 0; index < call.Arguments.Count && index < target.Parameters.Count; ++index)
                  if (target.Parameters[index].Type.IsString && Nullable(call.Arguments[index]))
                    changed |= facts._variables.Add((target.Name, target.Parameters[index]));
                break;
              case IrMethodCall method when slots.TryGetValue(method.Method.Root, out var implementations):
                foreach (var implementation in implementations.Select(name => byName.GetValueOrDefault(name)).OfType<IrFunction>())
                  for (var index = 0; index < method.Arguments.Count && index + 1 < implementation.Parameters.Count; ++index)
                    if (implementation.Parameters[index + 1].Type.IsString && Nullable(method.Arguments[index]))
                      changed |= facts._variables.Add((implementation.Name, implementation.Parameters[index + 1]));
                break;
            }
          }
        }
      }

      changed |= SlotGroups.Unify(groups, facts._results, facts._variables);
    }

    return facts;
  }

}
