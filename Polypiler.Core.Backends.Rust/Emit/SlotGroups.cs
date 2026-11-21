using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust.Emit;

/// <summary>
/// The functions of a module that are methods of one slot - a virtual method and its overrides, an
/// interface method and its implementations (#1422). The trait writes the slot with one signature,
/// so what a nullness analysis finds of one of them - a result or a parameter that may be null - holds
/// for all of them.
/// </summary>
internal static class SlotGroups {

  /// <summary>The functions of every slot with more than one method, each group in the order of the module's types.</summary>
  /// <param name="module">The module.</param>
  public static IReadOnlyList<IReadOnlyList<IrFunction>> Of(IrModule module) {
    var byName = module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    var methods = module.Types.OfType<IrClassType>().SelectMany(type => type.Methods)
      .Concat(module.Types.OfType<IrInterfaceType>().SelectMany(type => type.Methods))
      .Concat(module.Types.OfType<IrClassType>().SelectMany(type => type.AllInterfaces.SelectMany(face => face.Methods)
        .Select(member => (Member: member, Implementation: type.Implementation(member))).Where(pair => pair.Implementation is not null)
        .Select(pair => pair.Implementation!)));
    var groups = new List<IReadOnlyList<IrFunction>>();
    foreach (var group in methods.Where(method => !method.IsStatic).GroupBy(method => method.Root, ReferenceEqualityComparer.Instance)) {
      var functions = group.Select(method => byName.GetValueOrDefault(method.Function)).OfType<IrFunction>().Distinct().ToList();
      if (functions.Count > 1)
        groups.Add(functions);
    }

    return groups;
  }

  /// <summary>
  /// Makes the facts of one slot's functions the same: where one of them may return null all may, and
  /// where one's parameter may be null the same parameter of every other may.
  /// </summary>
  /// <param name="groups">The slots.</param>
  /// <param name="results">The functions that may return null.</param>
  /// <param name="variables">The parameters and variables that may hold null.</param>
  /// <returns>Whether a fact was added.</returns>
  public static bool Unify(IReadOnlyList<IReadOnlyList<IrFunction>> groups, HashSet<string> results, HashSet<(string Function, IrLocal Local)> variables) {
    var changed = false;
    foreach (var group in groups) {
      if (group.Any(function => results.Contains(function.Name)))
        foreach (var function in group)
          changed |= results.Add(function.Name);

      var count = group.Min(function => function.Parameters.Count);
      for (var index = 1; index < count; ++index)
        if (group.Any(function => variables.Contains((function.Name, function.Parameters[index]))))
          foreach (var function in group)
            changed |= variables.Add((function.Name, function.Parameters[index]));
    }

    return changed;
  }

}
