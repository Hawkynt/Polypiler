using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The generic definitions the crate writes, by the IR name of their function.</summary>
    private readonly Dictionary<string, GenericDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>The definition each instantiation it takes the place of belongs to, by the instantiation's IR name.</summary>
    private readonly Dictionary<string, GenericDefinition> _generalized = new(StringComparer.Ordinal);

    /// <summary>The definition being written, or <see langword="null"/>.</summary>
    private GenericDefinition? _definition;

    /// <summary>The name of each type parameter of the definition being written, by the type that stands for it.</summary>
    private Dictionary<IrType, string> _standIns = [];

    /// <summary>
    /// The module with the generic definitions it is written with (<see cref="GenericDefinitions"/>):
    /// each in the place of its first instantiation; the instantiations stay, for the analyses of their
    /// callers, but are not written.
    /// </summary>
    private IrModule Generalized(IrModule module) {
      var definitions = GenericDefinitions.Of(module);
      if (definitions.Count == 0)
        return module;

      var functions = module.Functions.ToList();
      foreach (var definition in definitions) {
        this._definitions[definition.Function.Name] = definition;
        foreach (var instance in definition.Instances.Keys)
          this._generalized[instance] = definition;

        var first = functions.FindIndex(function => function.Origin?.Name == definition.Function.Name);
        functions.Insert(first, definition.Function with { IsPublic = functions[first].IsPublic });
      }

      return module with { Functions = functions };
    }

    /// <summary>Names every instantiation written as its definition by the path its calls take, <c>max</c> or <c>def::&lt;i32&gt;</c>.</summary>
    private void NameInstances() {
      foreach (var (instance, definition) in this._generalized)
        this._functions[instance] = definition.CallPath(this._functions[definition.Function.Name], instance);
    }

  }

  /// <summary>The path below the module of every instantiation of a module's generic methods that the crate writes as its generic definition, by its IR name.</summary>
  /// <param name="module">The module.</param>
  /// <param name="edition">The edition, whose keywords are escaped.</param>
  internal static Dictionary<string, string> GeneralizedExports(IrModule module, int edition) {
    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var definition in GenericDefinitions.Of(module))
      foreach (var instance in definition.Instances.Keys)
        paths[instance] = definition.CallPath(Naming.RustNames.Snake(definition.Function.Name, edition), instance);

    return paths;
  }

}
