using System;
using System.Collections.Generic;
using System.Linq;
using Polypiler.Core.Frontends.Python.Ast;
using Polypiler.Core.Frontends.Python.Lowering;
using Polypiler.Core.Frontends.Python.Model;

namespace Polypiler.Core.Frontends.Python.Resolution;

/// <summary>A module of the import read from its file: its name, file and tree.</summary>
/// <param name="Name">The name an import gives it, such as <c>pkg.helper</c>.</param>
/// <param name="File">The file.</param>
/// <param name="Tree">The <c>Module</c> node.</param>
/// <param name="IsPackage">Whether the file is the <c>__init__.py</c> of a package, against which a relative import resolves.</param>
internal sealed record ModuleSource(string Name, SourceFile File, PyNode Tree, bool IsPackage = false);

/// <summary>
/// Collects what the statements of a module declare: its imports, functions, classes and module-level
/// names, the modules of the import it imports, in the order they are written. Everything at module
/// level other than these, a docstring, <c>from __future__ import</c> and the block under
/// <c>if __name__ == "__main__":</c>, which a translation does not run, is refused.
/// </summary>
internal sealed class Collector(Func<string, ModuleSource?> locate, Dictionary<PyNode, Variable> bound, List<Diagnostic> diagnostics) {

  private readonly Dictionary<string, ModuleModel> _modules = [];
  private readonly HashSet<string> _loading = [];

  /// <summary>The modules collected, each after the modules it imports.</summary>
  public List<ModuleModel> Modules { get; } = [];

  /// <summary>Collects a module and every module of the import it imports.</summary>
  public ModuleModel Collect(ModuleSource source, bool main) {
    var module = new ModuleModel(source.Name, source.File, source.Tree) { IsMain = main, IsPackage = source.IsPackage };
    this._modules[source.Name] = module;
    this._loading.Add(source.Name);
    foreach (var statement in source.Tree.Nodes("body"))
      this.Statement(module, statement);

    this._loading.Remove(source.Name);
    foreach (var function in module.Functions.Values)
      ScopeResolver.CollectLocals(function, bound);

    foreach (var method in module.Classes.Values.SelectMany(type => type.Methods.Values))
      ScopeResolver.CollectLocals(method, bound);

    // A name a function declares global and the module assigns is a variable of the module; every other module-level name holds one value.
    var declared = module.AllFunctions.SelectMany(function => function.Globals).ToHashSet();
    foreach (var statement in module.Statements.Where(statement => statement.Kind is "Assign" or "AnnAssign" or "AugAssign"))
      foreach (var target in Assigned(statement).Where(target => declared.Contains(target.Text("id")!)))
        module.GlobalVariables.TryAdd(target.Text("id")!, new GlobalModel(target.Text("id")!, statement));

    this.Modules.Add(module);
    return module;
  }

  /// <summary>The names a module-level assignment binds.</summary>
  public static IEnumerable<PyNode> Assigned(PyNode statement) => statement.Kind switch {
    "Assign" => statement.Nodes("targets").SelectMany(ScopeResolver.Targets),
    _ => ScopeResolver.Targets(statement.Node("target")!)
  };

  private void Refuse(ModuleModel module, PyNode node, DiagnosticDescriptor descriptor, string message) => diagnostics.Add(module.File.At(node, descriptor, message));

  private void Statement(ModuleModel module, PyNode statement) {
    switch (statement.Kind) {
      case "FunctionDef":
        if (Feature(statement) is { } feature) {
          this.Refuse(module, statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'{feature}' is not supported for functions by the Python frontend");
          break;
        }

        var function = new FunctionModel(statement);
        if (module.Functions.ContainsKey(function.Name) || module.Classes.ContainsKey(function.Name)) {
          this.Refuse(module, statement, PythonFrontend.UnsupportedFunctionDescriptor, $"'redefinition of {function.Name}' is not supported for functions by the Python frontend");
          break;
        }

        module.Functions[function.Name] = function;
        module.Statements.Add(statement);
        break;
      case "AsyncFunctionDef":
        this.Refuse(module, statement, PythonFrontend.EscapingFunctionDescriptor, SubsetCheck.Escaping(statement.Text("name")!) + " (coroutines: #752)");
        break;
      case "ClassDef":
        new ClassCollector(module, diagnostics).Collect(statement);
        if (module.Classes.ContainsKey(statement.Text("name")!))
          module.Statements.Add(statement);

        break;
      case "Expr" when statement.Node("value") is { Kind: "Constant" } docstring && docstring.Text("kind") == "str":
      case "ImportFrom" when statement.Text("module") == "__future__":
      case "If" when IsMainGuard(statement):
        break;
      case "Import":
        this.Import(module, statement);
        break;
      case "ImportFrom":
        this.ImportFrom(module, statement);
        break;
      case "Assign" when statement.Nodes("targets") is [{ Kind: "Name" } all] && all.Text("id") == "__all__":
        module.All = statement.Node("value")!.Nodes("elts").Select(element => element.Text("value") ?? "").ToList();
        break;
      case "Assign" when statement.Nodes("targets").All(target => ScopeResolver.Targets(target).Count() == (target.Is("Name") ? 1 : target.Nodes("elts").Count) && target.Kind is "Name" or "Tuple"):
      case "AnnAssign" when statement.Node("target")!.Is("Name") && statement.Node("value") is not null:
      case "AugAssign" when statement.Node("target")!.Is("Name"):
        module.Statements.Add(statement);
        foreach (var target in Assigned(statement))
          module.Names.Add(target.Text("id")!);

        break;
      default:
        this.Refuse(module, statement, PythonFrontend.UnsupportedStatementDescriptor, $"statement 'module-level {module.File.Quote(statement)}' is not supported by the Python frontend");
        break;
    }
  }

  private void Import(ModuleModel module, PyNode statement) {
    foreach (var alias in statement.Nodes("names")) {
      var name = alias.Text("name")!;
      var bound = alias.Text("asname") ?? name.Split('.')[0];
      if (LibraryCallMap.IsKnownModule(name)) {
        if (name.Contains('.') && alias.Text("asname") is null)
          this.Refuse(module, statement, PythonFrontend.UnresolvedDescriptor, $"'{name}' is neither part of this import nor in the Python call map: import it with 'from {name} import …'");
        else
          module.Imports[bound] = new ImportBinding(name, null, statement);

        continue;
      }

      if (this.Load(module, statement, name) is { } user) {
        if (name.Contains('.') && alias.Text("asname") is null)
          this.Refuse(module, statement, PythonFrontend.UnresolvedDescriptor, $"'{name}' is neither part of this import nor in the Python call map: import it with 'import {name} as …'");
        else
          module.Imports[bound] = new ImportBinding(name, null, statement) { User = user };
      }
    }
  }

  private void ImportFrom(ModuleModel module, PyNode statement) {
    var level = statement.Number("level");
    var named = statement.Text("module");
    var aliases = statement.Nodes("names");
    if (aliases.Any(alias => alias.Text("name") == "*")) {
      this.Refuse(module, statement, PythonFrontend.UnresolvedDescriptor, $"'{module.File.Quote(statement)}' is neither part of this import nor in the Python call map: a star import");
      return;
    }

    var target = Absolute(module.Name, named, module.IsPackage && level > 0 ? level - 1 : level, level > 0);
    if (level == 0 && named is not null && LibraryCallMap.IsKnownModule(named)) {
      foreach (var alias in aliases) {
        var name = alias.Text("name")!;
        if (!LibraryCallMap.Has(named, name))
          this.Refuse(module, alias.Line > 1 ? alias : statement, PythonFrontend.UnresolvedDescriptor, $"'{named}.{name}' is neither part of this import nor in the Python call map");
        else
          module.Imports[alias.Text("asname") ?? name] = new ImportBinding(named, name, statement);
      }

      return;
    }

    foreach (var alias in aliases) {
      var name = alias.Text("name")!;
      // from pkg import sub imports a module where the package has one of that name.
      var submodule = target.Length == 0 ? name : target + "." + name;
      if (named is null || locate(submodule) is not null) {
        if (this.Load(module, statement, submodule) is { } loaded)
          module.Imports[alias.Text("asname") ?? name] = new ImportBinding(submodule, null, statement) { User = loaded };

        continue;
      }

      if (this.Load(module, statement, target) is { } user) {
        if (!user.Functions.ContainsKey(name) && !user.Classes.ContainsKey(name) && !user.Names.Contains(name))
          this.Refuse(module, statement, PythonFrontend.UnresolvedDescriptor, $"'{target}.{name}' is neither part of this import nor in the Python call map: '{target}' has no function or class '{name}'");
        else
          module.Imports[alias.Text("asname") ?? name] = new ImportBinding(target, name, statement) { User = user };
      }
    }
  }

  /// <summary>The absolute name of a module a <c>from … import</c> names, relative to the importing module's package.</summary>
  private static string Absolute(string importer, string? named, int level, bool relative) {
    if (!relative)
      return named ?? "";

    var package = importer.Split('.').ToList();
    // The package of a module is its name without the last part; each further dot goes one package up.
    package.RemoveRange(Math.Max(0, package.Count - level), Math.Min(level, package.Count));
    if (named is not null)
      package.Add(named);

    return string.Join('.', package);
  }

  /// <summary>A module of the import by its name, collected on first use; refused where it is missing or imports itself back.</summary>
  private ModuleModel? Load(ModuleModel importer, PyNode statement, string name) {
    if (this._loading.Contains(name)) {
      this.Refuse(importer, statement, PythonFrontend.UnresolvedDescriptor, $"'{name}' is neither part of this import nor in the Python call map: the modules import each other in a cycle");
      return null;
    }

    if (this._modules.TryGetValue(name, out var known))
      return known;

    if (locate(name) is not { } source) {
      this.Refuse(importer, statement, PythonFrontend.UnresolvedDescriptor, $"'{name}' is neither part of this import nor in the Python call map");
      return null;
    }

    return this.Collect(source, false);
  }

  /// <summary>The first feature of a <c>def</c> outside the subset, or <see langword="null"/>.</summary>
  public static string? Feature(PyNode function) {
    var arguments = function.Node("args")!;
    var name = function.Text("name");
    return function.Nodes("decorator_list").Count > 0 ? $"a decorator of {name}"
      : arguments.Node("vararg") is not null ? $"*{arguments.Node("vararg")!.Text("arg")} of {name}"
      : arguments.Node("kwarg") is not null ? $"**{arguments.Node("kwarg")!.Text("arg")} of {name}"
      : function.Nodes("type_params").Count > 0 ? $"a type parameter of {name}"
      : null;
  }

  /// <summary>Whether a statement is <c>if __name__ == "__main__":</c>.</summary>
  public static bool IsMainGuard(PyNode statement)
    => statement.Node("test") is { Kind: "Compare" } test && test.Nodes("ops") is [{ Kind: "Eq" }]
      && test.Node("left") is { Kind: "Name" } left && left.Text("id") == "__name__"
      && test.Nodes("comparators") is [{ Kind: "Constant" } main] && main.Text("value") == "__main__";

}
