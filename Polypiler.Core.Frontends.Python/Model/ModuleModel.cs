using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.IR;

namespace Polypiler.Core.Frontends.Python.Model;

/// <summary>
/// The value a module-level name holds, or a default, worked out where the module's statements run: a
/// number, a truth value or a string as an IR constant, or a table of numbers as a global array of
/// the module that nothing writes.
/// </summary>
/// <param name="Type">The type of the value.</param>
/// <param name="Value">The constant expression of a scalar or a string; <see langword="null"/> for a table.</param>
/// <param name="Table">The global of a table; <see langword="null"/> for a scalar.</param>
internal sealed record ConstantValue(PyType Type, IrExpr? Value, IrGlobal? Table);

/// <summary>A module-level name a function declares <c>global</c> and assigns: a variable of the module.</summary>
/// <param name="Name">The name.</param>
/// <param name="Node">The module-level assignment that gives it its first value.</param>
internal sealed record GlobalModel(string Name, PyNode Node) {

  /// <summary>The type of the values it holds.</summary>
  public PyType Type { get; set; } = PyType.Unknown;

  /// <summary>The first value, worked out where the module's statements run.</summary>
  public ConstantValue? Initial { get; set; }

  /// <summary>The global of the IR.</summary>
  public IrGlobal? Ir { get; set; }

}

/// <summary>What an imported name is bound to.</summary>
/// <param name="Module">The module: one of the standard library's (<c>math</c>, <c>struct</c>, …) or a module of the import.</param>
/// <param name="Name">The name imported from it with <c>from … import</c>; <see langword="null"/> for the module itself.</param>
/// <param name="Node">The import statement.</param>
internal sealed record ImportBinding(string Module, string? Name, PyNode Node) {

  /// <summary>The module of the import a binding names, or <see langword="null"/> for a module of the standard library.</summary>
  public ModuleModel? User { get; init; }

}

/// <summary>A module of the import: its functions, classes, constants, globals and imports.</summary>
internal sealed class ModuleModel {

  /// <summary>Describes a module.</summary>
  /// <param name="name">The name of the module, as an import names it.</param>
  /// <param name="file">The file the module is read from.</param>
  /// <param name="node">The <c>Module</c> node.</param>
  public ModuleModel(string name, SourceFile file, PyNode node) {
    this.Name = name;
    this.File = file;
    this.Node = node;
  }

  /// <summary>The name of the module.</summary>
  public string Name { get; }

  /// <summary>The file the module is read from.</summary>
  public SourceFile File { get; }

  /// <summary>The <c>Module</c> node.</summary>
  public PyNode Node { get; }

  /// <summary>Whether the module is the one the import started from: its public functions are the exports.</summary>
  public bool IsMain { get; init; }

  /// <summary>Whether the module is the <c>__init__.py</c> of a package.</summary>
  public bool IsPackage { get; init; }

  /// <summary>The module-level functions, by name, in source order.</summary>
  public Dictionary<string, FunctionModel> Functions { get; } = [];

  /// <summary>The classes, by name, in source order.</summary>
  public Dictionary<string, ClassModel> Classes { get; } = [];

  /// <summary>The names the module's statements assign, known before their values are.</summary>
  public HashSet<string> Names { get; } = [];

  /// <summary>The module-level names that hold one value for every function: constants and tables.</summary>
  public Dictionary<string, ConstantValue> Constants { get; } = [];

  /// <summary>The module-level names functions assign through <c>global</c>.</summary>
  public Dictionary<string, GlobalModel> GlobalVariables { get; } = [];

  /// <summary>The names imports bind, by the name they bind.</summary>
  public Dictionary<string, ImportBinding> Imports { get; } = [];

  /// <summary>The names <c>__all__</c> lists, or <see langword="null"/> where the module has none.</summary>
  public IReadOnlyList<string>? All { get; set; }

  /// <summary>The source of the module's statements whose names the inference has to type from what they are assigned: the module-level assignments, in order.</summary>
  public List<PyNode> Statements { get; } = [];

  /// <summary>Every function of the module: module-level ones, methods, and those nested in them, each after the one it stands in.</summary>
  public IEnumerable<FunctionModel> AllFunctions {
    get {
      IEnumerable<FunctionModel> With(FunctionModel function) => function.Nested.Values.SelectMany(With).Prepend(function);
      return this.Functions.Values.Concat(this.Classes.Values.SelectMany(type => type.Methods.Values)).SelectMany(With);
    }
  }

  /// <summary>Whether a function of the module is exported: listed in <c>__all__</c>, or public where there is none.</summary>
  public bool Exports(FunctionModel function)
    => this.IsMain && function.Owner is null && function.Parent is null && (this.All?.Contains(function.Name) ?? function.IsPublic);

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}
