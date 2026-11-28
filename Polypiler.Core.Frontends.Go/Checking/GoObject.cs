using System.Collections.Generic;
using Polypiler.Core.Frontends.Go.Ast;

namespace Polypiler.Core.Frontends.Go.Checking;

/// <summary>The kinds of the objects a name denotes.</summary>
internal enum GoObjectKind {
  Var,
  Const,
  TypeName,
  Func,
  Builtin,
  PackageName,
  Nil,
  Label
}

/// <summary>How far the declaration of a package-level object is checked.</summary>
internal enum GoResolution {
  Unresolved,
  Resolving,
  Resolved
}

/// <summary>An object a name denotes: a variable, constant, type, function, built-in, imported package, <c>nil</c> or label.</summary>
internal sealed class GoObject(GoObjectKind kind, string name, GoType? type, int line = 0, int column = 0) {

  /// <summary>The kind.</summary>
  public GoObjectKind Kind { get; } = kind;

  /// <summary>The name.</summary>
  public string Name { get; } = name;

  /// <summary>The type; <see langword="null"/> while a package-level object is not resolved.</summary>
  public GoType? Type { get; set; } = type;

  /// <summary>The 1-based line of the declaring identifier; 0 outside the file.</summary>
  public int Line { get; } = line;

  /// <summary>The 1-based byte column of the declaring identifier.</summary>
  public int Column { get; } = column;

  /// <summary>The value of a constant.</summary>
  public GoConstant? Value { get; set; }

  /// <summary>Whether a variable or an import is used.</summary>
  public bool Used { get; set; }

  /// <summary>Whether a variable is a struct field.</summary>
  public bool IsField { get; init; }

  /// <summary>The import path of an imported package, or of the package an object of another package belongs to.</summary>
  public string? Package { get; init; }

  /// <summary>The members of an imported package.</summary>
  public GoScope? Members { get; init; }

  /// <summary>The declaration of a package-level object, checked when the object is first needed.</summary>
  public GoNode? Declaration { get; init; }

  /// <summary>How far a package-level object is checked.</summary>
  public GoResolution Resolution { get; set; } = GoResolution.Resolved;

  /// <summary>Whether a variable is declared at the package level.</summary>
  public bool IsGlobal { get; init; }

  /// <summary>Whether a method has a pointer receiver.</summary>
  public bool PointerReceiver { get; init; }

  /// <summary>The package-level objects the declaration of a variable or function refers to, for the order of initialization.</summary>
  public HashSet<GoObject> Dependencies { get; } = [];

  /// <summary>The name of the kind as the tree carries it.</summary>
  public string KindName => this.Kind switch {
    GoObjectKind.Var => "var",
    GoObjectKind.Const => "const",
    GoObjectKind.TypeName => "type",
    GoObjectKind.Func => "func",
    GoObjectKind.Builtin => "builtin",
    GoObjectKind.PackageName => "pkg",
    GoObjectKind.Nil => "nil",
    _ => "label"
  };

  /// <inheritdoc/>
  public override string ToString() => this.Name;

}

/// <summary>A scope: the objects declared in a block, a function, a file, the package or the universe, and the scope it is inside.</summary>
internal sealed class GoScope(GoScope? parent, string kind) {

  private readonly Dictionary<string, GoObject> _objects = new(System.StringComparer.Ordinal);

  /// <summary>The enclosing scope.</summary>
  public GoScope? Parent { get; } = parent;

  /// <summary>What the scope is: <c>universe</c>, <c>package</c>, <c>file</c>, <c>function</c> or <c>block</c>.</summary>
  public string Kind { get; } = kind;

  /// <summary>The objects declared here, in the order of declaration.</summary>
  public List<GoObject> Declared { get; } = [];

  /// <summary>The object of a name declared in this scope itself, or <see langword="null"/>.</summary>
  public GoObject? Local(string name) => this._objects.GetValueOrDefault(name);

  /// <summary>The object of a name in this scope or an enclosing one, and the scope it is in.</summary>
  public (GoObject Object, GoScope Scope)? Lookup(string name) {
    for (var scope = this; scope is not null; scope = scope.Parent)
      if (scope.Local(name) is { } found)
        return (found, scope);

    return null;
  }

  /// <summary>Declares an object; the one the name already denotes here, or <see langword="null"/> when it is new.</summary>
  public GoObject? Insert(GoObject @object) {
    if (this._objects.TryGetValue(@object.Name, out var existing))
      return existing;

    this._objects[@object.Name] = @object;
    this.Declared.Add(@object);
    return null;
  }

}
