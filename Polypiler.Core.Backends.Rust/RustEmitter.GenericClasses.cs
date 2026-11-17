using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Backends.Rust.Emit;
using Polypiler.Core.Backends.Rust.Naming;
using Polypiler.Core.IR;

namespace Polypiler.Core.Backends.Rust;

public static partial class RustEmitter {

  private sealed partial class Writer {

    /// <summary>The generic classes the crate may write (<see cref="GenericClasses"/>), by the class written in their place.</summary>
    private readonly Dictionary<IrClassType, GenericClass> _genericClasses = new(ReferenceEqualityComparer.Instance);

    /// <summary>The functions of the module the crate does not write: of an instantiation written as its generic class, or of a generic class not written.</summary>
    private readonly HashSet<string> _unwritten = new(StringComparer.Ordinal);

    /// <summary>The classes of the module the crate does not write, as <see cref="_unwritten"/> says of functions.</summary>
    private readonly HashSet<IrClassType> _unwrittenClasses = new(ReferenceEqualityComparer.Instance);

    /// <summary>The path a static member of a class is called by where it is not the struct's name: <c>Ring::&lt;i64&gt;</c>, <c>Self</c> within the generic class.</summary>
    private readonly Dictionary<IrClassType, string> _classPaths = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The module with the generic classes it may be written with: each in the place of its first
    /// instantiation, its functions after the module's. Whether one is written is decided once the
    /// ownership of the objects is known (<see cref="ConfirmGenericClasses"/>).
    /// </summary>
    private IrModule GeneralizedClasses(IrModule module) {
      var classes = GenericClasses.Of(module);
      if (classes.Count == 0)
        return module;

      var types = module.Types.ToList();
      foreach (var generic in classes) {
        this._genericClasses[generic.Class] = generic;
        types.Insert(types.IndexOf(generic.Instances.Keys.First()), generic.Class);
      }

      return module with { Types = types, Functions = [.. module.Functions, .. classes.SelectMany(generic => generic.Functions)] };
    }

    /// <summary>
    /// Writes each generic class in the place of the instantiations whose objects are what its own
    /// are - plain structs, the same fields kept, the same members writing their object - and leaves
    /// out the instantiations it takes the place of; a generic class that is no plain struct, that
    /// keeps no field of a parameter, or whose name another type has, is left out instead.
    /// </summary>
    private void ConfirmGenericClasses() {
      var functions = this._module.Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
      foreach (var (declared, generic) in this._genericClasses) {
        var name = RustNames.UpperCamel(generic.Name, this.Edition);
        var kept = this.PlainFields(declared).Select(field => field.Type).ToList();
        var instances = generic.Instances.Where(pair => this.Agrees(declared, pair.Key, functions)).ToList();
        if (!this.IsPlain(IrType.Object(declared)) || instances.Count == 0 || generic.Parameters.Any(parameter => !kept.Any(type => type == parameter.StandIn || type.Element == parameter.StandIn))
            || this._types.Any(pair => pair.Value == name && !ReferenceEquals(pair.Key, declared))) {
          this.Unwritten(declared);
          continue;
        }

        this._types[declared] = name;
        this._classPaths[declared] = "Self";
        foreach (var (instance, arguments) in instances) {
          var spelled = string.Join(", ", arguments.Select(argument => TypeName(argument)));
          this._types[instance] = $"{name}<{spelled}>";
          this._classPaths[instance] = $"{name}::<{spelled}>";
          this.Unwritten(instance);
        }
      }
    }

    /// <summary>Leaves a class and every function of its own out of the crate.</summary>
    private void Unwritten(IrClassType type) {
      this._unwrittenClasses.Add(type);
      this._unwritten.UnionWith(this._members.Where(pair => ReferenceEquals(pair.Value.Class, type)).Select(pair => pair.Key));
    }

    /// <summary>Whether the objects of an instantiation are held as those of its generic class: plain structs, the same fields kept, the same members writing their object.</summary>
    private bool Agrees(IrClassType declared, IrClassType instance, IReadOnlyDictionary<string, IrFunction> functions) {
      static IEnumerable<int> Positions(IrClassType type, IReadOnlyList<IrField> kept) => type.Fields.Select((field, index) => (field, index)).Where(pair => kept.Contains(pair.field)).Select(pair => pair.index);
      return this.IsPlain(IrType.Object(instance)) && Positions(declared, this.PlainFields(declared)).SequenceEqual(Positions(instance, this.PlainFields(instance)))
        && declared.Functions.Zip(instance.Functions).All(pair => functions[pair.First].Parameters.Select((_, index) => index)
          .All(index => this._ownership.MutatesParameter(functions[pair.First], index) == this._ownership.MutatesParameter(functions[pair.Second], index)));
    }

    /// <summary>The generic class a function of the module is a member of, its maker included, or <see langword="null"/>.</summary>
    private GenericClass? GenericClassOf(string function)
      => this._members.TryGetValue(function, out var member) ? this._genericClasses.GetValueOrDefault(member.Class) : null;

    /// <summary>The path a static member of a class is called by: <c>Ring::&lt;i64&gt;</c> for an instantiation written as its generic class, the struct's name for any other.</summary>
    private string ClassPath(IrClassType type) => this._classPaths.GetValueOrDefault(type) ?? this.StructName(type);

  }

}
