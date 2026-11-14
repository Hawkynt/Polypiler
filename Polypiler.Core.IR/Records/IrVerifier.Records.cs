using System.Collections.Generic;
using System.Linq;

namespace Polypiler.Core.IR;

public static partial class IrVerifier {

  /// <summary>
  /// Checks the types a module declares: each is a struct with fields, declared once under a name of
  /// its own, its fields named once and of a type a field can have, and none contains itself - by
  /// value, through any chain of struct fields, which no finite value could.
  /// </summary>
  private static TypeTable Types(IrModule module, List<Diagnostic> diagnostics) {
    var table = new TypeTable();
    var names = new HashSet<string>();
    foreach (var declaration in module.Types) {
      void Require(bool holds, string message) {
        if (!holds)
          diagnostics.Add(Error(declaration.Name, message));
      }

      Require(names.Add(declaration.Name), "the module declares the type twice");
      if (declaration is not IrStructType { IsDefined: true } type) {
        Require(false, declaration.IsDefined ? $"a declaration of the kind {declaration.GetType().Name}" : "a type without its fields");
        continue;
      }

      table.Add(type);
      var fields = new HashSet<string>();
      foreach (var field in type.Fields) {
        Require(fields.Add(field.Name), $"the field '{field.Name}' is declared twice");
        var element = field.Type.Element;
        Require(field.Type.IsStruct || field.Type == IrType.Bool || field.Type.IsNumeric || element is { IsNumeric: true },
          $"the field '{field.Name}' has the type {field.Type}");
      }
    }

    foreach (var type in table.Structs) {
      foreach (var field in type.Fields.Where(field => field.Type.StructType is { } nested && !table.Declares(nested)))
        diagnostics.Add(Error(type.Name, $"the field '{field.Name}' has the type {field.Type}, which is not among the types of the module"));

      if (table.Contains(type, type))
        diagnostics.Add(Error(type.Name, "the struct contains itself"));
    }

    return table;
  }

  /// <summary>The structs of a module and what the verifier asks about them.</summary>
  private sealed class TypeTable {

    private readonly HashSet<IrStructType> _structs = new(ReferenceEqualityComparer.Instance);

    public IEnumerable<IrStructType> Structs => this._structs;

    public void Add(IrStructType type) => this._structs.Add(type);

    public bool Declares(IrStructType type) => this._structs.Contains(type);

    /// <summary>Whether a value of a struct holds one of another through its fields; a type outside the module holds nothing.</summary>
    public bool Contains(IrStructType outer, IrStructType inner) {
      var seen = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance);
      var pending = new Stack<IrStructType>([outer]);
      while (pending.TryPop(out var current))
        foreach (var nested in current.Fields.Select(field => field.Type.StructType).OfType<IrStructType>()) {
          if (ReferenceEquals(nested, inner))
            return true;

          if (this.Declares(nested) && seen.Add(nested))
            pending.Push(nested);
        }

      return false;
    }

    /// <summary>Whether no struct a value of a struct consists of, itself included, has a field of an array type.</summary>
    public bool HasDefault(IrStructType type) {
      var seen = new HashSet<IrStructType>(ReferenceEqualityComparer.Instance) { type };
      var pending = new Stack<IrStructType>([type]);
      while (pending.TryPop(out var current))
        foreach (var field in current.Fields) {
          if (field.Type.IsArray)
            return false;

          if (field.Type.StructType is { } nested && this.Declares(nested) && seen.Add(nested))
            pending.Push(nested);
        }

      return true;
    }

  }

}
