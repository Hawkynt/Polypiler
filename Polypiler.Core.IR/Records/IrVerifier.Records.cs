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
      if (declaration is IrEnumType named) {
        table.Add(named);
        Enum(named, Require);
        continue;
      }

      if (declaration is IrInterfaceType { HasMembers: true } contract) {
        table.Add(contract);
        continue;
      }

      if (declaration is IrClassType { IsDefined: true, HasMembers: true } declared) {
        table.Add(declared);
        Fields(declared, true, Require);
        continue;
      }

      if (declaration is not IrStructType { IsDefined: true } type) {
        Require(false, declaration.IsDefined ? $"a declaration of the kind {declaration.GetType().Name}" : "a type without its fields");
        continue;
      }

      table.Add(type);
      Fields(type, false, Require);
    }

    foreach (var type in table.Classes)
      foreach (var field in type.Fields.Where(field => field.Type.Declaration is { } nested && !table.Declares(nested)))
        diagnostics.Add(Error(type.Name, $"the field '{field.Name}' has the type {field.Type}, which is not among the types of the module"));

    foreach (var type in table.Structs) {
      foreach (var field in type.Fields.Where(field => (field.Type.Element ?? field.Type).Declaration is { } nested && !table.Declares(nested)))
        diagnostics.Add(Error(type.Name, $"the field '{field.Name}' has the type {field.Type}, which is not among the types of the module"));

      if (table.Contains(type, type))
        diagnostics.Add(Error(type.Name, "the struct contains itself"));
    }

    return table;
  }

  /// <summary>
  /// The fields of a declaration, each named once and of a type a field can have: a truth value, a
  /// number, a struct, an array of numbers, and in a class also a reference to an object, and in a class
  /// that is no record a memory, a string, an array of anything an array holds and a collection (#1365),
  /// which a record could not compare as both languages do.
  /// </summary>
  private static void Fields(IrTypeDeclaration type, bool references, System.Action<bool, string> require) {
    var fields = new HashSet<string>();
    var plain = references && type is IrClassType { IsRecord: false };
    foreach (var field in type.Fields) {
      require(fields.Add(field.Name), $"the field '{field.Name}' is declared twice");
      var element = field.Type.Element;
      require(!field.Type.IsView && !OnHeap(field.Type), Stored);
      require(field.Type.IsStruct || (references && field.Type.IsObject) || field.Type == IrType.Bool || field.Type.IsNumeric || (element is { IsNumeric: true } && !field.Type.IsMemory)
        || (plain && (field.Type.IsMemory || field.Type.IsString || field.Type.IsCollection || field.Type.IsArray || field.Type.IsOptional) && field.Type.IsStorable),
        $"the field '{field.Name}' has the type {field.Type}");
    }
  }

  /// <summary>
  /// Checks an enum: its underlying type is one of the integers, its members are named once and each
  /// value is one of the underlying type, as the IR carries it.
  /// </summary>
  private static void Enum(IrEnumType type, System.Action<bool, string> require) {
    require(type.Underlying.IsInteger && !type.Underlying.IsEnum, $"an enum over the type {type.Underlying}");
    var members = new HashSet<string>();
    foreach (var member in type.Members) {
      require(member.Name.Length > 0 && members.Add(member.Name), $"the member '{member.Name}' is declared twice");
      require(!type.Underlying.IsInteger || FunctionCheck.InRange(new IrConst(type.Underlying, member.Value)), $"the member '{member.Name}' has the value {member.Value}, which is not one of {type.Underlying}");
    }
  }

  /// <summary>The structs and enums of a module and what the verifier asks about them.</summary>
  private sealed partial class TypeTable {

    private readonly HashSet<IrStructType> _structs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrEnumType> _enums = new(ReferenceEqualityComparer.Instance);

    public IEnumerable<IrStructType> Structs => this._structs;

    public void Add(IrStructType type) => this._structs.Add(type);

    public void Add(IrEnumType type) => this._enums.Add(type);

    public bool Declares(IrStructType type) => this._structs.Contains(type);

    public bool Declares(IrTypeDeclaration type) => type switch {
      IrStructType structure => this.Declares(structure),
      IrEnumType named => this._enums.Contains(named),
      IrInterfaceType contract => this.Declares(contract),
      _ => this.Declares((IrClassType)type)
    };

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
          if (field.Type.IsArray || field.Type.IsString)
            return false;

          if (field.Type.StructType is { } nested && this.Declares(nested) && seen.Add(nested))
            pending.Push(nested);
        }

      return true;
    }

  }

}
